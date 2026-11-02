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

namespace DataLogLib.Services.EventLog
{
    public class EventLogQuery
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public string Level { get; set; }
        public List<string> Levels { get; set; }
        public string Kind { get; set; }
        public int? SourceId { get; set; }
        public List<int> SourceIds { get; set; }
        public string Component { get; set; }
        public string AlarmState { get; set; }
        // Class labels of the alarm table (the symbol, else the class name); null or empty means all classes.
        public List<string> ClassLabels { get; set; }
        public string TextSearch { get; set; }
        public int Limit { get; set; } = EventLogConstants.DefaultQueryLimit;
        public int Offset { get; set; }
        public DateTime? CursorUtc { get; set; }
        // Null keeps the storage order (newest first). Any other column sorts the whole range.
        public EventSortColumn? SortColumn { get; set; }
        public bool SortAscending { get; set; } = true;
        // Display names of the data sources. The source sort key is the name the table shows.
        public IReadOnlyDictionary<int, string> SourceNames { get; set; }

        /// <summary>The same filter over another time range.</summary>
        public EventLogQuery WithRange(DateTime fromUtc, DateTime toUtc)
        {
            var copy = (EventLogQuery)MemberwiseClone();
            copy.FromUtc = fromUtc;
            copy.ToUtc = toUtc;
            return copy;
        }
    }

    public class EventLogQueryResult
    {
        public List<EventLogRecord> Items { get; set; } = [];
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        // Number of sorted alarms the table can scroll; null for the storage order, which is not counted.
        public long? Total { get; set; }
        // Number of alarms in a sorted range; larger than Total when only the first ones of the order are kept.
        public long? SortedOf { get; set; }
    }

    public class EventLogSummaryResult
    {
        public Dictionary<string, long> CountByLevel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public long Total { get; set; }
        public long ActiveAlarms { get; set; }
    }

    public class AlarmHistoryQuery
    {
        public string CorrelationId { get; set; } = "";
        public int SourceId { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public int Limit { get; set; } = EventLogConstants.AlarmHistoryLimit;
    }

    /// <summary>
    /// Counts of one alarm in one state per time bucket. The alarm is the one of the reference point: its
    /// condition for process alarms, the reported source and the alarm type for system alarms.
    /// </summary>
    public class AlarmCountQuery
    {
        public EventLogRecord Reference { get; set; }
        public string AlarmState { get; set; } = "";
        // The bucket bounds on full hours, ascending: bucket i runs from Edges[i] to Edges[i + 1]
        public IReadOnlyList<DateTime> Edges { get; set; } = [];
    }

    public class AlarmCountResult
    {
        public long[] Counts { get; set; } = [];
        // Older counts are still missing while the counting of the rows from before schema 17 has not reached them
        public DateTime? CompleteFromUtc { get; set; }
        // The time of the oldest stored process event, where the statistics stop going back; null for none
        public DateTime? OldestUtc { get; set; }
    }
}
