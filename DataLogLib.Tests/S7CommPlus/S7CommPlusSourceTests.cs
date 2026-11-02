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
using DataLogService.DataSource.S7CommPlus;
using S7CommPlusDriver;
using S7CommPlusDriver.Symbols;
using Xunit;

namespace DataLogLib.Tests.S7CommPlus
{
    public class S7CommPlusSourceTests
    {
        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, 100)]
        [InlineData(250, 500)]
        [InlineData(1000, 1000)]
        [InlineData(60000, 10000)]
        public void Cycles_are_mapped_to_the_offered_steps(int cycle, int expected)
        {
            Assert.Equal(expected, DataSourceS7CommPlus.NormalizeCycle(cycle));
        }

        [Theory]
        [InlineData(0xAB44A6000205FFFCUL, true)]
        [InlineData(0x8009890012CBFFEFUL, false)]
        [InlineData(0UL, false)]
        public void Resource_limit_is_detected(ulong plcReturn, bool expected)
        {
            Assert.Equal(expected, DataSourceS7CommPlus.IsResourceLimit(plcReturn));
        }

        [Fact]
        public void Paom_is_split_into_order_number_and_firmware()
        {
            var r = new S7ConnectionTestResult();
            DataSourceS7CommPlus.ParsePaom("1;6ES7 512-1DM03-0AB0;V4.1", r);
            Assert.Equal("6ES7 512-1DM03-0AB0", r.OrderNumber);
            Assert.Equal("V4.1", r.Firmware);
            Assert.Equal("S7-1500", r.CpuFamily);
        }

        [Theory]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_REAL, true)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, true)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_DTL, true)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING, false)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_STRING, false)]
        public void Only_tags_that_get_values_take_a_startup_value(uint softdatatype, bool expected)
        {
            // A WString never gets a value: its startup value would become its last value and move 5 s on every connect
            Assert.Equal(expected, DataSourceS7CommPlus.TakesStartupValue(new S7Symbol { Softdatatype = softdatatype }));
        }

        [Fact]
        public void An_unresolved_tag_takes_no_startup_value()
        {
            Assert.False(DataSourceS7CommPlus.TakesStartupValue(null));
        }

        [Theory]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_REAL, DataTagType.Number)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_DTL, DataTagType.Number)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, DataTagType.Bool)]
        public void The_browse_imports_loggable_values_with_their_type(uint softdatatype, DataTagType expected)
        {
            var item = DataSourceS7CommPlus.ToBrowseImportItem(new S7Symbol { Path = "\"DB_Test\".Value", Softdatatype = softdatatype });

            Assert.NotNull(item);
            Assert.Equal("\"DB_Test\".Value", item.Address);
            Assert.Equal((int)expected, item.TypeId);
        }

        [Theory]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING)]
        [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_STRING)]
        public void The_browse_leaves_out_strings(uint softdatatype)
        {
            // Not imported, so a new browse soft-deletes the ones earlier browses imported as type Unknown
            Assert.Null(DataSourceS7CommPlus.ToBrowseImportItem(new S7Symbol { Path = "\"DB_CharacterStrings\".Text", Softdatatype = softdatatype }));
        }
    }
}
