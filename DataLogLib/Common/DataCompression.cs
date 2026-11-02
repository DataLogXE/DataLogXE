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
    public class DataCompression
    {
        /// <summary>
        /// LargestTriangleThreeBuckets compression. The data must be sorted by x (ascending).
        /// </summary>
        /// <param name="data">Points (x, y) sorted by x</param>
        /// <param name="threshold">Maximum number of points to keep (0 = no compression)</param>
        /// <returns>The selected points</returns>
        public static IEnumerable<(double, double)> LTTB(IReadOnlyList<(double, double)> data, int threshold)
        {
            return LTTBIndices(data, threshold).Select(i => data[i]).ToList();
        }

        /// <summary>
        /// M4 reduction of one time window: keeps the first, the minimum, the maximum and the last
        /// point (at most 4, indices ascending). Drawn as a line this reproduces the window
        /// pixel-exact, and the M4 points of several sub-windows reduce to the M4 points of the
        /// whole window, so coarser levels can be built from finer ones without any loss.
        /// The data must be sorted by x and contain only finite values.
        /// </summary>
        public static List<int> M4Indices(IReadOnlyList<(double, double)> data)
        {
            int n = data.Count;
            if (n <= 4)
            {
                return [.. Enumerable.Range(0, n)];
            }

            int min = 0, max = 0;
            for (int i = 1; i < n; i++)
            {
                if (data[i].Item2 < data[min].Item2) min = i;
                if (data[i].Item2 > data[max].Item2) max = i;
            }

            return new SortedSet<int> { 0, min, max, n - 1 }.ToList();
        }

        /// <summary>
        /// Reduces plot points to the M4 points of fixed time buckets (one bucket per pixel).
        /// Gaps (points without a raw value) keep their first and last point per bucket.
        /// The points must be sorted by X.
        /// </summary>
        public static List<PlotPoint> M4Buckets(IReadOnlyList<PlotPoint> points, double start, double bucketSeconds)
        {
            List<PlotPoint> result = [];
            List<(double, double)> values = [];
            List<PlotPoint> valuePoints = [];
            PlotPoint firstGap = null, lastGap = null;
            long bucket = long.MinValue;

            void Flush()
            {
                List<PlotPoint> selected = [];
                M4Indices(values).ForEach(i => selected.Add(valuePoints[i]));
                if (firstGap != null) selected.Add(firstGap);
                if (lastGap != null && lastGap != firstGap) selected.Add(lastGap);
                result.AddRange(selected.OrderBy(p => p.X));
                values.Clear();
                valuePoints.Clear();
                firstGap = lastGap = null;
            }

            foreach (var point in points)
            {
                var b = (long)Math.Floor((point.X - start) / bucketSeconds);
                if (b != bucket)
                {
                    Flush();
                    bucket = b;
                }

                if (point.Raw is double raw && double.IsFinite(raw))
                {
                    values.Add((point.X, raw));
                    valuePoints.Add(point);
                }
                else
                {
                    firstGap ??= point;
                    lastGap = point;
                }
            }
            Flush();
            return result;
        }

        /// <summary>
        /// LargestTriangleThreeBuckets compression returning the indices of the selected
        /// points in ascending order. The data must be sorted by x (ascending) and must
        /// contain only finite values.
        /// </summary>
        /// <param name="data">Points (x, y) sorted by x</param>
        /// <param name="threshold">Maximum number of points to keep (0 = no compression)</param>
        /// <returns>Indices of the selected points</returns>
        public static List<int> LTTBIndices(IReadOnlyList<(double, double)> data, int threshold)
        {
            int dataLength = data.Count;

            if (threshold <= 0 || threshold >= dataLength)
            {
                // Nothing to do
                return [.. Enumerable.Range(0, dataLength)];
            }

            // Too few points for a bucket search: keep only the first (and the last) point
            if (threshold < 3)
            {
                return threshold == 1 ? [0] : [0, dataLength - 1];
            }

            List<int> sampled = new(threshold);

            // Bucket size. Leave room for start and end data points
            double every = (double)(dataLength - 2) / (threshold - 2);

            int a = 0;
            sampled.Add(a); // Always add the first point

            for (int i = 0; i < threshold - 2; i++)
            {
                // Calculate point average for next bucket (containing c)
                double avgX = 0;
                double avgY = 0;
                int avgRangeStart = (int)(Math.Floor((i + 1) * every) + 1);
                int avgRangeEnd = (int)(Math.Floor((i + 2) * every) + 1);
                avgRangeEnd = avgRangeEnd < dataLength ? avgRangeEnd : dataLength;

                int avgRangeLength = avgRangeEnd - avgRangeStart;

                for (; avgRangeStart < avgRangeEnd; avgRangeStart++)
                {
                    avgX += data[avgRangeStart].Item1;
                    avgY += data[avgRangeStart].Item2;
                }
                avgX /= avgRangeLength;
                avgY /= avgRangeLength;

                // Get the range for this bucket
                int rangeOffs = (int)(Math.Floor((i + 0) * every) + 1);
                int rangeTo = (int)(Math.Floor((i + 1) * every) + 1);
                rangeTo = Math.Min(rangeTo, dataLength - 1);

                // Point a
                double pointAx = data[a].Item1;
                double pointAy = data[a].Item2;

                double maxArea = -1;
                int nextA = rangeOffs;

                for (; rangeOffs < rangeTo; rangeOffs++)
                {
                    // Calculate triangle area over three buckets
                    double area = Math.Abs((pointAx - avgX) * (data[rangeOffs].Item2 - pointAy) -
                                           (pointAx - data[rangeOffs].Item1) * (avgY - pointAy)
                                      ) * 0.5;
                    if (area > maxArea)
                    {
                        maxArea = area;
                        nextA = rangeOffs; // Next a is this b
                    }
                }

                sampled.Add(nextA); // Pick this point from the bucket
                a = nextA; // This a is the next a (chosen b)
            }

            sampled.Add(dataLength - 1); // Always add last

            return sampled;
        }
    }
}
