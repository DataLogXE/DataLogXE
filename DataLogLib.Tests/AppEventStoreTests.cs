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
