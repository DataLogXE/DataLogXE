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
using DataLogLib.Models;
using Xunit;

namespace DataLogLib.Tests
{
    public class PlotSourceReaderTests
    {
        private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Local);

        /// <summary>
        /// Archive with one raw value per second from Start. Every level has one point per window
        /// (at the window start) inside the range it was written for.
        /// </summary>
        private sealed class Archive(DateTime rawEnd)
        {
            public Dictionary<CompressionType, (DateTime From, DateTime To)> Levels { get; } = [];
            public List<(CompressionType Compression, DateTime Start, DateTime End)> Reads { get; } = [];
            public List<(DateTime Start, DateTime End)> M4Reads { get; } = [];

            public long? Count(DateTime start, DateTime end)
            {
                var from = start > Start ? start : Start;
                var to = end < rawEnd ? end : rawEnd;
                return to > from ? (long)Math.Ceiling((to - from).TotalSeconds) : 0;
            }

            public List<PlotPoint> Read(CompressionType compression, DateTime start, DateTime end)
            {
                Reads.Add((compression, start, end));
                var (from, to) = compression == CompressionType.RAW
                    ? (Start, rawEnd)
                    : Levels.GetValueOrDefault(compression);
                var step = compression == CompressionType.RAW
                    ? 1
                    : CompressionLevels.All.First(l => l.Type == compression).Seconds;

                List<PlotPoint> points = [];
                for (var t = from; t < to; t = t.AddSeconds(step))
                {
                    if (t >= start && t < end)
                    {
                        points.Add(new PlotPoint { X = t.ToUnixSeconds(), Raw = 1 });
                    }
                }
                return points;
            }

            public List<PlotPoint> ReadM4(DateTime start, DateTime end)
            {
                M4Reads.Add((start, end));
                return [new PlotPoint { X = start.ToUnixSeconds(), Raw = 1 }];
            }

            public List<PlotSourceReader.Segment> ReadRange(DateTime start, DateTime end, double pixelSeconds, double readLimit = 100000)
            {
                var reader = new PlotSourceReader(Count, Read, ReadM4, pixelSeconds, 10000, readLimit);
                return reader.Read(start, end);
            }
        }

        [Fact]
        public void Reads_the_raw_values_while_they_fit()
        {
            var archive = new Archive(Start.AddHours(1));
            var segments = archive.ReadRange(Start, Start.AddHours(1), 2);

            var segment = Assert.Single(segments);
            Assert.Equal(CompressionType.RAW, segment.Compression);
            Assert.Equal(3600, segment.Points.Count);
        }

        [Fact]
        public void Selects_a_level_whose_window_fits_into_a_pixel()
        {
            // 10 days, 4 points per window within 10000 would be C600, but a pixel is 120 s
            var end = Start.AddDays(10);
            var archive = new Archive(end);
            archive.Levels[CompressionType.C060] = (Start, end);

            var segments = archive.ReadRange(Start, end, 120);

            Assert.Equal(CompressionType.C060, Assert.Single(segments).Compression);
        }

        [Fact]
        public void Keeps_within_the_read_limit_rather_than_pixel_exact()
        {
            // The same range on a small device: C060 would need 57600 values, more than its limit
            var end = Start.AddDays(10);
            var archive = new Archive(end);
            archive.Levels[CompressionType.C060] = (Start, end);
            archive.Levels[CompressionType.C600] = (Start, end);

            var segments = archive.ReadRange(Start, end, 120, readLimit: 10000);

            Assert.Equal(CompressionType.C600, Assert.Single(segments).Compression);
        }

        [Fact]
        public void Reads_the_open_windows_at_the_end_from_a_finer_source()
        {
            // The level is written up to 23:00, the raw values reach until midnight
            var end = Start.AddDays(1);
            var archive = new Archive(end);
            archive.Levels[CompressionType.C030] = (Start, end.AddHours(-1));

            var segments = archive.ReadRange(Start, end, 30);

            Assert.Equal([CompressionType.C030, CompressionType.RAW], segments.Select(s => s.Compression));
            Assert.Equal(end.AddHours(-1).ToUnixSeconds(), segments[1].Points[0].X);
            Assert.Equal(end.AddSeconds(-1).ToUnixSeconds(), segments[1].Points[^1].X);
        }

        [Fact]
        public void Reads_the_time_before_a_level_existed_from_a_finer_level()
        {
            // C030 exists only for the second day, C005 for both
            var end = Start.AddDays(2);
            var archive = new Archive(end);
            archive.Levels[CompressionType.C030] = (Start.AddDays(1), end);
            archive.Levels[CompressionType.C005] = (Start, end);

            // C010 has no points before the second day either
            var segments = archive.ReadRange(Start, end, 30);

            Assert.Equal([CompressionType.C005, CompressionType.C030], segments.Select(s => s.Compression));
            Assert.Equal(Start.ToUnixSeconds(), segments[0].Points[0].X);
            Assert.True(segments[0].Points[^1].X < Start.AddDays(1).ToUnixSeconds());
        }

        [Fact]
        public void Takes_the_M4_points_of_the_raw_values_when_no_level_has_points()
        {
            var end = Start.AddDays(2);
            var archive = new Archive(end);

            var segments = archive.ReadRange(Start, end, 60);

            var segment = Assert.Single(segments);
            Assert.Equal(CompressionType.RAW, segment.Compression);
            Assert.True(segment.Reduced);
            // Every level down to the read limit was tried once (C005 would need 138240 values), the raw values
            // were not read but reduced by the database over the whole range
            Assert.Equal(
                [CompressionType.C060, CompressionType.C030, CompressionType.C010],
                archive.Reads.Select(r => r.Compression));
            Assert.Equal([(Start, end)], archive.M4Reads);
        }
    }
}
