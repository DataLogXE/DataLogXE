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
    /// <summary>
    /// Slows bulk work (the OPC UA XML import, the removal of a deleted data source) down while the host is busy, so
    /// the logging of values keeps its share of CPU and disk. On a Raspberry Pi an import of 570 000 tags at full speed
    /// pushed the load to 5 per core: no values were stored for minutes and OPC UA sessions ran into their keep-alive
    /// timeout. Above the limit the work runs only <see cref="DefaultDutyCycle"/> of the time, and it pauses while the
    /// load guard is tense or critical.
    /// </summary>
    public sealed class LoadPacer
    {
        /// <summary>Load per core above which bulk work slows down, 0 = never.</summary>
        public const string MaxLoadKey = "BULK_WORK_MAX_LOAD";

        public const double DefaultMaxLoad = 0.8;
        public const double DefaultDutyCycle = 0.25;

        private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LoadReadInterval = TimeSpan.FromSeconds(1);

        private readonly double _maxLoad;
        private readonly double _dutyCycle;
        private readonly Func<double?> _readLoad;
        private readonly Action<TimeSpan, CancellationToken> _sleep;
        private readonly string _work;
        private readonly LoadGuard _guard;
        private readonly Stopwatch _busy = Stopwatch.StartNew();
        private readonly Stopwatch _sinceLoadRead = new();
        private double? _load;

        public LoadPacer(
            double maxLoad,
            double dutyCycle = DefaultDutyCycle,
            Func<double?> readLoad = null,
            Action<TimeSpan, CancellationToken> sleep = null,
            string work = "Bulk work",
            LoadGuard guard = null)
        {
            _work = work;
            _guard = guard ?? LoadGuard.Shared;
            _maxLoad = maxLoad;
            _dutyCycle = Math.Clamp(dutyCycle, 0.01, 1);
            _readLoad = readLoad ?? SystemLoad.ReadLoadPerCore;
            _sleep = sleep ?? ((pause, cancellationToken) => cancellationToken.WaitHandle.WaitOne(pause));
        }

        /// <summary>True when the last step paused because the host was busy.</summary>
        public bool Slowed { get; private set; }

        /// <summary>The time all steps paused.</summary>
        public TimeSpan Paused { get; private set; }

        /// <summary>
        /// Call after each unit of work: waits while the load guard is tense or critical; while the load per core is above
        /// the limit, pauses so that the work since the last step was <see cref="DefaultDutyCycle"/> of the time (at most
        /// 5 s at once).
        /// </summary>
        public void Step(CancellationToken cancellationToken = default)
        {
            _guard.WaitBelow(LoadLevel.Tense, _work, cancellationToken);
            var busy = _busy.Elapsed;
            if (_maxLoad <= 0 || _dutyCycle >= 1 || !(ReadLoad() > _maxLoad))
            {
                Slowed = false;
                _busy.Restart();
                return;
            }

            var pause = TimeSpan.FromTicks(Math.Min((long)(busy.Ticks * (1 - _dutyCycle) / _dutyCycle), MaxPause.Ticks));
            Slowed = true;
            Paused += pause;
            _sleep(pause, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _busy.Restart();
        }

        /// <summary>Reads <see cref="MaxLoadKey"/> from the environment; missing or invalid gives the default.</summary>
        public static double MaxLoadFromEnvironment()
        {
            var text = Environment.GetEnvironmentVariable(MaxLoadKey);
            if (string.IsNullOrWhiteSpace(text))
            {
                return DefaultMaxLoad;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) || load < 0)
            {
                Log.Error("Invalid {Key} {Value}, using {Default}", MaxLoadKey, text, DefaultMaxLoad);
                return DefaultMaxLoad;
            }

            return load;
        }

        // /proc/loadavg changes every 5 s; a step may come every few milliseconds.
        private double? ReadLoad()
        {
            if (!_sinceLoadRead.IsRunning || _sinceLoadRead.Elapsed >= LoadReadInterval)
            {
                _load = _readLoad();
                _sinceLoadRead.Restart();
            }

            return _load;
        }
    }
}
