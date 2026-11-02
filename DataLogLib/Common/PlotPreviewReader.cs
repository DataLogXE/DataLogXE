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
    /// Reads a coarse preview of a chart range, shown while the exact points load. Only compression
    /// levels are read, never the raw values, and the raw values are not counted: the level with at
    /// most maxPoints points over the range (see CompressionLevels.SelectForPreview), else the
    /// coarsest one. After its last window (the open windows at the live end, which the compression
    /// writes only after they end) the points come from finer levels, as long as they stay within
    /// maxPoints over that rest; a long rest without points (a tag that stopped) ends the read.
    /// </summary>
    public sealed class PlotPreviewReader(
        Func<CompressionType, DateTime, DateTime, List<PlotPoint>> readPoints,
        double maxPoints)
    {
        /// <param name="start">Range start</param>
        /// <param name="end">Range end (exclusive)</param>
        /// <returns>The segments in time order, without overlap</returns>
        public List<PlotSourceReader.Segment> Read(DateTime start, DateTime end)
        {
            List<PlotSourceReader.Segment> segments = [];
            if (end <= start)
            {
                return segments;
            }

            var level = CompressionLevels.SelectForPreview((end - start).TotalSeconds, maxPoints) ?? CompressionLevels.Coarsest;
            while (level != null && start < end)
            {
                var points = readPoints(level.Type, start, end);
                if (points.Count == 0)
                {
                    // The range spans many windows of the level; without a point in them the finer
                    // levels have none either, apart from the last seconds at the live end
                    break;
                }

                segments.Add(new(points, level.Type));
                start = CompressionLevels.WindowEnd(points[^1].X.FromUnixSeconds(), level);
                level = CompressionLevels.SelectForPreview((end - start).TotalSeconds, maxPoints, level);
            }
            return segments;
        }
    }
}
