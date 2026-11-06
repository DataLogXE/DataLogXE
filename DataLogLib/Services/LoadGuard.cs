/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Diagnostics;
using System.Globalization;
using DataLogLib.Services.ArchiveAccess;
using Serilog;

namespace DataLogLib.Services
{
    /// <summary>How busy the device is, as the load guard sees it.</summary>
    public enum LoadLevel
    {
        Normal,
        // Background work pauses, large queries wait
        Tense,
        // Medium queries wait as well; if that does not help, the oldest query of the UI is stopped
        Critical
    }

    /// <summary>What the load guard reads in one sample; null when it cannot be read (not Linux).</summary>
    internal readonly record struct LoadReadings(
        double? LoadPerCore,
        long? MemAvailableBytes,
        double? SwapInPagesPerSec,
        double InsertBacklog,
        TimeSpan PoolWait);

    /// <summary>
    /// Sees an overload before it costs values and slows the work down that can wait, so the recording of values
    /// keeps the device: first the recording, then the background work, last the queries of the UI. On
    /// 2026-10-31 six cursor reads blocked a Raspberry Pi for 22 minutes (load 31, 1 GB swap) and ~114 000 values were
    /// lost; the guard would have been critical about a minute after the start. Every 5 s it reads the load per core
    /// (it counts the waits for the SD card and the swap), the available memory, the pages read from swap, the share
    /// of the insert buffer in use (the values in the journal count as well) and the longest wait for a connection
    /// of a pool. Tense: background work (compression repair, counter backfills, imports) pauses and large queries
    /// wait. Critical: medium queries wait as well, and while it lasts the query jobs stop their oldest medium or
    /// large query (see QueryJobService). A level is entered after two samples in a row reach it, so a short peak of a
    /// single reading does not count, and left one step at a time after 30 s below it. The limits are fixed (the same
    /// on a Pi and a PC, per core).
    /// </summary>
    public sealed class LoadGuard
    {
        public const double TenseLoadPerCore = 1.0;
        public const double CriticalLoadPerCore = 2.0;
        public const double TenseSwapInPagesPerSec = 200;
        public const double CriticalSwapInPagesPerSec = 1000;
        public const double TenseInsertBacklog = 0.25;
        public const double CriticalInsertBacklog = 0.60;
        public const long CriticalMemAvailableBytes = 100L * 1024 * 1024;
        public static readonly TimeSpan TensePoolWait = TimeSpan.FromSeconds(1);

        internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
        internal const int EnterSamples = 2;
        internal const int LeaveSamples = 6;
        private static readonly TimeSpan StillLog = TimeSpan.FromMinutes(10);

        /// <summary>The guard of the app; it reads nothing and stays normal until it is started.</summary>
        public static LoadGuard Shared { get; } = new();

        private readonly object _lock = new();
        private readonly Func<double?> _readLoad;
        private readonly Func<long?> _readMemAvailable;
        private readonly Func<long?> _readSwapIns;
        private Func<double> _insertBacklog = () => 0;
        private long _poolWaitTicks;
        private Timer _timer;
        private long? _lastSwapIns;
        private long _lastSwapAt;
        private LoadLevel _level;
        private string _reason = "";
        private bool _recordingAtRisk;
        private int _above;
        private int _below;
        private long _since = Stopwatch.GetTimestamp();
        private long _loggedAt;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal LoadGuard(Func<double?> readLoad = null, Func<long?> readMemAvailable = null, Func<long?> readSwapIns = null)
        {
            _readLoad = readLoad ?? SystemLoad.ReadLoadPerCore;
            _readMemAvailable = readMemAvailable ?? ReadMemAvailable;
            _readSwapIns = readSwapIns ?? ReadSwapIns;
        }

        public LoadLevel Level
        {
            get
            {
                lock (_lock)
                {
                    return _level;
                }
            }
        }

        /// <summary>Raised on the thread of the guard after the level changed; a handler must not block it.</summary>
        public event Action<LoadLevel> LevelChanged;

        /// <summary>Why the level is not normal, e.g. "load 2.4 per core"; empty when it is.</summary>
        public string Reason
        {
            get
            {
                lock (_lock)
                {
                    return _reason;
                }
            }
        }

