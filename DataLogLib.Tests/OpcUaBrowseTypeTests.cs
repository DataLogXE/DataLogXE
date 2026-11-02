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

using DataLogLib.OpcUaNodeset;
using Opc.Ua;
using OpcUaSimpleClientApi;
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// Only values that can be logged become data tags: the browse of a connected server and the XML import leave out
    /// the text and object types, like the S7 browse leaves out strings.
    /// </summary>
    public class OpcUaBrowseTypeTests
    {
        // The kind by name: the enum is internal
        public static TheoryData<NodeId, string> BrowseTypes => new()
        {
            { DataTypeIds.Boolean, "Boolean" },
            { DataTypeIds.Double, "Number" },
            { DataTypeIds.Int32, "Number" },
            { DataTypeIds.Enumeration, "Number" },
            { DataTypeIds.BaseDataType, "Number" },
            { DataTypeIds.Structure, "Structure" },
            { DataTypeIds.String, "Rejected" },
            { DataTypeIds.LocalizedText, "Rejected" },
            { DataTypeIds.DateTime, "Rejected" },
            { DataTypeIds.ByteString, "Rejected" },
            { DataTypeIds.NodeId, "Rejected" },
            { DataTypeIds.StatusCode, "Rejected" },
        };

        [Theory]
        [MemberData(nameof(BrowseTypes))]
        public void The_browse_takes_numbers_and_bools_and_leaves_out_text_types(NodeId dataType, string expected)
        {
            Assert.Equal(expected, OpcUaBrowser.ClassifyKnownDataType(dataType)?.ToString());
        }

        [Theory]
        [InlineData("Boolean", 1)]
        [InlineData("Double", 2)]
        [InlineData("DINT", 2)]
        [InlineData("Enumeration", 2)]
        [InlineData("i=29", 2)]
        [InlineData("i=24", 2)]
        [InlineData("String", 0)]
        [InlineData("WSTRING", 0)]
        [InlineData("LocalizedText", 0)]
        [InlineData("DateTime", 0)]
        [InlineData("i=12", 0)]
        [InlineData("i=21", 0)]
        public void The_xml_import_takes_numbers_and_bools_and_leaves_out_text_types(string dataType, int expected)
        {
            Assert.Equal(expected, new XmlImportTypeGraph().Resolve(dataType, true));
        }

        [Fact]
        public void Own_types_take_the_kind_of_their_base_type()
        {
            var types = new XmlImportTypeGraph();
            types.SuperTypes["ns=3;i=100"] = "i=12";
            types.SuperTypes["ns=3;i=101"] = "i=29";
            types.Aliases["MyText"] = "ns=3;i=100";

            Assert.Equal(0, types.Resolve("MyText", true));
            Assert.Equal(2, types.Resolve("ns=3;i=101", true));
        }
    }
}
