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
    public class PlotPreviewReaderTests
    {
        private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Local);

        /// <summary>Archive whose levels have one point per window (at the window start) inside the range written for them.</summary>
        private sealed class Archive
        {
            public Dictionary<CompressionType, (DateTime From, DateTime To)> Levels { get; } = [];
            public List<(CompressionType Compression, DateTime Start, DateTime End)> Reads { get; } = [];

            public List<PlotPoint> Read(CompressionType compression, DateTime start, DateTime end)
            {
                Reads.Add((compression, start, end));
                Assert.NotEqual(CompressionType.RAW, compression);
                var (from, to) = Levels.GetValueOrDefault(compression);
                var step = CompressionLevels.All.First(l => l.Type == compression).Seconds;

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

            public List<PlotSourceReader.Segment> ReadRange(DateTime start, DateTime end, double maxPoints = 1500)
                => new PlotPreviewReader(Read, maxPoints).Read(start, end);
        }

        private static Archive WithAllLevels(DateTime end)
        {
            var archive = new Archive();
            foreach (var level in CompressionLevels.CascadeLevels)
            {
                archive.Levels[level.Type] = (Start, end);
            }
            return archive;
        }

        [Fact]
        public void Reads_only_the_level_for_the_budget()
        {
            var end = Start.AddDays(1);
            var archive = WithAllLevels(end);

            var segments = archive.ReadRange(Start, end);

            var segment = Assert.Single(segments);
            Assert.Equal(CompressionType.C300, segment.Compression);
            Assert.Equal(288, segment.Points.Count);
            Assert.Equal([CompressionType.C300], archive.Reads.Select(r => r.Compression));
        }

        [Fact]
        public void Fills_the_open_windows_at_the_live_end_from_finer_levels()
        {
            // 7 days up to 23:59:33; every level is written up to its last window that ended 10 s before
            var end = Start.AddDays(7).AddSeconds(-27);
            var archive = new Archive();
            foreach (var level in CompressionLevels.CascadeLevels)
            {
                archive.Levels[level.Type] = (Start, CompressionLevels.WindowStart(end.AddSeconds(-10), level));
            }

            var segments = archive.ReadRange(Start, end);

            Assert.Equal(
                [CompressionType.C1800, CompressionType.C005, CompressionType.C001],
                segments.Select(s => s.Compression));
            var times = segments.SelectMany(s => s.Points).Select(p => p.X).ToList();
            Assert.Equal(times.Order(), times);
            Assert.Equal(times.Count, times.Distinct().Count());
            Assert.Equal(Start.AddDays(7).AddMinutes(-30).ToUnixSeconds(), segments[1].Points[0].X);
            // C005 up to the window of 23:59:15, C001 from 23:59:20 to 23:59:22
            Assert.Equal(end.AddSeconds(-18).ToUnixSeconds(), segments[1].Points[^1].X);
            Assert.Equal(end.AddSeconds(-13).ToUnixSeconds(), segments[2].Points[0].X);
            Assert.Equal(end.AddSeconds(-11).ToUnixSeconds(), segments[2].Points[^1].X);
        }

        [Fact]
        public void Stops_when_the_tail_exceeds_the_budget()
        {
            // The tag stopped after the first day of the week
            var end = Start.AddDays(7);
            var archive = WithAllLevels(Start.AddDays(1));

            var segments = archive.ReadRange(Start, end);

            Assert.Equal(CompressionType.C1800, Assert.Single(segments).Compression);
            Assert.Single(archive.Reads);
        }

        [Fact]
        public void Empty_level_gives_no_segments()
        {
            var end = Start.AddDays(1);
            var archive = new Archive();
            archive.Levels[CompressionType.C001] = (Start, end);

            Assert.Empty(archive.ReadRange(Start, end));
            Assert.Single(archive.Reads);
        }

        [Fact]
        public void Reads_no_head_before_the_first_window()
        {
            // The level starts in the middle of the range; nothing finer is read for the first half
            var end = Start.AddDays(1);
            var archive = WithAllLevels(end);
            archive.Levels[CompressionType.C300] = (Start.AddHours(12), end);

            var segments = archive.ReadRange(Start, end);

            Assert.Equal(Start.AddHours(12).ToUnixSeconds(), Assert.Single(segments).Points[0].X);
            Assert.Single(archive.Reads);
        }

        [Fact]
        public void Uses_the_coarsest_level_when_none_fits()
        {
            var end = Start.AddDays(30);
            var archive = WithAllLevels(end);

            var segment = Assert.Single(archive.ReadRange(Start, end));

            Assert.Equal(CompressionType.C3600, segment.Compression);
            Assert.Equal(720, segment.Points.Count);
        }
    }
}
