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

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Values written per second over a sliding window of written chunks. A chunk holds the values
    /// that came in since the buffer was taken before, so the counts of the chunks divided by the
    /// time between their takes is the rate without the beat of the insert cycle against the cycle
    /// of the statistics (one chunk more or less in a second).
    /// </summary>
    internal class IngestRate
    {
        #region private fields
        private readonly double _windowS;
        private readonly double _idleS;
        // Take time in seconds and value count of each written chunk, oldest first
        private readonly Queue<(double TakeS, long Count)> _chunks = new();
        #endregion

        #region constructor
        internal IngestRate(double windowS, double idleS)
        {
            _windowS = windowS;
            _idleS = idleS;
        }
        #endregion

        #region internal methods
        /// <summary>
        /// Adds a written chunk with the time its values were taken from the buffer.
        /// </summary>
        internal void Add(double takeS, long count)
        {
            lock (_chunks)
            {
                _chunks.Enqueue((takeS, count));
                // One chunk before the window stays as its start
                while (_chunks.Count > 2 && takeS - _chunks.ElementAt(1).TakeS >= _windowS)
                {
                    _chunks.Dequeue();
                }
            }
        }

        /// <summary>
        /// Returns the values per second of the chunks in the window, 0 when nothing was written for
        /// the idle time (the archive is down) and null before the second chunk.
        /// </summary>
        internal double? Rate(double nowS)
        {
            lock (_chunks)
            {
                if (_chunks.Count < 2)
                {
                    return null;
                }

                var (lastS, _) = _chunks.Last();
                if (nowS - lastS > _idleS)
                {
                    return 0;
                }

                var (firstS, _) = _chunks.Peek();
                var count = _chunks.Skip(1).Sum(c => c.Count);
                return count / Math.Max(lastS - firstS, 0.001);
            }
        }
        #endregion
    }
}
