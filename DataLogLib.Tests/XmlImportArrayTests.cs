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

using System.Collections.Concurrent;
using DataLogLib.OpcUaNodeset;
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// The XML import gives array tags like the browse of a connected server: one tag per element of an array of
    /// numbers or bools, addressed by an IndexRange, no tags for the element nodes a TIA export lists.
    /// </summary>
    public class XmlImportArrayTests
    {
        private const string Ns = "nsu=http://www.siemens.com/simatic-s7-opcua;s=";

        // Shaped like a TIA export of a S7-1500: element nodes with only the index as BrowseName.
        private const string Nodeset = """
            <?xml version="1.0" encoding="utf-8"?>
            <UANodeSet xmlns="http://opcfoundation.org/UA/2011/03/UANodeSet.xsd">
                <NamespaceUris>
                    <Uri>urn:MyPlc</Uri>
                    <Uri>http://opcfoundation.org/UA/DI/</Uri>
                    <Uri>http://www.siemens.com/simatic-s7-opcua</Uri>
                </NamespaceUris>
                <Aliases>
                    <Alias Alias="DINT">i=6</Alias>
                    <Alias Alias="BOOL">i=1</Alias>
                    <Alias Alias="WSTRING">i=12</Alias>
                    <Alias Alias="DT">ns=3;i=3011</Alias>
                    <Alias Alias="MyUdt">ns=3;i=4000</Alias>
                </Aliases>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;" BrowseName="3:arr" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="DINT" ValueRank="1" ArrayDimensions="3">
                    <DisplayName>arr</DisplayName>
                </UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;[1]" BrowseName="3:1" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;" DataType="DINT"><DisplayName>1</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;[2]" BrowseName="3:2" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;" DataType="DINT"><DisplayName>2</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;[3]" BrowseName="3:3" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;arr&quot;" DataType="DINT"><DisplayName>3</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;m&quot;" BrowseName="3:m" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="BOOL" ValueRank="2" ArrayDimensions="2,2">
                    <DisplayName>m</DisplayName>
                </UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;dt&quot;" BrowseName="3:dt" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="DT" ValueRank="2" ArrayDimensions="2,8">
                    <DisplayName>dt</DisplayName>
                </UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;dt&quot;[0]" BrowseName="3:0" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;dt&quot;" DataType="DT" ValueRank="1" ArrayDimensions="8"><DisplayName>0</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;dt&quot;[1]" BrowseName="3:1" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;dt&quot;" DataType="DT" ValueRank="1" ArrayDimensions="8"><DisplayName>1</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;texts&quot;" BrowseName="3:texts" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="WSTRING" ValueRank="1" ArrayDimensions="2"><DisplayName>texts</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;texts&quot;[0]" BrowseName="3:0" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;texts&quot;" DataType="WSTRING"><DisplayName>0</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;open&quot;" BrowseName="3:open" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="DINT" ValueRank="1" ArrayDimensions="0"><DisplayName>open</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;sa&quot;" BrowseName="3:sa" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="MyUdt" ValueRank="1" ArrayDimensions="2"><DisplayName>sa</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;sa&quot;[0]" BrowseName="3:0" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;sa&quot;" DataType="MyUdt"><DisplayName>0</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;sa&quot;[0].&quot;x&quot;" BrowseName="3:x" ParentNodeId="ns=3;s=&quot;DB&quot;.&quot;sa&quot;[0]" DataType="DINT"><DisplayName>x</DisplayName></UAVariable>
                <UAVariable NodeId="ns=3;s=&quot;DB&quot;.&quot;s&quot;" BrowseName="3:s" ParentNodeId="ns=3;s=&quot;DB&quot;" DataType="DINT"><DisplayName>s</DisplayName></UAVariable>
                <UADataType NodeId="ns=3;i=3011" BrowseName="3:DT">
                    <DisplayName>DT</DisplayName>
                    <References><Reference ReferenceType="HasSubtype" IsForward="false">i=3</Reference></References>
                </UADataType>
                <UADataType NodeId="ns=3;i=4000" BrowseName="3:MyUdt">
                    <DisplayName>MyUdt</DisplayName>
                    <References><Reference ReferenceType="HasSubtype" IsForward="false">i=22</Reference></References>
                </UADataType>
            </UANodeSet>
            """;

        [Fact]
        public void An_array_of_numbers_gives_index_tags_named_like_the_plc_and_drops_its_element_nodes()
        {
            var (rows, renames) = Parse();

            var arr = rows.Where(r => r.Address.EndsWith(";" + Ns + "\"DB\".\"arr\"", StringComparison.Ordinal)).ToList();
            Assert.Equal(["index=0;" + Ns + "\"DB\".\"arr\"", "index=1;" + Ns + "\"DB\".\"arr\"", "index=2;" + Ns + "\"DB\".\"arr\""],
                arr.Select(r => r.Address));
            Assert.Equal(["DB.arr[1]", "DB.arr[2]", "DB.arr[3]"], arr.Select(r => r.Name));
            Assert.Equal("arr[1]", arr[0].DisplayName);
            Assert.All(arr, r => Assert.Equal(2, r.TypeId));
            Assert.DoesNotContain(rows, r => r.Address.StartsWith(Ns + "\"DB\".\"arr\"[", StringComparison.Ordinal));
            Assert.Contains(new KeyValuePair<string, string>(Ns + "\"DB\".\"arr\"[1]", "index=0;" + Ns + "\"DB\".\"arr\""), renames);
        }

        [Fact]
        public void A_matrix_and_an_array_of_dt_bytes_give_one_tag_per_element()
        {
            var (rows, _) = Parse();

            var m = rows.Where(r => r.Address.EndsWith(";" + Ns + "\"DB\".\"m\"", StringComparison.Ordinal)).ToList();
            Assert.Equal(["DB.m[0,0]", "DB.m[0,1]", "DB.m[1,0]", "DB.m[1,1]"], m.Select(r => r.Name));
            Assert.All(m, r => Assert.Equal(1, r.TypeId));

            // DT is a subtype of Byte: Array[0..1] of DT is a 2 x 8 matrix; its element nodes are no arrays of their own.
            Assert.Equal(16, rows.Count(r => r.Address.EndsWith(";" + Ns + "\"DB\".\"dt\"", StringComparison.Ordinal)));
            Assert.DoesNotContain(rows, r => r.Address.Contains("\"dt\"[", StringComparison.Ordinal));
        }

        [Fact]
        public void Text_arrays_and_arrays_without_fixed_size_give_no_tags_and_structure_arrays_keep_their_members()
        {
            var (rows, _) = Parse();

            Assert.DoesNotContain(rows, r => r.Address.Contains("\"texts\"", StringComparison.Ordinal) && r.TypeId != 0);
            Assert.DoesNotContain(rows, r => r.Address.Contains("\"open\"", StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Address == Ns + "\"DB\".\"sa\"[0].\"x\"" && r.TypeId == 2);
            Assert.Contains(rows, r => r.Address == Ns + "\"DB\".\"s\"" && r.TypeId == 2);
        }

        private static (List<XmlImportRow> Rows, List<KeyValuePair<string, string>> Renames) Parse()
        {
            var path = Path.Combine(Path.GetTempPath(), "datalogxe-nodeset-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, Nodeset);
            try
            {
                var rows = new List<XmlImportRow>();
                var renames = new List<KeyValuePair<string, string>>();
                using var batches = new BlockingCollection<List<XmlImportRow>>();
                OpcUaNodesetImportParser.Parse(
                    path, new XmlImportTypeGraph(), batches, null, new XmlImportCounters(), CancellationToken.None, renames);
                batches.CompleteAdding();
                foreach (var batch in batches.GetConsumingEnumerable())
                {
                    rows.AddRange(batch);
                }

                return (rows, renames);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
