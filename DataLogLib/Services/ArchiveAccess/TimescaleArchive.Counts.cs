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

using DataLogLib.Models;
using Npgsql;
using NpgsqlTypes;
using Serilog;
using System.Data;
using System.Diagnostics;
using System.Globalization;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// The number of raw values per tag and minute (archiverawcount1m) and per tag and hour (archiverawcount1h), from
    /// which the number of raw values of a chart range is summed instead of counted in archiveraw (schema 12).
    ///
    /// The counters hold exactly the raw values of [From, Until) (ARCHIVE_COUNT_FROM, ARCHIVE_COUNT_UNTIL in parameter,
    /// full minutes); an hour row exists only for the hours entirely within it. The roll-up counts the closed minutes
    /// after Until once a minute and moves Until, the backfill counts the minutes before From and moves From back.
    /// Late values (From &lt;= time &lt; Until) are counted in the transaction that writes them. Every write of raw
    /// values holds the count lock shared and every step that moves the range holds it exclusively, in the transaction
    /// that writes the counters and the range, so a raw value is counted exactly once, also across a crash. A step
    /// counts its window without the lock and holds it only to count the minutes written meanwhile once more (see
    /// RunCountStep). Outside of the range the raw values are counted.
    ///
    /// Counting per write would update the row of every active tag about once a second; the roll-up inserts it once a
    /// minute, which spares the SD card of a small device the WAL and the dead rows.
    /// </summary>
    public partial class TimescaleArchive
    {
        internal const string MinuteCountTable = "archiverawcount1m";
        internal const string HourCountTable = "archiverawcount1h";
        internal const string CountFromKey = "ARCHIVE_COUNT_FROM";
        internal const string CountUntilKey = "ARCHIVE_COUNT_UNTIL";

        // Advisory lock of the counted range ('DLXC'); the schema upgrade uses 'DLXE'
        private const long CountLockKey = 0x444C5843;
        private const int CountStepTimeoutSeconds = 10 * 60;
        // Minutes counted by one roll-up step at most (after a downtime)
        private static readonly TimeSpan MaxRollUpStep = TimeSpan.FromMinutes(10);
        // Within this time after the retention cutoff a chunk of the raw values or of a counter table may be dropped
        // (the hour chunks are 7 days long), so a range there checks the oldest chunks
        private static readonly TimeSpan RetentionEdge = TimeSpan.FromDays(8);
        private static readonly TimeSpan RetentionCacheAge = TimeSpan.FromMinutes(10);

        // Tags written by this process that may hold raw values at or after Until: archive id -> newest time (UTC)
        private readonly object _pendingLock = new();
        private readonly Dictionary<int, DateTime> _pending = [];
        private bool _pendingSeeded;

        // The window of the running count step and the minutes (tag, minute) written into it since it was set
        private const string StepTable = "archive_count_step";
        private readonly object _stepLock = new();
        private (DateTime From, DateTime Until)? _stepWindow;
        private readonly HashSet<(int TagId, DateTime Time)> _stepWrites = [];

        /// <summary>Called after a count step counted its window, before it takes the lock again; the tests write there.</summary>
        internal Action StepCounted { get; set; }

        // Minutes written into the window of a step that are recounted under the lock at most; with more the window
        // is counted again without it, at most this often
        internal const int MaxRecountMinutes = 200;
        private const int MaxCountAttempts = 3;

        private readonly object _retentionLock = new();
        private long _retentionSeconds;
        private DateTime _retentionReadAt = DateTime.MinValue;

        internal readonly record struct CountRange(DateTime From, DateTime Until);

        #region write
        /// <summary>
        /// Takes the count lock shared for the write transaction and returns the counted range (null before schema 12).
        /// Call before the raw values are written: the tags are remembered for the roll-up, the minutes written into
        /// the window of a running count step for that step.
        /// </summary>
        private CountRange? BeginRawWrite(NpgsqlConnection connection, NpgsqlTransaction transaction, List<(int TagId, ArchiveValue Value)> raw)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = WriteTimeoutSeconds;
                command.CommandText = "SELECT pg_advisory_xact_lock_shared(@key)";
                command.Parameters.AddWithValue("key", CountLockKey);
                command.ExecuteNonQuery();
            }

            // After the lock: a step that sets its window waits for this write, or this write sees the window
            lock (_pendingLock)
            {
                foreach (var (tagId, value) in raw)
                {
                    var time = ToDbTime(value.Time);
                    if (!_pending.TryGetValue(tagId, out var newest) || time > newest)
                    {
                        _pending[tagId] = time;
                    }
                    if (_stepWindow is { } window && time >= window.From && time < window.Until)
                    {
                        _stepWrites.Add((tagId, FloorMinute(time)));
                    }
                }
            }
            return ReadCountRange(connection, transaction);
        }

        /// <summary>
        /// Counts the raw values written into the counted range (late values), in the transaction that writes them.
        /// Values older than the retention are left out: their chunk is dropped soon and must not bring back a
        /// counter chunk that was dropped already.
        /// </summary>
        private void CountLateValues(NpgsqlConnection connection, NpgsqlTransaction transaction,
            List<(int TagId, ArchiveValue Value)> raw, CountRange range)
        {
            var retention = RetentionSeconds(connection, transaction);
            var cutoff = retention > 0 ? UtcNow().AddSeconds(-retention) : DateTime.MinValue;
            var minutes = raw
                .Select(r => (r.TagId, Time: ToDbTime(r.Value.Time)))
                .Where(r => r.Time >= range.From && r.Time < range.Until && r.Time >= cutoff)
                .GroupBy(r => (r.TagId, Time: FloorMinute(r.Time)))
                .Select(g => (g.Key.TagId, g.Key.Time, Count: g.Count()))
                .ToList();
            if (minutes.Count == 0)
            {
                return;
            }

            AddCounts(connection, transaction, MinuteCountTable, minutes);
            var hours = minutes
                .Where(m => FloorHour(m.Time) >= range.From && FloorHour(m.Time).AddHours(1) <= range.Until)
                .GroupBy(m => (m.TagId, Time: FloorHour(m.Time)))
                .Select(g => (g.Key.TagId, g.Key.Time, Count: g.Sum(m => m.Count)))
                .ToList();
            if (hours.Count > 0)
            {
                AddCounts(connection, transaction, HourCountTable, hours);
            }
            Log.Debug("Counted {Count} late raw values", minutes.Sum(m => m.Count));
        }

        /// <summary>
        /// Adds the counts to the rows of the table, new rows for the others. An UPDATE and an INSERT instead of
        /// ON CONFLICT, which TimescaleDB 2.17 does not handle reliably in compressed chunks (see WithoutExisting); only
        /// the write of the raw values adds to the rows, under the count lock, so no row appears in between.
        /// </summary>
        private static void AddCounts(NpgsqlConnection connection, NpgsqlTransaction transaction, string table,
            List<(int TagId, DateTime Time, int Count)> counts)
        {
            HashSet<(int, DateTime)> updated = [];
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandTimeout = WriteTimeoutSeconds;
                // The time range and the tags as parameters as well: from the arrays alone the planner takes every
                // chunk, and an UPDATE of a compressed chunk decompresses it (on the Pi the insert of late values
                // then timed out again and again and the insert buffer dropped the new values)
                update.CommandText =
                    $"UPDATE {table} c SET count = c.count + d.count "
                    + "FROM unnest(@times, @tagIds, @counts) AS d(time, tag_id, count) "
                    + "WHERE c.tag_id = d.tag_id AND c.time = d.time "
                    + "AND c.time >= @from AND c.time <= @to AND c.tag_id = ANY(@tagSet) RETURNING c.tag_id, c.time";
                AddCountParameters(update.Parameters, counts);
                update.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = counts.Min(c => c.Time) });
                update.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = counts.Max(c => c.Time) });
                update.Parameters.Add(new NpgsqlParameter("tagSet", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts.Select(c => c.TagId).Distinct().ToArray() });
                using var reader = update.ExecuteReader();
                while (reader.Read())
                {
                    updated.Add((reader.GetInt32(0), reader.GetDateTime(1)));
                }
            }

            var inserts = counts.Where(c => !updated.Contains((c.TagId, c.Time))).ToList();
            if (inserts.Count == 0)
            {
                return;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandTimeout = WriteTimeoutSeconds;
            insert.CommandText = $"INSERT INTO {table} (time, tag_id, count) SELECT * FROM unnest(@times, @tagIds, @counts)";
            AddCountParameters(insert.Parameters, inserts);
            insert.ExecuteNonQuery();
        }

        private static void AddCountParameters(NpgsqlParameterCollection parameters, List<(int TagId, DateTime Time, int Count)> counts)
        {
            parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = counts.Select(c => c.Time).ToArray() });
            parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts.Select(c => c.TagId).ToArray() });
            parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts.Select(c => c.Count).ToArray() });
        }
        #endregion

        #region roll-up and backfill
        /// <summary>
        /// Counts the raw values of the closed minutes up to <paramref name="until"/> (UTC, a full minute) into the
        /// counters and moves Until there, in steps of at most 10 minutes. False before schema 12.
        /// </summary>
        internal bool RollUpCounts(DateTime until) => RollUpCounts(until, out _);

        /// <inheritdoc cref="RollUpCounts(DateTime)"/>
        /// <param name="lockTime">The longest time a step held the count lock exclusively (and the writes back)</param>
        internal bool RollUpCounts(DateTime until, out TimeSpan lockTime)
        {
            until = FloorMinute(ToDbTime(until));
            lockTime = TimeSpan.Zero;
            while (true)
            {
                var step = RunCountStep(
                    range => range.Until >= until ? null : (range.Until, Min(until, range.Until + MaxRollUpStep)),
                    rollUp: true);
                if (step.LockTime > lockTime)
                {
                    lockTime = step.LockTime;
                }
                if (step.Range == null)
                {
                    return false;
                }
                if (!step.Counted)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Counts the raw values of the minutes before From, at most <paramref name="size"/> and not before
        /// <paramref name="target"/>, and moves From back there. Returns the new From, null before schema 12.
        /// </summary>
        internal DateTime? BackfillCountsStep(DateTime target, TimeSpan size) => BackfillCountsStep(target, size, out _);

        /// <inheritdoc cref="BackfillCountsStep(DateTime, TimeSpan)"/>
        /// <param name="lockTime">How long the step held the count lock exclusively (and the writes back)</param>
        internal DateTime? BackfillCountsStep(DateTime target, TimeSpan size, out TimeSpan lockTime)
        {
            target = FloorMinute(ToDbTime(target));
            var step = RunCountStep(
                range => range.From <= target ? null : (Max(target, FloorMinute(range.From - Max(size, TimeSpan.FromMinutes(1)))), range.From),
                rollUp: false);
            lockTime = step.LockTime;
            return step.Range == null ? null : step.Counted ? step.Start : step.Range.Value.From;
        }

        /// <summary>
        /// Counts the raw values of a window next to the counted range and adds it to the range: Until moves to the end
        /// of a roll-up window, From to the start of a backfill window. The raw values are counted without the count
        /// lock, so the writes go on: the window is set under the lock, from then on the writes report the minutes
        /// they write into the window, and under the lock again these minutes are counted once more before the
        /// counts, the hours and the range are committed together. Until then the counts are in a temporary table,
        /// so an interrupted step leaves nothing behind.
        /// </summary>
        private (CountRange? Range, bool Counted, DateTime Start, TimeSpan LockTime) RunCountStep(
            Func<CountRange, (DateTime Start, DateTime End)?> window, bool rollUp)
        {
            lock (_stepLock)
            {
                using var connection = _dataSource.OpenConnection();
                CountRange range;
                DateTime start;
                DateTime end;
                int[] tagIds = [];
                using (var transaction = connection.BeginTransaction())
                {
                    var locked = LockCountRange(connection, transaction);
                    if (locked == null)
                    {
                        return (null, false, default, default);
                    }
                    range = locked.Value;
                    var next = window(range);
                    if (next == null)
                    {
                        return (range, false, default, default);
                    }
                    (start, end) = next.Value;

                    if (rollUp)
                    {
                        SeedPending(connection, transaction, range.Until);
                        lock (_pendingLock)
                        {
                            tagIds = [.. _pending.Where(p => p.Value >= range.Until).Select(p => p.Key)];
                        }
                    }
                    lock (_pendingLock)
                    {
                        _stepWindow = (start, end);
                        _stepWrites.Clear();
                    }
                    transaction.Commit();
                }

                try
                {
                    ExecuteCountStep(connection, null,
                        $"CREATE TEMP TABLE IF NOT EXISTS {StepTable} (time timestamptz NOT NULL, tag_id integer NOT NULL, count integer NOT NULL)");
                    for (var attempt = 1; ; attempt++)
                    {
                        ExecuteCountStep(connection, null, $"TRUNCATE {StepTable}");
                        CountWindow(connection, rollUp, tagIds, start, end);
                        StepCounted?.Invoke();

                        int written;
                        lock (_pendingLock)
                        {
                            written = _stepWrites.Count;
                        }
                        if (written <= MaxRecountMinutes || attempt >= MaxCountAttempts)
                        {
                            break;
                        }

                        // Many minutes were written into the window meanwhile (the data sources catch up after a
                        // start): the window is counted again without the lock instead of recounting them under it.
                        // The lock waits for the writes that reported them, so the next count sees them.
                        using var restart = connection.BeginTransaction();
                        LockCountRange(connection, restart);
                        lock (_pendingLock)
                        {
                            _stepWrites.Clear();
                            if (rollUp)
                            {
                                tagIds = [.. _pending.Where(p => p.Value >= range.Until).Select(p => p.Key)];
                            }
                        }
                        restart.Commit();
                    }

                    var watch = Stopwatch.StartNew();
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (LockCountRange(connection, transaction) != range)
                        {
                            // Only this process moves the range, one step at a time
                            throw new InvalidOperationException("The counted range of the archive changed during a count step.");
                        }

                        List<(int TagId, DateTime Time)> writes;
                        lock (_pendingLock)
                        {
                            writes = [.. _stepWrites];
                            _stepWindow = null;
                            _stepWrites.Clear();
                        }
                        if (writes.Count > 0)
                        {
                            RecountMinutes(connection, transaction, writes, start, end);
                        }

                        ExecuteCountStep(connection, transaction,
                            $"INSERT INTO {MinuteCountTable} (time, tag_id, count) SELECT time, tag_id, count FROM {StepTable}");
                        if (rollUp)
                        {
                            CompleteHours(connection, transaction, range.From, end, start, end);
                            SetCountTime(connection, transaction, CountUntilKey, end);
                        }
                        else
                        {
                            CompleteHours(connection, transaction, start, range.Until, start, end);
                            SetCountTime(connection, transaction, CountFromKey, start);
                        }
                        transaction.Commit();
                    }
                    var lockTime = watch.Elapsed;

                    if (rollUp)
                    {
                        lock (_pendingLock)
                        {
                            foreach (var tagId in _pending.Where(p => p.Value < end).Select(p => p.Key).ToList())
                            {
                                _pending.Remove(tagId);
                            }
                        }
                    }
                    return (range, true, start, lockTime);
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        _stepWindow = null;
                        _stepWrites.Clear();
                    }
                }
            }
        }

        /// <summary>
        /// Counts the raw values of the window per tag and minute into the step table: one index range per tag
        /// written for a roll-up (archiveraw has no index on the time alone, and the current chunk is not compressed
        /// yet), the batches of all tags at once for the compressed older chunks of a backfill.
        /// </summary>
        private static void CountWindow(NpgsqlConnection connection, bool rollUp, int[] tagIds, DateTime start, DateTime end)
        {
            using var count = connection.CreateCommand();
            count.CommandTimeout = CountStepTimeoutSeconds;
            if (rollUp)
            {
                count.CommandText =
                    $"INSERT INTO {StepTable} (time, tag_id, count) "
                    + "SELECT c.minute, t.tag_id, c.count FROM unnest(@tagIds) AS t(tag_id) CROSS JOIN LATERAL ("
                    + "SELECT date_trunc('minute', r.time, 'UTC') AS minute, count(*)::integer AS count "
                    + $"FROM {RawTable} r WHERE r.tag_id = t.tag_id AND r.time >= @from AND r.time < @to GROUP BY 1) c";
                count.Parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tagIds });
            }
            else
            {
                count.CommandText =
                    $"INSERT INTO {StepTable} (time, tag_id, count) "
                    + "SELECT date_trunc('minute', time, 'UTC'), tag_id, count(*)::integer "
                    + $"FROM {RawTable} WHERE time >= @from AND time < @to GROUP BY 1, 2";
            }
            count.Parameters.Add(TimeParameter("from", start));
            count.Parameters.Add(TimeParameter("to", end));
            count.ExecuteNonQuery();
        }

        /// <summary>
        /// Replaces the counts of the minutes written during the step by a count of their raw values now: one index
        /// range per tag over its minutes, and the window as a fixed range, so the planner takes only its chunks (a
        /// range from the arrays alone plans every chunk of archiveraw, on the Pi a roll-up held the writes back 23 s).
        /// </summary>
        private static void RecountMinutes(NpgsqlConnection connection, NpgsqlTransaction transaction,
            List<(int TagId, DateTime Time)> minutes, DateTime start, DateTime end)
        {
            var keyTagIds = minutes.Select(m => m.TagId).ToArray();
            var keyTimes = minutes.Select(m => m.Time).ToArray();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandTimeout = CountStepTimeoutSeconds;
                delete.CommandText =
                    $"DELETE FROM {StepTable} s USING unnest(@tagIds, @times) AS k(tag_id, time) WHERE s.tag_id = k.tag_id AND s.time = k.time";
                delete.Parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keyTagIds });
                delete.Parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = keyTimes });
                delete.ExecuteNonQuery();
            }

            var tags = minutes
                .GroupBy(m => m.TagId)
                .Select(g => (TagId: g.Key, From: g.Min(m => m.Time), To: g.Max(m => m.Time).AddMinutes(1)))
                .ToList();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandTimeout = CountStepTimeoutSeconds;
            insert.CommandText =
                $"INSERT INTO {StepTable} (time, tag_id, count) "
                + "SELECT c.minute, t.tag_id, c.count FROM unnest(@tagIds, @froms, @tos) AS t(tag_id, from_time, to_time) "
                + "CROSS JOIN LATERAL (SELECT date_trunc('minute', r.time, 'UTC') AS minute, count(*)::integer AS count "
                + $"FROM {RawTable} r WHERE r.tag_id = t.tag_id AND r.time >= t.from_time AND r.time < t.to_time "
                + "AND r.time >= @from AND r.time < @to GROUP BY 1) c "
                + "JOIN unnest(@keyTagIds, @keyTimes) AS k(tag_id, time) ON k.tag_id = t.tag_id AND k.time = c.minute";
            insert.Parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tags.Select(t => t.TagId).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("froms", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = tags.Select(t => t.From).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("tos", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = tags.Select(t => t.To).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("keyTagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keyTagIds });
            insert.Parameters.Add(new NpgsqlParameter("keyTimes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = keyTimes });
            insert.Parameters.Add(TimeParameter("from", start));
            insert.Parameters.Add(TimeParameter("to", end));
            insert.ExecuteNonQuery();
        }

        private static void ExecuteCountStep(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CountStepTimeoutSeconds;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// The start of the oldest chunk of the raw values (UTC), null without raw values.
        /// </summary>
        internal DateTime? GetOldestRawChunkStart()
        {
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT min(range_start) FROM timescaledb_information.chunks WHERE hypertable_name = @table";
            command.Parameters.AddWithValue("table", RawTable);
            return command.ExecuteScalar() is DateTime start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null;
        }

        /// <summary>
        /// Writes the hour rows of the hours of the step [stepStart, stepEnd) that now lie entirely within
        /// [from, until), from their minute rows. None of them was within the range before, so none has a row yet.
        /// </summary>
        private static void CompleteHours(NpgsqlConnection connection, NpgsqlTransaction transaction,
            DateTime from, DateTime until, DateTime stepStart, DateTime stepEnd)
        {
            var hoursStart = Max(CeilHour(from), FloorHour(stepStart));
            var hoursEnd = Min(FloorHour(until), CeilHour(stepEnd));
            if (hoursStart >= hoursEnd)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CountStepTimeoutSeconds;
            command.CommandText =
                $"INSERT INTO {HourCountTable} (time, tag_id, count) "
                + "SELECT date_trunc('hour', time, 'UTC'), tag_id, sum(count)::integer "
                + $"FROM {MinuteCountTable} WHERE time >= @from AND time < @to GROUP BY 1, 2";
            command.Parameters.Add(TimeParameter("from", hoursStart));
            command.Parameters.Add(TimeParameter("to", hoursEnd));
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Remembers the tags of the raw values at or after Until that were written before this process started
        /// (once per process). Under the exclusive count lock, so no write is missed in between.
        /// </summary>
        private void SeedPending(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime until)
        {
            if (_pendingSeeded)
            {
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = CountStepTimeoutSeconds;
                // The newest value of every tag from its index, instead of reading all chunks since Until
                command.CommandText =
                    "SELECT t.id, n.newest FROM archivetag t CROSS JOIN LATERAL ("
                    + $"SELECT max(r.time) AS newest FROM {RawTable} r WHERE r.tag_id = t.id AND r.time >= @from) n "
                    + "WHERE n.newest IS NOT NULL";
                command.Parameters.Add(TimeParameter("from", until));
                using var reader = command.ExecuteReader();
                lock (_pendingLock)
                {
                    while (reader.Read())
                    {
                        var tagId = reader.GetInt32(0);
                        var newest = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                        if (!_pending.TryGetValue(tagId, out var known) || newest > known)
                        {
                            _pending[tagId] = newest;
                        }
                    }
                }
            }
            _pendingSeeded = true;
        }

        private static CountRange? LockCountRange(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = CountStepTimeoutSeconds;
                command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
                command.Parameters.AddWithValue("key", CountLockKey);
                command.ExecuteNonQuery();
            }
            return ReadCountRange(connection, transaction);
        }

        private static void SetCountTime(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, DateTime time)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE parameter SET value = @value WHERE key = @key";
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("value",
                new DateTimeOffset(time).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        #endregion

        #region query
        /// <summary>
        /// The number of raw values of the tag in [startTime, endTime), equal to a count in archiveraw: the full
        /// minutes and hours within the counted range are summed from the counters, the rest is counted in
        /// archiveraw. Reads the range and the counters in one snapshot.
        /// </summary>
        private long CountRawValues(int tagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = ToDbTime(startTime);
            var end = ToDbTime(endTime);
            using var connection = _dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = ReadTimeoutSeconds;
            AddRange(command.Parameters, tagId, start, end);

            var parts = CountedPart(connection, transaction, start, end);
            if (parts == null)
            {
                command.CommandText = $"SELECT count(*) FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end";
            }
            else
            {
                var (minutesStart, hoursStart, hoursEnd, minutesEnd) = parts.Value;
                command.CommandText =
                    $"SELECT (SELECT count(*) FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @m1) "
                    + $"+ (SELECT coalesce(sum(count), 0) FROM {MinuteCountTable} WHERE tag_id = @tagId AND time >= @m1 AND time < @h1) "
                    + $"+ (SELECT coalesce(sum(count), 0) FROM {HourCountTable} WHERE tag_id = @tagId AND time >= @h1 AND time < @h2) "
                    + $"+ (SELECT coalesce(sum(count), 0) FROM {MinuteCountTable} WHERE tag_id = @tagId AND time >= @h2 AND time < @m2) "
                    + $"+ (SELECT count(*) FROM {RawTable} WHERE tag_id = @tagId AND time >= @m2 AND time < @end)";
                command.Parameters.Add(TimeParameter("m1", minutesStart));
                command.Parameters.Add(TimeParameter("h1", hoursStart));
                command.Parameters.Add(TimeParameter("h2", hoursEnd));
                command.Parameters.Add(TimeParameter("m2", minutesEnd));
            }

            try
            {
                using var registration = cancellationToken.Register(command.Cancel);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException("The archive read was cancelled.", ex, cancellationToken);
            }
        }

        /// <summary>
        /// About how many raw values of the tag [startTime, endTime) holds: the counters of its counted minutes, scaled
        /// to the whole range. Reads only the counters (no raw values, which a count of an uncounted range must read),
        /// so it decides the cost of a read before it starts; null without a counted minute in the range.
        /// </summary>
        private long? EstimateRawValues(int tagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = ToDbTime(startTime);
            var end = ToDbTime(endTime);
            using var connection = _dataSource.OpenConnection();
            var parts = CountedPart(connection, null, start, end);
            if (parts == null)
            {
                return null;
            }

            var (m1, h1, h2, m2) = parts.Value;
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT (SELECT coalesce(sum(count), 0) FROM {MinuteCountTable} WHERE tag_id = @tagId AND time >= @m1 AND time < @h1) "
                + $"+ (SELECT coalesce(sum(count), 0) FROM {HourCountTable} WHERE tag_id = @tagId AND time >= @h1 AND time < @h2) "
                + $"+ (SELECT coalesce(sum(count), 0) FROM {MinuteCountTable} WHERE tag_id = @tagId AND time >= @h2 AND time < @m2)";
            command.Parameters.AddWithValue("tagId", tagId);
            command.Parameters.Add(TimeParameter("m1", m1));
            command.Parameters.Add(TimeParameter("h1", h1));
            command.Parameters.Add(TimeParameter("h2", h2));
            command.Parameters.Add(TimeParameter("m2", m2));
            using var registration = cancellationToken.Register(command.Cancel);
            var counted = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return (long)Math.Round(counted * (end - start).TotalSeconds / (m2 - m1).TotalSeconds);
        }

        /// <summary>
        /// The full minutes [m1, m2) of the range that the counters hold, and the full hours [h1, h2) within them
        /// (h1 = h2 = m2 without one); null when no full minute is counted.
        /// </summary>
        private (DateTime M1, DateTime H1, DateTime H2, DateTime M2)? CountedPart(
            NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime start, DateTime end)
        {
            var range = ReadCountRange(connection, transaction);
            if (range == null || end <= range.Value.From || start >= range.Value.Until)
            {
                return null;
            }

            // Near the retention cutoff a chunk of archiveraw or of a counter table may be gone already: the counters
            // are used only from the newest of the oldest chunks on, where all three tables are complete
            var from = range.Value.From;
            var retention = RetentionSeconds(connection, transaction);
            if (retention > 0 && start < UtcNow().AddSeconds(-retention) + RetentionEdge)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                // A table without a chunk is complete nowhere (NULL): then everything is counted in archiveraw
                command.CommandText =
                    "SELECT CASE WHEN count(oldest) = 3 THEN max(oldest) END FROM (SELECT ("
                    + "SELECT min(range_start) FROM timescaledb_information.chunks c WHERE c.hypertable_name = t.name) AS oldest "
                    + "FROM unnest(@tables) AS t(name)) chunks";
                command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = new[] { RawTable, MinuteCountTable, HourCountTable }
                });
                if (command.ExecuteScalar() is not DateTime oldest)
                {
                    return null;
                }
                from = Max(from, CeilMinute(DateTime.SpecifyKind(oldest, DateTimeKind.Utc)));
            }

            var m1 = CeilMinute(Max(start, from));
            var m2 = FloorMinute(Min(end, range.Value.Until));
            if (m1 >= m2)
            {
                return null;
            }

            var h1 = CeilHour(m1);
            var h2 = FloorHour(m2);
            return h1 < h2 ? (m1, h1, h2, m2) : (m1, m2, m2, m2);
        }
        #endregion

        /// <summary>
        /// The time of the last raw value of the tag, when it has none at or after <paramref name="before"/>. The
        /// newest hour row of the tag tells the hour of its last value within the counted range; the hour table has
        /// 13 chunks at a retention of 90 days. Before and after the hour rows (the partial hours at the ends of the
        /// range, the minutes not rolled up yet, the values before the backfill) the raw values are searched, each
        /// part with a time range.
        /// </summary>
        private DateTime? LastCountedTime(int tagId, DateTime before)
        {
            CountRange? range;
            using (var connection = _dataSource.OpenConnection())
            {
                range = ReadCountRange(connection, null);
            }
            if (range == null || range.Value.From >= before)
            {
                return FirstOrLastRawTime(tagId, true, null, before);
            }

            var (from, until) = range.Value;
            // Not rolled up yet (after a downtime) and the partial hour at the end of the range
            var notInHours = Max(from, FloorHour(Min(until, before)));
            var time = FirstOrLastRawTime(tagId, true, notInHours, before);
            if (time != null)
            {
                return time;
            }

            DateTime? hour = null;
            StreamRows(
                $"SELECT max(time) FROM {HourCountTable} WHERE tag_id = @tagId AND time < @before",
                p =>
                {
                    p.AddWithValue("tagId", tagId);
                    p.Add(TimeParameter("before", notInHours));
                },
                reader => hour = reader.IsDBNull(0) ? null : DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
            if (hour != null)
            {
                time = FirstOrLastRawTime(tagId, true, hour.Value, hour.Value.AddHours(1));
                if (time != null)
                {
                    return time;
                }
            }

            // The partial hour at the start of the range, then the values before it
            var firstHour = Min(CeilHour(from), notInHours);
            return (from < firstHour ? FirstOrLastRawTime(tagId, true, from, firstHour) : null)
                ?? FirstOrLastRawTime(tagId, true, null, from);
        }

        /// <summary>
        /// The time of the last (or first) raw value of the tag in [from, until), for a range too long to search in
        /// archiveraw at once: for a tag without values there each of its chunks is probed (1 440 in 30 days), and a
        /// compressed one is decompressed. The full hours of the counted range are looked up in the hour counters (7-day
        /// chunks) and only the hour found is read; the rest of the range (before and after the counted range, the
        /// partial hours at its ends) is searched in the raw values a day at a time, the nearer part first. Every
        /// statement has fixed bounds within [from, until) and plans the chunks of a day at most.
        /// </summary>
        private DateTime? CountedRawTime(int tagId, bool last, DateTime from, DateTime until, CancellationToken cancellationToken)
        {
            if (from >= until)
            {
                return null;
            }

            CountRange? range;
            using (var connection = _dataSource.OpenConnection())
            {
                range = ReadCountRange(connection, null);
            }
            var h1 = range == null ? until : CeilHour(Max(from, range.Value.From));
            var h2 = range == null ? until : FloorHour(Min(until, range.Value.Until));
            if (h1 >= h2)
            {
                return SteppedRawTime(tagId, last, from, until, cancellationToken);
            }

            var (nearFrom, nearUntil, farFrom, farUntil) = last ? (h2, until, from, h1) : (from, h1, h2, until);
            var time = SteppedRawTime(tagId, last, nearFrom, nearUntil, cancellationToken);
            if (time != null)
            {
                return time;
            }

            DateTime? hour = null;
            StreamRows(
                $"SELECT {(last ? "max" : "min")}(time) FROM {HourCountTable} "
                + "WHERE tag_id = @tagId AND time >= @start AND time < @end AND count > 0",
                p =>
                {
                    p.AddWithValue("tagId", tagId);
                    p.Add(TimeParameter("start", h1));
                    p.Add(TimeParameter("end", h2));
                },
                reader => hour = reader.IsDBNull(0) ? null : DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                cancellationToken);
            if (hour != null)
            {
                time = FirstOrLastRawTime(tagId, last, hour.Value, hour.Value.AddHours(1), cancellationToken)
                    // The raw values of the hour were dropped by the retention after the counter was read
                    ?? (last
                        ? SteppedRawTime(tagId, true, h1, hour.Value, cancellationToken)
                        : SteppedRawTime(tagId, false, hour.Value.AddHours(1), h2, cancellationToken));
                if (time != null)
                {
                    return time;
                }
            }

            return SteppedRawTime(tagId, last, farFrom, farUntil, cancellationToken);
        }

        // The longest range that one search in archiveraw covers: 48 chunks of 30 minutes
        private static readonly TimeSpan RawSearchStep = TimeSpan.FromDays(1);

        /// <summary>The last (or first) raw value of the tag in [from, until), searched a day at a time from until (from).</summary>
        private DateTime? SteppedRawTime(int tagId, bool last, DateTime from, DateTime until, CancellationToken cancellationToken)
        {
            while (from < until)
            {
                var (stepFrom, stepUntil) = last
                    ? (until - from > RawSearchStep ? until - RawSearchStep : from, until)
                    : (from, until - from > RawSearchStep ? from + RawSearchStep : until);
                var time = FirstOrLastRawTime(tagId, last, stepFrom, stepUntil, cancellationToken);
                if (time != null)
                {
                    return time;
                }
                (from, until) = last ? (from, stepFrom) : (stepUntil, until);
            }
            return null;
        }

        #region common
        private static CountRange? ReadCountRange(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            DateTime? from = null;
            DateTime? until = null;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT key, value FROM parameter WHERE key = ANY(@keys)";
            command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { CountFromKey, CountUntilKey }
            });
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!long.TryParse(reader.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    continue;
                }
                var time = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                if (reader.GetString(0) == CountFromKey)
                {
                    from = time;
                }
                else
                {
                    until = time;
                }
            }
            return from != null && until != null && from <= until ? new CountRange(from.Value, until.Value) : null;
        }

        /// <summary>The retention of the raw values in seconds (0 = forever), read at most every 10 minutes.</summary>
        private long RetentionSeconds(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            lock (_retentionLock)
            {
                if (DateTime.UtcNow - _retentionReadAt < RetentionCacheAge)
                {
                    return _retentionSeconds;
                }
            }

            var seconds = ReadRetentionSeconds(connection, transaction);
            lock (_retentionLock)
            {
                _retentionSeconds = seconds;
                _retentionReadAt = DateTime.UtcNow;
            }
            return seconds;
        }

        private void SetCachedRetentionSeconds(long seconds)
        {
            lock (_retentionLock)
            {
                _retentionSeconds = seconds;
                _retentionReadAt = DateTime.UtcNow;
            }
        }

        private static DateTime FloorMinute(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

        private static DateTime CeilMinute(DateTime utc)
        {
            var floor = FloorMinute(utc);
            return floor == utc ? floor : floor.AddMinutes(1);
        }

        private static DateTime FloorHour(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

        private static DateTime CeilHour(DateTime utc)
        {
            var floor = FloorHour(utc);
            return floor == utc ? floor : floor.AddHours(1);
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
        #endregion
    }
}
