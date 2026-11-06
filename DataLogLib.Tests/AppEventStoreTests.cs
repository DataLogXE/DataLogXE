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

using DataLogLib.Common.Serilog;
using DataLogLib.Services.EventLog;
using Npgsql;
using Serilog;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class AppEventStoreTests
    {
        private readonly AppEventStore _store;
        private readonly DateTime _now = DateTime.UtcNow;

        public AppEventStoreTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            PostgresTestConfiguration.EnsureBaselineApplied();
            Execute("TRUNCATE appevent");
            _store = new AppEventStore(PostgresTestConfiguration.Create());
        }

        [Fact]
        public void SourceLogPagesNewestFirstAndOnlyOfTheSource()
        {
            if (_store == null)
            {
                return;
            }

            Insert(Enumerable.Range(0, 5).Select(i => Point(3, _now.AddMinutes(-i), "Information", $"s3 {i}"))
                .Append(Point(4, _now.AddSeconds(-30), "Error", "other source")));

            var first = _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, Limit = 2 });
            Assert.Equal(["s3 0", "s3 1"], first.Items.Select(r => r.Message));
            Assert.True(first.Truncated);

            var second = _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, Limit = 10, BeforeUtc = first.NextBeforeUtc });
            Assert.Equal(["s3 2", "s3 3", "s3 4"], second.Items.Select(r => r.Message));
            Assert.False(second.Truncated);
            // A short page goes on one window further back
            Assert.NotNull(second.NextBeforeUtc);
            Assert.True(second.NextBeforeUtc < _now.AddHours(-23));
        }

        [Fact]
        public void SourceLogGoesOnBeforeAQuietDay()
        {
            if (_store == null)
            {
                return;
            }

            Insert([Point(3, _now.AddDays(-3), "Warning", "three days ago")]);

            var result = new SourceLogQuery { SourceId = 3 };
            var pages = 0;
            SourceLogResult page;
            do
            {
                page = _store.QuerySourceLog(result);
                result.BeforeUtc = page.NextBeforeUtc;
                pages++;
            }
            while (page.Items.Count == 0 && page.NextBeforeUtc.HasValue);

            Assert.Equal("three days ago", Assert.Single(page.Items).Message);
            Assert.InRange(pages, 3, 5);
        }

        [Fact]
        public void SourceLogFiltersLevelTextAndNewerEvents()
        {
            if (_store == null)
            {
                return;
            }

            Insert([
                Point(3, _now.AddMinutes(-3), "Information", "Connected to 192.168.0.1"),
                Point(3, _now.AddMinutes(-2), "Warning", "No response for sequence number 7"),
                Point(3, _now.AddMinutes(-1), "Error", "Connect failed", exception: "SocketException: timed out")]);

            var problems = _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, MinLevel = "warning" });
            Assert.Equal(["Error", "Warning"], problems.Items.Select(r => r.Level));

            var text = _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, Text = "timed out" });
            Assert.Equal("Connect failed", Assert.Single(text.Items).Message);

            var live = _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, AfterUtc = _now.AddMinutes(-2) });
            Assert.Equal("Connect failed", Assert.Single(live.Items).Message);
            Assert.Null(live.NextBeforeUtc);

            Assert.Throws<ArgumentException>(() => _store.QuerySourceLog(new SourceLogQuery { SourceId = 3, MinLevel = "loud" }));
        }

        [Fact]
        public void SourceLogCountsWarningsAndErrorsOfTheLastDay()
        {
            if (_store == null)
            {
                return;
            }

            Insert([
                Point(3, _now.AddHours(-1), "Warning", "w"),
                Point(3, _now.AddHours(-2), "Warning", "w"),
                Point(3, _now.AddHours(-3), "Error", "e"),
                Point(3, _now.AddHours(-1), "Information", "i"),
                Point(3, _now.AddHours(-30), "Error", "too old"),
                Point(0, _now.AddHours(-1), "Error", "application")]);

            var counts = _store.QuerySourceLogCounts(_now.AddHours(-24));
            Assert.Equal(
                [new SourceLogCount(3, "Error", 1), new SourceLogCount(3, "Warning", 2)],
                counts.OrderBy(c => c.Level));
        }

        [Fact]
        public void SourceLoggerFilesItsEventsUnderTheDataSource()
        {
            var sink = new SerilogSink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            logger.ForContext("DsId", 7).ForContext("DsName", "plc").Warning("S7CommPlusConnection: dropping response");

            Assert.True(sink.Events.TryDequeue(out var logEvent));
            var record = EventLogMapper.FromLogEvent(logEvent);
            Assert.Equal(7, record.SourceId);
            Assert.Equal("datasource", record.Kind);
            Assert.Contains("\"DsName\":\"plc\"", record.Props);
        }

        [Fact]
        public void SystemLogReadsEverySourceAndFilters()
        {
            if (_store == null)
            {
                return;
            }

            var app = Point(0, _now.AddMinutes(-3), "Warning", "archive slow");
            app.Component = "archive";
            var s3 = Point(3, _now.AddMinutes(-2), "Information", "s3 connected");
            var s4 = Point(4, _now.AddMinutes(-1), "Error", "s4 failed");
            s4.CorrelationId = "op-42";
            Insert([app, s3, s4]);

            var all = _store.QueryAppLog(new AppLogQuery());
            Assert.Equal(["s4 failed", "s3 connected", "archive slow"], all.Items.Select(r => r.Message));
            Assert.Equal([4, 3, 0], all.Items.Select(r => r.SourceId));
            Assert.Equal("op-42", all.Items[0].CorrelationId);

            Assert.Equal(["archive slow"], _store.QueryAppLog(new AppLogQuery { SourceId = 0 }).Items.Select(r => r.Message));
            Assert.Equal(["archive slow"], _store.QueryAppLog(new AppLogQuery { Component = "archive" }).Items.Select(r => r.Message));
            Assert.Equal(["s4 failed"], _store.QueryAppLog(new AppLogQuery { CorrelationId = "op-42" }).Items.Select(r => r.Message));
            Assert.Equal(["s4 failed", "archive slow"],
                _store.QueryAppLog(new AppLogQuery { MinLevel = "Warning" }).Items.Select(r => r.Message));

            // The log of a data source stays the same
            Assert.Equal(["s3 connected"], _store.QuerySourceLog(new SourceLogQuery { SourceId = 3 }).Items.Select(r => r.Message));
        }

        [Fact]
        public async Task ExportReadsOldestFirstOverSeveralDays()
        {
            if (_store == null)
            {
                return;
            }

            Insert([
                Point(0, _now.AddHours(-1), "Error", "newest"),
                Point(3, _now.AddDays(-3), "Warning", "oldest"),
                Point(0, _now.AddDays(-1.5), "Information", "middle")
            ]);

            var all = new List<string>();
            await foreach (var record in _store.ExportAppLog(new AppLogQuery(), _now.AddDays(-4), _now.AddMinutes(1)))
            {
                all.Add(record.Message);
            }

            Assert.Equal(["oldest", "middle", "newest"], all);

            var problems = new List<string>();
            await foreach (var record in _store.ExportAppLog(new AppLogQuery { MinLevel = "Warning" }, _now.AddDays(-4), _now.AddMinutes(1)))
            {
                problems.Add(record.Message);
            }

            Assert.Equal(["oldest", "newest"], problems);
        }

        [Fact]
        public void UnknownLevelIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new AppLogQuery { MinLevel = "Loud" }.Validate());
        }

        private void Insert(IEnumerable<EventLogRecord> records)
        {
            _store.InsertEventPoints(records.Select(r => r.ToPoint()).ToList());
        }

        private static EventLogRecord Point(int sourceId, DateTime timeUtc, string level, string message, string exception = "")
        {
            return new EventLogRecord
            {
                TimeUtc = timeUtc,
                Level = level,
                Kind = sourceId > 0 ? "datasource" : "application",
                SourceId = sourceId,
                Message = message,
                Exception = exception
            };
        }

        private static void Execute(string sql)
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var command = dataSource.CreateCommand(sql);
            command.ExecuteNonQuery();
        }
    }
}
