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
using DataLogLib.Common;
using Xunit;

namespace DataLogLib.Tests
{
    public class DataCompressionTests
    {
        private static List<(double, double)> Sine(int n) =>
            Enumerable.Range(0, n).Select(i => ((double)i, Math.Sin(i / 10.0))).ToList();

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        [InlineData(500)]
        public void NothingToDo_ReturnsAllIndices(int threshold)
        {
            var data = Sine(100);
            Assert.Equal(Enumerable.Range(0, 100), DataCompression.LTTBIndices(data, threshold));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(60, 60)]
        public void ReturnsThresholdPoints(int threshold, int expected)
        {
            var idx = DataCompression.LTTBIndices(Sine(1000), threshold);
            Assert.Equal(expected, idx.Count);
        }

        [Fact]
        public void KeepsFirstAndLast_StrictlyAscending_NoDuplicates()
        {
            var idx = DataCompression.LTTBIndices(Sine(997), 60);
            Assert.Equal(0, idx[0]);
            Assert.Equal(996, idx[^1]);
            for (int i = 1; i < idx.Count; i++)
                Assert.True(idx[i] > idx[i - 1]);
        }

        [Fact]
        public void KeepsSpike()
        {
            var data = Enumerable.Range(0, 1000).Select(i => ((double)i, i == 437 ? 100.0 : 0.0)).ToList();
            Assert.Contains(437, DataCompression.LTTBIndices(data, 20));
        }

        [Fact]
        public void LTTB_ReturnsPointsOfIndices()
        {
            var data = Sine(500);
            var idx = DataCompression.LTTBIndices(data, 30);
            Assert.Equal(idx.Select(i => data[i]), DataCompression.LTTB(data, 30));
        }

        [Fact]
        public void M4_KeepsFirstMinMaxLast()
        {
            var data = Enumerable.Range(0, 100).Select(i => ((double)i, i == 30 ? -5.0 : i == 70 ? 9.0 : 1.0)).ToList();
            Assert.Equal([0, 30, 70, 99], DataCompression.M4Indices(data));
        }

        [Fact]
        public void M4_RemovesDuplicateIndices()
        {
            var data = Enumerable.Range(0, 50).Select(i => ((double)i, (double)i)).ToList();
            Assert.Equal([0, 49], DataCompression.M4Indices(data));
        }

        [Fact]
        public void M4_OfSubWindowsEqualsM4OfWholeWindow()
        {
            var rnd = new Random(1);
            var data = Enumerable.Range(0, 600).Select(i => ((double)i, rnd.NextDouble())).ToList();
            var whole = DataCompression.M4Indices(data).Select(i => data[i]).ToList();

            var reduced = new List<(double, double)>();
            for (int w = 0; w < 600; w += 60)
            {
                var part = data.GetRange(w, 60);
                reduced.AddRange(DataCompression.M4Indices(part).Select(i => part[i]));
            }
            Assert.Equal(whole, DataCompression.M4Indices(reduced).Select(i => reduced[i]).ToList());
        }

        [Fact]
        public void M4Buckets_KeepsPeaksPerBucketAndBoundsThePoints()
        {
            // 10000 points over 100 s, a spike at 42.37 s, 50 buckets of 2 s
            var points = Enumerable.Range(0, 10000)
                .Select(i => new PlotPoint { X = i / 100.0, Raw = i == 4237 ? 50 : Math.Sin(i / 100.0) })
                .ToList();
            var reduced = DataCompression.M4Buckets(points, 0, 2);

            Assert.True(reduced.Count <= 4 * 50);
            Assert.Contains(reduced, p => p.Raw == 50);
            Assert.Equal(points.Min(p => p.Raw), reduced.Min(p => p.Raw));
            Assert.Equal(reduced.OrderBy(p => p.X), reduced);
        }

        [Fact]
        public void M4Buckets_KeepsTheBordersOfAGap()
        {
            var points = Enumerable.Range(0, 100)
                .Select(i => new PlotPoint { X = i, Raw = i is >= 20 and < 60 ? null : 1.0 })
                .ToList();
            var reduced = DataCompression.M4Buckets(points, 0, 100);

            Assert.Equal([0.0, 20, 59, 99], reduced.Select(p => p.X));
            Assert.Null(reduced[1].Raw);
            Assert.Null(reduced[2].Raw);
        }
    }
}
