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

using DataLogLib.Common;
using DataLogLib.Models;
using Serilog;
using System.Diagnostics;
using DataLogLib.Services.DataAccess;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Rebuilds missing parts of the compression levels from the raw values in the archive. The
    /// levels miss values when the service stops without a shutdown (crash, power loss, kill), when
    /// compressed values are dropped after a long archive outage, when the compression of a tag
    /// fails, and for raw values written before the levels existed.
    ///
    /// On startup every level is compared with the raw values before the startup: where a level
    /// ends before the last raw value (the open windows of a crash), the missing part is rebuilt.
    /// Then the older hours since the last check (at most checkDays) are checked: a complete hour
    /// of the coarsest level keeps the first and the last raw value of the hour (M4), and every
    /// hour without them is rebuilt. The hours are checked day by day and the time up to which all
    /// hours were checked is kept in the archive after each day, so a restart continues there. While running, the hours of the values the CompressionWorker could not write are
    /// rebuilt.
    ///
    /// Made for small devices: the raw values are streamed through the cascade in batches, so the
    /// memory does not depend on the density of a tag, and the worker pauses between its steps, so
    /// it takes at most the duty cycle of the time and leaves the archive to the live values. Before
    /// each step it waits while the load per core is above maxLoad, until it falls below
    /// ResumeShare of it, so it runs only while the device has time to spare.
    ///
    /// A rebuilt range is written as further window parts: all its points are raw values and the
    /// union of the parts still contains the M4 points of every window, so existing parts stay valid.
    /// </summary>
    internal sealed class CompressionRepairWorker(
        IArchive archive,
        int checkDays = CompressionRepairWorker.DefaultCheckDays,
        double dutyCycle = CompressionRepairWorker.DefaultDutyCycle,
        double maxLoad = CompressionRepairWorker.DefaultMaxLoad,
        Func<double?> readLoad = null,
        int loadCheckMs = CompressionRepairWorker.LoadCheckMs)
    {
        // Days before the startup that are checked when the archive has no check yet (0 = none)
        internal const int DefaultCheckDays = 7;
        // Share of the time the worker runs, it pauses the rest
        internal const double DefaultDutyCycle = 0.25;
        // Load average per core above which the worker pauses (0 = no limit)
        internal const double DefaultMaxLoad = 0.8;
        // Share of maxLoad below which a paused worker resumes, so it does not toggle at the limit
        internal const double ResumeShare = 0.75;
        internal const int LoadCheckMs = 30_000;
        // Raw values read at most for one rebuild (several hours of a slow tag are read at once)
        internal const long MaxRepairValues = 200_000;
        // Raw values passed through the cascade at once and compressed values written at once
        internal const int BatchValues = 20_000;
        internal const int WriteValues = 5_000;

        // The startup of the service and the archive comes first
        private const int STARTUP_DELAY_MS = 120_000;
        private const int RETRY_DELAY_MS = 30_000;
        private const int IDLE_DELAY_MS = 5_000;
        private const int MAX_PAUSE_MS = 60_000;
        private const int PROGRESS_LOG_MS = 60_000;
        private const int PAUSED_LOG_MS = 600_000;
        // Hours checked with one coverage query of a tag; the progress is kept after each step
        private static readonly TimeSpan CheckStep = TimeSpan.FromDays(1);

        private readonly IArchive _archive = archive;
        private readonly int _checkDays = Math.Max(0, checkDays);
        private readonly double _dutyCycle = Math.Clamp(dutyCycle, 0.01, 1);
        private readonly double _maxLoad = maxLoad;
        private readonly Func<double?> _readLoad = readLoad ?? SystemLoad.ReadLoadPerCore;
        private readonly int _loadCheckMs = Math.Max(1, loadCheckMs);
        private readonly object _queueLock = new();
        private readonly SortedSet<(string Uid, DateTime Hour)> _queue = [];
        private TimeSpan _busy;

        internal void Start(DateTime startup)
        {
            new Thread(() => DatabaseLimits.Background(() => Worker(startup))) { IsBackground = true, Name = "CompressionRepair" }.Start();
        }

        /// <summary>
        /// Queues the hours of values that did not reach the compression levels.
        /// </summary>
        internal void Enqueue(List<ArchiveValue> values)
        {
            var hours = values
                .Select(v => (v.Id, Hour: HourOf(v.Time)))
                .Distinct()
                .ToList();

            lock (_queueLock)
            {
                foreach (var (id, hour) in hours)
                {
                    if (_archive.TryGetUid(id, out var uid))
                    {
                        _queue.Add((uid, hour));
                    }
                }
            }
            Log.Warning("Compression values of {Count} tag hours were lost, they are rebuilt from the raw values", hours.Count);
        }

        private void Worker(DateTime startup)
        {
            Log.Information("Startup worker thread: Archive compression repair");
            Thread.Sleep(STARTUP_DELAY_MS);
            RunStartup(startup);

            while (true)
            {
                ProcessQueue();
                Thread.Sleep(IDLE_DELAY_MS);
            }
        }

        /// <summary>
        /// Rebuilds what the levels miss before the startup and checks the hours since the last check.
        /// </summary>
        internal void RunStartup(DateTime startup)
        {
            // The current hour and the previous one, whose windows close only after the watermark
            // passed the hour, are compared level by level. The older hours are checked by the hour.
            var recentStart = HourOf(startup).AddHours(-1);
            RepairRecent(recentStart, startup);

            if (_checkDays == 0)
            {
                return;
            }

            var checkStart = recentStart.AddDays(-_checkDays);
            var verifiedUntil = Work(() => _archive.GetCompressionVerifiedUntil());
            if (verifiedUntil == null)
            {
                Log.Information("Compression levels are checked from {Start}, older hours stay as they are", checkStart);
            }
            else if (verifiedUntil > checkStart)
            {
                checkStart = verifiedUntil.Value;
            }

            if (checkStart < recentStart)
            {
                CheckHours(checkStart, recentStart);
            }
        }

        private void RepairRecent(DateTime recentStart, DateTime startup)
        {
            var lastTimes = Work(() => _archive.GetLastTimes(recentStart, startup));
            var repaired = 0;
            foreach (var tag in lastTimes.GroupBy(t => t.Uid))
            {
                var from = MissingFrom(tag.ToList(), recentStart);
                if (from != null)
                {
                    Work(() => Repair(tag.Key, from.Value, startup));
                    repaired++;
                }
            }
            Log.Information("Compression levels before the startup rebuilt for {Count} tags", repaired);
        }

        /// <summary>
        /// Returns the earliest time from which a level misses raw values: the end of its last window,
        /// or the range start for a level without values in the range. Null when every level reaches
        /// the last raw value (e.g. after a shutdown, which writes the open windows).
        /// </summary>
        internal static DateTime? MissingFrom(List<(string Uid, CompressionType Compression, DateTime Last)> lastTimes, DateTime rangeStart)
        {
            var raw = lastTimes.Where(t => t.Compression == CompressionType.RAW).Select(t => (DateTime?)t.Last).FirstOrDefault();
            if (raw == null)
            {
                return null;
            }

            DateTime? from = null;
            foreach (var level in CompressionLevels.CascadeLevels)
            {
                var last = lastTimes.Where(t => t.Compression == level.Type).Select(t => (DateTime?)t.Last).FirstOrDefault();
                var levelFrom = last == null ? rangeStart : CompressionLevels.WindowEnd(last.Value, level);
                if (levelFrom <= raw && (from == null || levelFrom < from))
                {
                    from = levelFrom;
                }
            }
            return from;
        }

        /// <summary>
        /// Checks the hours step by step from the oldest one and keeps the end of each checked step
        /// in the archive.
        /// </summary>
        private void CheckHours(DateTime start, DateTime end)
        {
            var steps = (int)Math.Ceiling((end - start) / CheckStep);
            Log.Information("Checking the compression levels from {Start} to {End} in {Steps} steps", start, end, steps);
            var repairedHours = 0;
            var progress = Stopwatch.StartNew();

            for (var step = 0; step < steps; step++)
            {
                var stepStart = start + step * CheckStep;
                var stepEnd = stepStart + CheckStep < end ? stepStart + CheckStep : end;
                var tags = Work(() => _archive.GetRawUids(stepStart, stepEnd));

                for (var t = 0; t < tags.Count; t++)
                {
                    var (uid, first) = tags[t];
                    var from = HourOf(first);
                    var missing = Work(() => _archive.GetHourCoverage(uid, from, stepEnd))
                        .Where(h => !h.IsComplete)
                        .ToList();
                    repairedHours += RepairHours(uid, missing);

                    // Values lost while running are not kept waiting for the whole check
                    ProcessQueue();

                    if (progress.ElapsedMilliseconds >= PROGRESS_LOG_MS)
                    {
                        Log.Information("Compression check: step {Step} of {Steps} ({StepStart}), {Done} of {Count} tags, {Hours} tag hours rebuilt",
                            step + 1, steps, stepStart, t + 1, tags.Count, repairedHours);
                        progress.Restart();
                    }
                }

                // A restart continues after the checked steps
                Work(() => _archive.SetCompressionVerifiedUntil(stepEnd));
            }

            Log.Information("Compression levels checked, {Count} incomplete tag hours rebuilt", repairedHours);
        }

        /// <summary>
        /// Rebuilds the hours, consecutive hours together as long as their raw values fit into one read.
        /// Returns the number of rebuilt hours.
        /// </summary>
        private int RepairHours(string uid, List<HourCoverage> hours)
        {
            var repaired = 0;
            var i = 0;
            while (i < hours.Count)
            {
                var start = hours[i].Hour;
                var end = start.AddHours(1);
                var count = hours[i].RawCount;
                i++;
                while (i < hours.Count && hours[i].Hour == end && count + hours[i].RawCount <= MaxRepairValues)
                {
                    count += hours[i].RawCount;
                    end = end.AddHours(1);
                    i++;
                }

                Work(() => Repair(uid, start, end));
                repaired += (int)(end - start).TotalHours;
            }
            return repaired;
        }

        internal void ProcessQueue()
        {
            while (true)
            {
                (string Uid, DateTime Hour) item;
                lock (_queueLock)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    item = _queue.Min;
                    _queue.Remove(item);
                }
                Work(() => Repair(item.Uid, item.Hour, item.Hour.AddHours(1)));
            }
        }

        /// <summary>
        /// Streams the raw values of the tag through the cascade and writes the M4 points of all
        /// levels. Only one batch of raw values and the open windows are held at a time.
        /// </summary>
        private void Repair(string uid, DateTime start, DateTime end)
        {
            var cascade = new CompressionCascade();
            List<ArchiveValue> batch = new(BatchValues);
            List<ArchiveValue> output = [];
            List<ArchiveValue> lost = [];

            void Compress(long watermark)
            {
                output.AddRange(cascade.Compress(batch, watermark, lost));
                batch.Clear();
                if (output.Count >= WriteValues || (watermark == long.MaxValue && output.Count > 0))
                {
                    _archive.InsertCompressedValuesAndWait(uid, output);
                    output.Clear();
                }
            }

            _archive.ReadRawValues(uid, start, end, value =>
            {
                batch.Add(value);
                if (batch.Count >= BatchValues)
                {
                    // The windows before the window of the latest value are complete
                    Compress(value.Time.Ticks);
                }
            });
            Compress(long.MaxValue);

            if (lost.Count > 0)
            {
                Log.Error("Rebuilding the compression levels of {Uid} from {Start} to {End} failed for {Count} values", uid, start, end, lost.Count);
            }
        }

        /// <summary>
        /// Runs the action until the archive accepts it (e.g. after an outage), then pauses for
        /// the rest of the duty cycle. Waits for a low load before each attempt.
        /// </summary>
        private T Work<T>(Func<T> action)
        {
            while (true)
            {
                WaitForLoad();
                var sw = Stopwatch.StartNew();
                try
                {
                    var result = action();
                    _busy += sw.Elapsed;
                    Pause();
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Compression repair failed, retrying in {Delay} ms", RETRY_DELAY_MS);
                    Thread.Sleep(RETRY_DELAY_MS);
                }
            }
        }

        private void Work(Action action) => Work(() => { action(); return true; });

        /// <summary>
        /// Waits while the load guard is tense or critical, and while the load per core is above maxLoad, until it falls
        /// below ResumeShare of it.
        /// A step that started is not interrupted: its archive read must not stall.
        /// </summary>
        private void WaitForLoad()
        {
            LoadGuard.Shared.WaitBelow(LoadLevel.Tense, "Compression repair");
            if (_maxLoad <= 0)
            {
                return;
            }

            var load = _readLoad();
            if (load == null || load <= _maxLoad)
            {
                return;
            }

            Log.Information("Compression repair paused, system load {Load:P0} per core is above {Max:P0}", load, _maxLoad);
            var paused = Stopwatch.StartNew();
            var logged = Stopwatch.StartNew();
            while (true)
            {
                Thread.Sleep(_loadCheckMs);
                load = _readLoad();
                if (load == null || load < _maxLoad * ResumeShare)
                {
                    break;
                }
                if (logged.ElapsedMilliseconds >= PAUSED_LOG_MS)
                {
                    Log.Information("Compression repair still paused for {Duration}, system load {Load:P0} per core", paused.Elapsed, load);
                    logged.Restart();
                }
            }
            Log.Information("Compression repair resumed after {Duration}, system load {Load:P0} per core", paused.Elapsed, load);
        }

        private void Pause()
        {
            if (_dutyCycle >= 1)
            {
                return;
            }

            var pause = _busy.TotalMilliseconds * (1 - _dutyCycle) / _dutyCycle;
            _busy = TimeSpan.Zero;
            Thread.Sleep((int)Math.Min(pause, MAX_PAUSE_MS));
        }

        private static DateTime HourOf(DateTime time) => CompressionLevels.WindowStart(time, CompressionLevels.Coarsest);
    }
}
