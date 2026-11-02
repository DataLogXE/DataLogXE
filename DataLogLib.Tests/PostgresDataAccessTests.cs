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
using Dapper;
using DataLogLib.Common.Crypt;
using DataLogLib.Services.DataAccess;
using Npgsql;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.OpcUaNodeset;
using DataLogLib.Services;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class PostgresDataAccessTests : IDisposable
    {
        private readonly DataAccessCtrl _access;
        private readonly int _dataSourceId;

        public PostgresDataAccessTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            _access = new DataAccessCtrl();
            _access.Init(PostgresTestConfiguration.Create());
            var source = new DataSourceModel
            {
                Name = "test-" + Guid.NewGuid().ToString("N"),
                TypeId = 2,
                IsActive = 0,
                Host = "localhost",
                Pass = ""
            };
            _access.DataSourceCreate(source);
            _dataSourceId = source.Id;
        }

        public void Dispose()
        {
            if (_access == null)
            {
                return;
            }

            _access.DataSourceDelete(_dataSourceId);
            _access.Close();
        }

        [Fact]
        public void S7_alarm_properties_are_stored_and_off_by_default()
        {
            if (_access == null)
            {
                return;
            }

            var s7 = new DataSourceModel
            {
                Name = "s7-" + Guid.NewGuid().ToString("N"),
                TypeId = (int)DataSourceType.S7CommPlus,
                Host = "192.168.0.10",
                Pass = ""
            };
            _access.DataSourceCreate(s7);
            try
            {
                var loaded = _access.DataSourceGetById(s7.Id);
                Assert.False(loaded.S7AlarmsEnabled);
                Assert.Equal("", loaded.S7Locale);
                Assert.Equal(DataSourceModel.DefaultClockWarnMs, loaded.ClockWarnMs);
                Assert.True(loaded.ClockMonitor);
                Assert.True(loaded.ClockCompensate);

                loaded.S7AlarmsEnabled = true;
                loaded.S7Locale = "de-DE";
                loaded.ClockWarnMs = 2000;
                loaded.ClockMonitor = false;
                loaded.ClockCompensate = false;
                _access.DataSourceUpdate(loaded);

                loaded = _access.DataSourceGetById(s7.Id);
                Assert.True(loaded.S7AlarmsEnabled);
                Assert.Equal("de-DE", loaded.S7Locale);
                Assert.Equal(2000, loaded.ClockWarnMs);
                Assert.False(loaded.ClockMonitor);
                Assert.False(loaded.ClockCompensate);
            }
            finally
            {
                _access.DataSourceDelete(s7.Id);
            }
        }

        [Fact]
        public void Opc_ua_security_is_unsecured_without_rows_and_stored_when_set()
        {
            if (_access == null)
            {
                return;
            }

            var opc = new DataSourceModel
            {
                Name = "opc-" + Guid.NewGuid().ToString("N"),
                TypeId = (int)DataSourceType.OpcUa,
                Host = "192.168.0.11",
                Port = 4840,
                Pass = ""
            };
            _access.DataSourceCreate(opc);
            try
            {
                // A source from before 0.0.200 has no security rows
                using (var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString))
                using (var connection = dataSource.OpenConnection())
                {
                    connection.Execute(
                        "DELETE FROM datasourceproperty WHERE datasourceid = @id AND key LIKE 'opcua.se%' OR " +
                        "datasourceid = @id AND key IN ('opcua.certtrust')", new { id = opc.Id });
                }

                var loaded = _access.DataSourceGetById(opc.Id);
                Assert.Equal(OpcUaSecurityValues.ModeNone, loaded.OpcUaSecurityMode);
                Assert.Equal(OpcUaSecurityValues.PolicyBest, loaded.OpcUaSecurityPolicy);
                Assert.Equal(OpcUaSecurityValues.TrustAny, loaded.OpcUaCertTrust);
                Assert.Equal("", loaded.OpcUaServerCertThumbprint);
                Assert.Equal("", loaded.OpcUaServerCertSubject);
                Assert.Null(loaded.OpcUaServerCertNotAfter);

                loaded.OpcUaSecurityMode = OpcUaSecurityValues.ModeSignAndEncrypt;
                loaded.OpcUaSecurityPolicy = "Basic256Sha256";
                loaded.OpcUaCertTrust = OpcUaSecurityValues.TrustPinned;
                loaded.OpcUaServerCertThumbprint = "06:a8:26:13:a3:fd:7a:c5:b7:88:09:67:01:c8:da:9d:3c:3d:38:9b";
                loaded.OpcUaServerCertSubject = "CN=PLC_1, O=Siemens";
                loaded.OpcUaServerCertNotAfter = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
                _access.DataSourceUpdate(loaded);

                loaded = _access.DataSourceGetById(opc.Id);
                Assert.Equal(OpcUaSecurityValues.ModeSignAndEncrypt, loaded.OpcUaSecurityMode);
                Assert.Equal("Basic256Sha256", loaded.OpcUaSecurityPolicy);
                Assert.Equal(OpcUaSecurityValues.TrustPinned, loaded.OpcUaCertTrust);
                Assert.Equal("06A82613A3FD7AC5B788096701C8DA9D3C3D389B", loaded.OpcUaServerCertThumbprint);
                Assert.Equal("CN=PLC_1, O=Siemens", loaded.OpcUaServerCertSubject);
                Assert.Equal(new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc), loaded.OpcUaServerCertNotAfter);
                Assert.Equal(DateTimeKind.Utc, loaded.OpcUaServerCertNotAfter?.Kind);
            }
            finally
            {
                _access.DataSourceDelete(opc.Id);
            }
        }

        [Fact]
        public void Passwords_are_stored_with_aes_and_old_des_values_are_migrated_once()
        {
            if (_access == null)
            {
                return;
            }

            var opc = new DataSourceModel
            {
                Name = "pass-" + Guid.NewGuid().ToString("N"),
                TypeId = (int)DataSourceType.OpcUa,
                Host = "192.168.0.12",
                Port = 4840,
                User = "user",
                Pass = "geheim;=\"x"
            };
            _access.DataSourceCreate(opc);
            try
            {
                using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
                using var connection = dataSource.OpenConnection();
                var stored = connection.ExecuteScalar<string>("SELECT pass FROM datasource WHERE id = @id", new { id = opc.Id });
                Assert.StartsWith(SecretProtector.Prefix, stored);
                Assert.DoesNotContain("geheim", stored);
                Assert.Equal("geheim;=\"x", _access.DataSourceGetById(opc.Id).Pass);

                // The value an older version wrote
                var guid = connection.ExecuteScalar<string>("SELECT guid FROM datasource WHERE id = @id", new { id = opc.Id });
                connection.Execute("UPDATE datasource SET pass = @pass WHERE id = @id",
                    new { id = opc.Id, pass = LegacyDesCrypt.Encrypt("old-secret", guid) });
                Assert.Equal("old-secret", _access.DataSourceGetById(opc.Id).Pass);

                Assert.True(PostgresConnection.MigrateDataSourcePasswords(connection) >= 1);
                stored = connection.ExecuteScalar<string>("SELECT pass FROM datasource WHERE id = @id", new { id = opc.Id });
                Assert.StartsWith(SecretProtector.Prefix, stored);
                Assert.Equal("old-secret", _access.DataSourceGetById(opc.Id).Pass);

                // Nothing left to migrate for this row
                var before = stored;
                PostgresConnection.MigrateDataSourcePasswords(connection);
                Assert.Equal(before,
                    connection.ExecuteScalar<string>("SELECT pass FROM datasource WHERE id = @id", new { id = opc.Id }));

                // A value that does not open reads as no password instead of failing the read
                connection.Execute("UPDATE datasource SET pass = 'v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' WHERE id = @id",
                    new { id = opc.Id });
                Assert.Equal("", _access.DataSourceGetById(opc.Id).Pass);
            }
            finally
            {
                _access.DataSourceDelete(opc.Id);
            }
        }

        [Fact]
        public void A_stored_clock_threshold_of_0_without_clock_monitor_reads_as_watch_off()
        {
            if (_access == null)
            {
                return;
            }

            // A source stored before clock.monitor: a threshold of 0 turned the clock watch off.
            using (var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString))
            {
                connection.Open();
                using var command = new Npgsql.NpgsqlCommand(
                    "DELETE FROM datasourceproperty WHERE datasourceid = @id AND key = 'clock.monitor'; "
                    + "UPDATE datasourceproperty SET value = '0' WHERE datasourceid = @id AND key = 'clock.warnms'",
                    connection);
                command.Parameters.AddWithValue("@id", _dataSourceId);
                command.ExecuteNonQuery();
            }

            var loaded = _access.DataSourceGetById(_dataSourceId);
            Assert.False(loaded.ClockMonitor);
            Assert.Equal(DataSourceModel.DefaultClockWarnMs, loaded.ClockWarnMs);
            Assert.True(loaded.ClockCompensate);
        }

        [Fact]
        public void Data_source_properties_are_stored_one_row_each_and_read_back()
        {
            if (_access == null)
            {
                return;
            }

            var opcUa = _access.DataSourceGetById(_dataSourceId);
            Assert.Equal(0, opcUa.MinCycle);
            Assert.True(opcUa.OpcUaAlarmsEnabled);
            Assert.Equal("", opcUa.OpcUaLocale);
            Assert.Empty(opcUa.OpcUaAvailableLocales);
            opcUa.MinCycle = 100;
            opcUa.OpcUaAlarmsEnabled = false;
            opcUa.OpcUaLocale = "de-DE";
            opcUa.OpcUaAvailableLocales = ["en-US", "de-DE"];
            _access.DataSourceUpdate(opcUa);

            opcUa = _access.DataSourceGetById(_dataSourceId);
            Assert.Equal(100, opcUa.MinCycle);
            Assert.False(opcUa.OpcUaAlarmsEnabled);
            Assert.Equal("de-DE", opcUa.OpcUaLocale);
            Assert.Equal(["en-US", "de-DE"], opcUa.OpcUaAvailableLocales);

            var test = new DataSourceModel
            {
                Name = "test-" + Guid.NewGuid().ToString("N"),
                TypeId = 99,
                Pass = "",
                TestCycleMs = 500,
                TestTagCount = 25
            };
            _access.DataSourceCreate(test);
            try
            {
                var loaded = _access.DataSourceGetAll().Single(ds => ds.Id == test.Id);
                Assert.Equal(500, loaded.TestCycleMs);
                Assert.Equal(25, loaded.TestTagCount);
                Assert.Equal(0, loaded.MinCycle);

                loaded.TestCycleMs = 100;
                _access.DataSourceUpdate(loaded);
                Assert.Equal(100, _access.DataSourceGetById(test.Id).TestCycleMs);

                Assert.Equal(
                    "mincycle=0|test.cyclems=100|test.tagcount=25",
                    PropertyRows(test.Id));
                Assert.Equal(
                    "clock.compensate=true|clock.monitor=true|clock.warnms=10000|mincycle=100|opcua.alarmsenabled=false|opcua.availablelocales=en-US,de-DE|" +
                    "opcua.certtrust=any|opcua.locale=de-DE|opcua.securitymode=none|opcua.securitypolicy=best|" +
                    "opcua.servercertnotafter=|opcua.servercertsubject=|opcua.servercertthumbprint=",
                    PropertyRows(_dataSourceId));
            }
            finally
            {
                _access.DataSourceDelete(test.Id);
            }

            Assert.Equal("", PropertyRows(test.Id));
        }

        [Fact]
        public void Min_cycle_set_writes_only_that_property()
        {
            if (_access == null)
            {
                return;
            }

            var opcUa = _access.DataSourceGetById(_dataSourceId);
            opcUa.OpcUaLocale = "de-DE";
            _access.DataSourceUpdate(opcUa);

            _access.DataSourceMinCycleSet(_dataSourceId, 250);
            var loaded = _access.DataSourceGetById(_dataSourceId);
            Assert.Equal(250, loaded.MinCycle);
            Assert.Equal("de-DE", loaded.OpcUaLocale);

            // A data source that is gone gets no row (and no foreign key error).
            _access.DataSourceMinCycleSet(int.MaxValue, 100);
            Assert.Equal("", PropertyRows(int.MaxValue));
        }

        private static string PropertyRows(int dataSourceId)
        {
            using var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT coalesce(string_agg(key || '=' || value, '|' ORDER BY key), '') " +
                "FROM datasourceproperty WHERE datasourceid = @id";
            cmd.Parameters.AddWithValue("@id", dataSourceId);
            return (string)cmd.ExecuteScalar();
        }

        [Fact]
        public void Browse_checkpoint_ignores_duplicate_keys()
        {
            if (_access == null)
            {
                return;
            }

            _access.BrowseCheckpointCreate(_dataSourceId);
            _access.BrowseCheckpointSetInflight(_dataSourceId, "browse", ["a", "a", " ", "b"]);
            _access.BrowseCheckpointCommitBrowse(
                _dataSourceId,
                ["root"],
                ["obj1", "obj1", "obj2"],
                [new BrowseCheckpointNode { NodeKey = "var1", DisplayName = "V1" }, new BrowseCheckpointNode { NodeKey = "var1" }]);
            _access.BrowseCheckpointCommitBrowse(_dataSourceId, ["obj1"], ["obj2"], []);
            _access.BrowseCheckpointCommitClassify(
                _dataSourceId,
                ["var1"],
                [],
                [
                    new DataTagBrowseImportItem { Address = "ns=1;s='x'", Name = "X", TypeId = 2 },
                    new DataTagBrowseImportItem { Address = "ns=1;s='x'", Name = "X", TypeId = 2 }
                ]);

            var state = _access.BrowseCheckpointLoad(_dataSourceId);
            Assert.True(state.Exists);
            Assert.Equal(["obj2"], state.Queue);
            Assert.Contains("obj1", state.Visited);
            Assert.Empty(state.Pending);
            Assert.Equal(1, state.FoundCount);

            var result = _access.DataTagsSyncFromBrowseCheckpoint(_dataSourceId, 1000, null, CancellationToken.None);
            Assert.Equal(1, result.Created);
            var again = _access.DataTagsSyncFromBrowseCheckpoint(_dataSourceId, 1000, null, CancellationToken.None);
            Assert.Equal(0, again.Created);
            Assert.Equal(0, again.Updated);
        }

        [Fact]
        public void Active_and_cycle_are_set_together_and_null_keeps_the_stored_value()
        {
            if (_access == null)
            {
                return;
            }

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "ns=2;s=A", Name = "A", DisplayName = "Aa", TypeId = 1 },
                new() { Address = "ns=2;s=B", Name = "B", TypeId = 2 },
                new() { Address = "ns=2;s=C", Name = "C", TypeId = 2 }
            };
            _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            var byName = _access.DataTagsGetByDataSourceId(_dataSourceId).ToDictionary(tag => tag.Name, tag => tag.Id);
            var ab = new List<int> { byName["A"], byName["B"] };

            _access.DataTagsUpdateActiveAndCycle(ab, 1, 500);
            _access.DataTagsUpdateActiveAndCycle(ab, null, 2000);
            _access.DataTagsUpdateActiveAndCycle([byName["B"]], 0, null);
            _access.DataTagsUpdateActiveAndCycle([byName["C"]], null, null);

            var tags = _access.DataTagsGetByIds(_dataSourceId, byName.Values.ToList()).ToDictionary(tag => tag.Name);
            Assert.Equal((1, 2000), (tags["A"].IsActive, tags["A"].Cycle));
            Assert.Equal((0, 2000), (tags["B"].IsActive, tags["B"].Cycle));
            Assert.Equal((0, 1000), (tags["C"].IsActive, tags["C"].Cycle));
            // The list rows are patched with the result, so the address must come along.
            Assert.Equal("ns=2;s=A", tags["A"].Address);
        }

        [Fact]
        public void Active_tags_are_counted_and_listed_only_when_not_deleted_and_of_an_active_source()
        {
            if (_access == null)
            {
                return;
            }

            var before = _access.DataTagsCountActive();
            var running = new DataSourceModel { Name = "running-" + Guid.NewGuid().ToString("N"), TypeId = 2, IsActive = 1, Host = "localhost", Pass = "" };
            _access.DataSourceCreate(running);
            try
            {
                var items = new List<DataTagBrowseImportItem>
                {
                    new() { Address = "ns=2;s=A", Name = "A", TypeId = 2 },
                    new() { Address = "ns=2;s=B", Name = "B", TypeId = 2 },
                    new() { Address = "ns=2;s=C", Name = "C", TypeId = 2 },
                    new() { Address = "ns=2;s=D", Name = "D", TypeId = 2 }
                };
                _access.DataTagsSyncFromBrowse(running.Id, 1000, items, null, CancellationToken.None);
                var byName = _access.DataTagsGetByDataSourceId(running.Id).ToDictionary(tag => tag.Name, tag => tag.Id);
                _access.DataTagsUpdateActiveAndCycle([byName["A"], byName["B"], byName["C"]], 1, null);
                _access.DataTagsUpdateDeleted([byName["C"]], 1);

                // An active tag of the inactive source of the fixture is not logged
                _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, [new() { Address = "ns=2;s=X", Name = "X", TypeId = 2 }], null, CancellationToken.None);
                _access.DataTagsUpdateActiveAndCycle([_access.DataTagsGetByDataSourceId(_dataSourceId).Single().Id], 1, null);

                Assert.Equal(before + 2, _access.DataTagsCountActive());
                var page = WaitForPage(running.Id, "", [], active: 1);
                Assert.Equal(["A", "B"], page.Items.Select(t => t.Name).Order());
            }
            finally
            {
                _access.DataSourceDelete(running.Id);
            }
        }

        [Fact]
        public void Details_by_ids_skip_deleted_tags_and_tags_of_other_sources()
        {
            if (_access == null)
            {
                return;
            }

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "ns=2;s=A", Name = "A", DisplayName = "Aa", TypeId = 1 },
                new() { Address = "ns=2;s=B", Name = "B", TypeId = 2 }
            };
            _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            var byName = _access.DataTagsGetByDataSourceId(_dataSourceId).ToDictionary(tag => tag.Name, tag => tag.Id);
            _access.DataTagsUpdateDeleted([byName["B"]], 1);

            var other = new DataSourceModel { Name = "other-" + Guid.NewGuid().ToString("N"), TypeId = 2, Host = "localhost", Pass = "" };
            _access.DataSourceCreate(other);
            try
            {
                _access.DataTagsSyncFromBrowse(other.Id, 1000, [new() { Address = "ns=2;s=X", Name = "X", TypeId = 2 }], null, CancellationToken.None);
                var otherId = _access.DataTagsGetByDataSourceId(other.Id).Single().Id;

                var details = _access.DataTagDetailsGetByIds(_dataSourceId, [byName["B"], otherId, byName["A"]]);
                var tag = Assert.Single(details);
                Assert.Equal(byName["A"], tag.Id);
                Assert.Equal(("A", "Aa", "ns=2;s=A", 1, 1000), (tag.Name, tag.DisplayName, tag.Address, tag.TypeId, tag.Cycle));
                Assert.False(string.IsNullOrEmpty(tag.Guid));
                Assert.NotEqual(default, tag.CreatedAt);
            }
            finally
            {
                _access.DataSourceDelete(other.Id);
            }
        }

        [Fact]
        public void Browse_sync_handles_quotes_duplicates_and_id_lists()
        {
            if (_access == null)
            {
                return;
            }

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "ns=2;s=O'Brien", Name = "O'Brien", TypeId = 2 },
                new() { Address = "ns=2;s=O'Brien", Name = "dup", TypeId = 2 },
                new() { Address = "ns=2;s=B", Name = "B", DisplayName = "Bee", TypeId = 1 }
            };
            var result = _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            Assert.Equal(2, result.Created);

            items[2].DisplayName = null;
            var update = _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            Assert.Equal(1, update.Updated);

            var tags = _access.DataTagsGetByDataSourceId(_dataSourceId);
            var ids = tags.Select(tag => tag.Id).ToList();
            Assert.Equal(2, _access.DataTagsGetByIds(_dataSourceId, ids).Count);

            _access.DataTagsUpdateDeleted([], 1);
            _access.DataTagsUpdateDeleted(ids, 1);
            Assert.Empty(_access.DataTagsGetByDataSourceId(_dataSourceId, deleted: 0));

            var details = _access.DataTagDetailsGet(dataSourceId: _dataSourceId);
            Assert.Equal(2, details.Count);

            var existing = _access.DataTagGetOrCreateByAddress(new DataTagModel
            {
                DataSourceId = _dataSourceId,
                Address = "ns=2;s=O'Brien",
                Name = "ignored"
            });
            Assert.Equal("O'Brien", existing.Name);
        }

        [Fact]
        public void Array_element_tags_move_to_the_index_address_and_keep_id_and_activation()
        {
            if (_access == null)
            {
                return;
            }

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "ns=3;s=\"DB\".\"a\"[1]", Name = "DB.a[1]", TypeId = 2 },
                new() { Address = "ns=3;s=\"DB\".\"a\"[2]", Name = "DB.a[2]", TypeId = 2 },
                new() { Address = "index=1;ns=3;s=\"DB\".\"a\"", Name = "DB.a[2]", TypeId = 2 }
            };
            _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            var before = _access.DataTagsGetByDataSourceId(_dataSourceId);
            var first = before.Single(t => t.Address == items[0].Address);
            _access.DataTagsUpdateActive([first.Id], 1);

            var renamed = _access.DataTagsRenameAddresses(_dataSourceId,
            [
                new("ns=3;s=\"DB\".\"a\"[1]", "index=0;ns=3;s=\"DB\".\"a\""),
                // The index address has a tag already: the old tag keeps its address.
                new("ns=3;s=\"DB\".\"a\"[2]", "index=1;ns=3;s=\"DB\".\"a\""),
                new("ns=3;s=\"DB\".\"a\"[3]", "index=2;ns=3;s=\"DB\".\"a\"")
            ]);

            Assert.Equal(1, renamed);
            var after = _access.DataTagsGetByDataSourceId(_dataSourceId);
            var moved = after.Single(t => t.Id == first.Id);
            Assert.Equal("index=0;ns=3;s=\"DB\".\"a\"", moved.Address);
            Assert.Equal(1, moved.IsActive);
            Assert.Contains(after, t => t.Address == items[1].Address);
            Assert.Equal(3, after.Count);
        }

        [Fact]
        public void Browse_sync_writes_the_description()
        {
            if (_access == null)
            {
                return;
            }

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "\"DB\".a", Name = "\"DB\".a", TypeId = 1, Description = "{\"de-DE\":\"Motor an\",\"en-US\":\"Motor on\"}" },
                new() { Address = "\"DB\".b", Name = "\"DB\".b", TypeId = 1 }
            };
            Assert.Equal(2, _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None).Created);
            var ids = _access.DataTagsGetByDataSourceId(_dataSourceId).OrderBy(t => t.Address).Select(t => t.Id).ToList();
            var descriptions = _access.DataTagDescriptionsGet(_dataSourceId, ids);
            Assert.Single(descriptions);
            Assert.Equal(items[0].Description, descriptions[ids[0]]);
            Assert.Equal(items[0].Description, _access.DataTagDetailsGetByIds(_dataSourceId, ids)[0].Description);

            // Unchanged: nothing to update.
            Assert.Equal(0, _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None).Updated);

            // A comment changed in the PLC, another one was added, one removed.
            items[0].Description = null;
            items[1].Description = "{\"en-US\":\"Valve\"}";
            Assert.Equal(2, _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None).Updated);
            descriptions = _access.DataTagDescriptionsGet(_dataSourceId, ids);
            Assert.False(descriptions.ContainsKey(ids[0]));
            Assert.Equal("{\"en-US\":\"Valve\"}", descriptions[ids[1]]);
        }

        [Fact]
        public void Tag_list_filters_and_sorts_by_the_comment_in_the_language_of_the_source()
        {
            if (_access == null)
            {
                return;
            }

            var s7 = new DataSourceModel
            {
                Name = "s7-" + Guid.NewGuid().ToString("N"),
                TypeId = (int)DataSourceType.S7CommPlus,
                Host = "192.168.0.10",
                Pass = ""
            };
            _access.DataSourceCreate(s7);
            try
            {
                var items = new List<DataTagBrowseImportItem>
                {
                    new() { Address = "\"DB\".a", Name = "\"DB\".a", TypeId = 1, Description = "{\"de-DE\":\"Zulauf\",\"en-US\":\"Valve\"}" },
                    new() { Address = "\"DB\".b", Name = "\"DB\".b", TypeId = 1, Description = "{\"en-US\":\"Motor on\"}" },
                    new() { Address = "\"DB\".c", Name = "\"DB\".c", TypeId = 1 }
                };
                _access.DataTagsSyncFromBrowse(s7.Id, 1000, items, null, CancellationToken.None);

                var motor = WaitForPage(s7.Id, "motor", []);
                Assert.Equal(["DB.b"], motor.Items.Select(t => t.Name));
                Assert.Equal("Motor on", motor.Items[0].Comment);

                var sorted = WaitForPage(s7.Id, "DB", [new DataTagSortLevel(DataTagSortColumn.Comment, false)]);
                Assert.Equal(["DB.a", "DB.b", "DB.c"], sorted.Items.Select(t => t.Name));

                // German: the list is built again, an untranslated comment is empty.
                var source = _access.DataSourceGetById(s7.Id);
                source.S7Locale = "de-DE";
                _access.DataSourceUpdate(source);
                var german = WaitForPage(s7.Id, "DB", [new DataTagSortLevel(DataTagSortColumn.Comment, true)]);
                Assert.Equal(["", "", "Zulauf"], german.Items.Select(t => t.Comment));
                Assert.Empty(WaitForPage(s7.Id, "motor", []).Items);
            }
            finally
            {
                _access.DataSourceDelete(s7.Id);
            }
        }

        // The list build outlives the request that started it; the end of that request must not cancel it
        // ("57014: canceling statement due to user request" in the tag list and the chart editor, 0.0.247)
        [Fact]
        public void Tag_list_build_is_not_cancelled_by_the_end_of_the_request_that_started_it()
        {
            if (_access == null)
            {
                return;
            }

            _access.DataTagsSyncFromBrowse(_dataSourceId, 1000,
                [new DataTagBrowseImportItem { Address = "\"DB\".x", Name = "\"DB\".x", TypeId = 1 }], null, CancellationToken.None);
            using (var blocker = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction();
                using (var lockTable = new Npgsql.NpgsqlCommand("LOCK TABLE datatag IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                {
                    lockTable.ExecuteNonQuery();
                }

                // As a read of the API: the first page starts the build and the request ends while it waits
                using var request = new CancellationTokenSource();
                using (DatabaseLimits.Use(PostgresWork.Ui, request.Token))
                {
                    Assert.True(_access.DataTagsGetPage(_dataSourceId, null, "", 0, 100).Preparing);
                    // The build opens its connection while the request still runs
                    Thread.Sleep(300);
                }
                request.Cancel();
                Thread.Sleep(1000);
                transaction.Rollback();
            }

            var page = WaitForPage(_dataSourceId, "", []);
            Assert.False(page.Preparing);
            Assert.Equal(["DB.x"], page.Items.Select(t => t.Name));
        }

        private DataTagListPageModel WaitForPage(int dataSourceId, string q, IReadOnlyList<DataTagSortLevel> sorts, int? active = null)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                var page = _access.DataTagsGetPage(dataSourceId, active, q, 0, 100, sorts);
                if (!page.Preparing || DateTime.UtcNow > until)
                {
                    return page;
                }

                Thread.Sleep(50);
            }
        }

        [Fact]
        public void Xml_stage_sync_inserts_several_chunks_and_skips_existing_tags()
        {
            if (_access == null)
            {
                return;
            }

            var jobId = Guid.NewGuid();
            var rows = XmlImportLimits.InsertChunkRows * 2 + 123;
            using (var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString))
            {
                connection.Open();
                using var fill = connection.CreateCommand();
                fill.CommandText =
                    "INSERT INTO xmlimportstage (jobid, address, name, displayname, datatype, typeid) " +
                    "SELECT @jobId, 'ns=4;s=Tag' || lpad(g::text, 7, '0'), 'Tag' || g, NULL, 'Int32', 2 " +
                    "FROM generate_series(1, @rows) g";
                fill.Parameters.AddWithValue("@jobId", jobId);
                fill.Parameters.AddWithValue("@rows", rows);
                fill.ExecuteNonQuery();
            }

            try
            {
                _access.DataTagGetOrCreateByAddress(new DataTagModel
                {
                    DataSourceId = _dataSourceId,
                    Address = "ns=4;s=Tag0000007",
                    Name = "old"
                });

                var stages = new List<DataTagBrowseSyncProgress>();
                var result = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, jobId, stages.Add, CancellationToken.None);
                Assert.Equal(rows - 1, result.Created);
                Assert.Equal(rows, stages.Last(stage => stage.Phase == "new").Scanned);

                var tags = _access.DataTagsGetByDataSourceId(_dataSourceId);
                Assert.Equal(rows, tags.Count);
                Assert.Equal(rows, tags.Select(tag => tag.Address).Distinct().Count());

                using var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString);
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText =
                    "SELECT COUNT(DISTINCT guid), COUNT(*) FILTER (WHERE guid ~ '^[0-9A-F]{32}$') " +
                    "FROM datatag WHERE datasourceid = @id";
                check.Parameters.AddWithValue("@id", _dataSourceId);
                using var reader = check.ExecuteReader();
                reader.Read();
                Assert.Equal(rows, reader.GetInt64(0));
                Assert.Equal(rows, reader.GetInt64(1));
                reader.Close();

                // A second run finds everything and creates nothing.
                var again = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, jobId, null, CancellationToken.None);
                Assert.Equal(0, again.Created);
            }
            finally
            {
                _access.XmlImportStageDelete(jobId);
            }
        }

        [Fact]
        public void Xml_stage_sync_cancelled_between_chunks_leaves_the_tags_untouched()
        {
            if (_access == null)
            {
                return;
            }

            var jobId = Guid.NewGuid();
            using (var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString))
            {
                connection.Open();
                using var fill = connection.CreateCommand();
                fill.CommandText =
                    "INSERT INTO xmlimportstage (jobid, address, name, displayname, datatype, typeid) " +
                    "SELECT @jobId, 'ns=5;s=Tag' || lpad(g::text, 7, '0'), 'Tag' || g, NULL, 'Int32', 2 " +
                    "FROM generate_series(1, @rows) g";
                fill.Parameters.AddWithValue("@jobId", jobId);
                fill.Parameters.AddWithValue("@rows", XmlImportLimits.InsertChunkRows * 2);
                fill.ExecuteNonQuery();
            }

            try
            {
                using var cancel = new CancellationTokenSource();
                Assert.Throws<OperationCanceledException>(() => _access.DataTagsSyncFromXmlStage(
                    _dataSourceId,
                    1000,
                    jobId,
                    progress =>
                    {
                        if (progress.Phase == "new" && progress.Scanned > 0)
                        {
                            cancel.Cancel();
                        }
                    },
                    cancel.Token));
                Assert.Empty(_access.DataTagsGetByDataSourceId(_dataSourceId));
            }
            finally
            {
                _access.XmlImportStageDelete(jobId);
            }
        }

        [Fact]
        public void Xml_stage_sync_restores_updates_and_deletes_existing_tags()
        {
            if (_access == null)
            {
                return;
            }

            var first = FillStage(
                ("ns=6;s=A", "A", null, 2),
                ("ns=6;s=B", "B", "Bee", 1),
                ("ns=6;s=C", "C", null, 2),
                ("ns=6;s=D", "D", null, 2));
            var second = FillStage(
                ("ns=6;s=A", "A", null, 2),
                ("ns=6;s=B", "B", "Bumblebee", 1),
                ("ns=6;s=C", "C", null, 2),
                ("ns=6;s=E", "E", null, 2));
            try
            {
                Assert.Equal(4, _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, first, null, CancellationToken.None).Created);
                var tags = _access.DataTagsGetByDataSourceId(_dataSourceId).ToDictionary(tag => tag.Address);
                _access.DataTagsUpdateDeleted([tags["ns=6;s=C"].Id], 1);
                ExecuteTestSql(
                    "INSERT INTO datatag (guid, datasourceid, typeid, name, address, cycle, isactive, issys) " +
                    "VALUES (md5(random()::text), @id, 2, 'Sys', 'ns=6;s=Sys', 1000, 1, 1)");

                var stages = new List<DataTagBrowseSyncProgress>();
                var result = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, second, stages.Add, CancellationToken.None);
                Assert.Equal(1, result.Created);
                Assert.Equal(1, result.Updated);
                Assert.Equal(1, result.Undeleted);
                Assert.Equal(1, result.Deleted);
                Assert.Equal(5, stages.Last(stage => stage.Phase == "existing").Scanned);

                tags = _access.DataTagsGetByDataSourceId(_dataSourceId).ToDictionary(tag => tag.Address);
                Assert.Equal("Bumblebee", QueryTestScalar("SELECT displayname FROM datatag WHERE datasourceid = @id AND address = 'ns=6;s=B'"));
                Assert.Equal(0, tags["ns=6;s=C"].IsDeleted);
                Assert.Equal(1, tags["ns=6;s=D"].IsDeleted);
                Assert.Equal(0, tags["ns=6;s=Sys"].IsDeleted);

                // The same file again changes nothing.
                var again = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, second, null, CancellationToken.None);
                Assert.Equal(0, again.Created + again.Updated + again.Undeleted + again.Deleted);
            }
            finally
            {
                _access.XmlImportStageDelete(first);
                _access.XmlImportStageDelete(second);
            }
        }

        [Fact]
        public void Xml_stage_sync_deletes_across_chunks_and_a_cancel_keeps_the_tags()
        {
            if (_access == null)
            {
                return;
            }

            var rows = XmlImportLimits.InsertChunkRows * 2 + 77;
            var all = Guid.NewGuid();
            var thinned = Guid.NewGuid();
            ExecuteTestSql(
                "INSERT INTO xmlimportstage (jobid, address, name, displayname, datatype, typeid) " +
                "SELECT @all, 'ns=7;s=Tag' || lpad(g::text, 7, '0'), 'Tag' || g, NULL, 'Int32', 2 " +
                "FROM generate_series(1, @rows) g; " +
                "INSERT INTO xmlimportstage (jobid, address, name, displayname, datatype, typeid) " +
                "SELECT @thinned, address, name, displayname, datatype, typeid FROM xmlimportstage " +
                "WHERE jobid = @all AND substring(address from 12)::integer % 3 <> 0",
                ("@all", all),
                ("@thinned", thinned),
                ("@rows", rows));
            try
            {
                Assert.Equal(rows, _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, all, null, CancellationToken.None).Created);

                using (var cancel = new CancellationTokenSource())
                {
                    Assert.Throws<OperationCanceledException>(() => _access.DataTagsSyncFromXmlStage(
                        _dataSourceId,
                        1000,
                        thinned,
                        progress =>
                        {
                            if (progress.Phase == "existing" && progress.Scanned > 0)
                            {
                                cancel.Cancel();
                            }
                        },
                        cancel.Token));
                }

                Assert.Equal(rows, _access.DataTagsGetByDataSourceId(_dataSourceId, deleted: 0).Count);

                var result = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, thinned, null, CancellationToken.None);
                Assert.Equal(rows / 3, result.Deleted);
                Assert.Equal(0, result.Updated + result.Undeleted + result.Created);
                Assert.Equal(rows - rows / 3, _access.DataTagsGetByDataSourceId(_dataSourceId, deleted: 0).Count);

                // Back to the full file: the deleted tags are restored.
                var restored = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, all, null, CancellationToken.None);
                Assert.Equal(rows / 3, restored.Undeleted);
                Assert.Equal(0, restored.Deleted + restored.Created + restored.Updated);
            }
            finally
            {
                _access.XmlImportStageDelete(all);
                _access.XmlImportStageDelete(thinned);
            }
        }

        [Fact]
        public void Xml_stage_sync_without_importable_rows_deletes_nothing()
        {
            if (_access == null)
            {
                return;
            }

            var first = FillStage(("ns=8;s=A", "A", null, 2), ("ns=8;s=B", "B", null, 2));
            var empty = FillStage(("ns=8;s=X", "X", null, 0));
            try
            {
                _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, first, null, CancellationToken.None);
                var result = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, empty, null, CancellationToken.None);
                Assert.Equal(0, result.Deleted + result.Created);
                Assert.Equal(2, _access.DataTagsGetByDataSourceId(_dataSourceId, deleted: 0).Count);
            }
            finally
            {
                _access.XmlImportStageDelete(first);
                _access.XmlImportStageDelete(empty);
            }
        }

        [Fact]
        public void Deleted_data_source_is_hidden_at_once_and_removed_in_chunks()
        {
            if (_access == null)
            {
                return;
            }

            var source = new DataSourceModel
            {
                Name = "deleting-" + Guid.NewGuid().ToString("N"),
                TypeId = 2,
                IsActive = 0,
                Host = "localhost",
                Pass = ""
            };
            _access.DataSourceCreate(source);
            var rows = DataAccessCtrl.DataSourceDeleteChunkRows + 10;
            ExecuteTestSql(
                "INSERT INTO datatag (guid, datasourceid, typeid, name, address, cycle) " +
                "SELECT md5(random()::text || g), @source, 2, 'T' || g, 'ns=9;s=T' || g, 1000 FROM generate_series(1, @rows) g",
                ("@source", source.Id),
                ("@rows", rows));

            Assert.True(_access.DataSourceMarkDeleting(source.Id));
            Assert.False(_access.DataSourceMarkDeleting(source.Id));
            Assert.False(_access.DataSourceMarkDeleting(1));
            Assert.DoesNotContain(_access.DataSourceGetAll(), model => model.Id == source.Id);
            Assert.Throws<InvalidOperationException>(() => _access.DataSourceGetById(source.Id));
            Assert.Contains(source.Id, _access.DataSourcesDeleting());

            Assert.Equal(DataAccessCtrl.DataSourceDeleteChunkRows, _access.DataSourceDeleteTagChunk(source.Id, DataAccessCtrl.DataSourceDeleteChunkRows));
            Assert.Equal(10, _access.DataSourceDeleteTagChunk(source.Id, DataAccessCtrl.DataSourceDeleteChunkRows));
            Assert.Equal(0, _access.DataSourceDeleteTagChunk(source.Id, DataAccessCtrl.DataSourceDeleteChunkRows));
            _access.DataSourceDeleteRemoved(source.Id);
            Assert.DoesNotContain(source.Id, _access.DataSourcesDeleting());
            Assert.Equal(0L, QueryTestScalar("SELECT count(*) FROM datasource WHERE id = " + source.Id));
        }

        private Guid FillStage(params (string Address, string Name, string DisplayName, int TypeId)[] rows)
        {
            var jobId = Guid.NewGuid();
            using var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString);
            connection.Open();
            using var fill = connection.CreateCommand();
            fill.CommandText =
                "INSERT INTO xmlimportstage (jobid, address, name, displayname, datatype, typeid) " +
                "SELECT @jobId, r.address, r.name, r.displayname, 'Int32', r.typeid " +
                "FROM unnest(@addresses, @names, @displayNames, @typeIds) AS r(address, name, displayname, typeid)";
            fill.Parameters.AddWithValue("@jobId", jobId);
            fill.Parameters.AddWithValue("@addresses", rows.Select(row => row.Address).ToArray());
            fill.Parameters.AddWithValue("@names", rows.Select(row => row.Name).ToArray());
            fill.Parameters.AddWithValue("@displayNames", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
                rows.Select(row => (object)row.DisplayName ?? DBNull.Value).ToArray());
            fill.Parameters.AddWithValue("@typeIds", rows.Select(row => row.TypeId).ToArray());
            fill.ExecuteNonQuery();
            return jobId;
        }

        private object QueryTestScalar(string sql)
        {
            using var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", _dataSourceId);
            return command.ExecuteScalar();
        }

        private void ExecuteTestSql(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = new Npgsql.NpgsqlConnection(TestPostgres.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", _dataSourceId);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }

        [Fact]
        public void Xml_stage_copy_finalize_and_sync()
        {
            if (_access == null)
            {
                return;
            }

            var previous = Environment.GetEnvironmentVariable("DATABASE_URL");
            Environment.SetEnvironmentVariable("DATABASE_URL", TestPostgres.ConnectionString);
            try
            {
                var jobId = Guid.NewGuid();
                var batches = new BlockingCollection<List<XmlImportRow>>();
                batches.Add(
                [
                    new XmlImportRow { Address = "ns=3;s=A", Name = "A", DataType = "Int32", TypeId = 2 },
                    new XmlImportRow { Address = "ns=3;s=A", Name = "A2", DataType = "Int32", TypeId = 0 },
                    new XmlImportRow { Address = "ns=3;s=B", Name = "B", DisplayName = "Bee", DataType = "Boolean", TypeId = 0 },
                    new XmlImportRow { Address = "ns=3;s=C", Name = "C", DataType = "ns=9;i=1", TypeId = 0 }
                ]);
                batches.CompleteAdding();
                var counters = new XmlImportCounters();
                XmlImportSidecar.Write(
                    jobId,
                    batches,
                    new ConcurrentBag<List<XmlImportRow>>(),
                    counters,
                    new XmlImportTypeGraph(),
                    _ => { },
                    CancellationToken.None);
                Assert.Equal(2, counters.ItemsAccepted);

                var result = _access.DataTagsSyncFromXmlStage(_dataSourceId, 1000, jobId, null, CancellationToken.None);
                Assert.Equal(2, result.Created);
                _access.XmlImportStageDelete(jobId);

                var tags = _access.DataTagsGetByDataSourceId(_dataSourceId);
                Assert.Equal(["ns=3;s=A", "ns=3;s=B"], tags.Select(tag => tag.Address).Order().ToArray());
                // Of two rows with one address the first one stays.
                Assert.Equal("A", tags.Single(tag => tag.Address == "ns=3;s=A").Name);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DATABASE_URL", previous);
            }
        }
    }
}
