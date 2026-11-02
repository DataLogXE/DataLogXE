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

using DataLogLib.Models;

namespace DataLogLib.Common
{
    /// <summary>
    /// Reads the points of a chart range from the most suitable sources. The raw values as long as
    /// they fit, otherwise a compression level whose window is not wider than a pixel (see
    /// CompressionLevels.SelectForPlot). Where the level has no points before its first or after its
    /// last window (windows still open at the live end, or not written), the points come from finer
    /// sources: a finer level or the raw values. No read exceeds readLimit values; a source within
    /// the limit that is not pixel exact is preferred to a larger read (small devices set a low
    /// limit). Only when no source fits, the database picks the M4 points per pixel from the raw
    /// values (readRawM4): real raw points, pixel exact, at the cost of a read of all of them.
    /// </summary>
    public sealed class PlotSourceReader(
        Func<DateTime, DateTime, long?> countRaw,
        Func<CompressionType, DateTime, DateTime, List<PlotPoint>> readPoints,
        Func<DateTime, DateTime, List<PlotPoint>> readRawM4,
        double pixelSeconds,
        double maxReadPoints,
        double readLimit)
    {
        /// <summary>
        /// Points of one part of the range read from one source, in time order. Reduced points are
        /// the M4 points per pixel already and need no further reduction.
        /// </summary>
        public sealed record Segment(List<PlotPoint> Points, CompressionType Compression, bool Reduced = false);

        /// <param name="start">Range start</param>
        /// <param name="end">Range end (exclusive)</param>
        /// <returns>The segments in time order, without overlap</returns>
        public List<Segment> Read(DateTime start, DateTime end)
        {
            List<Segment> segments = [];
            Read(start, end, null, segments);
            return segments;
        }

        private void Read(DateTime start, DateTime end, CompressionLevel finerThan, List<Segment> segments)
        {
            if (end <= start)
            {
                return;
            }

            var rawCount = countRaw(start, end) ?? 0;
            if (rawCount == 0)
            {
                return;
            }
            if (rawCount <= maxReadPoints)
            {
                segments.Add(new(readPoints(CompressionType.RAW, start, end), CompressionType.RAW));
                return;
            }

            var rangeSeconds = (end - start).TotalSeconds;
            var level = CompressionLevels.SelectForPlot(rangeSeconds, pixelSeconds, maxReadPoints, readLimit, finerThan);
            if (level == null)
            {
                // Only the raw values fit into a pixel: they are read, as long as they are within the limit
                if (rawCount <= readLimit)
                {
                    segments.Add(new(readPoints(CompressionType.RAW, start, end), CompressionType.RAW));
                    return;
                }

                // Otherwise the finest level within the limit (not pixel exact), else the M4 points of the raw values
                level = CompressionLevels.CascadeLevels.FirstOrDefault(l =>
                    (finerThan == null || l.Seconds < finerThan.Seconds) && rangeSeconds * l.PointsPerSecond <= readLimit);
                if (level == null)
                {
                    segments.Add(new(readRawM4(start, end), CompressionType.RAW, Reduced: true));
                    return;
                }
            }

            var points = readPoints(level.Type, start, end);
            if (points.Count == 0)
            {
                Read(start, end, level, segments);
                return;
            }

            // Before the first and after the last window of the level the finer sources are read
            var headEnd = CompressionLevels.WindowStart(points[0].X.FromUnixSeconds(), level);
            var tailStart = CompressionLevels.WindowEnd(points[^1].X.FromUnixSeconds(), level);
            Read(start, headEnd, level, segments);
            segments.Add(new(points, level.Type));
            Read(tailStart, end, level, segments);
        }
    }
}
