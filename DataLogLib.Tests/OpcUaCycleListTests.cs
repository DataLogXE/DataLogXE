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

using DataLogService.DataSource.OpcUa;
using Xunit;

namespace DataLogLib.Tests
{
    public class OpcUaCycleListTests
    {
        [Theory]
        [InlineData(0, new[] { 0, 10, 50, 100, 500, 1000, 5000, 10000 })]
        [InlineData(1, new[] { 10, 50, 100, 500, 1000, 5000, 10000 })]
        [InlineData(50, new[] { 50, 100, 500, 1000, 5000, 10000 })]
        [InlineData(100, new[] { 100, 500, 1000, 5000, 10000 })]
        [InlineData(250, new[] { 500, 1000, 5000, 10000 })]
        [InlineData(10000, new[] { 10000 })]
        [InlineData(60000, new[] { 10000 })]
        public void Cycles_start_at_the_server_rate(int minCycle, int[] expected)
        {
            Assert.Equal(expected, DataSourceOpcUa.GetCycles(minCycle));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(100, 100)]
        [InlineData(250, 500)]
        public void New_tags_get_the_fastest_offered_cycle(int minCycle, int expected)
        {
            Assert.Equal(expected, DataSourceOpcUa.GetDefaultTagCycle(minCycle));
        }
    }
}
