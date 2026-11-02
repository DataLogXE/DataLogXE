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

using DataLogLib.Services.EventLog;
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class ProcessEventStoreTests
    {
        private readonly ProcessEventStore _store;
        private readonly DateTime _base = DateTime.UtcNow.AddHours(-1);

        public ProcessEventStoreTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            PostgresTestConfiguration.EnsureBaselineApplied();
            using (var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString))
            using (var command = dataSource.CreateCommand("TRUNCATE processevent, processeventopen"))
            {
                command.ExecuteNonQuery();
            }

            _store = new ProcessEventStore(PostgresTestConfiguration.Create());
            _store.InvalidateListCache();
        }

        [Fact]
        public void Inserted_rows_round_trip_with_every_field()
        {
            if (_store == null)
            {
                return;
            }

            var point = Point(10, "c1", AlarmStateValues.Incoming, severity: 500, message: "Pump 2 failed");
            point.Active = true;
            point.Acked = false;
            point.Retain = true;
            point.ActiveSinceUtc = At(9);
            point.EventType = "ns=0;i=2915";
            point.EventTypeName = "AlarmCondition";
            point.AlarmClassSymbol = "!";
            point.AlarmClassName = "Fault";
            point.TextColor = 0xFF0000;
            point.BackColor = 0xFFFFFFFF;
            point.Flashing = true;
            point.DurationMs = 1234;
            point.Props = "{\"a\":1}";
            _store.InsertEventPoints([point]);

            var row = Assert.Single(_store.QueryEvents(Query()).Items);
            Assert.Equal(At(10), row.TimeUtc);
            Assert.Equal(EventLogConstants.ProcessMeasurementName, row.Measurement);
            Assert.Equal("c1", row.CorrelationId);
            Assert.Equal(AlarmStateValues.Incoming, row.AlarmState);
            Assert.Equal(500, row.Severity);
            Assert.True(row.Active);
            Assert.False(row.Acked);
            Assert.True(row.Retain);
            Assert.Equal(At(9), row.ActiveSinceUtc);
            Assert.Equal("ns=0;i=2915", row.EventType);
            Assert.Equal("AlarmCondition", row.EventTypeName);
            Assert.Equal("!", row.AlarmClassSymbol);
            Assert.Equal("Fault", row.AlarmClassName);
            Assert.Equal(0xFF0000u, row.TextColor);
            Assert.Equal(0xFFFFFFFFu, row.BackColor);
            Assert.True(row.Flashing);
            Assert.Equal(1234, row.DurationMs);
            Assert.Equal("{\"a\":1}", row.Props);
            Assert.Equal("Pump 2 failed", row.Message);
        }

        [Fact]
        public void Equal_times_get_distinct_microseconds()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(10, "a", AlarmStateValues.Incoming),
                Point(10, "b", AlarmStateValues.Incoming),
                Point(10, "c", AlarmStateValues.Incoming)
            ]);

            var times = _store.QueryEvents(Query()).Items.Select(item => item.TimeUtc).ToList();
            Assert.Equal(3, times.Distinct().Count());
            Assert.All(times, time => Assert.Equal(0, time.Ticks % TimeSpan.TicksPerMicrosecond));
        }

        [Fact]
        public void Default_pages_are_newest_first_and_follow_the_cursor()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints([.. Enumerable.Range(1, 5).Select(i => Point(i, "c" + i, AlarmStateValues.Incoming))]);

            var query = Query(limit: 2);
            var first = _store.QueryEvents(query);
            Assert.Equal(["c5", "c4"], first.Items.Select(item => item.CorrelationId));
            Assert.True(first.HasMore);

            query.CursorUtc = DateTime.Parse(
                first.NextCursor,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            var second = _store.QueryEvents(query);
            Assert.Equal(["c3", "c2"], second.Items.Select(item => item.CorrelationId));
        }

        [Fact]
        public void Offset_pages_across_hours_and_days_match_the_full_order()
        {
            if (_store == null)
            {
                return;
            }

            // Uneven hours over three days, with empty hours between them and two rows in the same second.
            var points = new List<EventLogPoint>();
            var hours = new[] { -50, -49, -30, -26, -25, -3, -1, 0 };
            for (var hour = 0; hour < hours.Length; hour += 1)
            {
                for (var row = 0; row <= hour * 3 % 7; row += 1)
                {
                    var state = row % 3 == 0 ? AlarmStateValues.Removed : AlarmStateValues.Incoming;
                    points.Add(Point(hours[hour] * 3600 + row * 60, $"h{hour}r{row}", state));
                }
            }

            points.Add(Point(-25 * 3600 + 5, "same-a", AlarmStateValues.Incoming));
            points.Add(Point(-25 * 3600 + 5, "same-b", AlarmStateValues.Incoming));
            // Oldest first: the store gives every event a later time than the one before.
            _store.InsertEventPoints([.. points.OrderBy(point => point.TimeUtc)]);

            foreach (var state in new[] { null, AlarmStateValues.Incoming })
            {
                var query = Query(limit: 1000, state: state);
                query.FromUtc = At(-60 * 3600);
                var expected = _store.QueryEvents(query).Items.Select(item => item.CorrelationId).ToList();
                Assert.Equal(_store.QueryEventCount(query), expected.Count);
                Assert.Equal(expected.Count, expected.Distinct().Count());

                foreach (var limit in new[] { 1, 3, 4 })
                {
                    var paged = new List<string>();
                    for (var offset = 0; ; offset += limit)
                    {
                        var page = Query(limit: limit, state: state);
                        page.FromUtc = query.FromUtc;
                        page.Offset = offset;
                        var result = _store.QueryEvents(page);
                        paged.AddRange(result.Items.Select(item => item.CorrelationId));
                        Assert.Equal(offset + limit < expected.Count, result.HasMore);
                        if (!result.HasMore)
                        {
                            break;
                        }
                    }

                    Assert.Equal(expected, paged);
                }

                var beyond = Query(limit: 5, state: state);
                beyond.FromUtc = query.FromUtc;
                beyond.Offset = expected.Count;
                var empty = _store.QueryEvents(beyond);
                Assert.Empty(empty.Items);
                Assert.False(empty.HasMore);
            }
        }

        [Fact]
        public void Page_and_count_of_one_text_filter_running_together_agree_with_the_rows()
        {
            if (_store == null)
            {
                return;
            }

            var points = new List<EventLogPoint>();
            for (var hour = 0; hour < 30; hour += 1)
            {
                points.Add(Point(-hour * 3600 + 10, "m" + hour, AlarmStateValues.Incoming, message: hour % 4 == 0 ? "Motor hot" : "Valve"));
                points.Add(Point(-hour * 3600 + 20, "v" + hour, AlarmStateValues.Removed, message: "Valve"));
            }

            // Oldest first: the store gives every event a later time than the one before.
            _store.InsertEventPoints([.. points.OrderBy(point => point.TimeUtc)]);
            var query = Query(limit: 3, text: "motor");
            query.FromUtc = At(-31 * 3600);

            for (var run = 0; run < 5; run += 1)
            {
                var store = new ProcessEventStore(PostgresTestConfiguration.Create());
                var count = Task.Run(() => store.QueryEventCount(query));
                var page = Task.Run(() => store.QueryEvents(query));
                Task.WaitAll(count, page);
                Assert.Equal(8, count.Result);
                Assert.Equal(["m0", "m4", "m8"], page.Result.Items.Select(item => item.CorrelationId));
                Assert.True(page.Result.HasMore);
            }
        }

        [Fact]
        public void Cached_hour_counts_follow_inserts_into_the_range_only()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints([.. Enumerable.Range(1, 5).Select(i => Point(i * 10, "c" + i, AlarmStateValues.Incoming))]);
            var query = Query(limit: 2);
            Assert.Equal(5, _store.QueryEventCount(query));
            Assert.Equal(["c2", "c1"], _store.QueryEvents(WithOffset(query, 3)).Items.Select(item => item.CorrelationId));

            // Into the range, into the hour counted before: the count and the pages see it.
            _store.InsertEventPoints([Point(55, "inside", AlarmStateValues.Incoming)]);
            Assert.Equal(6, _store.QueryEventCount(query));
            Assert.Equal(["inside", "c5"], _store.QueryEvents(WithOffset(query, 0)).Items.Select(item => item.CorrelationId));
            Assert.Equal(["c2", "c1"], _store.QueryEvents(WithOffset(query, 4)).Items.Select(item => item.CorrelationId));

            // After the end of the range: nothing changes.
            _store.InsertEventPoints([Point(120, "later", AlarmStateValues.Incoming)]);
            Assert.Equal(6, _store.QueryEventCount(query));
            Assert.Equal(["c2", "c1"], _store.QueryEvents(WithOffset(query, 4)).Items.Select(item => item.CorrelationId));

            static EventLogQuery WithOffset(EventLogQuery query, int offset)
            {
                var copy = query.WithRange(query.FromUtc, query.ToUtc);
                copy.Offset = offset;
                return copy;
            }
        }

        [Fact]
        public void Count_and_summary_apply_state_text_and_source_filters()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "a", AlarmStateValues.Incoming, message: "Motor hot"),
                Point(2, "a", AlarmStateValues.Removed, message: "Motor hot"),
                Point(3, "b", AlarmStateValues.Incoming, message: "Valve stuck"),
                Point(4, "c", AlarmStateValues.Incoming, sourceId: 3, message: "Other source"),
                SystemPoint(5, "s", affectedSourceId: 3)
            ]);

            Assert.Equal(5, _store.QueryEventCount(Query()));
            Assert.Equal(4, _store.QueryEventCount(Query(state: AlarmStateValues.Incoming)));
            Assert.Equal(2, _store.QueryEventCount(Query(text: "Motor")));
            Assert.Equal(2, _store.QueryEventCount(Query(text: "motor")));
            Assert.Equal(2, _store.QueryEventCount(Query(text: "M*t*h")));
            Assert.Equal(1, _store.QueryEventCount(Query(text: "?alve")));
            Assert.Equal(1, _store.QueryEventCount(Query(text: "removed")));
            Assert.Equal(2, _store.QueryEventCount(Query(sourceIds: [3])));

            var summary = _store.QueryEventSummary(Query());
            Assert.Equal(5, summary.Total);
            // a is removed; b, c and the system alarm are still incoming.
            Assert.Equal(3, summary.ActiveAlarms);

            var states = _store.QueryAlarmStates(Query());
            Assert.Equal(
                new[] { AlarmStateValues.Incoming, AlarmStateValues.Removed }.Order(),
                states.Order());
        }

        [Fact]
        public void Text_search_matches_class_and_limit_state()
        {
            if (_store == null)
            {
                return;
            }

            var classed = Point(1, "a", AlarmStateValues.Incoming, message: "Pressure");
            classed.AlarmClassSymbol = "AL";
            classed.AlarmClassName = "Plant::Alarm high";
            var limit = Point(2, "b", AlarmStateValues.Incoming, message: "Pressure");
            limit.Props = "{\"LimitState\":\"HighHigh\"}";
            var unconfirmed = Point(3, "c", AlarmStateValues.InactiveUnacked + AlarmStateValues.UnconfirmedSuffix, message: "Pressure");
            _store.InsertEventPoints([classed, limit, unconfirmed]);

            Assert.Equal(1, _store.QueryEventCount(Query(text: "plant::*high")));
            Assert.Equal(1, _store.QueryEventCount(Query(text: "al")));
            Assert.Equal(1, _store.QueryEventCount(Query(text: "highhigh")));
            Assert.Equal(1, _store.QueryEventCount(Query(text: "unacknowledged, unconfirmed")));
            Assert.Equal(3, _store.QueryEventCount(Query(text: "pressure")));
            Assert.Equal(0, _store.QueryEventCount(Query(text: "50%")));
        }

        [Fact]
        public void Class_filter_matches_the_label_of_the_table()
        {
            if (_store == null)
            {
                return;
            }

            var symbol = Point(1, "a", AlarmStateValues.Incoming);
            symbol.AlarmClassSymbol = " AL ";
            symbol.AlarmClassName = "Plant::Alarm high";
            var named = Point(2, "b", AlarmStateValues.Incoming);
            named.AlarmClassName = "Plant::Warning";
            var plain = Point(3, "c", AlarmStateValues.Removed);
            plain.AlarmClassName = "Fault";
            var none = Point(4, "d", AlarmStateValues.Incoming);
            _store.InsertEventPoints([symbol, named, plain, none]);

            Assert.Equal(4, _store.QueryEventCount(Query()));
            Assert.Equal(1, _store.QueryEventCount(Query(classes: ["AL"])));
            Assert.Equal(1, _store.QueryEventCount(Query(classes: ["Warning"])));
            Assert.Equal(3, _store.QueryEventCount(Query(classes: ["AL", "Warning", "Fault"])));
            Assert.Equal(0, _store.QueryEventCount(Query(classes: ["Alarm high"])));
            Assert.Equal(0, _store.QueryEventCount(Query(classes: ["AL", "Warning"], state: AlarmStateValues.Removed)));
            Assert.Equal(1, _store.QueryEventCount(Query(classes: ["Fault"], state: AlarmStateValues.Removed)));
            Assert.Equal(["b", "a"], _store.QueryEvents(Query(classes: ["AL", "Warning"])).Items.Select(item => item.CorrelationId));
            Assert.Equal([AlarmStateValues.Removed], _store.QueryAlarmStates(Query(classes: ["Fault"])));
        }

        [Fact]
        public void Sorted_batches_follow_the_column_order_without_overlap()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "a", AlarmStateValues.Incoming, severity: 300),
                Point(2, "b", AlarmStateValues.Incoming, severity: 100),
                Point(3, "c", AlarmStateValues.Incoming, severity: null),
                Point(4, "d", AlarmStateValues.Incoming, severity: 200)
            ]);

            var query = Query(limit: 4);
            query.SortColumn = EventSortColumn.Severity;
            query.SortAscending = true;
            var first = _store.QuerySortedBatch(query, EventSortColumn.Severity, 2, 0);
            var second = _store.QuerySortedBatch(query, EventSortColumn.Severity, 2, 2);

            Assert.Equal(4, first.Total);
            Assert.True(first.HasMore);
            Assert.False(second.HasMore);
            // Empty cells come last in both directions.
            Assert.Equal(
                ["b", "d", "a", "c"],
                first.Items.Concat(second.Items).Select(item => item.CorrelationId));

            query.SortAscending = false;
            var descending = _store.QuerySortedBatch(query, EventSortColumn.Severity, 4, 0);
            Assert.Equal(["a", "d", "b", "c"], descending.Items.Select(item => item.CorrelationId));
        }

        [Fact]
        public void Count_with_progress_counts_day_by_day_and_matches_the_plain_count()
        {
            if (_store == null)
            {
                return;
            }

            // Spread over three calendar days so the count runs in several day steps.
            var day = TimeSpan.FromDays(1).TotalSeconds;
            _store.InsertEventPoints(
            [
                Point(-(int)(2 * day), "a", AlarmStateValues.Incoming),
                Point(-(int)day, "b", AlarmStateValues.Incoming),
                Point(-(int)day + 1, "c", AlarmStateValues.Incoming),
                Point(1, "d", AlarmStateValues.Incoming)
            ]);

            var query = Query();
            query.FromUtc = At(-(int)(3 * day));
            var progress = new RecordingProgress();

            Assert.Equal(4, _store.QueryEventCount(query));
            Assert.Equal(4, _store.QueryEventCount(query, progress));
            Assert.True(progress.Reports.Count >= 4);
            Assert.Equal(100, progress.Reports[^1].Percent!.Value, 3);
            Assert.Equal(4, progress.Reports[^1].Count);
            var counts = progress.Reports.Select(report => report.Count ?? 0).ToList();
            Assert.Equal(counts.Order(), counts);
        }

        [Fact]
        public void Sorted_ids_come_from_the_cache_until_an_insert_into_the_range()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "a", AlarmStateValues.Incoming, severity: 300),
                Point(2, "b", AlarmStateValues.Incoming, severity: 100)
            ]);

            var query = Query();
            var first = _store.LoadSortedIds(query, EventSortColumn.Severity, null);
            Assert.Same(first, _store.LoadSortedIds(query, EventSortColumn.Severity, null));
            Assert.Equal(["b", "a"], _store.LoadRows(query, first.Ids).Select(item => item.CorrelationId));

            _store.InsertEventPoints([Point(3, "c", AlarmStateValues.Incoming, severity: 200)]);
            var progress = new RecordingProgress();
            var second = _store.LoadSortedIds(query, EventSortColumn.Severity, progress);
            Assert.Equal(3, second.Ids.Length);
            Assert.Contains(progress.Reports, report => report.Phase == "Sorting alarms" && report.Count == 3);

            // Alarms after the end of the range keep the list.
            _store.InsertEventPoints([Point(90, "later", AlarmStateValues.Incoming, severity: 200)]);
            Assert.Same(second, _store.LoadSortedIds(query, EventSortColumn.Severity, null));
        }

        [Fact]
        public void A_range_longer_than_the_sort_limit_keeps_the_first_ones_of_the_order()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints([.. Enumerable.Range(1, 6).Select(i => Point(i, "c" + i, AlarmStateValues.Incoming, severity: i * 100))]);
            _store.SortLimit = 4;
            var query = Query(limit: 2);
            query.SortColumn = EventSortColumn.Severity;

            query.SortAscending = true;
            var sorted = _store.LoadSortedIds(query, EventSortColumn.Severity, null);
            Assert.Equal(6, sorted.Total);
            Assert.Equal(["c1", "c2", "c3", "c4"], _store.LoadRows(query, sorted.Ids).Select(item => item.CorrelationId));

            // The reverse order reaches the other end of the range.
            query.SortAscending = false;
            var page = _store.QuerySortedBatch(query, EventSortColumn.Severity, 2, 2);
            Assert.Equal(["c4", "c3"], page.Items.Select(item => item.CorrelationId));
            Assert.Equal(4, page.Total);
            Assert.Equal(6, page.SortedOf);
            Assert.False(page.HasMore);

            var beyond = _store.QuerySortedBatch(query, EventSortColumn.Severity, 2, 4);
            Assert.Empty(beyond.Items);
            Assert.Equal(6, beyond.SortedOf);
        }

        private sealed class RecordingProgress : DataLogLib.Services.IQueryProgress
        {
            public List<(string Phase, double? Percent, long? Count)> Reports { get; } = [];

            public void Report(string phase, double? percent = null, long? count = null)
            {
                Reports.Add((phase, percent, count));
            }
        }

        [Fact]
        public void Sorted_by_message_and_source_use_the_text_keys()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "a", AlarmStateValues.Incoming, sourceId: 2, message: "item 10"),
                Point(2, "b", AlarmStateValues.Incoming, sourceId: 3, message: "Item 9"),
                Point(3, "c", AlarmStateValues.Incoming, sourceId: 4, message: "")
            ]);

            var query = Query(limit: 10);
            query.SortAscending = true;
            var byMessage = _store.QuerySortedBatch(query, EventSortColumn.Message, 10, 0);
            Assert.Equal(["b", "a", "c"], byMessage.Items.Select(item => item.CorrelationId));

            query.SourceNames = new Dictionary<int, string> { [2] = "Zeta", [3] = "Alpha" };
            var bySource = _store.QuerySortedBatch(query, EventSortColumn.Source, 10, 0);
            Assert.Equal(["b", "a", "c"], bySource.Items.Select(item => item.CorrelationId));
        }

        [Fact]
        public void Alarm_history_returns_one_occurrence_oldest_first()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "a", AlarmStateValues.Incoming),
                Point(2, "b", AlarmStateValues.Incoming),
                Point(3, "a", AlarmStateValues.Removed)
            ]);

            var history = _store.QueryAlarmHistory(new AlarmHistoryQuery
            {
                CorrelationId = "a",
                SourceId = 2,
                FromUtc = At(0),
                ToUtc = At(60)
            });

            Assert.Equal(
                [AlarmStateValues.Incoming, AlarmStateValues.Removed],
                history.Items.Select(item => item.AlarmState));
        }

        [Fact]
        public void Open_alarm_seed_returns_the_latest_open_point_per_occurrence()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "open", AlarmStateValues.Incoming),
                Point(2, "open", AlarmStateValues.IncomingOutgoing),
                Point(3, "closed", AlarmStateValues.Incoming),
                Point(4, "closed", AlarmStateValues.Removed),
                Point(5, "other", AlarmStateValues.Incoming, sourceId: 9)
            ]);

            var open = _store.QueryOpenAlarms(2);
            var row = Assert.Single(open.Items);
            Assert.Equal("open", row.CorrelationId);
            Assert.Equal(AlarmStateValues.IncomingOutgoing, row.AlarmState);

            var lookback = _store.QueryOpenAlarmLookback(2, ["open"]);
            Assert.Equal(
                [AlarmStateValues.Incoming, AlarmStateValues.IncomingOutgoing],
                lookback.Items.Select(item => item.AlarmState));
        }

        [Fact]
        public void Open_alarm_seed_follows_the_newest_point_of_each_occurrence()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints(
            [
                Point(1, "closed", AlarmStateValues.Incoming),
                Point(2, "reopened", AlarmStateValues.Incoming),
                Point(3, "closed", AlarmStateValues.Removed),
                Point(4, "reopened", AlarmStateValues.Removed)
            ]);
            _store.InsertEventPoints([Point(5, "reopened", AlarmStateValues.IncomingOutgoing)]);
            _store.InsertEventPoints([Point(6, "late", AlarmStateValues.Incoming)]);
            Assert.Equal(["reopened", "late"], OpenIds());

            // A store of a restarted service: its points may be older than the newest one of the occurrence and then
            // change nothing, like the newest point by time decided before.
            var restarted = new ProcessEventStore(PostgresTestConfiguration.Create());
            restarted.InsertEventPoints([Point(2, "closed", AlarmStateValues.Incoming), Point(5, "late", AlarmStateValues.Removed)]);
            Assert.Equal(["reopened", "late"], OpenIds());

            restarted.InsertEventPoints([Point(7, "late", AlarmStateValues.Removed)]);
            Assert.Equal(["reopened"], OpenIds());
            Assert.Equal(AlarmStateValues.IncomingOutgoing, Assert.Single(_store.QueryOpenAlarms(2).Items).AlarmState);

            List<string> OpenIds()
            {
                return _store.QueryOpenAlarms(2).Items.Select(item => item.CorrelationId).ToList();
            }
        }

        [Fact]
        public void Retention_can_be_changed_and_is_read_back()
        {
            if (_store == null)
            {
                return;
            }

            // The default comes from the archive since schema 16 (Upgrade_sixteen_gives_the_process_events_the_retention_of_the_archive),
            // and the archive tests change that one: the test leaves the retention as it found it
            var before = _store.ReadRetentionSeconds();
            try
            {
                Assert.Equal(7L * 24 * 3600, _store.SetRetentionSeconds(7L * 24 * 3600));
                Assert.Equal(7L * 24 * 3600, _store.ReadRetentionSeconds());

                Assert.Equal(0, _store.SetRetentionSeconds(0));
                Assert.Equal(0, _store.ReadRetentionSeconds());

                Assert.Throws<ArgumentException>(() => _store.SetRetentionSeconds(60));
                Assert.Throws<ArgumentException>(() => _store.SetRetentionSeconds(ProcessEventStore.RetentionMaxSeconds + 1));
            }
            finally
            {
                _store.SetRetentionSeconds(before);
            }
        }

        private DateTime At(int seconds)
        {
            return _base.AddSeconds(seconds).AddTicks(-(_base.Ticks % TimeSpan.TicksPerSecond));
        }

        private EventLogQuery Query(
            int limit = 100,
            string state = null,
            string text = null,
            List<int> sourceIds = null,
            List<string> classes = null)
        {
            return new EventLogQuery
            {
                FromUtc = At(0),
                ToUtc = At(60),
                Limit = limit,
                AlarmState = state,
                TextSearch = text,
                SourceIds = sourceIds,
                ClassLabels = classes
            };
        }

        private EventLogPoint Point(
            int seconds,
            string correlationId,
            string state,
            int sourceId = 2,
            int? severity = 100,
            string message = "Alarm")
        {
            return new EventLogPoint
            {
                TimeUtc = At(seconds),
                Measurement = EventLogConstants.ProcessMeasurementName,
                Level = "Warning",
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.AlarmComponent,
                SourceId = sourceId,
                SourceType = "OpcUa",
                CorrelationId = correlationId,
                AlarmState = state,
                Severity = severity,
                Message = message
            };
        }

        private EventLogPoint SystemPoint(int seconds, string correlationId, int affectedSourceId)
        {
            var point = Point(seconds, correlationId, AlarmStateValues.Incoming, sourceId: 1, message: "Connection lost");
            point.Component = EventLogConstants.SystemAlarmComponent;
            point.AffectedSourceId = affectedSourceId;
            return point;
        }
    }
}
