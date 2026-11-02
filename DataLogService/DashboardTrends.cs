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
using Serilog;

namespace DataLogService
{
    /// <summary>
    /// The trends of the dashboard: values per second and system load of the last minutes, the number of
    /// active tags, the database size a week ago and the alarms of the last 24 hours. Everything is read on
    /// the sample cycle, never while the dashboard asks, so its poll every 2 s costs no database query.
    /// </summary>
    public sealed class DashboardTrends : IDisposable
    {
        #region public const
        public static readonly TimeSpan SampleCycle = TimeSpan.FromMilliseconds(Consts.VALUES_PER_SECOND_CYCLE_MS);
        public static readonly int SampleCapacity =
            (Consts.DATASOURCE_THROUGHPUT_SPARKLINE_MINUTES * 60 * 1000) / Consts.VALUES_PER_SECOND_CYCLE_MS;
        public static readonly TimeSpan AlarmCountCycle = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan GrowthCycle = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan GrowthSpan = TimeSpan.FromDays(7);
        public static readonly TimeSpan AlarmSpan = TimeSpan.FromHours(24);
        #endregion

        #region private fields
        private readonly object _gate = new();
        private readonly Func<double?> _valuesPerSec;
        private readonly Func<double?> _loadPercent;
        private readonly Func<int?> _activeTags;
        private readonly Func<DateTime, double?> _databaseSizeGbAt;
        private readonly Func<DateTime, DateTime, long?> _incomingAlarms;
        private readonly Queue<double> _valuesPerSecSeries = new();
        private readonly Queue<double> _loadSeries = new();
        private Timer _timer;
        private int _sampling;
        private DateTime? _growthReadAt;
        private DateTime? _alarmsReadAt;
        private int? _activeTagCount;
        private double? _databaseSizeGbWeekAgo;
        private long? _incomingAlarms24h;
        #endregion

        /// <param name="valuesPerSec">The values per second of the archive now.</param>
        /// <param name="loadPercent">The system load in percent now.</param>
        /// <param name="activeTags">The number of active tags of all active data sources, null when unknown.</param>
        /// <param name="databaseSizeGbAt">The database size in GB at a time (UTC), null when unknown.</param>
        /// <param name="incomingAlarms">The number of alarms that came in the range (UTC), null when unknown.</param>
        public DashboardTrends(
            Func<double?> valuesPerSec,
            Func<double?> loadPercent,
            Func<int?> activeTags,
            Func<DateTime, double?> databaseSizeGbAt,
            Func<DateTime, DateTime, long?> incomingAlarms)
        {
            _valuesPerSec = valuesPerSec;
            _loadPercent = loadPercent;
            _activeTags = activeTags;
            _databaseSizeGbAt = databaseSizeGbAt;
            _incomingAlarms = incomingAlarms;
        }

        #region public methods
        public void Start()
        {
            lock (_gate)
            {
                _timer ??= new Timer(_ => OnTick(), null, TimeSpan.Zero, SampleCycle);
            }
        }

        public DashboardTrendsSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new DashboardTrendsSnapshot
                {
                    ValuesPerSecSeries = _valuesPerSecSeries.ToArray(),
                    LoadPercentSeries = _loadSeries.ToArray(),
                    ActiveTags = _activeTagCount,
                    DatabaseSizeGbWeekAgo = _databaseSizeGbWeekAgo,
                    IncomingAlarms24h = _incomingAlarms24h
                };
            }
        }

        /// <summary>One sample cycle; the database is asked only when its cycle has passed.</summary>
        public void Sample(DateTime nowUtc)
        {
            var valuesPerSec = Read(_valuesPerSec, "values per second");
            var load = Read(_loadPercent, "system load");
            var activeTags = Read(_activeTags, "active tags");

            bool readGrowth;
            bool readAlarms;
            lock (_gate)
            {
                Push(_valuesPerSecSeries, valuesPerSec);
                Push(_loadSeries, load);
                _activeTagCount = activeTags;
                readGrowth = Due(_growthReadAt, GrowthCycle, nowUtc);
                readAlarms = Due(_alarmsReadAt, AlarmCountCycle, nowUtc);
            }

            if (readGrowth)
            {
                var gb = Read(() => _databaseSizeGbAt(nowUtc - GrowthSpan), "database size a week ago");
                lock (_gate)
                {
                    _databaseSizeGbWeekAgo = gb;
                    _growthReadAt = nowUtc;
                }
            }

            if (readAlarms)
            {
                var count = Read(() => _incomingAlarms(nowUtc - AlarmSpan, nowUtc), "alarms of the last 24 hours");
                lock (_gate)
                {
                    _incomingAlarms24h = count;
                    _alarmsReadAt = nowUtc;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
        #endregion

        #region private methods
        private void OnTick()
        {
            // A slow database must not stack up cycles
            if (Interlocked.Exchange(ref _sampling, 1) != 0)
            {
                return;
            }

            try
            {
                Sample(DateTime.UtcNow);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        }

        // An unknown value is left out: a load that was not read is no load of 0 %.
        private static void Push(Queue<double> series, double? value)
        {
            if (value is not double v || !double.IsFinite(v))
            {
                return;
            }

            series.Enqueue(v);
            while (series.Count > SampleCapacity)
            {
                series.Dequeue();
            }
        }

        private static bool Due(DateTime? lastAt, TimeSpan cycle, DateTime nowUtc)
        {
            return lastAt == null || nowUtc - lastAt.Value >= cycle || nowUtc < lastAt.Value;
        }

        private static T? Read<T>(Func<T?> read, string what) where T : struct
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reading the {DashboardTrend} for the dashboard failed", what);
                return null;
            }
        }
        #endregion
    }

    public readonly struct DashboardTrendsSnapshot
    {
        public double[] ValuesPerSecSeries { get; init; }
        public double[] LoadPercentSeries { get; init; }
        public int? ActiveTags { get; init; }
        public double? DatabaseSizeGbWeekAgo { get; init; }
        public long? IncomingAlarms24h { get; init; }
    }
}
