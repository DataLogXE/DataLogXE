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

using DataLogService.DataSource.Test;
using Xunit;

namespace DataLogLib.Tests
{
    public class TestWriteFilterTests
    {
        [Fact]
        public void The_first_value_of_a_tag_is_written()
        {
            Assert.True(new TestWriteFilter().ShouldWrite(1, 0, 0));
        }

        [Fact]
        public void An_unchanged_value_is_not_written_again()
        {
            var filter = new TestWriteFilter();
            filter.ShouldWrite(1, 1, 0);

            Assert.False(filter.ShouldWrite(1, 1, 100));
            Assert.False(filter.ShouldWrite(1, 1, 5_000));
        }

        [Fact]
        public void A_changed_value_is_written_at_once()
        {
            var filter = new TestWriteFilter();
            filter.ShouldWrite(1, 1, 0);

            Assert.True(filter.ShouldWrite(1, 0, 100));
            Assert.True(filter.ShouldWrite(1, 1, 200));
        }

        [Fact]
        public void An_unchanged_value_is_repeated_after_20_seconds_without_a_write()
        {
            var filter = new TestWriteFilter();
            filter.ShouldWrite(1, 1, 1_000);

            Assert.False(filter.ShouldWrite(1, 1, 20_999));
            Assert.True(filter.ShouldWrite(1, 1, 21_000));
            Assert.False(filter.ShouldWrite(1, 1, 40_999));
            Assert.True(filter.ShouldWrite(1, 1, 41_000));
        }

        [Fact]
        public void Tags_are_filtered_independently()
        {
            var filter = new TestWriteFilter();
            filter.ShouldWrite(1, 1, 0);

            Assert.True(filter.ShouldWrite(2, 1, 100));
            Assert.False(filter.ShouldWrite(1, 1, 100));
        }
    }
}
