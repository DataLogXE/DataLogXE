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

using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Serilog;

namespace DataLogLib.Services.EventLog
{
    public sealed class AlarmCountBackfillStep
    {
        // How far back the rows from before the counting are counted; null when there is nothing to count yet
        public DateTime? Before { get; init; }
        public bool Done { get; init; }
        public TimeSpan Took { get; init; }
    }

    /// <summary>
    /// The hourly counts of every alarm in every state (processeventcount1h, schema 17), from which the statistics of
    /// the alarm details sum the counts of one alarm instead of reading its rows. processevent_condition_hash names the
    /// alarm of a row. A write counts its points in its own transaction once live_from_id is set, which the first write
    /// that counts sets to the next id; the rows below it are counted by the backfill, one window of hours per step from
    /// the newest back to the oldest, and the window and the new backfill_before are written in one transaction. So every
    /// row is counted once, also across a crash. Both insert their counts ordered by the key, so they cannot deadlock.
    /// </summary>
    public sealed partial class ProcessEventStore
    {
        private const string CountTableName = "processeventcount1h";
        private const string CountStateTableName = "processeventcountstate";

        private const string CountInsert = $"INSERT INTO {CountTableName} (source_id, condition_hash, alarm_state, hour, count) ";
        private const string CountConflict = " GROUP BY 1, 2, 3, 4 ORDER BY 1, 2, 3, 4 "
            + "ON CONFLICT (source_id, condition_hash, alarm_state, hour) "
            + $"DO UPDATE SET count = {CountTableName}.count + EXCLUDED.count";

        // The next id of processevent; null for a sequence that was never used gives 1
        private const string NextIdSql =
            $"COALESCE(pg_sequence_last_value(pg_get_serial_sequence('{TableName}', 'id')::regclass), 0) + 1";

        // The hours of the counts are UTC hours, whatever the time zone of the session (date_bin from this origin)
        private static readonly DateTime HourEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Set once schema 17 is there: the counts table exists and the writes count from live_from_id on.
        private volatile bool _countsReady;
        private volatile bool _liveFromSet;

        // The tests write rows as if they came before the counting
        internal bool CountOnWrite { get; set; } = true;

