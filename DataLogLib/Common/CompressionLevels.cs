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

namespace DataLogLib.Common
{
    /// <summary>
    /// One archive resolution. Seconds is the window of the M4 reduction (0 = raw values),
    /// PointsPerSecond the maximum number of stored points per second.
    /// </summary>
    public sealed record CompressionLevel(CompressionType Type, int Seconds, double PointsPerSecond);

    /// <summary>
    /// All archive resolutions ordered from fine to coarse. Every window divides the next one,
    /// so the levels can be built as a cascade (C001 -> C005 -> ... -> C3600). Every level keeps
    /// the M4 points (first, min, max, last) of each window, which makes the cascade lossless:
    /// a level built from the previous one equals the level built from the raw values.
    /// </summary>
    public static class CompressionLevels
    {
        // Points kept per window: first, min, max, last (M4)
        public const int PointsPerWindow = 4;

        public static readonly IReadOnlyList<CompressionLevel> All =
        [
            new(CompressionType.RAW, 0, double.PositiveInfinity),
            Level(CompressionType.C001, 1),
            Level(CompressionType.C005, 5),
            Level(CompressionType.C010, 10),
            Level(CompressionType.C030, 30),
            Level(CompressionType.C060, 60),
            Level(CompressionType.C300, 300),
            Level(CompressionType.C600, 600),
            Level(CompressionType.C1800, 1800),
            Level(CompressionType.C3600, 3600),
        ];

        /// <summary>All compressed levels (everything but raw), built as a cascade.</summary>
        public static IEnumerable<CompressionLevel> CascadeLevels => All.Where(l => l.Seconds > 0);

        public static CompressionLevel Coarsest => All[^1];

        /// <summary>
        /// Returns the level for a chart range of rangeSeconds with pixels of pixelSeconds, from the
        /// levels finer than finerThan (null = all):
        /// the finest level with at most maxPoints points, if its window fits into a pixel;
        /// else the coarsest level whose window fits into a pixel, if it has at most readLimit points
        /// (more points, but a window wider than a pixel loses the shape within the window);
        /// else the finest level with at most readLimit points, which is not pixel exact.
        /// Null when no level fits into a pixel or none has at most readLimit points; then only the
        /// raw values are exact or readable.
        /// </summary>
        public static CompressionLevel SelectForPlot(double rangeSeconds, double pixelSeconds, double maxPoints, double readLimit, CompressionLevel finerThan = null)
        {
            var levels = All.Where(l => l.Seconds > 0 && (finerThan == null || l.Seconds < finerThan.Seconds)).ToList();
            var level = levels.FirstOrDefault(l => rangeSeconds * l.PointsPerSecond <= maxPoints);
            if (level != null && level.Seconds <= pixelSeconds)
            {
                return level;
            }

            var exact = levels.LastOrDefault(l => l.Seconds <= pixelSeconds);
            if (exact == null)
            {
                return null;
            }
            if (rangeSeconds * exact.PointsPerSecond <= readLimit)
            {
                return exact;
            }
            return levels.FirstOrDefault(l => rangeSeconds * l.PointsPerSecond <= readLimit);
        }

        /// <summary>
        /// Returns the level for a coarse preview of a chart range: the finest level finer than
        /// finerThan (null = all) with at most maxPoints points over rangeSeconds. Null when none has
        /// so few points; for the whole range the caller then takes the coarsest level.
        /// </summary>
        public static CompressionLevel SelectForPreview(double rangeSeconds, double maxPoints, CompressionLevel finerThan = null)
            => CascadeLevels.FirstOrDefault(l =>
                (finerThan == null || l.Seconds < finerThan.Seconds) && rangeSeconds * l.PointsPerSecond <= maxPoints);

        /// <summary>
        /// Start of the window of the level that contains the time. The windows are aligned to the
        /// ticks of the local time, like the CompressionWorker builds them.
        /// </summary>
        public static DateTime WindowStart(DateTime time, CompressionLevel level)
        {
            var windowTicks = level.Seconds * TimeSpan.TicksPerSecond;
            return new DateTime(time.Ticks / windowTicks * windowTicks, time.Kind);
        }

        /// <summary>End (exclusive) of the window of the level that contains the time.</summary>
        public static DateTime WindowEnd(DateTime time, CompressionLevel level)
            => WindowStart(time, level).AddSeconds(level.Seconds);

        private static CompressionLevel Level(CompressionType type, int seconds)
            => new(type, seconds, (double)PointsPerWindow / seconds);
    }
}
