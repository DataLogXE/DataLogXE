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

using System.Globalization;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Builds a binary collation key that sorts like the alarm table text comparer
    /// (case insensitive, digit aware). Empty input returns null so SQL can keep empty cells last.
    /// </summary>
    public static class EventSortText
    {
        public const int MaxSourceChars = 256;
        private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

        public static byte[] KeyBytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var text = value.Length <= MaxSourceChars ? value : value.Substring(0, MaxSourceChars);
            var sortKey = Compare.GetSortKey(
                text,
                CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            return sortKey.KeyData;
        }
    }

    /// <summary>Sortable alarm columns. The names match the column ids of the alarm table.</summary>
    public enum EventSortColumn
    {
        Time,
        State,
        Severity,
        Active,
        Acked,
        Retain,
        Duration,
        Source,
        Class,
        Type,
        Message
    }

    public static class EventSortColumns
    {
        /// <summary>
        /// The storage fields a sort key can be read from. The key reader always adds the anchor
        /// field of the count query, so the keys cover exactly the rows the alarm count covers.
        /// </summary>
        public static IReadOnlyList<string> FieldsFor(EventSortColumn column)
        {
            return column switch
            {
                EventSortColumn.State => ["alarm_state", "props"],
                EventSortColumn.Severity => ["severity"],
                EventSortColumn.Active => ["active"],
                EventSortColumn.Acked => ["acked"],
                EventSortColumn.Retain => ["retain"],
                EventSortColumn.Duration => ["duration"],
                EventSortColumn.Class => ["alarm_class_symbol", "alarm_class_name"],
                EventSortColumn.Type => ["event_type", "event_type_name"],
                EventSortColumn.Message => ["message"],
                // The time is the storage order and the source is a tag, so neither needs a field.
                _ => []
            };
        }

        /// <summary>Parses a column id of the alarm table. Unknown ids keep the storage order.</summary>
        public static bool TryParse(string value, out EventSortColumn column)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "time":
                    column = EventSortColumn.Time;
                    return true;
                case "state":
                    column = EventSortColumn.State;
                    return true;
                case "severity":
                    column = EventSortColumn.Severity;
                    return true;
                case "active":
                    column = EventSortColumn.Active;
                    return true;
                case "acked":
                    column = EventSortColumn.Acked;
                    return true;
                case "retain":
                    column = EventSortColumn.Retain;
                    return true;
                case "duration":
                    column = EventSortColumn.Duration;
                    return true;
                case "source":
                    column = EventSortColumn.Source;
                    return true;
                case "class":
                    column = EventSortColumn.Class;
                    return true;
                case "type":
                    column = EventSortColumn.Type;
                    return true;
                case "message":
                    column = EventSortColumn.Message;
                    return true;
                default:
                    column = EventSortColumn.Time;
                    return false;
            }
        }

        public static bool TryParseDirection(string value, out bool ascending)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "asc":
                    ascending = true;
                    return true;
                case "desc":
                    ascending = false;
                    return true;
                default:
                    ascending = true;
                    return false;
            }
        }
    }

    /// <summary>
    /// One sort key: event identity (time and correlation id) plus the column value as number or text.
    /// Numbers stay longs, a double would lose the 100 ns resolution of the event times.
    /// </summary>
    public readonly record struct EventSortValue(
        DateTime TimeUtc,
        string CorrelationId,
        long? Number,
        string Text)
    {
        public bool IsEmpty => Number == null && string.IsNullOrEmpty(Text);
    }

    public static class EventSortValues
    {
        /// <summary>
        /// Key of a full row. Mirrors the alarm table comparer of the pending page so both pages
        /// order a column the same way.
        /// </summary>
        public static EventSortValue FromRecord(
            EventSortColumn column,
            EventLogRecord record,
            IReadOnlyDictionary<int, string> sourceNames)
        {
            var time = record.TimeUtc;
            var correlationId = record.CorrelationId ?? "";
            switch (column)
            {
                case EventSortColumn.Time:
                    return new EventSortValue(time, correlationId, time.Ticks, null);
                case EventSortColumn.Severity:
                    return new EventSortValue(time, correlationId, record.Severity, null);
                case EventSortColumn.Active:
                    return new EventSortValue(time, correlationId, Flag(record.Active), null);
                case EventSortColumn.Acked:
                    return new EventSortValue(time, correlationId, Flag(record.Acked), null);
                case EventSortColumn.Retain:
                    return new EventSortValue(time, correlationId, Flag(record.Retain), null);
                case EventSortColumn.Duration:
                    return new EventSortValue(
                        time,
                        correlationId,
                        record.DurationMs is > 0 ? record.DurationMs : null,
                        null);
                case EventSortColumn.State:
                    return new EventSortValue(
                        time,
                        correlationId,
                        null,
                        EventLogLabels.AlarmState(record.AlarmState, EventLogLabels.LimitStateFromProps(record.Props)));
                case EventSortColumn.Source:
                    return new EventSortValue(time, correlationId, null, SourceName(record.SourceId, sourceNames));
                case EventSortColumn.Class:
                    return new EventSortValue(
                        time,
                        correlationId,
                        null,
                        EventLogLabels.ClassLabel(record.AlarmClassSymbol, record.AlarmClassName));
                case EventSortColumn.Type:
                    return new EventSortValue(
                        time,
                        correlationId,
                        null,
                        Trimmed(record.EventTypeName) is { Length: > 0 } typeName
                            ? typeName
                            : Trimmed(record.EventType));
                case EventSortColumn.Message:
                    return new EventSortValue(time, correlationId, null, record.Message ?? "");
                default:
                    return new EventSortValue(time, correlationId, null, "");
            }
        }

        /// <summary>
        /// Returns full rows for one sorted page in the order of the pre-sorted keys.
        /// </summary>
        public static List<EventLogRecord> MapPageRows(
            IReadOnlyList<EventSortValue> pageKeys,
            IReadOnlyList<EventLogRecord> rows)
        {
            var byPoint = new Dictionary<(DateTime TimeUtc, string CorrelationId), EventLogRecord>();
            foreach (var row in rows)
            {
                byPoint[(row.TimeUtc, row.CorrelationId ?? "")] = row;
            }

            var items = new List<EventLogRecord>(pageKeys.Count);
            foreach (var key in pageKeys)
            {
                if (byPoint.TryGetValue((key.TimeUtc, key.CorrelationId ?? ""), out var row))
                {
                    items.Add(row);
                }
            }

            return items;
        }

        public static void Sort(List<EventSortValue> values, bool ascending)
        {
            values.Sort((left, right) => Compare(left, right, ascending));
        }

        /// <summary>
        /// Empty cells always come last, numbers compare numerically and text like the table does
        /// (case insensitive, digit aware). Equal values keep the newest event first so paging stays
        /// deterministic.
        /// </summary>
        public static int Compare(EventSortValue left, EventSortValue right, bool ascending)
        {
            var direction = ascending ? 1 : -1;
            if (left.IsEmpty || right.IsEmpty)
            {
                if (left.IsEmpty != right.IsEmpty)
                {
                    return left.IsEmpty ? 1 : -1;
                }

                return NewestFirst(left, right);
            }

            var result = left.Number.HasValue && right.Number.HasValue
                ? left.Number.Value.CompareTo(right.Number.Value)
                : string.Compare(
                    left.Text ?? "",
                    right.Text ?? "",
                    CultureInfo.InvariantCulture,
                    CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            if (result != 0)
            {
                return result * direction;
            }

            return NewestFirst(left, right);
        }

        private static int NewestFirst(EventSortValue left, EventSortValue right)
        {
            var timeCompare = right.TimeUtc.CompareTo(left.TimeUtc);
            if (timeCompare != 0)
            {
                return timeCompare;
            }

            return string.Compare(
                left.CorrelationId,
                right.CorrelationId,
                StringComparison.Ordinal);
        }

        private static long? Flag(bool? value)
        {
            if (value == null)
            {
                return null;
            }

            return value.Value ? 1 : 0;
        }

        private static string Trimmed(string value)
        {
            return value?.Trim() ?? "";
        }

        private static string SourceName(int sourceId, IReadOnlyDictionary<int, string> sourceNames)
        {
            if (sourceId <= 0 || sourceNames == null || !sourceNames.TryGetValue(sourceId, out var name))
            {
                return "";
            }

            return name ?? "";
        }
    }
}