        private bool CountsReady(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            if (_countsReady)
            {
                return true;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT to_regclass('{CountTableName}') IS NOT NULL";
            _countsReady = command.ExecuteScalar() is true;
            return _countsReady;
        }

        private static void SetLiveFrom(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {CountStateTableName} SET live_from_id = {NextIdSql} WHERE live_from_id IS NULL";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Before the points of a write: true when the write counts them, after it set live_from_id if it was not set
        /// yet. It is set before the points take their ids, so they are all at or above it.
        /// </summary>
        private bool BeginCounting(NpgsqlConnection connection, NpgsqlTransaction transaction, out bool settingLiveFrom)
        {
            settingLiveFrom = false;
            if (!CountOnWrite || !CountsReady(connection, transaction))
            {
                return false;
            }

            if (!_liveFromSet)
            {
                SetLiveFrom(connection, transaction);
                settingLiveFrom = true;
            }

            return true;
        }

        /// <summary>
        /// Counts the points of a write in its transaction. A failed count loses only the counts of these points: it is
        /// rolled back to a savepoint, so the points are still stored.
        /// </summary>
        private static void AddCounts(NpgsqlConnection connection, NpgsqlTransaction transaction, List<EventLogRecord> records)
        {
            if (records.Count == 0)
            {
                return;
            }

            transaction.Save("counts");
            try
            {
                InsertCounts(connection, transaction, records);
                transaction.Release("counts");
            }
            catch (PostgresException ex)
            {
                transaction.Rollback("counts");
                Log.Warning(ex, "Alarm counts of {Count} points failed; the points are stored without them", records.Count);
            }
        }

        private static void InsertCounts(NpgsqlConnection connection, NpgsqlTransaction transaction, List<EventLogRecord> records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = CountInsert
                + "SELECT b.source_id, processevent_condition_hash(b.component, b.affected_source_id, b.event_type, b.props, b.message), "
                + "b.alarm_state, date_bin('1 hour', b.time, @epoch), count(*) "
                + "FROM unnest(@cSources, @cComponents, @cAffected, @cTypes, @cProps, @cMessages, @cStates, @cTimes) "
                + "AS b(source_id, component, affected_source_id, event_type, props, message, alarm_state, time)"
                + CountConflict;
            command.Parameters.AddWithValue("@cSources", records.Select(record => record.SourceId).ToArray());
            command.Parameters.AddWithValue("@cComponents", records.Select(record => record.Component ?? "").ToArray());
            command.Parameters.AddWithValue("@cAffected", records.Select(record => record.AffectedSourceId).ToArray());
            command.Parameters.Add(new NpgsqlParameter("@cTypes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = records.Select(record => record.EventType).ToArray()
            });
            command.Parameters.AddWithValue("@cProps", records.Select(record => record.Props ?? "").ToArray());
            command.Parameters.AddWithValue("@cMessages", records.Select(record => record.Message ?? "").ToArray());
            command.Parameters.AddWithValue("@cStates", records.Select(record => record.AlarmState).ToArray());
            command.Parameters.AddWithValue("@cTimes", records.Select(record => ToUtc(record.TimeUtc)).ToArray());
            command.Parameters.AddWithValue("@epoch", HourEpoch);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// How often the alarm of the reference point entered the state, per bucket between the edges (full hours).
        /// CompleteFromUtc is set while the backfill has not reached the first edge yet, OldestUtc is the time of the oldest
        /// stored event.
        /// </summary>
        public AlarmCountResult QueryAlarmCounts(AlarmCountQuery query, CancellationToken cancellationToken = default)
        {
            var edges = (query.Edges ?? []).Select(ToUtc).ToArray();
            var result = new AlarmCountResult { Counts = new long[Math.Max(0, edges.Length - 1)] };
            var reference = query.Reference;
            if (reference == null || edges.Length < 2 || string.IsNullOrEmpty(query.AlarmState))
            {
                return result;
            }

            using var connection = _dataSource.OpenConnection();
            using (var command = CreateReadCommand(connection))
            {
                command.CommandText = "SELECT width_bucket(c.hour, @edges), SUM(c.count)::bigint "
                    + $"FROM {CountTableName} c WHERE c.source_id = @source "
                    + "AND c.condition_hash = processevent_condition_hash(@component, @affected, @eventType, @props, @message) "
                    + "AND c.alarm_state = @state AND c.hour >= @fromUtc AND c.hour < @toUtc GROUP BY 1";
                command.Parameters.AddWithValue("@edges", edges);
                command.Parameters.AddWithValue("@source", reference.SourceId);
                command.Parameters.AddWithValue("@component", reference.Component ?? "");
                // As the write stores it
                command.Parameters.AddWithValue("@affected", reference.AffectedSourceId > 0
                    ? reference.AffectedSourceId
                    : EventLogSourceFilter.TryParseAffectedSourceIdFromProps(reference.Props));
                command.Parameters.Add(new NpgsqlParameter("@eventType", NpgsqlDbType.Text) { Value = (object)reference.EventType ?? DBNull.Value });
                command.Parameters.AddWithValue("@props", reference.Props ?? "");
                command.Parameters.AddWithValue("@message", reference.Message ?? "");
                command.Parameters.AddWithValue("@state", query.AlarmState);
                command.Parameters.AddWithValue("@fromUtc", edges[0]);
                command.Parameters.AddWithValue("@toUtc", edges[^1]);
                using var registration = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // width_bucket gives 1 for the first bucket; the range ends before the last edge
                    var bucket = reader.GetInt32(0) - 1;
                    if (bucket >= 0 && bucket < result.Counts.Length)
                    {
                        result.Counts[bucket] = reader.GetInt64(1);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var state = CreateReadCommand(connection))
            {
                state.CommandText = $"SELECT live_from_id IS NOT NULL, backfill_before FROM {CountStateTableName}";
                using var reader = state.ExecuteReader();
                // Before the first count and before the backfill nothing older than now is complete
                DateTime? completeFrom = DateTime.UtcNow;
                if (reader.Read() && reader.GetBoolean(0) && !reader.IsDBNull(1))
                {
                    completeFrom = ToUtc(reader.GetDateTime(1));
                }

                result.CompleteFromUtc = completeFrom > edges[0] ? completeFrom : null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            result.OldestUtc = EdgeEventTime(connection, null, newest: false);
            return result;
        }

        /// <summary>True once schema 17 is there.</summary>
        public bool CountsAvailable()
        {
            using var connection = _dataSource.OpenConnection();
            return CountsReady(connection, null);
        }

        /// <summary>
        /// Sets live_from_id unless the first write that counts did, so the backfill also starts while no alarm comes.
        /// Call it some time after schema 17 appeared, when every write that began before that has ended.
        /// </summary>
        public void PrepareCountBackfill()
        {
            using var connection = _dataSource.OpenConnection();
            SetLiveFrom(connection, null);
        }

        /// <summary>The time of the oldest process event; null for none.</summary>
        public DateTime? ReadOldestEventTime()
        {
            using var connection = _dataSource.OpenConnection();
            return EdgeEventTime(connection, null, newest: false);
        }

        // Growing ranges from the oldest or newest chunk in which the edge of the events is searched, then a month at a
        // time
        private static readonly TimeSpan[] EdgeSearchWindows = [TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];

        /// <summary>
        /// The time of the oldest or newest process event; null for none. A min(time) or max(time) without a range
        /// plans every daily chunk of processevent and locks each of them with its indexes, so the edge is searched in
        /// ranges from the oldest or newest chunk on.
        /// </summary>
        private static DateTime? EdgeEventTime(NpgsqlConnection connection, NpgsqlTransaction transaction, bool newest)
        {
            DateTime first, last;
            using (var chunks = connection.CreateCommand())
            {
                chunks.Transaction = transaction;
                chunks.CommandText = "SELECT min(range_start), max(range_end) FROM timescaledb_information.chunks WHERE hypertable_name = @table";
                chunks.Parameters.AddWithValue("table", TableName);
                using var reader = chunks.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    return null;
                }
                first = reader.GetDateTime(0);
                last = reader.GetDateTime(1);
            }

            IEnumerable<(DateTime From, DateTime To)> Ranges()
            {
                var reached = TimeSpan.Zero;
                foreach (var window in EdgeSearchWindows)
                {
                    yield return newest ? (last - window, last - reached) : (first + reached, first + window);
                    reached = window;
                }
                var step = EdgeSearchWindows[^1];
                for (; first + reached < last; reached += step)
                {
                    yield return newest ? (last - reached - step, last - reached) : (first + reached, first + reached + step);
                }
            }

            foreach (var (from, to) in Ranges())
            {
                using var command = CreateReadCommand(connection);
                command.Transaction = transaction;
                command.CommandText = $"SELECT {(newest ? "max" : "min")}(time) FROM {TableName} WHERE time >= @from AND time < @to";
                command.Parameters.AddWithValue("from", from);
                command.Parameters.AddWithValue("to", to);
                if (command.ExecuteScalar() is DateTime time)
                {
                    return ToUtc(time);
                }
            }
            return null;
        }

        /// <summary>
        /// Counts the rows below live_from_id in the window of the given hours before backfill_before and moves
        /// backfill_before back to its start, not past the hour of the oldest event. The first step starts after the
        /// hour of the newest event.
        /// </summary>
        public AlarmCountBackfillStep BackfillCountsStep(int hours, DateTime? oldestUtc)
        {
            var watch = Stopwatch.StartNew();
            using var connection = _dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            long liveFrom;
            DateTime? before;
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = $"SELECT live_from_id, backfill_before FROM {CountStateTableName} FOR UPDATE";
                using var reader = state.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                {
                    return new AlarmCountBackfillStep { Took = watch.Elapsed };
                }

                liveFrom = reader.GetInt64(0);
                before = reader.IsDBNull(1) ? null : ToUtc(reader.GetDateTime(1));
            }

            var stop = oldestUtc.HasValue ? FloorHour(oldestUtc.Value) : (DateTime?)null;
            if (before == null)
            {
                before = EdgeEventTime(connection, transaction, newest: true) is DateTime time ? FloorHour(time).AddHours(1) : stop;
            }

            if (stop == null || before <= stop)
            {
                WriteBackfillBefore(connection, transaction, before ?? DateTime.UtcNow);
                transaction.Commit();
                return new AlarmCountBackfillStep { Before = before, Done = true, Took = watch.Elapsed };
            }

            var to = before.Value;
            var from = to.AddHours(-Math.Max(1, hours));
            if (from < stop)
            {
                from = stop.Value;
            }

            using (var count = CreateReadCommand(connection))
            {
                count.Transaction = transaction;
                // The bounds as parameters, so only the chunks of the window are planned
                count.CommandText = CountInsert
                    + "SELECT e.source_id, processevent_condition_hash(e.component, e.affected_source_id, e.event_type, e.props, e.message), "
                    + "e.alarm_state, date_bin('1 hour', e.time, @epoch), count(*) "
                    + $"FROM {TableName} e WHERE e.time >= @from AND e.time < @to AND e.id < @liveFrom "
                    + "AND e.correlation_id <> '' AND e.alarm_state <> ''"
                    + CountConflict;
                count.Parameters.AddWithValue("@from", from);
                count.Parameters.AddWithValue("@to", to);
                count.Parameters.AddWithValue("@liveFrom", liveFrom);
                count.Parameters.AddWithValue("@epoch", HourEpoch);
                count.ExecuteNonQuery();
            }

            WriteBackfillBefore(connection, transaction, from);
            transaction.Commit();
            return new AlarmCountBackfillStep { Before = from, Done = from <= stop, Took = watch.Elapsed };
        }

        private static void WriteBackfillBefore(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime before)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {CountStateTableName} SET backfill_before = @before";
            command.Parameters.AddWithValue("@before", before);
            command.ExecuteNonQuery();
        }

        // The counts of the hours the retention removed; the hour that holds the limit stays
        private void PruneCounts(NpgsqlConnection connection, long seconds)
        {
            if (!CountsReady(connection, null))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {CountTableName} WHERE hour < now() - make_interval(secs => @seconds) - INTERVAL '1 hour'";
            command.Parameters.AddWithValue("@seconds", (double)seconds);
            command.ExecuteNonQuery();
        }

        private static DateTime FloorHour(DateTime time)
        {
            return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        }
    }
}
