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
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// The archive sorts a column on the labels the alarm table shows. These tests pin the mapping
    /// and the ordering rules so the history page orders like the pending page.
    /// </summary>
    public class EventSortTests
    {
        private static readonly Dictionary<int, string> SourceNames = new()
        {
            [2] = "Line A",
            [7] = "Pump"
        };

        [Theory]
        [InlineData("time", EventSortColumn.Time)]
        [InlineData("state", EventSortColumn.State)]
        [InlineData("severity", EventSortColumn.Severity)]
        [InlineData("active", EventSortColumn.Active)]
        [InlineData("acked", EventSortColumn.Acked)]
        [InlineData("retain", EventSortColumn.Retain)]
        [InlineData("duration", EventSortColumn.Duration)]
        [InlineData("source", EventSortColumn.Source)]
        [InlineData("class", EventSortColumn.Class)]
        [InlineData("type", EventSortColumn.Type)]
        [InlineData("message", EventSortColumn.Message)]
        [InlineData("  Message ", EventSortColumn.Message)]
        public void TryParse_takes_the_column_ids_of_the_alarm_table(string value, EventSortColumn expected)
        {
            Assert.True(EventSortColumns.TryParse(value, out var column));
            Assert.Equal(expected, column);
        }

        [Theory]
        [InlineData("exception")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("unknown")]
        public void TryParse_rejects_columns_without_a_sort_field(string value)
        {
            Assert.False(EventSortColumns.TryParse(value, out _));
        }

        [Theory]
        [InlineData("asc", true)]
        [InlineData("DESC", false)]
        public void TryParseDirection_reads_the_direction(string value, bool expected)
        {
            Assert.True(EventSortColumns.TryParseDirection(value, out var ascending));
            Assert.Equal(expected, ascending);
        }

        [Fact]
        public void TryParseDirection_rejects_anything_else()
        {
            Assert.False(EventSortColumns.TryParseDirection("up", out _));
            Assert.False(EventSortColumns.TryParseDirection(null, out _));
        }

        [Fact]
        public void FieldsFor_lists_the_storage_fields_of_the_column()
        {
            Assert.Equal(["alarm_state", "props"], EventSortColumns.FieldsFor(EventSortColumn.State));
            Assert.Equal(["alarm_class_symbol", "alarm_class_name"], EventSortColumns.FieldsFor(EventSortColumn.Class));
            Assert.Equal(["event_type", "event_type_name"], EventSortColumns.FieldsFor(EventSortColumn.Type));
            Assert.Empty(EventSortColumns.FieldsFor(EventSortColumn.Time));
            Assert.Empty(EventSortColumns.FieldsFor(EventSortColumn.Source));
        }

        [Fact]
        public void Compare_keeps_empty_cells_last_in_both_directions()
        {
            var filled = new EventSortValue(At(1), "a", 10, null);
            var empty = new EventSortValue(At(2), "b", null, null);

            Assert.True(EventSortValues.Compare(filled, empty, ascending: true) < 0);
            Assert.True(EventSortValues.Compare(filled, empty, ascending: false) < 0);
            Assert.True(EventSortValues.Compare(empty, filled, ascending: true) > 0);
            Assert.Equal(0, EventSortValues.Compare(empty, empty, ascending: true));
        }

        [Fact]
        public void Compare_sorts_numbers_by_value_and_text_by_label()
        {
            var low = new EventSortValue(At(1), "a", null, "alarm 2");
            var high = new EventSortValue(At(2), "b", null, "alarm 10");

            Assert.True(EventSortValues.Compare(low, high, ascending: true) < 0);
            Assert.True(EventSortValues.Compare(low, high, ascending: false) > 0);

            var small = new EventSortValue(At(1), "a", 5, null);
            var large = new EventSortValue(At(2), "b", 500, null);
            Assert.True(EventSortValues.Compare(small, large, ascending: true) < 0);
            Assert.True(EventSortValues.Compare(large, small, ascending: true) > 0);
        }

        [Fact]
        public void Compare_is_case_insensitive_and_keeps_equal_values_newest_first()
        {
            var newer = new EventSortValue(At(5), "a", null, "Pump");
            var older = new EventSortValue(At(1), "b", null, "pump");

            // Same label, so the newest event stays first whatever the direction is.
            Assert.True(EventSortValues.Compare(newer, older, ascending: true) < 0);
            Assert.True(EventSortValues.Compare(newer, older, ascending: false) < 0);
            Assert.True(EventSortValues.Compare(older, newer, ascending: true) > 0);
        }

        [Fact]
        public void Sort_orders_a_page_deterministically()
        {
            var values = new List<EventSortValue>
            {
                new(At(3), "c", 7, null),
                new(At(1), "a", null, null),
                new(At(2), "b", 3, null)
            };

            EventSortValues.Sort(values, ascending: true);

            Assert.Equal([At(2), At(3), At(1)], values.Select(value => value.TimeUtc));
        }

        [Fact]
        public void FromRecord_reads_the_number_and_the_label_of_the_column()
        {
            var record = new EventLogRecord
            {
                TimeUtc = At(4),
                Severity = 500,
                Active = true,
                Acked = false,
                Retain = null,
                DurationMs = 0,
                AlarmState = "ActiveUnacked",
                SourceId = 7,
                AlarmClassSymbol = "ProcessAlarm",
                EventType = "EventType",
                EventTypeName = "Event type name",
                Message = "Boiler hot"
            };

            Assert.Equal((long?)500, Key(EventSortColumn.Severity, record).Number);
            Assert.Equal((long?)1, Key(EventSortColumn.Active, record).Number);
            Assert.Equal((long?)0, Key(EventSortColumn.Acked, record).Number);
            Assert.Null(Key(EventSortColumn.Retain, record).Number);
            Assert.Null(Key(EventSortColumn.Duration, record).Number);
            Assert.Equal("Incoming", Key(EventSortColumn.State, record).Text);
            Assert.Equal("Pump", Key(EventSortColumn.Source, record).Text);
            Assert.Equal("ProcessAlarm", Key(EventSortColumn.Class, record).Text);
            Assert.Equal("Event type name", Key(EventSortColumn.Type, record).Text);
            Assert.Equal("Boiler hot", Key(EventSortColumn.Message, record).Text);
            Assert.Equal((long?)At(4).Ticks, Key(EventSortColumn.Time, record).Number);
        }

        [Fact]
        public void FromRecord_falls_back_to_the_event_type_and_the_class_name()
        {
            var record = new EventLogRecord
            {
                TimeUtc = At(1),
                EventType = "EventType",
                AlarmClassName = "Namespace::Boiler",
                SourceId = 99
            };

            Assert.Equal("EventType", Key(EventSortColumn.Type, record).Text);
            Assert.Equal("Boiler", Key(EventSortColumn.Class, record).Text);
            Assert.True(Key(EventSortColumn.Source, record).IsEmpty);
        }

        [Fact]
        public void MapPageRows_keeps_page_order_when_two_events_share_a_time()
        {
            var sharedTime = At(2);
            var pageKeys = new List<EventSortValue>
            {
                new(sharedTime, "first", 100, null),
                new(sharedTime, "second", 200, null)
            };
            var rows = new List<EventLogRecord>
            {
                new() { TimeUtc = sharedTime, CorrelationId = "second", Severity = 200 },
                new() { TimeUtc = sharedTime, CorrelationId = "first", Severity = 100 },
                new() { TimeUtc = sharedTime, CorrelationId = "other", Severity = 999 }
            };

            var page = EventSortValues.MapPageRows(pageKeys, rows);

            Assert.Equal(2, page.Count);
            Assert.Equal("first", page[0].CorrelationId);
            Assert.Equal("second", page[1].CorrelationId);
        }

        [Fact]
        public void Compare_breaks_ties_by_correlation_id_when_time_and_value_match()
        {
            var sharedTime = At(2);
            var left = new EventSortValue(sharedTime, "aaa", 5, null);
            var right = new EventSortValue(sharedTime, "bbb", 5, null);

            Assert.True(EventSortValues.Compare(left, right, ascending: true) < 0);
            Assert.True(EventSortValues.Compare(right, left, ascending: true) > 0);
        }

        private static EventSortValue Key(EventSortColumn column, EventLogRecord record)
        {
            return EventSortValues.FromRecord(column, record, SourceNames);
        }

        private static DateTime At(int minutes)
        {
            return new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);
        }
    }
}
