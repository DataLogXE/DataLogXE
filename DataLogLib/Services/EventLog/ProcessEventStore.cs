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
using DataAccessLib.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace DataLogLib.Services.EventLog
{
    public sealed class ProcessEventPage
    {
        public List<EventLogRecord> Items { get; set; } = [];
        public bool HasMore { get; set; }
        public long Total { get; set; }
        public long SortedOf { get; set; }
    }

    /// <summary>
    /// The ordered ids of a sorted range: the first ones of the order, at most the sort limit, and the
    /// number of alarms in the range (larger than the ids when the range is longer than the limit).
    /// </summary>
    public sealed class SortedIds
    {
        public long[] Ids { get; init; } = [];
        public long Total { get; init; }
    }

    /// <summary>
    /// Process events (OPC UA and system alarms) in the processevent hypertable. The sort keys of the
    /// alarm table are stored with each row, so sorted pages are plain SQL over the same table.
    /// </summary>
    public sealed partial class ProcessEventStore : IEventLogStore
    {
        public const string TableName = "processevent";
        // The occurrences whose newest point is open (schema 11), for the seed of the open alarms.
        private const string OpenTableName = "processeventopen";
        public const long RetentionMinSeconds = 3600;
        public const long RetentionMaxSeconds = 10L * 365 * 24 * 3600;

        private const string Columns =
            "time, level, kind, component, source_id, affected_source_id, source_type, message, template, "
            + "exception, props, correlation_id, alarm_state, severity, active, acked, retain, active_since, "
            + "event_type, event_type_name, alarm_class_symbol, alarm_class_name, text_color, back_color, "
            + "flashing, duration_ms";

        private const string SelectColumns =
            "e.time, e.level, e.kind, e.component, e.source_id, e.affected_source_id, e.source_type, e.message, "
            + "e.template, e.exception, e.props, e.correlation_id, e.alarm_state, e.severity, e.active, e.acked, "
            + "e.retain, e.active_since, e.event_type, e.event_type_name, e.alarm_class_symbol, e.alarm_class_name, "
            + "e.text_color, e.back_color, e.flashing, e.duration_ms";

        // The class column of the alarm table in SQL, as EventLogLabels.ClassLabel: the symbol, else the class name behind "::"
        private const string ClassNameSql = "btrim(COALESCE(e.alarm_class_name, ''))";
        private const string ClassLabelSql = "COALESCE(NULLIF(btrim(COALESCE(e.alarm_class_symbol, '')), ''), "
            + "CASE WHEN strpos(" + ClassNameSql + ", '::') > 0 "
            + "THEN btrim(substr(" + ClassNameSql + ", strpos(" + ClassNameSql + ", '::') + 2)) ELSE " + ClassNameSql + " END)";

        private static readonly string[] ActiveAlarmStates =
        [
            "Incoming",
            "ActiveUnacked",
            "ActiveAcked",
            "ActiveUnackedUnconfirmed",
            "ActiveAckedUnconfirmed"
        ];

        private static readonly string[] OpenAlarmStates =
        [
            AlarmStateValues.Incoming,
            AlarmStateValues.IncomingOutgoing
        ];

        // Hour counts of this many filters are kept, each at most for the age below: the retention job of
        // Timescale drops chunks without telling the store.
        private const int MaxHourCountFilters = 8;
        private static readonly TimeSpan HourCountMaxAge = TimeSpan.FromMinutes(10);

        private readonly DataAccess.PostgresPools _dataSource;
        // A sorted range keeps this many ids (the first ones of the order); the tests lower it.
        internal int SortLimit { get; set; } = EventLogConstants.MaxSortableEvents;
        // Every process event gets its own microsecond, so (time, correlation id) identifies a row and a
        // time cursor never splits events that share a timestamp.
        private readonly EventSeriesClock _clock = new(TimeSpan.TicksPerMicrosecond);
        private readonly object _cacheGate = new();
        private string _listCacheKey;
        private SortedIds _listCacheIds;
        private long _listCacheToTicks;
        private readonly Dictionary<string, HourCounts> _hourCounts = new(StringComparer.Ordinal);
        // Reads whose result is cached when they end; an insert into their range makes them stale.
        private readonly List<PendingRead> _pendingReads = [];

        /// <summary>
        /// Alarm counts of one filter per hour slice: a whole UTC hour, cut at the ends of the range
        /// it was counted for. Keyed by the start and end ticks of the slice.
        /// </summary>
        private sealed class HourCounts
        {
            public readonly Dictionary<(long Start, long End), long> Counts = [];
            public readonly SemaphoreSlim Gate = new(1, 1);
            public DateTime CreatedUtc = DateTime.UtcNow;
            public long LastUsed;
        }

        private sealed class PendingRead
        {
            public long EndTicks;
            public bool Stale;
        }

        private readonly record struct HourSlice(DateTime Start, DateTime End);

        private readonly record struct DayCounts(DateTime Start, DateTime End, HourSlice[] Slices, long[] Counts);

        public ProcessEventStore(IConfiguration configuration)
        {
            var info = new DataConnectionInfo(configuration);
            _dataSource = DataAccess.PostgresDataSources.Get(info.ConnectionString);
        }

        #region write
        public void InsertEventPoints(IReadOnlyList<EventLogPoint> points)
        {
            if (points == null || points.Count == 0)
            {
                return;
            }

            using var connection = _dataSource.OpenIngestConnection();
            using var transaction = connection.BeginTransaction();
            var counting = BeginCounting(connection, transaction, out var settingLiveFrom);
            var counted = new List<EventLogRecord>();
            var oldest = DateTime.MaxValue;
            // The newest point of each occurrence in the batch decides whether it is open now.
            var newest = new Dictionary<(int SourceId, string CorrelationId), (DateTime Time, bool Open)>();
            using (var writer = connection.BeginBinaryImport(
                $"COPY {TableName} ({Columns}, state_key, class_key, type_key, message_key) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var point in points)
                {
                    var record = WriteRow(writer, point);
                    if (record.TimeUtc < oldest)
                    {
                        oldest = record.TimeUtc;
                    }

                    var correlationId = record.CorrelationId?.Trim() ?? "";
                    if (correlationId.Length > 0 && !string.IsNullOrEmpty(record.AlarmState))
                    {
                        var key = (record.SourceId, correlationId);
                        if (!newest.TryGetValue(key, out var seen) || record.TimeUtc >= seen.Time)
                        {
                            newest[key] = (record.TimeUtc, OpenAlarmStates.Contains(record.AlarmState));
                        }

                        if (counting)
                        {
                            counted.Add(record);
                        }
                    }
                }

                writer.Complete();
            }

            UpdateOpenOccurrences(connection, transaction, newest);
            if (counting)
            {
                AddCounts(connection, transaction, counted);
            }

            transaction.Commit();
            if (settingLiveFrom)
            {
                _liveFromSet = true;
            }

            InvalidateFrom(oldest);
        }

        /// <summary>
        /// Keeps processeventopen in step with the points just written: an occurrence whose newest point is open is
        /// in it, any other is not. A point older than the newest point of its occurrence changes nothing, as the
        /// seed took the newest point by time before.
        /// </summary>
        private static void UpdateOpenOccurrences(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Dictionary<(int SourceId, string CorrelationId), (DateTime Time, bool Open)> newest)
        {
            if (newest.Count == 0)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "WITH b AS (SELECT * FROM unnest(@sources, @correlations, @times, @open) AS b(source_id, correlation_id, time, open)), "
                + $"closed AS (DELETE FROM {OpenTableName} o USING b WHERE NOT b.open "
                + "AND o.source_id = b.source_id AND o.correlation_id = b.correlation_id AND o.time <= b.time) "
                + $"INSERT INTO {OpenTableName} (source_id, correlation_id, time) "
                + "SELECT b.source_id, b.correlation_id, b.time FROM b WHERE b.open "
                // @oldest as a parameter: p.time > b.time alone excludes the chunks only when the statement runs, after
                // the planning locked all of them
                + $"AND NOT EXISTS (SELECT 1 FROM {TableName} p WHERE p.source_id = b.source_id "
                + "AND p.correlation_id = b.correlation_id AND p.time > b.time AND p.time > @oldest) "
                + "ON CONFLICT (source_id, correlation_id) DO UPDATE SET time = EXCLUDED.time "
                + $"WHERE {OpenTableName}.time <= EXCLUDED.time";
            command.Parameters.AddWithValue("@sources", newest.Keys.Select(key => key.SourceId).ToArray());
            command.Parameters.AddWithValue("@correlations", newest.Keys.Select(key => key.CorrelationId).ToArray());
            command.Parameters.AddWithValue("@times", newest.Values.Select(value => value.Time).ToArray());
            command.Parameters.AddWithValue("@open", newest.Values.Select(value => value.Open).ToArray());
            command.Parameters.AddWithValue("@oldest", newest.Values.Min(value => value.Time));
            command.ExecuteNonQuery();
        }

        public void FlushWrites()
        {
            // Rows are written synchronously, nothing is buffered here.
        }

        private EventLogRecord WriteRow(NpgsqlBinaryImporter writer, EventLogPoint point)
        {
            point.TimeUtc = _clock.Assign(TableName, "", "", "", "", "", point.TimeUtc);
            var record = EventLogRecord.FromPoint(point);
            if (record.AffectedSourceId <= 0)
            {
                record.AffectedSourceId = EventLogSourceFilter.TryParseAffectedSourceIdFromProps(record.Props);
            }

            writer.StartRow();
            writer.Write(record.TimeUtc, NpgsqlDbType.TimestampTz);
            writer.Write(record.Level ?? "", NpgsqlDbType.Text);
            writer.Write(record.Kind ?? "", NpgsqlDbType.Text);
            writer.Write(record.Component ?? "", NpgsqlDbType.Text);
            writer.Write(record.SourceId, NpgsqlDbType.Integer);
            writer.Write(record.AffectedSourceId, NpgsqlDbType.Integer);
            writer.Write(record.SourceType ?? "", NpgsqlDbType.Text);
            writer.Write(record.Message ?? "", NpgsqlDbType.Text);
            writer.Write(record.Template ?? "", NpgsqlDbType.Text);
            writer.Write(record.Exception ?? "", NpgsqlDbType.Text);
            writer.Write(record.Props ?? "", NpgsqlDbType.Text);
            writer.Write(record.CorrelationId?.Trim() ?? "", NpgsqlDbType.Text);
            writer.Write(record.AlarmState ?? "", NpgsqlDbType.Text);
            WriteNullable(writer, record.Severity, NpgsqlDbType.Integer);
            WriteNullable(writer, record.Active, NpgsqlDbType.Boolean);
            WriteNullable(writer, record.Acked, NpgsqlDbType.Boolean);
            WriteNullable(writer, record.Retain, NpgsqlDbType.Boolean);
            WriteNullable(writer, record.ActiveSinceUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
            WriteText(writer, record.EventType);
            WriteText(writer, record.EventTypeName);
            WriteText(writer, record.AlarmClassSymbol);
            WriteText(writer, record.AlarmClassName);
            WriteNullable(writer, (long?)record.TextColor, NpgsqlDbType.Bigint);
            WriteNullable(writer, (long?)record.BackColor, NpgsqlDbType.Bigint);
            WriteNullable(writer, record.Flashing, NpgsqlDbType.Boolean);
            WriteNullable(writer, record.DurationMs, NpgsqlDbType.Bigint);
            WriteBytes(writer, EventSortText.KeyBytes(EventSortValues.FromRecord(EventSortColumn.State, record, null).Text));
            WriteBytes(writer, EventSortText.KeyBytes(EventSortValues.FromRecord(EventSortColumn.Class, record, null).Text));
            WriteBytes(writer, EventSortText.KeyBytes(EventSortValues.FromRecord(EventSortColumn.Type, record, null).Text));
            WriteBytes(writer, EventSortText.KeyBytes(record.Message ?? ""));
            return record;
        }

        private static void WriteNullable<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type)
            where T : struct
        {
            if (value.HasValue)
            {
                writer.Write(value.Value, type);
            }
            else
            {
                writer.WriteNull();
            }
        }

        private static void WriteText(NpgsqlBinaryImporter writer, string value)
        {
            if (value == null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.Write(value, NpgsqlDbType.Text);
            }
        }

        private static void WriteBytes(NpgsqlBinaryImporter writer, byte[] value)
        {
            if (value == null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.Write(value, NpgsqlDbType.Bytea);
            }
        }
        #endregion

        #region read
        /// <summary>
        /// A page of the alarms in the storage order (newest first) at the offset of the query, read from
        /// the hours that hold it (see <see cref="LocatePage"/>).
        /// </summary>
        public EventLogQueryResult QueryEvents(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            var stop = query.ToUtc;
            if (query.CursorUtc.HasValue && query.CursorUtc.Value < stop)
            {
                stop = query.CursorUtc.Value;
            }

            if (query.FromUtc >= stop)
            {
                return new EventLogQueryResult();
            }

            var limit = Math.Clamp(query.Limit, 1, EventLogConstants.MaxQueryLimit);
            using var connection = _dataSource.OpenConnection();
            var page = LocatePage(connection, query.WithRange(query.FromUtc, stop), Math.Max(0, query.Offset), limit + 1, cancellationToken);
            if (page == null)
            {
                return new EventLogQueryResult();
            }

            using var command = CreateReadCommand(connection);
            var where = BuildWhereClause(query.WithRange(page.Value.From, page.Value.To), command);
            command.CommandText = $"SELECT {SelectColumns} FROM {TableName} e WHERE {where} "
                + "ORDER BY e.time DESC, e.id DESC LIMIT @fetch OFFSET @offset";
            command.Parameters.AddWithValue("@fetch", limit + 1);
            command.Parameters.AddWithValue("@offset", page.Value.Skip);
            var items = ReadRecords(command, cancellationToken);
            var hasMore = items.Count > limit;
            if (hasMore)
            {
                items.RemoveRange(limit, items.Count - limit);
            }

            return new EventLogQueryResult
            {
                Items = items,
                HasMore = hasMore,
                NextCursor = hasMore && items.Count > 0 ? ToCursor(items[^1].TimeUtc) : null
            };
        }

        public long QueryEventCount(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryEventCount(query, null, cancellationToken);
        }

        public long QueryEventCount(EventLogQuery query, IQueryProgress progress, CancellationToken cancellationToken = default)
        {
            if (query.FromUtc >= query.ToUtc)
            {
                return 0;
            }

            using var connection = _dataSource.OpenConnection();
            return CountMatching(connection, query, progress, cancellationToken);
        }

        public EventLogSummaryResult QueryEventSummary(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryEventSummary(query, null, cancellationToken);
        }

        public EventLogSummaryResult QueryEventSummary(
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            var result = new EventLogSummaryResult();
            if (query.FromUtc >= query.ToUtc)
            {
                return result;
            }

            using var connection = _dataSource.OpenConnection();
            var noText = CopyWithoutText(query);
            result.Total = CountMatching(connection, noText, progress, cancellationToken);
            progress?.Report("Counting active alarms", null, result.Total);

            // The newest point of each occurrence decides whether it is still active.
            using var command = CreateReadCommand(connection);
            var where = BuildWhereClause(noText, command, includeState: false);
            var active = AddTextList(command, "@active", ActiveAlarmStates);
            var stateFilter = "";
            if (!string.IsNullOrWhiteSpace(query.AlarmState))
            {
                stateFilter = $" AND t.alarm_state IN ({AddTextList(command, "@state", AlarmStateValues.TokensForFilter(query.AlarmState))})";
            }

            command.CommandText = "SELECT COUNT(*) FROM ("
                + $"SELECT DISTINCT ON (e.correlation_id) e.alarm_state FROM {TableName} e WHERE {where} "
                + "ORDER BY e.correlation_id, e.time DESC) t "
                + $"WHERE t.alarm_state IN ({active}){stateFilter}";
            result.ActiveAlarms = ExecuteLong(command, cancellationToken);
            return result;
        }

        public IReadOnlyList<string> QueryAlarmStates(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryAlarmStates(query, null, cancellationToken);
        }

        public IReadOnlyList<string> QueryAlarmStates(
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (query.FromUtc >= query.ToUtc)
            {
                return [];
            }

            progress?.Report("Reading alarm states");

            using var connection = _dataSource.OpenConnection();
            using var command = CreateReadCommand(connection);
            var where = BuildWhereClause(CopyWithoutText(query), command, includeState: false);
            command.CommandText = $"SELECT DISTINCT e.alarm_state FROM {TableName} e WHERE {where}";
            using var registration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var states = new List<string>();
            while (reader.Read())
            {
                states.Add(reader.GetString(0));
            }

            return states;
        }

        public EventLogQueryResult QueryAlarmHistory(AlarmHistoryQuery query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query.CorrelationId) || query.FromUtc >= query.ToUtc)
            {
                return new EventLogQueryResult();
            }

            var limit = Math.Clamp(query.Limit, 1, EventLogConstants.AlarmHistoryLimit);
            using var connection = _dataSource.OpenConnection();
            using var command = CreateReadCommand(connection);
            var where = BuildWhereClause(
                new EventLogQuery { FromUtc = query.FromUtc, ToUtc = query.ToUtc, SourceId = query.SourceId },
                command);
            command.CommandText = $"SELECT {SelectColumns} FROM {TableName} e WHERE {where} "
                + "AND e.correlation_id = @correlationId ORDER BY e.time DESC, e.id DESC LIMIT @limit";
            command.Parameters.AddWithValue("@correlationId", query.CorrelationId.Trim());
            command.Parameters.AddWithValue("@limit", limit);
            var items = ReadRecords(command, cancellationToken);
            items.Reverse();
            return new EventLogQueryResult { Items = items };
        }

        /// <summary>
        /// The newest point of every open occurrence of the data source, from processeventopen: the store keeps the
        /// open occurrences there when it writes the points, so the seed reads the open ones only instead of the
        /// newest point of every occurrence of the last days (260 000 a source on the Pi, 47 s).
        /// </summary>
        public EventLogQueryResult QueryOpenAlarms(int sourceId, CancellationToken cancellationToken = default)
        {
            using var connection = _dataSource.OpenConnection();
            using var command = CreateReadCommand(connection);
            var where = BuildSeedWhereClause(sourceId, command);
            var open = AddTextList(command, "@open", OpenAlarmStates);
            command.CommandText = $"SELECT {SelectColumns} FROM {OpenTableName} o "
                + $"CROSS JOIN LATERAL (SELECT e.* FROM {TableName} e WHERE e.source_id = o.source_id "
                // The time bounds as parameters: o.time alone excludes the chunks only when the statement runs, after
                // the planning locked all of them
                + "AND e.correlation_id = o.correlation_id AND e.time = o.time AND e.time >= @seedFrom AND e.time < @seedTo "
                + "ORDER BY e.id DESC LIMIT 1) e "
                + "WHERE o.source_id = @seedSource AND o.time >= @seedFrom AND o.time < @seedTo "
                + $"AND {where} AND e.alarm_state IN ({open}) ORDER BY e.time LIMIT @limit";
            command.Parameters.AddWithValue("@limit", EventLogConstants.MaxTrackedConditions);
            return new EventLogQueryResult { Items = ReadRecords(command, cancellationToken) };
        }

        public EventLogQueryResult QueryOpenAlarmLookback(
            int sourceId,
            IReadOnlyList<string> correlationIds,
            CancellationToken cancellationToken = default)
        {
            var ids = (correlationIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Take(EventLogConstants.MaxTrackedConditions)
                .ToArray();
            if (ids.Length == 0)
            {
                return new EventLogQueryResult();
            }

            using var connection = _dataSource.OpenConnection();
            using var command = CreateReadCommand(connection);
            var where = BuildSeedWhereClause(sourceId, command);
            command.CommandText = $"SELECT {SelectColumns} FROM ("
                + "SELECT e.*, row_number() OVER (PARTITION BY e.correlation_id ORDER BY e.time DESC, e.id DESC) AS rn "
                + $"FROM {TableName} e WHERE {where} AND e.correlation_id = ANY(@ids)) e "
                + "WHERE e.rn <= @lookback ORDER BY e.time";
            command.Parameters.AddWithValue("@ids", ids);
            command.Parameters.AddWithValue("@lookback", EventLogConstants.AlarmSeedLookback);
            return new EventLogQueryResult { Items = ReadRecords(command, cancellationToken) };
        }

        /// <summary>
        /// One block of a sorted page. The ordered id list of the range is computed once and cached,
        /// so every following block only reads its own rows instead of re-sorting the whole range.
        /// </summary>
        public ProcessEventPage QuerySortedBatch(
            EventLogQuery query,
            EventSortColumn column,
            int batchSize,
            int offset,
            CancellationToken cancellationToken = default)
        {
            var sorted = LoadSortedIds(query, column, null, cancellationToken);
            var ids = sorted.Ids;
            var start = Math.Max(0, offset);
            if (start >= ids.Length)
            {
                return new ProcessEventPage { Total = ids.Length, SortedOf = sorted.Total, HasMore = false };
            }

            var take = Math.Min(Math.Max(1, batchSize), ids.Length - start);
            return new ProcessEventPage
            {
                Items = LoadRows(query, ids.AsSpan(start, take).ToArray(), cancellationToken),
                HasMore = start + take < ids.Length,
                Total = ids.Length,
                SortedOf = sorted.Total
            };
        }

        /// <summary>
        /// Ids of the alarms of the range in the sorted order of the column: all of them, or the first
        /// <see cref="SortLimit"/> of the order when the range has more (the ids are held in memory and
        /// cached, so a long range on the Pi must not grow them without bound). The list is cached until
        /// an insert into the range. The total of a sorted page is the length of this list.
        /// </summary>
        public SortedIds LoadSortedIds(
            EventLogQuery query,
            EventSortColumn column,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = BuildListCacheKey(query, column);
            var toTicks = ToUtc(query.ToUtc).Ticks;
            PendingRead pending;
            lock (_cacheGate)
            {
                if (_listCacheIds != null && string.Equals(_listCacheKey, cacheKey, StringComparison.Ordinal))
                {
                    return _listCacheIds;
                }

                pending = BeginPendingRead(toTicks);
            }

            try
            {
                using var connection = _dataSource.OpenConnection();
                var total = CountMatching(connection, query, progress, cancellationToken);
                progress?.Report("Sorting alarms", null, total);
                var ids = new SortedIds
                {
                    Ids = LoadOrderedIds(connection, query, column, (int)Math.Min(total, SortLimit), cancellationToken),
                    Total = total
                };
                lock (_cacheGate)
                {
                    // An insert into the range during the sort makes the list stale for the next request.
                    if (!pending.Stale)
                    {
                        _listCacheIds = ids;
                        _listCacheKey = cacheKey;
                        _listCacheToTicks = toTicks;
                    }
                }

                return ids;
            }
            finally
            {
                EndPendingRead(pending);
            }
        }

        /// <summary>Rows of the given ids in the order of the ids.</summary>
        public List<EventLogRecord> LoadRows(EventLogQuery query, long[] ids, CancellationToken cancellationToken = default)
        {
            if (ids.Length == 0)
            {
                return [];
            }

            using var connection = _dataSource.OpenConnection();
            return LoadRowsByIds(connection, query, ids, cancellationToken);
        }

        /// <summary>Drops every cached list and count, for example after chunks were dropped.</summary>
        public void InvalidateListCache()
        {
            InvalidateFrom(DateTime.MinValue);
        }

        /// <summary>
        /// An insert changes the counts and lists of the ranges that end after its oldest row; the
        /// ranges before it stay cached. The pages of the UI end at the time they were loaded, so
        /// the alarms that arrive later do not throw their counts away.
        /// </summary>
        private void InvalidateFrom(DateTime oldestUtc)
        {
            var ticks = oldestUtc.Ticks;
            lock (_cacheGate)
            {
                if (_listCacheIds != null && _listCacheToTicks > ticks)
                {
                    _listCacheIds = null;
                    _listCacheKey = null;
                }

                foreach (var counts in _hourCounts.Values)
                {
                    foreach (var slice in counts.Counts.Keys.Where(slice => slice.End > ticks).ToList())
                    {
                        counts.Counts.Remove(slice);
                    }
                }

                foreach (var read in _pendingReads)
                {
                    if (read.EndTicks > ticks)
                    {
                        read.Stale = true;
                    }
                }
            }
        }

        private PendingRead BeginPendingRead(long endTicks)
        {
            var read = new PendingRead { EndTicks = endTicks };
            _pendingReads.Add(read);
            return read;
        }

        private void EndPendingRead(PendingRead read)
        {
            lock (_cacheGate)
            {
                _pendingReads.Remove(read);
            }
        }

        /// <summary>
        /// Counts the matching alarms one day at a time (the chunk size of the hypertable), newest
        /// first, so no statement reads more than a day and the user sees the count grow on a slow
        /// machine. The hour counts stay cached for the pages of the same filter.
        /// </summary>
        private long CountMatching(
            NpgsqlConnection connection,
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken)
        {
            var from = ToUtc(query.FromUtc);
            var to = ToUtc(query.ToUtc);
            var rangeTicks = Math.Max(1L, (to - from).Ticks);
            long total = 0;
            progress?.Report("Counting alarms", 0, 0);
            foreach (var day in CountDays(connection, query, cancellationToken))
            {
                total += day.Counts.Sum();
                progress?.Report("Counting alarms", 100.0 * (to - day.Start).Ticks / rangeTicks, total);
            }

            return total;
        }

        /// <summary>
        /// The window of a page in the storage order (newest first): the hours that hold its rows,
        /// and how many rows of the window come before the page. A page therefore reads only the
        /// hours it lies in, wherever it is in the range; an OFFSET over the whole range read every
        /// row before the page. Null when the range ends before the offset.
        /// </summary>
        private (DateTime From, DateTime To, long Skip)? LocatePage(
            NpgsqlConnection connection,
            EventLogQuery query,
            long offset,
            int fetch,
            CancellationToken cancellationToken)
        {
            long before = 0;
            DateTime? to = null;
            long skip = 0;
            foreach (var day in CountDays(connection, query, cancellationToken))
            {
                for (var index = 0; index < day.Slices.Length; index += 1)
                {
                    var count = day.Counts[index];
                    if (to == null && before + count > offset)
                    {
                        to = day.Slices[index].End;
                        skip = offset - before;
                    }

                    before += count;
                    if (to != null && before >= offset + fetch)
                    {
                        return (day.Slices[index].Start, to.Value, skip);
                    }
                }
            }

            return to == null ? null : (ToUtc(query.FromUtc), to.Value, skip);
        }

        /// <summary>
        /// The hour counts of the range, one UTC day after the other, newest first. Each day is read
        /// lazily, so a page near the newest end stops after its first day. Hours counted before are
        /// taken from the cache; the hours missing in a day are counted with one statement.
        /// </summary>
        private IEnumerable<DayCounts> CountDays(
            NpgsqlConnection connection,
            EventLogQuery query,
            CancellationToken cancellationToken)
        {
            var from = ToUtc(query.FromUtc);
            var stop = ToUtc(query.ToUtc);
            var key = BuildFilterKey(query);
            while (stop > from)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dayStart = stop.Date == stop ? stop.AddDays(-1) : stop.Date;
                var start = dayStart < from ? from : dayStart;
                var slices = HourSlices(start, stop);
                yield return new DayCounts(start, stop, slices, CountSlices(connection, query, key, slices, cancellationToken));
                stop = start;
            }
        }

        private long[] CountSlices(
            NpgsqlConnection connection,
            EventLogQuery query,
            string key,
            HourSlice[] slices,
            CancellationToken cancellationToken)
        {
            var counts = new long[slices.Length];
            SemaphoreSlim gate;
            lock (_cacheGate)
            {
                if (ReadCachedSlices(key, slices, counts, out _, out _))
                {
                    return counts;
                }

                gate = HourCountsOf(key).Gate;
            }

            // One count of a filter at a time: the alarm page starts its first page and the count of the same
            // filter together, and both walk the days from the newest. The second one takes the hours the first
            // counted instead of reading the same day again (a text filter reads every full row of the day).
            gate.Wait(cancellationToken);
            try
            {
                int newestMissing;
                int oldestMissing;
                PendingRead pending;
                lock (_cacheGate)
                {
                    if (ReadCachedSlices(key, slices, counts, out newestMissing, out oldestMissing))
                    {
                        return counts;
                    }

                    pending = BeginPendingRead(slices[newestMissing].End.Ticks);
                }

                try
                {
                    var byHour = CountHours(
                        connection,
                        query.WithRange(slices[oldestMissing].Start, slices[newestMissing].End),
                        cancellationToken);
                    lock (_cacheGate)
                    {
                        var cached = HourCountsOf(key);
                        for (var index = newestMissing; index <= oldestMissing; index += 1)
                        {
                            var slice = slices[index];
                            counts[index] = byHour.GetValueOrDefault(HourOf(slice.Start).Ticks);
                            if (!pending.Stale)
                            {
                                cached.Counts[(slice.Start.Ticks, slice.End.Ticks)] = counts[index];
                            }
                        }
                    }

                    return counts;
                }
                finally
                {
                    EndPendingRead(pending);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Fills the counts of the cached slices; true when every slice was cached, else the newest and the oldest
        /// missing slice. Called under the cache lock.
        /// </summary>
        private bool ReadCachedSlices(string key, HourSlice[] slices, long[] counts, out int newestMissing, out int oldestMissing)
        {
            newestMissing = -1;
            oldestMissing = -1;
            var cached = HourCountsOf(key);
            for (var index = 0; index < slices.Length; index += 1)
            {
                if (cached.Counts.TryGetValue((slices[index].Start.Ticks, slices[index].End.Ticks), out var count))
                {
                    counts[index] = count;
                }
                else
                {
                    newestMissing = newestMissing < 0 ? index : newestMissing;
                    oldestMissing = index;
                }
            }

            return newestMissing < 0;
        }

        /// <summary>Matching alarms per UTC hour of the range, keyed by the start ticks of the hour.</summary>
        private static Dictionary<long, long> CountHours(
            NpgsqlConnection connection,
            EventLogQuery query,
            CancellationToken cancellationToken)
        {
            using var command = CreateReadCommand(connection);
            command.CommandText = "SELECT time_bucket(INTERVAL '1 hour', e.time), COUNT(*) "
                + $"FROM {TableName} e WHERE {BuildWhereClause(query, command)} GROUP BY 1";
            var counts = new Dictionary<long, long>();
            using var registration = cancellationToken.Register(command.Cancel);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    counts[ToUtc(reader.GetDateTime(0)).Ticks] = reader.GetInt64(1);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return counts;
        }

        /// <summary>The cached hour counts of a filter; called under the cache lock.</summary>
        private HourCounts HourCountsOf(string key)
        {
            if (!_hourCounts.TryGetValue(key, out var counts) || DateTime.UtcNow - counts.CreatedUtc > HourCountMaxAge)
            {
                if (counts == null && _hourCounts.Count >= MaxHourCountFilters)
                {
                    _hourCounts.Remove(_hourCounts.MinBy(entry => entry.Value.LastUsed).Key);
                }

                counts = new HourCounts();
                _hourCounts[key] = counts;
            }

            counts.LastUsed = Environment.TickCount64;
            return counts;
        }

        /// <summary>The hours of [start, stop), newest first, cut at both ends.</summary>
        private static HourSlice[] HourSlices(DateTime start, DateTime stop)
        {
            var slices = new List<HourSlice>(25);
            var end = stop;
            while (end > start)
            {
                var hour = HourOf(end) == end ? end.AddHours(-1) : HourOf(end);
                var sliceStart = hour < start ? start : hour;
                slices.Add(new HourSlice(sliceStart, end));
                end = sliceStart;
            }

            return slices.ToArray();
        }

        private static DateTime HourOf(DateTime time)
        {
            return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        }

        /// <summary>
        /// Reads of the UI run without a command deadline. They end when they are done or when the
        /// caller cancels them through the token.
        /// </summary>
        private static NpgsqlCommand CreateReadCommand(NpgsqlConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            return command;
        }

        private long[] LoadOrderedIds(
            NpgsqlConnection connection,
            EventLogQuery query,
            EventSortColumn column,
            int limit,
            CancellationToken cancellationToken)
        {
            if (limit <= 0)
            {
                return [];
            }

            using var command = CreateReadCommand(connection);
            var sourceNames = column == EventSortColumn.Source ? query.SourceNames : null;
            var sourceCte = BuildSourceCte(sourceNames, command);
            var where = BuildWhereClause(query, command);
            command.CommandText = sourceCte
                + $"SELECT e.id FROM {TableName} e "
                + (sourceCte.Length > 0 ? "LEFT JOIN src ON src.id = e.source_id " : "")
                + "WHERE " + where + " "
                + BuildOrderClause(column, query.SortAscending, sourceCte.Length > 0)
                + " LIMIT @sortLimit";
            command.Parameters.AddWithValue("@sortLimit", limit);
            using var registration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var ids = new List<long>(limit);
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids.ToArray();
        }

        private List<EventLogRecord> LoadRowsByIds(
            NpgsqlConnection connection,
            EventLogQuery query,
            long[] ids,
            CancellationToken cancellationToken)
        {
            if (ids.Length == 0)
            {
                return [];
            }

            using var command = CreateReadCommand(connection);
            // The time range lets Timescale skip every chunk outside the page's range.
            command.CommandText = $"SELECT e.id, {SelectColumns} FROM {TableName} e "
                + "WHERE e.id = ANY(@ids) AND e.time >= @from AND e.time < @to";
            command.Parameters.AddWithValue("@ids", ids);
            command.Parameters.AddWithValue("@from", ToUtc(query.FromUtc));
            command.Parameters.AddWithValue("@to", ToUtc(query.ToUtc));
            var byId = new Dictionary<long, EventLogRecord>(ids.Length);
            using (var registration = cancellationToken.Register(command.Cancel))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    byId[reader.GetInt64(0)] = ReadRecord(reader, 1);
                }
            }

            var items = new List<EventLogRecord>(ids.Length);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var row))
                {
                    items.Add(row);
                }
            }

            return items;
        }

        /// <summary>The filter of a query without its range and order: the key of its hour counts.</summary>
        private static string BuildFilterKey(EventLogQuery query)
        {
            return new System.Text.StringBuilder()
                .Append(query.Component).Append('|')
                .Append(query.AlarmState).Append('|')
                .Append(query.TextSearch).Append('|')
                .Append(string.Join(",", SourceIds(query))).Append('|')
                .Append(string.Join("\u001f", query.ClassLabels ?? []))
                .ToString();
        }

        private static string BuildListCacheKey(EventLogQuery query, EventSortColumn column)
        {
            var key = new System.Text.StringBuilder()
                .Append(query.FromUtc.Ticks).Append('|')
                .Append(query.ToUtc.Ticks).Append('|')
                .Append(BuildFilterKey(query)).Append('|')
                .Append(column).Append('|')
                .Append(query.SortAscending);
            if (column == EventSortColumn.Source && query.SourceNames != null)
            {
                foreach (var entry in query.SourceNames.OrderBy(item => item.Key))
                {
                    key.Append('|').Append(entry.Key).Append('=').Append(entry.Value);
                }
            }

            return key.ToString();
        }

        private static string BuildSourceCte(IReadOnlyDictionary<int, string> sourceNames, NpgsqlCommand command)
        {
            if (sourceNames == null || sourceNames.Count == 0)
            {
                return "";
            }

            var rows = new List<string>();
            var index = 0;
            foreach (var entry in sourceNames.OrderBy(item => item.Key))
            {
                var idName = "@srcId" + index;
                var keyName = "@srcKey" + index;
                rows.Add($"({idName}, {keyName})");
                command.Parameters.AddWithValue(idName, entry.Key);
                command.Parameters.AddWithValue(keyName, EventSortText.KeyBytes(entry.Value ?? "") ?? Array.Empty<byte>());
                index += 1;
            }

            return "WITH src(id, k) AS (VALUES " + string.Join(", ", rows) + ") ";
        }

        private static string BuildOrderClause(EventSortColumn column, bool ascending, bool hasSourceNames)
        {
            var direction = ascending ? "ASC" : "DESC";
            const string tieBreak = "e.time DESC, e.correlation_id ASC, e.id DESC";
            return column switch
            {
                EventSortColumn.Time => $"ORDER BY e.time {direction}, e.correlation_id ASC, e.id {direction}",
                EventSortColumn.Severity => OrderBy("e.severity"),
                EventSortColumn.Active => OrderBy("e.active"),
                EventSortColumn.Acked => OrderBy("e.acked"),
                EventSortColumn.Retain => OrderBy("e.retain"),
                EventSortColumn.Duration => OrderBy("NULLIF(e.duration_ms, 0)"),
                EventSortColumn.State => OrderBy("e.state_key"),
                EventSortColumn.Class => OrderBy("e.class_key"),
                EventSortColumn.Type => OrderBy("e.type_key"),
                EventSortColumn.Message => OrderBy("e.message_key"),
                EventSortColumn.Source when hasSourceNames => OrderBy("src.k"),
                _ => $"ORDER BY {tieBreak}"
            };

            // Empty cells always come last, equal values keep the newest event first.
            string OrderBy(string expression)
            {
                return $"ORDER BY ({expression} IS NULL), {expression} {direction}, {tieBreak}";
            }
        }

        private static string BuildWhereClause(
            EventLogQuery query,
            NpgsqlCommand command,
            bool includeState = true)
        {
            var parts = new List<string>
            {
                "e.time >= @fromUtc",
                "e.time < @toUtc",
                "e.correlation_id <> ''",
                "e.alarm_state <> ''"
            };
            command.Parameters.AddWithValue("@fromUtc", ToUtc(query.FromUtc));
            command.Parameters.AddWithValue("@toUtc", ToUtc(query.ToUtc));

            if (!string.IsNullOrWhiteSpace(query.Component))
            {
                parts.Add("e.component = @component");
                command.Parameters.AddWithValue("@component", query.Component.Trim());
            }
            else
            {
                parts.Add("e.component IN (@alarmComponent, @systemComponent)");
                command.Parameters.AddWithValue("@alarmComponent", EventLogConstants.AlarmComponent);
                command.Parameters.AddWithValue("@systemComponent", EventLogConstants.SystemAlarmComponent);
            }

            var sourceIds = SourceIds(query);
            if (sourceIds.Count > 0)
            {
                // System alarms belong to the source they report on.
                parts.Add("(e.source_id = ANY(@sourceIds) "
                    + "OR (e.component = @systemComponentFilter AND e.affected_source_id = ANY(@sourceIds)))");
                command.Parameters.AddWithValue("@sourceIds", sourceIds.ToArray());
                command.Parameters.AddWithValue("@systemComponentFilter", EventLogConstants.SystemAlarmComponent);
            }

            if (includeState && !string.IsNullOrWhiteSpace(query.AlarmState))
            {
                var tokens = AlarmStateValues.TokensForFilter(query.AlarmState);
                parts.Add(tokens.Length == 0
                    ? "false"
                    : $"e.alarm_state IN ({AddTextList(command, "@filterState", tokens)})");
            }

            if (query.ClassLabels is { Count: > 0 })
            {
                parts.Add($"{ClassLabelSql} = ANY(@classLabels)");
                command.Parameters.AddWithValue("@classLabels", query.ClassLabels.ToArray());
            }

            var search = EventTextFilter.Parse(query.TextSearch);
            if (!search.IsEmpty)
            {
                // The columns the alarm table shows: message, class, state label and limit state.
                var matches = new List<string>
                {
                    "e.message ILIKE @search",
                    "e.alarm_class_symbol ILIKE @search",
                    "e.alarm_class_name ILIKE @search",
                    // The regex only runs on the alarms that have a limit state; most have none.
                    "(e.props LIKE '%\"LimitState\"%' AND substring(e.props from '\"LimitState\"\\s*:\\s*\"([^\"]*)\"') ILIKE @search)"
                };
                command.Parameters.AddWithValue("@search", search.LikePattern);
                var states = search.MatchingAlarmStates();
                if (states.Count > 0)
                {
                    matches.Add($"e.alarm_state IN ({AddTextList(command, "@searchState", states)})");
                }

                parts.Add("(" + string.Join(" OR ", matches) + ")");
            }

            return string.Join(" AND ", parts);
        }

        private static string BuildSeedWhereClause(int sourceId, NpgsqlCommand command)
        {
            var nowUtc = DateTime.UtcNow;
            command.Parameters.AddWithValue("@seedFrom", nowUtc.AddDays(-EventLogConstants.AlarmSeedRangeDays));
            command.Parameters.AddWithValue("@seedTo", nowUtc.AddMinutes(1));
            command.Parameters.AddWithValue("@seedSource", sourceId);
            command.Parameters.AddWithValue("@seedComponent", EventLogConstants.AlarmComponent);
            return "e.time >= @seedFrom AND e.time < @seedTo AND e.source_id = @seedSource "
                + "AND e.component = @seedComponent AND e.correlation_id <> ''";
        }

        private static string AddTextList(NpgsqlCommand command, string prefix, IReadOnlyList<string> values)
        {
            var names = new List<string>(values.Count);
            for (var index = 0; index < values.Count; index += 1)
            {
                var name = prefix + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, values[index]);
            }

            return string.Join(", ", names);
        }

        private static EventLogQuery CopyWithoutText(EventLogQuery query)
        {
            return new EventLogQuery
            {
                FromUtc = query.FromUtc,
                ToUtc = query.ToUtc,
                Component = query.Component,
                SourceId = query.SourceId,
                SourceIds = query.SourceIds,
                AlarmState = query.AlarmState,
                ClassLabels = query.ClassLabels
            };
        }

        private static List<int> SourceIds(EventLogQuery query)
        {
            var ids = new List<int>();
            if (query.SourceId is > 0)
            {
                ids.Add(query.SourceId.Value);
            }

            if (query.SourceIds != null)
            {
                foreach (var id in query.SourceIds)
                {
                    if (id > 0 && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        private static long ExecuteLong(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(command.Cancel);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }

        private static List<EventLogRecord> ReadRecords(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            var items = new List<EventLogRecord>();
            using var registration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadRecord(reader, 0));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return items;
        }

        private static EventLogRecord ReadRecord(NpgsqlDataReader reader, int first)
        {
            var i = first;
            return new EventLogRecord
            {
                Measurement = EventLogConstants.ProcessMeasurementName,
                TimeUtc = DateTime.SpecifyKind(reader.GetDateTime(i++), DateTimeKind.Utc),
                Level = reader.GetString(i++),
                Kind = reader.GetString(i++),
                Component = reader.GetString(i++),
                SourceId = reader.GetInt32(i++),
                AffectedSourceId = reader.GetInt32(i++),
                SourceType = reader.GetString(i++),
                Message = reader.GetString(i++),
                Template = reader.GetString(i++),
                Exception = reader.GetString(i++),
                Props = reader.GetString(i++),
                CorrelationId = reader.GetString(i++),
                AlarmState = reader.GetString(i++),
                Severity = reader.IsDBNull(i) ? null : reader.GetInt32(i),
                Active = reader.IsDBNull(++i) ? null : reader.GetBoolean(i),
                Acked = reader.IsDBNull(++i) ? null : reader.GetBoolean(i),
                Retain = reader.IsDBNull(++i) ? null : reader.GetBoolean(i),
                ActiveSinceUtc = reader.IsDBNull(++i) ? null : DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc),
                EventType = reader.IsDBNull(++i) ? null : reader.GetString(i),
                EventTypeName = reader.IsDBNull(++i) ? null : reader.GetString(i),
                AlarmClassSymbol = reader.IsDBNull(++i) ? null : reader.GetString(i),
                AlarmClassName = reader.IsDBNull(++i) ? null : reader.GetString(i),
                TextColor = reader.IsDBNull(++i) ? null : (uint)reader.GetInt64(i),
                BackColor = reader.IsDBNull(++i) ? null : (uint)reader.GetInt64(i),
                Flashing = reader.IsDBNull(++i) ? null : reader.GetBoolean(i),
                DurationMs = reader.IsDBNull(++i) ? null : reader.GetInt64(i)
            };
        }

        private static DateTime ToUtc(DateTime time)
        {
            return time.Kind switch
            {
                DateTimeKind.Utc => time,
                DateTimeKind.Local => time.ToUniversalTime(),
                _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
            };
        }

        private static string ToCursor(DateTime time)
        {
            return ToUtc(time).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", DateTimeFormatInfo.InvariantInfo) + "Z";
        }
        #endregion

        #region retention
        /// <summary>
        /// Retention of the hypertable in seconds, 0 for forever (no retention policy).
        /// </summary>
        public long ReadRetentionSeconds()
        {
            using var connection = _dataSource.OpenConnection();
            return ReadRetentionSeconds(connection, null);
        }

        public long SetRetentionSeconds(long everySeconds)
        {
            if (everySeconds < 0 || (everySeconds > 0 && everySeconds < RetentionMinSeconds))
            {
                throw new ArgumentException("Retention must be forever, or at least 1 hour.");
            }

            if (everySeconds > RetentionMaxSeconds)
            {
                throw new ArgumentException("Retention cannot be longer than 10 years.");
            }

            using var connection = _dataSource.OpenConnection();
            using (var transaction = connection.BeginTransaction())
            {
                using (var remove = connection.CreateCommand())
                {
                    remove.Transaction = transaction;
                    remove.CommandText = $"SELECT remove_retention_policy('{TableName}', if_exists => true)";
                    remove.ExecuteNonQuery();
                }

                if (everySeconds > 0)
                {
                    using var add = connection.CreateCommand();
                    add.Transaction = transaction;
                    add.CommandText = $"SELECT add_retention_policy('{TableName}', make_interval(secs => @seconds))";
                    add.Parameters.AddWithValue("@seconds", (double)everySeconds);
                    add.ExecuteNonQuery();
                }

                var confirmed = ReadRetentionSeconds(connection, transaction);
                if (confirmed != everySeconds)
                {
                    throw new InvalidOperationException("TimescaleDB did not confirm the new retention.");
                }

                transaction.Commit();
            }

            DropExpiredChunks(connection, everySeconds);
            return everySeconds;
        }

        /// <summary>
        /// Drops the chunks older than the retention. The Timescale job does the same, but the
        /// background workers are capped on the small boards, so the service enforces it as well.
        /// </summary>
        public int ApplyRetention()
        {
            using var connection = _dataSource.OpenConnection();
            var seconds = ReadRetentionSeconds(connection, null);
            return seconds > 0 ? DropExpiredChunks(connection, seconds) : 0;
        }

        private int DropExpiredChunks(NpgsqlConnection connection, long seconds)
        {
            if (seconds <= 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM drop_chunks('{TableName}', older_than => make_interval(secs => @seconds))";
            command.Parameters.AddWithValue("@seconds", (double)seconds);
            var dropped = Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            if (dropped > 0)
            {
                InvalidateListCache();
            }

            // Open occurrences whose newest point the retention removed (also by the Timescale job) go too.
            using var prune = connection.CreateCommand();
            prune.CommandText = $"DELETE FROM {OpenTableName} WHERE time < now() - make_interval(secs => @seconds)";
            prune.Parameters.AddWithValue("@seconds", (double)seconds);
            prune.ExecuteNonQuery();
            PruneCounts(connection, seconds);
            return dropped;
        }

        private static long ReadRetentionSeconds(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT extract(epoch FROM (config->>'drop_after')::interval)::bigint "
                + "FROM timescaledb_information.jobs "
                + "WHERE proc_name = 'policy_retention' AND hypertable_name = @table LIMIT 1";
            command.Parameters.AddWithValue("@table", TableName);
            var raw = command.ExecuteScalar();
            return raw == null || raw is DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        #endregion
    }
}