        /// <summary>
        /// True when the last sample put the recording of values at risk: the insert buffer, the available memory or the
        /// reads from swap at the critical limit. A high load alone is not, it is also caused by the background work
        /// itself (a compression of TimescaleDB reads and writes the SD card).
        /// </summary>
        public bool RecordingAtRisk
        {
            get
            {
                lock (_lock)
                {
                    return _recordingAtRisk;
                }
            }
        }

        internal static bool PutsRecordingAtRisk(LoadReadings r)
            => r.InsertBacklog > CriticalInsertBacklog
                || r.MemAvailableBytes is long memory && memory < CriticalMemAvailableBytes
                || r.SwapInPagesPerSec is double swap && swap > CriticalSwapInPagesPerSec;

        /// <summary>How long the current level lasts.</summary>
        public TimeSpan LevelFor
        {
            get
            {
                lock (_lock)
                {
                    return Stopwatch.GetElapsedTime(_since);
                }
            }
        }

        /// <summary>Starts reading every 5 s; backlog tells the values waiting in the buffer and the journal as a share of the buffer (0..1).</summary>
        public void Start(Func<double> insertBacklog)
        {
            lock (_lock)
            {
                _insertBacklog = insertBacklog ?? (() => 0);
                _timer ??= new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
            }
        }

        /// <summary>Reports how long the work waited for a connection of a pool.</summary>
        internal void ReportPoolWait(TimeSpan wait)
        {
            var ticks = wait.Ticks;
            long current;
            while (ticks > (current = Interlocked.Read(ref _poolWaitTicks))
                && Interlocked.CompareExchange(ref _poolWaitTicks, ticks, current) != current)
            {
            }
        }

        /// <summary>
        /// Blocks the thread while the level is at least level; for background work, before each step. Returns at once
        /// when the guard is below it.
        /// </summary>
        public void WaitBelow(LoadLevel level, string work, CancellationToken cancellationToken = default)
        {
            if (Level < level)
            {
                return;
            }

            var paused = Stopwatch.StartNew();
            Log.Information("{Work} paused, the load guard is {Level} ({Reason})", work, Describe(Level), Reason);
            while (Level >= level)
            {
                Task changed;
                lock (_lock)
                {
                    changed = _changed.Task;
                }
                changed.Wait(cancellationToken);
            }
            Log.Information("{Work} resumed after {Seconds:0} s", work, paused.Elapsed.TotalSeconds);
        }

