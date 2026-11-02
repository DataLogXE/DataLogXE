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
using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public class CompressionLevelsTests
    {
        [Fact]
        public void Every_level_window_divides_the_next_one()
        {
            var windows = CompressionLevels.All.Where(l => l.Seconds > 0).Select(l => l.Seconds).ToList();
            for (int i = 1; i < windows.Count; i++)
                Assert.Equal(0, windows[i] % windows[i - 1]);
        }

        [Theory]
        [InlineData(60, CompressionType.C001)]
        [InlineData(3 * 3600, CompressionType.C005)]
        [InlineData(4 * 3600, CompressionType.C010)]
        [InlineData(20 * 3600, CompressionType.C030)]
        [InlineData(24 * 3600, CompressionType.C060)]
        [InlineData(6 * 24 * 3600, CompressionType.C300)]
        [InlineData(15 * 24 * 3600, CompressionType.C600)]
        [InlineData(30 * 24 * 3600, CompressionType.C1800)]
        [InlineData(90 * 24 * 3600, CompressionType.C3600)]
        public void Selects_the_finest_level_that_fits(double rangeSeconds, CompressionType expected)
        {
            Assert.Equal(expected, CompressionLevels.SelectForPlot(rangeSeconds, double.PositiveInfinity, 10000, 10000).Type);
        }

        [Fact]
        public void Returns_null_when_no_level_is_within_the_read_limit()
        {
            Assert.Null(CompressionLevels.SelectForPlot(365 * 24 * 3600, 10000, 10000, 10000));
        }

        [Fact]
        public void Every_level_has_its_hypertable_in_the_schema()
        {
            var script = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Templates", "postgres", "001-baseline.sql"));
            foreach (var level in CompressionLevels.All)
            {
                var table = TimescaleArchive.TableOf(level.Type);
                Assert.Equal($"archive{level.Type.ToString().ToLowerInvariant()}", table);
                Assert.Contains($"CREATE TABLE {table} (", script);
                Assert.Contains($"SELECT add_retention_policy('{table}'", script);
            }
        }

        [Theory]
        // The finest level within the point limit, while its window fits into a pixel
        [InlineData(24 * 3600, 60, CompressionType.C060)]
        // 1 week on a wide chart: C300 would fit the limit, but a pixel is only 151 s
        [InlineData(7 * 24 * 3600, 151, CompressionType.C060)]
        // 5 min of a fast tag: no level fits into a pixel of 0.2 s
        [InlineData(600, 0.2, null)]
        public void Selects_for_a_plot_a_level_that_fits_into_a_pixel(double rangeSeconds, double pixelSeconds, CompressionType? expected)
        {
            Assert.Equal(expected, CompressionLevels.SelectForPlot(rangeSeconds, pixelSeconds, 10000, 1e9)?.Type);
        }

        [Fact]
        public void Selects_for_a_plot_the_finest_level_within_the_read_limit_when_the_exact_one_is_too_dense()
        {
            // 1 week on a wide chart with a low limit: C060 would fit the pixel, but needs 40320 values
            Assert.Equal(CompressionType.C300, CompressionLevels.SelectForPlot(7 * 24 * 3600, 151, 10000, 10000).Type);
        }

        [Fact]
        public void Selects_for_a_plot_only_finer_levels_than_given()
        {
            var c030 = CompressionLevels.All.First(l => l.Type == CompressionType.C030);
            Assert.Equal(CompressionType.C010, CompressionLevels.SelectForPlot(24 * 3600, 60, 10000, 1e9, c030).Type);
        }

        [Theory]
        // About one point per 2 pixels of a 3000 px wide chart
        [InlineData(60, CompressionType.C001)]
        [InlineData(3600, CompressionType.C010)]
        [InlineData(24 * 3600, CompressionType.C300)]
        [InlineData(7 * 24 * 3600, CompressionType.C1800)]
        // 30 days have 2880 points even in C3600
        [InlineData(30 * 24 * 3600, null)]
        public void Selects_the_preview_level_for_the_budget(double rangeSeconds, CompressionType? expected)
        {
            Assert.Equal(expected, CompressionLevels.SelectForPreview(rangeSeconds, 1500)?.Type);
        }

        [Fact]
        public void Preview_level_is_finer_than_the_given_one()
        {
            var c010 = CompressionLevels.All.First(l => l.Type == CompressionType.C010);
            // 1 h would be C010, which is not finer than itself; C005 has 2880 points
            Assert.Null(CompressionLevels.SelectForPreview(3600, 1500, c010));
            Assert.Equal(CompressionType.C005, CompressionLevels.SelectForPreview(1200, 1500, c010).Type);
        }

        [Fact]
        public void Window_start_and_end_follow_the_local_time()
        {
            var c600 = CompressionLevels.All.First(l => l.Type == CompressionType.C600);
            var time = new DateTime(2026, 10, 8, 10, 17, 23, DateTimeKind.Local);
            Assert.Equal(new DateTime(2026, 10, 8, 10, 10, 0, DateTimeKind.Local), CompressionLevels.WindowStart(time, c600));
            Assert.Equal(new DateTime(2026, 10, 8, 10, 20, 0, DateTimeKind.Local), CompressionLevels.WindowEnd(time, c600));
        }
    }
}
