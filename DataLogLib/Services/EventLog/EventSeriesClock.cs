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

namespace DataLogLib.Services.EventLog
{
    internal sealed class EventSeriesClock
    {
        internal const int Capacity = 2048;

        private readonly object _gate = new();
        private readonly Dictionary<SeriesKey, SeriesState> _series = new();
        private readonly long _stepTicks;
        private long _use;

        /// <param name="stepTicks">Finest time step of the store. Times are rounded down to it.</param>
        public EventSeriesClock(long stepTicks = 1)
        {
            _stepTicks = Math.Max(1, stepTicks);
        }

        public DateTime Assign(
            string measurement,
            string level,
            string kind,
            string component,
            string sourceId,
            string sourceType,
            DateTime time)
        {
            var key = new SeriesKey(measurement, level, kind, component, sourceId, sourceType);
            var desired = ToUtc(time).Ticks;
            desired -= desired % _stepTicks;
            lock (_gate)
            {
                if (_series.TryGetValue(key, out var state))
                {
                    if (desired <= state.LastTicks)
                    {
                        // Every event of a series gets its own time, which identifies it as a cursor.
                        // One tick is 100 ns, the finest step DateTime keeps; PostgreSQL keeps 1 µs.
                        desired = state.LastTicks + _stepTicks;
                    }
                }
                else if (_series.Count >= Capacity)
                {
                    EvictLeastRecentlyUsed();
                }

                _series[key] = new SeriesState(desired, ++_use);
            }

            return new DateTime(desired, DateTimeKind.Utc);
        }

        private void EvictLeastRecentlyUsed()
        {
            SeriesKey oldestKey = default;
            var oldestUse = long.MaxValue;
            foreach (var pair in _series)
            {
                if (pair.Value.Use < oldestUse)
                {
                    oldestUse = pair.Value.Use;
                    oldestKey = pair.Key;
                }
            }

            if (oldestUse != long.MaxValue)
            {
                _series.Remove(oldestKey);
            }
        }

        private static DateTime ToUtc(DateTime time)
        {
            if (time.Kind == DateTimeKind.Local)
            {
                return time.ToUniversalTime();
            }

            if (time.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(time, DateTimeKind.Utc);
            }

            return time;
        }

        private readonly struct SeriesKey : IEquatable<SeriesKey>
        {
            private readonly string _measurement;
            private readonly string _level;
            private readonly string _kind;
            private readonly string _component;
            private readonly string _sourceId;
            private readonly string _sourceType;

            public SeriesKey(
                string measurement,
                string level,
                string kind,
                string component,
                string sourceId,
                string sourceType)
            {
                _measurement = measurement ?? "";
                _level = level ?? "";
                _kind = kind ?? "";
                _component = component ?? "";
                _sourceId = sourceId ?? "";
                _sourceType = sourceType ?? "";
            }

            public bool Equals(SeriesKey other)
            {
                return _measurement == other._measurement
                    && _level == other._level
                    && _kind == other._kind
                    && _component == other._component
                    && _sourceId == other._sourceId
                    && _sourceType == other._sourceType;
            }

            public override bool Equals(object obj)
            {
                return obj is SeriesKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_measurement, _level, _kind, _component, _sourceId, _sourceType);
            }
        }

        private readonly struct SeriesState
        {
            public SeriesState(long lastTicks, long use)
            {
                LastTicks = lastTicks;
                Use = use;
            }

            public long LastTicks { get; }
            public long Use { get; }
        }
    }
}
