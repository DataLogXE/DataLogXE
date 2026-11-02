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
using Opc.Ua;
using OpcUaSimpleClientApi;
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// The browse of a connected server against an OPC UA server in the test process: big folders come in bigger
    /// pages, an array is one variable and its elements become tags read through an IndexRange.
    /// </summary>
    public class OpcUaBrowseArrayTests : IAsyncLifetime
    {
        private const string Ns = "nsu=" + OpcUaTestServer.NamespaceUri + ";s=";
        private OpcUaTestServer _server;
        private OpcUaClientApi _client;

        public async Task InitializeAsync()
        {
            _server = await OpcUaTestServer.StartAsync();
            _client = await ConnectAsync(_server.Url);
        }

        public async Task DisposeAsync()
        {
            _client?.Disconnect();
            await _server.DisposeAsync();
        }

        [Fact]
        public void An_array_of_numbers_gives_one_tag_per_element_with_an_index_address()
        {
            var result = Browse(_client);

            var arr = result.Tags.Where(t => t.Address.EndsWith(";" + Ns + "Arr", StringComparison.Ordinal)).ToList();
            Assert.Equal(10, arr.Count);
            Assert.Equal("index=0;" + Ns + "Arr", arr[0].Address);
            Assert.Equal("index=9;" + Ns + "Arr", arr[9].Address);
            Assert.All(arr, t => Assert.Equal(2, t.TypeId));
            // The element nodes of the server are not browsed.
            Assert.DoesNotContain(result.Tags, t => t.Address.StartsWith(Ns + "Arr[", StringComparison.Ordinal));
        }

        [Fact]
        public void Element_names_take_the_first_index_of_the_server_and_old_element_tags_move_to_the_index_address()
        {
            var result = Browse(_client);

            var names = result.Tags.Where(t => t.Address.EndsWith(";" + Ns + "Arr", StringComparison.Ordinal)).Select(t => t.Name).ToList();
            Assert.Equal(Enumerable.Range(1, 10).Select(i => $"Arr[{i}]"), names);
            Assert.Contains(new KeyValuePair<string, string>(Ns + "Arr[1]", "index=0;" + Ns + "Arr"), result.Renames);
            Assert.Contains(new KeyValuePair<string, string>(Ns + "Arr[10]", "index=9;" + Ns + "Arr"), result.Renames);
        }

        [Fact]
        public void Element_nodes_that_do_not_start_at_the_first_element_are_not_taken_for_names_or_renames()
        {
            var result = Browse(_client);

            var names = result.Tags.Where(t => t.Address.EndsWith(";" + Ns + "Wrong", StringComparison.Ordinal)).Select(t => t.Name).ToList();
            Assert.Equal(["Wrong[0]", "Wrong[1]", "Wrong[2]", "Wrong[3]"], names);
            Assert.DoesNotContain(result.Renames, r => r.Value.EndsWith(";" + Ns + "Wrong", StringComparison.Ordinal));
        }

        [Fact]
        public void An_array_without_array_dimensions_takes_the_size_of_its_value()
        {
            var result = Browse(_client);

            var names = result.Tags.Where(t => t.Address.EndsWith(";" + Ns + "NoDims", StringComparison.Ordinal)).Select(t => t.Name).ToList();
            Assert.Equal(["NoDims[0]", "NoDims[1]", "NoDims[2]", "NoDims[3]", "NoDims[4]"], names);
        }

        [Fact]
        public void A_matrix_gives_one_tag_per_element_with_one_index_per_dimension()
        {
            var result = Browse(_client);

            var addresses = result.Tags.Where(t => t.Address.EndsWith(";" + Ns + "Matrix", StringComparison.Ordinal)).Select(t => t.Address).ToList();
            Assert.Equal(6, addresses.Count);
            Assert.Equal("index=0,0;" + Ns + "Matrix", addresses[0]);
            Assert.Equal("index=1,2;" + Ns + "Matrix", addresses[5]);
        }

        [Fact]
        public void Arrays_of_text_are_left_out_and_arrays_of_structures_keep_their_members()
        {
            var result = Browse(_client);

            Assert.DoesNotContain(result.Tags, t => t.Address.Contains("Texts", StringComparison.Ordinal));
            Assert.Contains(result.Tags, t => t.Address == Ns + "StructArr[0].x");
            Assert.Contains(result.Tags, t => t.Address == Ns + "StructArr[1].x");
        }

        [Fact]
        public void Program_alarm_instances_are_left_out_like_in_the_TIA_export_and_other_instances_stay()
        {
            var result = Browse(_client);

            Assert.DoesNotContain(result.Tags, t => t.Address.Contains("Alm", StringComparison.Ordinal));
            Assert.Contains(result.Tags, t => t.Address == Ns + "Trig.Q");
        }

        [Fact]
        public void Only_the_program_alarm_type_of_the_Siemens_namespace_is_left_out()
        {
            var namespaces = new NamespaceTable();
            namespaces.Append("urn:other");
            namespaces.Append(OpcUaTestServer.SiemensNamespaceUri);

            Assert.True(OpcUaBrowser.IsProgramAlarmInstance(new ExpandedNodeId("OT_Program_Alarm", 2), namespaces));
            Assert.True(OpcUaBrowser.IsProgramAlarmInstance(
                new ExpandedNodeId("OT_Program_Alarm", OpcUaTestServer.SiemensNamespaceUri), namespaces));
            Assert.False(OpcUaBrowser.IsProgramAlarmInstance(new ExpandedNodeId("OT_Program_Alarm", 1), namespaces));
            Assert.False(OpcUaBrowser.IsProgramAlarmInstance(new ExpandedNodeId("OT_R_TRIG", 2), namespaces));
            Assert.False(OpcUaBrowser.IsProgramAlarmInstance(null, namespaces));
        }

        [Fact]
        public void A_huge_array_is_cut_and_reported()
        {
            var result = Browse(_client);

            var huge = result.Tags.Count(t => t.Address.EndsWith(";" + Ns + "Huge", StringComparison.Ordinal));
            Assert.Equal(OpcUaBrowser.MaxArrayElements, huge);
            Assert.Equal(1, _client.LastArraysTruncated);
        }

        [Fact]
        public void A_folder_with_more_children_than_one_page_is_read_completely()
        {
            var result = Browse(_client);

            Assert.Equal(0, _client.LastBrowseFailures);
            Assert.Equal(OpcUaTestServer.BigFolderChildren, result.Tags.Count(t => t.Address.StartsWith(Ns + "Big.v", StringComparison.Ordinal)));
            Assert.Equal(15, result.Tags.Count(t => t.Address.StartsWith(Ns + "Small", StringComparison.Ordinal)));
            Assert.Contains(result.Tags, t => t.Address == Ns + "Scalar1");
            Assert.Equal(0, _client.LastBrowseFailures);
            Assert.Equal(result.Tags.Count, result.Tags.Select(t => t.Address).Distinct().Count());
        }

        [Fact]
        public async Task A_server_with_few_continuation_points_gives_the_same_result()
        {
            var expected = Browse(_client);
            await using var server = await OpcUaTestServer.StartAsync(maxBrowseContinuationPoints: 2);
            var client = await ConnectAsync(server.Url);
            try
            {
                var result = Browse(client);

                Assert.Equal(expected.Tags.Select(t => t.Address).Order(), result.Tags.Select(t => t.Address).Order());
                Assert.Equal(expected.Tags.Select(t => t.Name).Order(), result.Tags.Select(t => t.Name).Order());
                Assert.Equal(expected.Renames.Count, result.Renames.Count);
                Assert.Equal(0, client.LastBrowseFailures);
            }
            finally
            {
                client.Disconnect();
            }
        }

        [Fact]
        public async Task A_server_without_continuation_points_makes_the_browse_incomplete_instead_of_losing_tags()
        {
            await using var server = await OpcUaTestServer.StartAsync(maxBrowseContinuationPoints: 0);
            var client = await ConnectAsync(server.Url);
            try
            {
                var result = Browse(client);

                // The big folder cannot be read; the failure keeps the sync from deleting its tags.
                Assert.True(client.LastBrowseFailures > 0);
                Assert.Equal(15, result.Tags.Count(t => t.Address.StartsWith(Ns + "Small", StringComparison.Ordinal)));
                Assert.Equal(10, result.Tags.Count(t => t.Address.EndsWith(";" + Ns + "Arr", StringComparison.Ordinal)));
            }
            finally
            {
                client.Disconnect();
            }
        }

        [Theory]
        [InlineData("index=3;" + Ns + "Arr", 103)]
        [InlineData("index=0;" + Ns + "Arr", 100)]
        [InlineData("index=1,2;" + Ns + "Matrix", 12)]
        [InlineData(Ns + "Scalar1", 7)]
        public async Task A_monitored_element_delivers_its_scalar_value(string address, int expected)
        {
            var received = new TaskCompletionSource<MonitoredValueNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.OnMonitoredValue += (_, e) =>
            {
                if (e.ExpNodeIdString == address)
                {
                    received.TrySetResult(e);
                }
            };

            _client.AddMonitoredItem(new MonitoredItemInfo { ExpNodeIdString = address, SamplingInterval = 100 });

            var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expected, Convert.ToInt32(value.Value));
            Assert.Equal(ValueRanks.Scalar, value.DataType.ValueRank);
        }

        [Theory]
        [InlineData("index=5;ns=2;s=x", "ns=2;s=x", "5")]
        [InlineData("index=1,2;nsu=urn:a;s=\"DB\".\"m\"", "nsu=urn:a;s=\"DB\".\"m\"", "1,2")]
        [InlineData("ns=2;s=index=5;x", "ns=2;s=index=5;x", null)]
        [InlineData("nsu=urn:a;s=\"DB\".\"a\"[5]", "nsu=urn:a;s=\"DB\".\"a\"[5]", null)]
        public void An_address_splits_into_node_and_index_range(string address, string node, string range)
        {
            Assert.True(OpcUaTagAddress.TryParse(address, out var nodeAddress, out var indexRange));
            Assert.Equal(node, nodeAddress);
            Assert.Equal(range, indexRange);
        }

        [Theory]
        [InlineData("index=1:3;ns=2;s=x")]
        [InlineData("index=;ns=2;s=x")]
        [InlineData("index=-1;ns=2;s=x")]
        [InlineData("index=5")]
        [InlineData("index=5;")]
        public void An_index_prefix_without_one_element_is_invalid(string address)
        {
            Assert.False(OpcUaTagAddress.TryParse(address, out _, out _));
        }

        [Fact]
        public void An_element_value_is_unwrapped_from_its_one_element_array()
        {
            Assert.True(OpcUaTagAddress.TryUnwrapElement(new Variant(new[] { 42 }), out var value, out var type));
            Assert.Equal(42, value);
            Assert.Equal(BuiltInType.Int32, type.BuiltInType);
            Assert.Equal(ValueRanks.Scalar, type.ValueRank);

            Assert.True(OpcUaTagAddress.TryUnwrapElement(new Variant(new Matrix(new[] { 1.5 }, BuiltInType.Double, 1, 1)), out value, out _));
            Assert.Equal(1.5, value);

            Assert.False(OpcUaTagAddress.TryUnwrapElement(new Variant(new[] { 1, 2 }), out _, out _));
        }

        [Theory]
        [InlineData("Arr[1]", 1, new[] { 1 })]
        [InlineData("5", 1, new[] { 5 })]
        [InlineData("-3", 1, new[] { -3 })]
        [InlineData("m[1,2]", 2, new[] { 1, 2 })]
        [InlineData("1,2", 2, new[] { 1, 2 })]
        public void An_element_node_name_gives_its_indexes(string browseName, int rank, int[] expected)
        {
            Assert.Equal(expected, OpcUaBrowser.ParseElementIndexes(browseName, rank));
        }

        [Theory]
        [InlineData("Arr", 1)]
        [InlineData("x5", 1)]
        [InlineData("5]", 1)]
        [InlineData("[5", 1)]
        [InlineData("1,2", 1)]
        [InlineData("a[1]x", 1)]
        public void Other_names_give_no_indexes(string browseName, int rank)
        {
            Assert.Null(OpcUaBrowser.ParseElementIndexes(browseName, rank));
        }

        [Theory]
        [InlineData(1000u, 1, 1000u)]
        [InlineData(1000u, 20, 50u)]
        [InlineData(1000u, 100, 32u)]
        [InlineData(16u, 4, 16u)]
        public void A_shared_browse_call_splits_its_reference_budget(uint budget, int nodes, uint expected)
        {
            Assert.Equal(expected, OpcUaBrowser.ReferencesPerNode(budget, nodes));
        }

        private static async Task<OpcUaClientApi> ConnectAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPCUA_CERT_PATH")))
            {
                Environment.SetEnvironmentVariable(
                    "OPCUA_CERT_PATH", Path.Combine(Path.GetTempPath(), "datalogxe-opcua-test-client"));
            }

            var client = new OpcUaClientApi();
            client.Connect(url, "DataLogXETest");
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!client.IsConnected)
            {
                Assert.True(DateTime.UtcNow < until, "The client did not connect to the test server.");
                await Task.Delay(100);
            }

            return client;
        }

        private static BrowseResult Browse(OpcUaClientApi client)
        {
            var store = new MemoryStore();
            var sessions = client.OpenBrowseSessions(1, CancellationToken.None);
            try
            {
                client.BrowseVariableNodes(sessions[0], _ => { }, null, CancellationToken.None, store, null);
            }
            finally
            {
                client.CloseBrowseSessions(sessions);
            }

            return new BrowseResult(store.Tags, store.Renames);
        }

        private sealed record BrowseResult(List<DataTagBrowseImportItem> Tags, List<KeyValuePair<string, string>> Renames);

        private sealed class MemoryStore : IOpcUaBrowseStore
        {
            public List<DataTagBrowseImportItem> Tags { get; } = [];
            public List<KeyValuePair<string, string>> Renames { get; } = [];

            public void SetInflight(string phase, IReadOnlyList<string> nodeKeys)
            {
            }

            public void CommitBrowse(IReadOnlyList<string> finishedParents, IReadOnlyList<string> newObjects, IReadOnlyList<BrowseCheckpointNode> newVariables)
            {
            }

            public void CommitClassify(IReadOnlyList<string> finishedVariables, IReadOnlyList<string> newObjects, IReadOnlyList<DataTagBrowseImportItem> scalars)
            {
                Tags.AddRange(scalars);
            }

            public void RenameTags(IReadOnlyList<KeyValuePair<string, string>> renames)
            {
                Renames.AddRange(renames);
            }
        }
    }
}
