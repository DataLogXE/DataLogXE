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

namespace DataLogService.DataSource
{
    /// <summary>
    /// Estimates the shift between the local clock and the clock of a device (local minus device, in ms) from time
    /// stamps the device sends. A sample "receive time minus device time" is the shift plus the transfer and processing
    /// delay, which is never negative, so every sample is an upper bound and the smallest one of a sliding window is
    /// the best estimate (as NTP does). One slow sample, e.g. right after a start under load, is replaced as soon as a
    /// faster one arrives.
    /// <para>
    /// A device clock set forward lowers the samples and is taken at once. A device clock set back raises them: it is
    /// taken once every sample stayed more than <see cref="StepThresholdMs"/> above the estimate for
    /// <see cref="StepConfirmMs"/>, so a load peak does not move the estimate. Thread-safe.
    /// </para>
    /// </summary>
    internal sealed class ClockOffsetEstimator
    {
        internal const long BucketMs = 10_000;
        internal const int WindowBuckets = 60;          // 10 min: follows the drift of the device clock
        internal const long StepThresholdMs = 2_000;
        internal const long StepConfirmMs = 60_000;
        internal const int StepMinSamples = 3;
        internal const long ReportChangeMs = 1_000;

        private const long Unknown = long.MinValue;

        private readonly object _lock = new();
        // Oldest first, one entry per bucket that has a sample: the smallest sample of the bucket.
        private readonly List<(long Index, long MinMs)> _buckets = [];
        private long _estimateMs = Unknown;
        private long _reportedMs = Unknown;
        private DateTime _stepSinceUtc = DateTime.MinValue;
        private long _stepMinMs;
        private int _stepSamples;

        /// <summary>Local clock minus device clock in ms, null before the first sample.</summary>
        public long? ShiftMs
        {
            get
            {
                long value = Interlocked.Read(ref _estimateMs);
                return value == Unknown ? null : value;
            }
        }

        /// <summary>
        /// Adds one sample (receive time minus device time, ms). True when the estimate became known or moved by more
        /// than <see cref="ReportChangeMs"/> since the last true, so the caller logs only real changes.
        /// </summary>
        public bool AddSample(long shiftMs, DateTime nowUtc, out long estimateMs)
        {
            lock (_lock)
            {
                long index = nowUtc.Ticks / (BucketMs * TimeSpan.TicksPerMillisecond);
                AddToBucket(index, shiftMs);
                // Oldest first: no allocation on the hot path (one sample per S7 notification).
                while (_buckets.Count > 0 && _buckets[0].Index <= index - WindowBuckets)
                {
                    _buckets.RemoveAt(0);
                }
                CheckStep(shiftMs, nowUtc, index);

                estimateMs = Minimum();
                Interlocked.Exchange(ref _estimateMs, estimateMs);
                if (_reportedMs == Unknown || Math.Abs(estimateMs - _reportedMs) > ReportChangeMs)
                {
                    _reportedMs = estimateMs;
                    return true;
                }
                return false;
            }
        }

        /// <summary>Forgets every sample, e.g. when the connection is lost.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _buckets.Clear();
                _reportedMs = Unknown;
                _stepSinceUtc = DateTime.MinValue;
                Interlocked.Exchange(ref _estimateMs, Unknown);
            }
        }

        private void AddToBucket(long index, long shiftMs)
        {
            int last = _buckets.Count - 1;
            if (last >= 0 && _buckets[last].Index == index)
            {
                if (shiftMs < _buckets[last].MinMs)
                {
                    _buckets[last] = (index, shiftMs);
                }
                return;
            }
            _buckets.Add((index, shiftMs));
        }

        // A sample far above the estimate starts or continues a step; one near it ends the step. A step that lasts
        // long enough replaces the window by the samples of the step.
        private void CheckStep(long shiftMs, DateTime nowUtc, long index)
        {
            long current = Interlocked.Read(ref _estimateMs);
            if (current == Unknown || shiftMs - current <= StepThresholdMs)
            {
                _stepSinceUtc = DateTime.MinValue;
                return;
            }

            if (_stepSinceUtc == DateTime.MinValue)
            {
                _stepSinceUtc = nowUtc;
                _stepMinMs = shiftMs;
                _stepSamples = 1;
                return;
            }

            _stepMinMs = Math.Min(_stepMinMs, shiftMs);
            _stepSamples++;
            if (_stepSamples >= StepMinSamples && (nowUtc - _stepSinceUtc).TotalMilliseconds >= StepConfirmMs)
            {
                _buckets.Clear();
                _buckets.Add((index, _stepMinMs));
                _stepSinceUtc = DateTime.MinValue;
            }
        }

        private long Minimum()
        {
            long min = long.MaxValue;
            foreach (var bucket in _buckets)
            {
                if (bucket.MinMs < min)
                {
                    min = bucket.MinMs;
                }
            }
            return min;
        }
    }
}
