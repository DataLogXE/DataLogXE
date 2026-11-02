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

using System.Text.RegularExpressions;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services;
using DataLogLib.Services.ArchiveAccess;
using DataLogLib.Services.DataAccess;
using DataLogLib.Services.EventLog;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// Every statement on a hypertable must plan only the chunks of its range: one that plans all chunks locks each of
    /// them and its index, and on a Raspberry Pi with weeks of 30-minute chunks that filled the lock table (2026-10-25)
    /// or held the device for minutes (2026-10-31). The archive here holds 12 days of values in every table; each read
    /// covers 2 hours, 6 days back. Postgres sends the plan of each statement (auto_explain at notice level) and the test
    /// counts its chunks.
    /// </summary>
    [Collection(PostgresTestConfiguration.Collection)]
    public partial class ArchiveChunkTests
    {
        // A day of raw chunks (48) and the edges; the 12 days hold 577 chunks of archiveraw
        internal const int MaxChunks = 50;

        private sealed class UidLookup : ITagUidLookup
        {
            public IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds)
                => dataTagIds.Where(id => id > 0).Distinct().ToDictionary(id => id, Uid);
        }

        private readonly TimescaleArchive _archive;
        private readonly List<(string Sql, int Chunks)> _plans = [];
        private readonly DateTime _base;
        private readonly string _connectionString;

        public ArchiveChunkTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            PostgresTestConfiguration.EnsureBaselineApplied();
            foreach (var table in TimescaleArchive.RetentionTables)
            {
                Execute($"TRUNCATE {table}");
            }
            Execute("TRUNCATE archivetag RESTART IDENTITY");
            Execute("DELETE FROM parameter WHERE key = 'COMPRESSION_VERIFIED_UNTIL'");
            Execute($"UPDATE parameter SET value = (floor(extract(epoch FROM now()) / 60) * 60)::bigint::text "
                + $"WHERE key IN ('{TimescaleArchive.CountFromKey}', '{TimescaleArchive.CountUntilKey}')");

            _connectionString = PlannedConnectionString();
            _archive = new TimescaleArchive();
            _archive.Init(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["DATABASE_URL"] = _connectionString })
                .Build());
            _archive.SetUidLookup(new UidLookup());
            var start = DateTime.Now.AddHours(-3);
            _base = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Local);
        }

        /// <summary>The test database, with the plan of every statement sent to the client as a notice.</summary>
        internal static string PlannedConnectionString()
        {
            var builder = new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString)
            {
                Options = "-c session_preload_libraries=auto_explain -c auto_explain.log_min_duration=0 "
                    + "-c auto_explain.log_level=notice -c auto_explain.log_nested_statements=on"
            };
            return builder.ConnectionString;
        }

        /// <summary>Records the chunks of the plan of every statement on a connection of the pools of the string.</summary>
        internal static void RecordPlans(string connectionString, List<(string Sql, int Chunks)> plans)
        {
            PostgresDataSources.Get(connectionString).Opened = connection =>
            {
                void OnNotice(object sender, NpgsqlNoticeEventArgs e)
                {
                    var text = e.Notice.MessageText;
                    if (!text.Contains("plan:", StringComparison.Ordinal))
                    {
                        return;
                    }
                    var chunks = ChunkPattern().Matches(text).Select(m => m.Value).Distinct().Count();
                    var query = text.Split('\n').FirstOrDefault(l => l.StartsWith("Query Text:", StringComparison.Ordinal)) ?? text;
                    lock (plans)
                    {
                        plans.Add((query, chunks));
                    }
                }
                void OnState(object sender, System.Data.StateChangeEventArgs e)
                {
                    if (e.CurrentState != System.Data.ConnectionState.Open)
                    {
                        connection.Notice -= OnNotice;
                        connection.StateChange -= OnState;
                    }
                }
                connection.Notice += OnNotice;
                connection.StateChange += OnState;
            };
        }

        /// <summary>The statements that planned more than max chunks, for the message of a failed test.</summary>
        internal static string Wide(List<(string Sql, int Chunks)> plans, int max)
        {
            lock (plans)
            {
                return string.Join("\n\n", plans.Where(p => p.Chunks > max)
                    .GroupBy(p => p.Sql)
                    .Select(g => $"{g.Max(p => p.Chunks)} chunks: {g.Key}"));
            }
        }

        [GeneratedRegex(@"_hyper_\d+_\d+_chunk")]
        private static partial Regex ChunkPattern();

        [Fact]
        public void Every_read_of_the_archive_plans_only_the_chunks_of_its_range()
        {
            if (_archive == null)
            {
                return;
            }

            const double Hour = 3600;
            const double Day = 24 * Hour;
            List<ArchiveValue> raw = [];
            for (var seconds = -12 * Day; seconds <= 0; seconds += Hour / 2)
            {
                raw.Add(Value(1, seconds, seconds));
                raw.Add(Value(2, seconds % 2 == 0 ? 1 : 0, seconds));
                _archive.InsertValueCount(_base.AddSeconds(seconds), 100);
            }
            raw.Add(Value(1, double.NaN, -6 * Day + 60, ValueState.Invalid));
            _archive.InsertValuesAndWait(raw);
            foreach (var level in CompressionLevels.All)
            {
                _archive.InsertCompressedValuesAndWait(Uid(1), raw.Where(v => v.Id == 1)
                    .Select(v => new ArchiveValue { Id = 1, Value = v.Value, Time = v.Time, State = v.State, Compression = level.Type })
                    .ToList());
            }
            // Tag 3 has no raw values at all: the searches for its rows run through every fallback
            _archive.InsertCompressedValuesAndWait(Uid(3), [new ArchiveValue
            {
                Id = 3, Value = 1, Time = _base.AddDays(-12), State = ValueState.Valid, Compression = CompressionType.C3600
            }]);
            // The counters hold the raw values from 13 days back on, as after the backfill
            Execute($"INSERT INTO {TimescaleArchive.HourCountTable} (time, tag_id, count) "
                + $"SELECT time_bucket('1 hour', time), tag_id, count(*) FROM {TimescaleArchive.RawTable} GROUP BY 1, 2");
            Execute($"INSERT INTO {TimescaleArchive.MinuteCountTable} (time, tag_id, count) "
                + $"SELECT time_bucket('1 minute', time), tag_id, count(*) FROM {TimescaleArchive.RawTable} WHERE time > now() - interval '1 day' GROUP BY 1, 2");
            Execute($"UPDATE parameter SET value = (floor(extract(epoch FROM now() - interval '13 days') / 3600) * 3600)::bigint::text "
                + $"WHERE key = '{TimescaleArchive.CountFromKey}'");

            RecordPlans(_connectionString, _plans);
            var (from, to) = (_base.AddDays(-6).AddHours(-1), _base.AddDays(-6).AddHours(1));
            var lookback = TimeSpan.FromDays(30);
            var hold = TimeSpan.FromSeconds(30);

            // The reads of the UI
            Assert.NotNull(_archive.GetArchiveStartTime());
            Assert.NotNull(_archive.GetArchiveEndTime(1));
            _archive.GetValueCountSum();
            Assert.True(_archive.GetRawValueCount(1, from, to) >= 4);
            _archive.EstimateRawValueCount(1, from, to);
            Assert.True(_archive.GetRawStatistics(1, from, to, lookback, hold).Count >= 4);
            _archive.GetBoolDurations(2, from, to, lookback, hold);
            _archive.GetInvalidSeconds(1, from, to, lookback, hold);
            foreach (var level in Enum.GetValues<CompressionType>())
            {
                _archive.GetPlotPoints(1, level, from, to);
            }
            Assert.NotEmpty(_archive.GetRawPlotPoints(1, from, to, 1000));
            Assert.NotEmpty(_archive.StreamRawPlotPoints(1, from, to).ToBlockingEnumerable().ToList());
            Assert.NotEmpty(_archive.GetRawM4PlotPoints(1, from, to, from.ToUnixSeconds(), 600));
            _archive.GetPlotPointAt(1, from);
            Assert.NotNull(_archive.GetRawValueAt(1, from, lookback, hold));
            Assert.Equal(DateTime.MinValue, _archive.GetArchiveEndTime(3));
            Assert.Null(_archive.GetRawValueAt(3, from, lookback, hold));
            Assert.Equal(0, _archive.GetRawStatistics(3, from, to, lookback, hold).Count);
            _archive.GetBoolDurations(3, from, to, lookback, hold);
            _archive.GetInvalidSeconds(3, from, to, lookback, hold);

            // The compression repair and the counters
            Assert.NotEmpty(_archive.GetRawUids(from, to));
            List<ArchiveValue> read = [];
            _archive.ReadRawValues(Uid(1), from, to, read.Add);
            Assert.NotEmpty(read);
            _archive.GetLastTimes(from, to);
            _archive.GetHourCoverage(Uid(1), from, to);
            _archive.GetOldestRawChunkStart();
            _archive.RollUpCounts(DateTime.Now.AddMinutes(-1));
            _archive.BackfillCountsStep(DateTime.Now.AddDays(-14), TimeSpan.FromHours(1));
            _archive.ApplyRetention();

            // A write of the current values
            _archive.InsertValuesAndWait([Value(1, 1, 60), Value(2, 1, 60)]);

            Assert.True(_plans.Count > 20, $"only {_plans.Count} plans recorded");
            var wide = Wide(_plans, MaxChunks);
            Assert.True(wide.Length == 0, "Statements that plan more chunks than their range needs:\n" + wide);
        }

        [Fact]
        public void Every_read_of_the_alarms_and_events_plans_only_the_chunks_of_its_range()
        {
            if (_archive == null)
            {
                return;
            }

            Execute("TRUNCATE processevent, processeventopen");
            var store = new ProcessEventStore(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["DATABASE_URL"] = _connectionString })
                .Build());
            store.InvalidateListCache();
            // An alarm per hour over 40 days: a chunk of processevent a day
            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-40);
            List<EventLogPoint> points = [];
            for (var time = start; time < now; time = time.AddHours(1))
            {
                points.Add(Event(time, "c" + (time.Hour % 3), time.Hour % 2 == 0 ? AlarmStateValues.Incoming : AlarmStateValues.IncomingOutgoing));
            }
            store.InsertEventPoints(points);
            store.FlushWrites();

            RecordPlans(_connectionString, _plans);
            // A write of the current alarms, with the open occurrences kept in step
            store.InsertEventPoints([Event(now.AddSeconds(-5), "c1", AlarmStateValues.Incoming), Event(now.AddSeconds(-4), "c2", AlarmStateValues.IncomingOutgoing)]);
            store.FlushWrites();
            var (from, to) = (now.AddDays(-20).AddHours(-1), now.AddDays(-20).AddHours(1));
            var query = new EventLogQuery { FromUtc = from, ToUtc = to, Limit = 100 };
            var page = store.QueryEvents(query);
            Assert.NotEmpty(page.Items);
            store.QueryEventCount(query);
            store.QueryEventSummary(query);
            store.QueryAlarmStates(query);
            foreach (var column in Enum.GetValues<EventSortColumn>())
            {
                store.QuerySortedBatch(query, column, 50, 0);
            }
            store.LoadRows(query, store.LoadSortedIds(query, EventSortColumn.Time, null).Ids);
            store.QueryAlarmHistory(new AlarmHistoryQuery { CorrelationId = "c1", SourceId = 2, FromUtc = from, ToUtc = to });
            store.QueryOpenAlarms(2);
            store.QueryOpenAlarmLookback(2, ["c1", "c2"]);
            store.QueryAlarmCounts(new AlarmCountQuery { Reference = page.Items[0], AlarmState = AlarmStateValues.Incoming, Edges = [from, to] });
            store.CountsAvailable();
            store.BackfillCountsStep(24, store.ReadOldestEventTime());
            store.ApplyRetention();

            Assert.True(_plans.Count > 10, $"only {_plans.Count} plans recorded");
            // The 2 hours lie in one or two daily chunks of the 41; the seed of the alarm tracker reads its fixed range of
            // 15 days (16 chunks)
            var wide = Wide(_plans, EventLogConstants.AlarmSeedRangeDays + 2);
            Assert.True(wide.Length == 0, "Statements that plan more chunks than their range needs:\n" + wide);
        }

        private static EventLogPoint Event(DateTime timeUtc, string correlationId, string state) => new()
        {
            TimeUtc = timeUtc,
            Measurement = EventLogConstants.ProcessMeasurementName,
            Level = "Warning",
            Kind = EventLogConstants.AlarmKind,
            Component = EventLogConstants.AlarmComponent,
            SourceId = 2,
            SourceType = "OpcUa",
            CorrelationId = correlationId,
            AlarmState = state,
            Severity = 100,
            Message = "Alarm " + correlationId
        };

        private static string Uid(int tagId) => tagId.ToString("D32");

        private ArchiveValue Value(int tagId, double value, double seconds, ValueState state = ValueState.Valid)
            => new() { Id = tagId, Value = value, Time = _base.AddSeconds(seconds), State = state };

        private static void Execute(string sql)
        {
            using var connection = new NpgsqlConnection(TestPostgres.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }
    }
}