        /// <summary>Waits while the level is at least level; reports meanwhile why.</summary>
        public async Task WaitBelowAsync(LoadLevel level, Action<string> report, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_level < level)
                    {
                        return;
                    }
                    report?.Invoke($"Waiting: the system is {(_level == LoadLevel.Critical ? "overloaded" : "busy")} ({_reason})");
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken);
            }
        }

        internal void Sample()
        {
            try
            {
                Apply(Read());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Load guard could not read the load");
            }
        }

        private LoadReadings Read()
        {
            var now = Stopwatch.GetTimestamp();
            double? swapIn = null;
            var swapIns = _readSwapIns();
            if (swapIns != null && _lastSwapIns != null)
            {
                var seconds = Stopwatch.GetElapsedTime(_lastSwapAt, now).TotalSeconds;
                swapIn = seconds > 0 ? Math.Max(0, swapIns.Value - _lastSwapIns.Value) / seconds : null;
            }
            _lastSwapIns = swapIns;
            _lastSwapAt = now;

            double backlog;
            try
            {
                backlog = _insertBacklog();
            }
            catch (Exception)
            {
                backlog = 0;
            }
            return new LoadReadings(_readLoad(), _readMemAvailable(), swapIn, backlog,
                TimeSpan.FromTicks(Interlocked.Exchange(ref _poolWaitTicks, 0)));
        }

        /// <summary>The level of one sample and what reached it.</summary>
        internal static (LoadLevel Level, string Reason) Classify(LoadReadings r)
        {
            List<string> critical = [];
            List<string> tense = [];
            if (r.LoadPerCore is double load)
            {
                (load > CriticalLoadPerCore ? critical : load > TenseLoadPerCore ? tense : null)
                    ?.Add(string.Create(CultureInfo.InvariantCulture, $"load {load:0.0} per core"));
            }
            if (r.MemAvailableBytes is long memory && memory < CriticalMemAvailableBytes)
            {
                critical.Add($"{memory / (1024 * 1024)} MB memory available");
            }
            if (r.SwapInPagesPerSec is double swap)
            {
                (swap > CriticalSwapInPagesPerSec ? critical : swap > TenseSwapInPagesPerSec ? tense : null)
                    ?.Add($"{swap:0} pages/s read from swap");
            }
            (r.InsertBacklog > CriticalInsertBacklog ? critical : r.InsertBacklog > TenseInsertBacklog ? tense : null)
                ?.Add($"insert buffer {Math.Min(r.InsertBacklog, 1) * 100:0}% full");
            if (r.PoolWait > TensePoolWait)
            {
                tense.Add(string.Create(CultureInfo.InvariantCulture, $"{r.PoolWait.TotalSeconds:0.0} s wait for a database connection"));
            }

            return critical.Count > 0 ? (LoadLevel.Critical, string.Join(", ", critical.Concat(tense)))
                : tense.Count > 0 ? (LoadLevel.Tense, string.Join(", ", tense))
                : (LoadLevel.Normal, "");
        }

        internal void Apply(LoadReadings readings)
        {
            var (sampled, reason) = Classify(readings);
            TaskCompletionSource changed = null;
            lock (_lock)
            {
                _recordingAtRisk = PutsRecordingAtRisk(readings);
                var from = _level;
                if (sampled > _level)
                {
                    _below = 0;
                    if (++_above >= EnterSamples)
                    {
                        _level = sampled;
                    }
                }
                else if (sampled < _level)
                {
                    _above = 0;
                    // One step at a time: from critical over tense to normal
                    if (++_below >= LeaveSamples)
                    {
                        _level--;
                        _below = 0;
                    }
                }
                else
                {
                    _above = 0;
                    _below = 0;
                }

                if (_level != LoadLevel.Normal && sampled >= _level)
                {
                    _reason = reason;
                }
                else if (_level < from && _level != LoadLevel.Normal)
                {
                    // A step down: what is left of the load, if the last sample still showed some
                    _reason = sampled != LoadLevel.Normal ? reason : $"calming down after {Describe(from)}";
                }

                if (_level != from)
                {
                    var lasted = Stopwatch.GetElapsedTime(_since);
                    _since = Stopwatch.GetTimestamp();
                    _loggedAt = _since;
                    if (_level == LoadLevel.Normal)
                    {
                        _reason = "";
                        Log.Information("Load guard: normal again after {Seconds:0} s {From}", lasted.TotalSeconds, Describe(from));
                    }
                    else if (_level > from)
                    {
                        Log.Warning("Load guard: {Level} ({Reason}); {Effect}", Describe(_level), _reason, Effect(_level));
                    }
                    else
                    {
                        Log.Information("Load guard: {Level} again after {Seconds:0} s {From} ({Reason}); {Effect}",
                            Describe(_level), lasted.TotalSeconds, Describe(from), _reason, Effect(_level));
                    }
                    changed = _changed;
                    _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else if (_level != LoadLevel.Normal && Stopwatch.GetElapsedTime(_loggedAt) >= StillLog)
                {
                    _loggedAt = Stopwatch.GetTimestamp();
                    Log.Warning("Load guard: still {Level} after {Minutes:0} min ({Reason})", Describe(_level), Stopwatch.GetElapsedTime(_since).TotalMinutes, _reason);
                }
            }
            if (changed != null)
            {
                changed.TrySetResult();
                LevelChanged?.Invoke(Level);
            }
        }

        public static string Describe(LoadLevel level) => level switch
        {
            LoadLevel.Tense => "tense",
            LoadLevel.Critical => "critical",
            _ => "normal"
        };

        private static string Effect(LoadLevel level) => level == LoadLevel.Critical
            ? "background work paused, medium and large queries wait, the oldest of them is stopped if it lasts"
            : "background work paused, large queries wait";

        // MemAvailable of /proc/meminfo: what can be taken without swapping, the page cache included
        private static long? ReadMemAvailable()
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        return long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // pswpin of /proc/vmstat: the pages read from swap since the boot
        private static long? ReadSwapIns()
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                foreach (var line in File.ReadLines("/proc/vmstat"))
                {
                    if (line.StartsWith("pswpin ", StringComparison.Ordinal))
                    {
                        return long.Parse(line.AsSpan(7), CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
