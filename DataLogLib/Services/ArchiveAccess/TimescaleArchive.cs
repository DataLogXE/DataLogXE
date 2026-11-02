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

using DataAccessLib.Models;
using DataLogLib.Common;
using DataLogLib.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using Serilog;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// The measurement archive in TimescaleDB: one hypertable per compression level (archiveraw,
    /// archivec001 ... archivec3600) with the columns time, tag_id (archivetag id of the data tag uid),
    /// value (real, NULL for NaN) and state. The chunks are compressed by the Timescale policies
    /// shortly after they close; segmented by tag, so a read of one tag decompresses only its batches.
    /// </summary>
    public partial class TimescaleArchive : IArchive
    {
        #region const
        internal const string RawTable = "archiveraw";
        internal const string CountTable = "archivecount";
        private const long RetentionMinSeconds = 3600;
        private const long RetentionMaxSeconds = 10L * 365 * 24 * 3600;
        // Reads of the UI run as cancellable jobs without a deadline, the repair streams whole days
        private const int ReadTimeoutSeconds = 30 * 60;
        private const int WriteTimeoutSeconds = 60;
        // Windows of a level times tags that one conflict check of a write reads at most (four points per window)
        internal const long CheckWindowBudget = 25_000;
        private const string EpochOrigin = "'1970-01-01 00:00:00+00'::timestamptz";
        #endregion

        #region private fields
        private DataAccess.PostgresPools _dataSource;
        private readonly ArchiveTagCache _tags;
        #endregion

        /// <summary>The clock that tells what is the future (nothing is known about it); the tests set it.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Sees every read of StreamRows before it runs; the tests count the chunks that each one plans.</summary>
        internal Action<NpgsqlCommand> ReadObserver { get; set; }

        public TimescaleArchive()
        {
            _tags = new ArchiveTagCache(ResolveArchiveIds);
        }

        /// <summary>The level tables, archivecount and the raw value counters, which all share the retention.</summary>
        internal static IReadOnlyList<string> RetentionTables { get; } =
            [.. CompressionLevels.All.Select(l => TableOf(l.Type)), CountTable, MinuteCountTable, HourCountTable];

        internal static string TableOf(CompressionType compression)
        {
            if (!Enum.IsDefined(compression))
            {
                throw new ArgumentOutOfRangeException(nameof(compression));
            }
            return "archive" + compression.ToString().ToLowerInvariant();
        }

        #region public methods
        public void Init(IConfiguration configuration)
        {
            var info = new DataConnectionInfo(configuration);
            _dataSource = DataAccess.PostgresDataSources.Get(info.ConnectionString);
            Log.Information("Init archive: TimescaleDB hypertables {Tables}", string.Join(", ", RetentionTables));
        }

        public void SetUidLookup(ITagUidLookup lookup)
        {
            _tags.SetLookup(lookup);
        }

        #region insert values
        public void InsertValues(List<ArchiveValue> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            // Resolve the archive ids of all new tags with one lookup
            _tags.Prefetch(values.Select(v => v.Id));
            List<(int TagId, ArchiveValue Value)> raw = new(values.Count);
            List<(int TagId, ArchiveValue Value)> compressed = [];
            var unresolved = 0;
            foreach (var value in values)
            {
                if (!_tags.TryGet(value.Id, out var tagId))
                {
                    unresolved++;
                    continue;
                }
                (value.Compression == CompressionType.RAW ? raw : compressed).Add((tagId, value));
            }

            if (unresolved > 0)
            {
                Log.Error("Dropped {Count} archive values of data tags without uid", unresolved);
            }

            Write(raw, compressed);
        }

        public void InsertValuesAndWait(List<ArchiveValue> values)
        {
            InsertValues(values);
        }

        public void InsertValueCount(DateTime time, long count)
        {
            using var connection = _dataSource.OpenIngestConnection();
            using var command = connection.CreateCommand();
            command.CommandTimeout = WriteTimeoutSeconds;
            command.CommandText =
                $"INSERT INTO {CountTable} (time, count) VALUES (@time, @count) "
                + $"ON CONFLICT (time) DO UPDATE SET count = {CountTable}.count + EXCLUDED.count";
            command.Parameters.Add(TimeParameter("time", time));
            command.Parameters.AddWithValue("count", count);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Writes the raw values with a binary COPY and the compressed values with ON CONFLICT DO
        /// NOTHING (the repair writes window parts again), all in one transaction. The commit does not
        /// wait for the WAL flush: a power loss can lose the last moment of values, never consistency,
        /// and the SD card of a small device is spared a flush per write. Raw values written into the counted range
        /// are counted in the same transaction (see TimescaleArchive.Counts.cs).
        /// </summary>
        private void Write(List<(int TagId, ArchiveValue Value)> raw, List<(int TagId, ArchiveValue Value)> compressed)
        {
            if (raw.Count == 0 && compressed.Count == 0)
            {
                return;
            }

            using var connection = _dataSource.OpenIngestConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SET LOCAL synchronous_commit = off";
                command.ExecuteNonQuery();
            }

            if (raw.Count > 0)
            {
                var countRange = BeginRawWrite(connection, transaction, raw);
                using (var writer = connection.BeginBinaryImport(
                    $"COPY {RawTable} (time, tag_id, value, state) FROM STDIN (FORMAT BINARY)"))
                {
                    writer.Timeout = TimeSpan.FromSeconds(WriteTimeoutSeconds);
                    foreach (var (tagId, value) in raw)
                    {
                        writer.StartRow();
                        writer.Write(ToDbTime(value.Time), NpgsqlDbType.TimestampTz);
                        writer.Write(tagId, NpgsqlDbType.Integer);
                        if (double.IsNaN(value.Value))
                        {
                            writer.WriteNull();
                        }
                        else
                        {
                            writer.Write((float)value.Value, NpgsqlDbType.Real);
                        }
                        writer.Write((short)value.State, NpgsqlDbType.Smallint);
                    }
                    writer.Complete();
                }

                if (countRange != null)
                {
                    CountLateValues(connection, transaction, raw, countRange.Value);
                }
            }

            foreach (var level in compressed.GroupBy(c => c.Value.Compression))
            {
                var rows = WithoutExisting(connection, transaction, level.Key, level);
                if (rows.Count == 0)
                {
                    continue;
                }

                var times = new DateTime[rows.Count];
                var tagIds = new int[rows.Count];
                var values = new float?[rows.Count];
                var states = new short[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var (tagId, value) = rows[i];
                    times[i] = ToDbTime(value.Time);
                    tagIds[i] = tagId;
                    values[i] = double.IsNaN(value.Value) ? null : (float)value.Value;
                    states[i] = (short)value.State;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = WriteTimeoutSeconds;
                command.CommandText =
                    $"INSERT INTO {TableOf(level.Key)} (time, tag_id, value, state) "
                    + "SELECT * FROM unnest(@times, @tagIds, @values, @states) ON CONFLICT DO NOTHING";
                command.Parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = times });
                command.Parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tagIds });
                command.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Real) { Value = values });
                command.Parameters.Add(new NpgsqlParameter("states", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = states });
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        /// <summary>
        /// The rows whose tag and time are not in the table yet, each once. TimescaleDB 2.17 skips
        /// every further row of an INSERT ... ON CONFLICT DO NOTHING after the first conflict with a
        /// row of a compressed chunk, so the conflicts are removed before. ON CONFLICT stays for a
        /// concurrent write of the same time. The table is read in the short ranges of CheckRanges and
        /// only the rows of the batch are kept, so a read does not depend on how far apart its times are.
        /// </summary>
        private static List<(int TagId, ArchiveValue Value)> WithoutExisting(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CompressionType compression,
            IEnumerable<(int TagId, ArchiveValue Value)> rows)
        {
            var unique = rows
                .Select(r => (Key: (r.TagId, Time: ToDbTime(r.Value.Time)), Row: r))
                .DistinctBy(r => r.Key)
                .ToList();
            var keys = unique.Select(r => r.Key).ToHashSet();
            var windowSeconds = CompressionLevels.All.First(l => l.Type == compression).Seconds;

            HashSet<(int, DateTime)> existing = [];
            foreach (var (tagIds, first, last) in CheckRanges(keys, windowSeconds))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = WriteTimeoutSeconds;
                command.CommandText = $"SELECT tag_id, time FROM {TableOf(compression)} WHERE tag_id = ANY(@tagIds) AND time >= @first AND time <= @last";
                command.Parameters.Add(new NpgsqlParameter("tagIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tagIds });
                command.Parameters.Add(new NpgsqlParameter("first", NpgsqlDbType.TimestampTz) { Value = first });
                command.Parameters.Add(new NpgsqlParameter("last", NpgsqlDbType.TimestampTz) { Value = last });
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = (reader.GetInt32(0), reader.GetDateTime(1));
                    if (keys.Contains(key))
                    {
                        existing.Add(key);
                    }
                }
            }

            return unique.Where(r => !existing.Contains(r.Key)).Select(r => r.Row).ToList();
        }

        /// <summary>
        /// Splits the keys in time order into ranges with the tags that have a key in them, each with at most
        /// CheckWindowBudget windows over all its tags. One range over a whole batch reads every row of its tags
        /// in between: on the Raspberry Pi (2026-10-29) a batch of the current windows and one startup value
        /// 4 days back read 4 days of archivec001 (45 million rows) into memory, the app swapped and read at
        /// ~160 rows/s (CommandTimeout bounds each wait on the socket, not the read), and its transaction blocked
        /// the compression policy of the level, which took the last TimescaleDB worker from all other policies.
        /// </summary>
        internal static List<(int[] TagIds, DateTime First, DateTime Last)> CheckRanges(
            IEnumerable<(int TagId, DateTime Time)> keys, int windowSeconds)
        {
            var windowTicks = Math.Max(1, windowSeconds) * TimeSpan.TicksPerSecond;
            List<(int[], DateTime, DateTime)> ranges = [];
            HashSet<int> tags = [];
            DateTime first = default, last = default;
            foreach (var (tagId, time) in keys.OrderBy(k => k.Time))
            {
                var tagCount = tags.Contains(tagId) ? tags.Count : tags.Count + 1;
                if (tags.Count > 0 && ((time - first).Ticks / windowTicks + 1) * tagCount > CheckWindowBudget)
                {
                    ranges.Add(([.. tags], first, last));
                    tags.Clear();
                }
                if (tags.Count == 0)
                {
                    first = time;
                }
                tags.Add(tagId);
                last = time;
            }
            if (tags.Count > 0)
            {
                ranges.Add(([.. tags], first, last));
            }
            return ranges;
        }
        #endregion

        #region query values
        public DateTime? GetArchiveStartTime()
        {
            // The first archive value is read from the first system data tag (id 1)
            if (!_tags.TryGet(1, out var tagId))
            {
                return DateTime.MinValue;
            }

            return FirstOrLastTime(tagId, last: false) ?? DateTime.MinValue;
        }

        public DateTime? GetArchiveEndTime(int dataTagId)
        {
            try
            {
                if (!_tags.TryGet(dataTagId, out var tagId))
                {
                    return DateTime.MinValue;
                }

                return FirstOrLastTime(tagId, last: true) ?? DateTime.MinValue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown!");
            }
            return null;
        }

        /// <summary>
        /// The time of the first or the last raw value of the tag. A query without a time range plans every chunk of
        /// archiveraw (4 320 at a retention of 90 days, seconds on the Raspberry Pi), and the last time is read for
        /// every tag when a data source starts: the first time is searched from the oldest chunk on in growing ranges,
        /// then a month at a time, and the last time in the last hour and day, then in the raw value counters (see
        /// LastCountedTime), each with a range.
        /// </summary>
        private DateTime? FirstOrLastTime(int tagId, bool last)
        {
            if (!last)
            {
                var oldest = GetOldestRawChunkStart();
                if (oldest == null)
                {
                    return null;
                }
                foreach (var window in TimeSearchWindows)
                {
                    var first = FirstOrLastRawTime(tagId, false, null, oldest.Value + window);
                    if (first != null)
                    {
                        return first;
                    }
                }
                // Further on a month at a time, up to now: an open range would plan every chunk
                var end = ToDbTime(UtcNow()).AddDays(1);
                for (var from = oldest.Value + TimeSearchWindows[^1]; from < end; from += TimeSearchWindows[^1])
                {
                    var first = FirstOrLastRawTime(tagId, false, from, from + TimeSearchWindows[^1]);
                    if (first != null)
                    {
                        return first;
                    }
                }
                return null;
            }

            var now = ToDbTime(UtcNow());
            foreach (var window in TimeSearchWindows.Take(2))
            {
                var newest = FirstOrLastRawTime(tagId, true, now - window, null);
                if (newest != null)
                {
                    return newest;
                }
            }
            return LastCountedTime(tagId, now - TimeSearchWindows[1]);
        }

        // Growing ranges in which the first or last value of a tag is searched
        private static readonly TimeSpan[] TimeSearchWindows =
            [TimeSpan.FromHours(1), TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];

        /// <summary>The first or last raw value of the tag in [from, until), either end open when null.</summary>
        private DateTime? FirstOrLastRawTime(int tagId, bool last, DateTime? from, DateTime? until, CancellationToken cancellationToken = default)
        {
            DateTime? time = null;
            StreamRows(
                $"SELECT time FROM {RawTable} WHERE tag_id = @tagId"
                + (from != null ? " AND time >= @from" : "")
                + (until != null ? " AND time < @until" : "")
                + $" ORDER BY time {(last ? "DESC" : "ASC")} LIMIT 1",
                p =>
                {
                    p.AddWithValue("tagId", tagId);
                    if (from != null)
                    {
                        p.Add(TimeParameter("from", from.Value));
                    }
                    if (until != null)
                    {
                        p.Add(TimeParameter("until", until.Value));
                    }
                },
                reader => time = FromDbTime(reader.GetDateTime(0)),
                cancellationToken);
            return time;
        }

        public long? GetValueCountSum()
        {
            try
            {
                using var connection = _dataSource.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT coalesce(sum(count), 0)::bigint FROM {CountTable}";
                return (long)command.ExecuteScalar();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown!");
            }
            return null;
        }

        public long? GetRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            return CountRawValues(tagId, startTime, endTime, cancellationToken);
        }

        public long? EstimateRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            return EstimateRawValues(tagId, startTime, endTime, cancellationToken);
        }

        /// <summary>
        /// The number of valid values stored in the range, and min, max, mean and standard deviation of the signal in
        /// it. A stored value holds until the next one is stored, but only when that one is valid (see
        /// GetBoolDurations): the value before the range counts for the time it still held in it, a value before an
        /// invalid one does not count, and the mean and the deviation are weighted by the time each value held. Nothing
        /// is known about the future, so the range is cut at now, and the last value of the tag holds for the hold time
        /// at most (see SignalLostFrom): a source that stopped delivering has no signal. The database does the work in
        /// one pass without holding the values.
        /// </summary>
        public RawStatistics GetRawStatistics(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            // The range is cut at now; a range that lies in the future has no signal, but it counts no values either
            if (!TryCapRange(startTime, endTime, out var start, out var end))
            {
                return new RawStatistics(0, null, null, null, null);
            }

            var (head, first, tail) = RowsAround(tagId, start, end, lookback, cancellationToken);
            var lostFrom = SignalLostFrom(tagId, start, end, hold, head, first, tail, cancellationToken);
            if (lostFrom != null)
            {
                // Nothing counts after the last value stopped holding; the rows of the range all lie before that
                end = lostFrom.Value;
                if (end <= start)
                {
                    return new RawStatistics(0, null, null, null, null);
                }
            }

            var rows = (head == null
                    ? ""
                    : "SELECT (@start - interval '1 millisecond') AS time, @headValue AS value, @headInv AS inv, 0 AS ord UNION ALL ")
                + "SELECT time, value, (value IS NULL OR state = @invalid) AS inv, 1 AS ord "
                + $"FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end"
                + (tail == null ? "" : " UNION ALL SELECT @end AS time, NULL::real AS value, @tailInv AS inv, 2 AS ord");
            // seg: one segment per row, from its time (not before the start) to the next row (not after the end); ok when
            // the row and the next one are valid
            var sql =
                $"WITH s AS ({rows}), "
                + "seg AS (SELECT ord, inv, value::float8 AS v, NOT (inv OR coalesce(lead(inv) OVER w, false)) AS ok, "
                + "extract(epoch FROM (coalesce(lead(time) OVER w, @end) - greatest(time, @start)))::float8 AS d "
                + "FROM s WINDOW w AS (ORDER BY time)) "
                + "SELECT count(*) FILTER (WHERE ord = 1 AND NOT inv), "
                + "min(v) FILTER (WHERE ok AND d > 0), max(v) FILTER (WHERE ok AND d > 0), "
                + "sum(d * v) FILTER (WHERE ok AND d > 0), sum(d * v * v) FILTER (WHERE ok AND d > 0), "
                + "sum(d) FILTER (WHERE ok AND d > 0) FROM seg";

            RawStatistics result = new(0, null, null, null, null);
            StreamRows(
                sql,
                p =>
                {
                    AddRange(p, tagId, start, end);
                    p.AddWithValue("invalid", (short)ValueState.Invalid);
                    p.Add(new NpgsqlParameter("headValue", NpgsqlDbType.Real) { Value = head?.Value ?? 0f });
                    p.Add(new NpgsqlParameter("headInv", NpgsqlDbType.Boolean) { Value = head?.Invalid ?? false });
                    p.Add(new NpgsqlParameter("tailInv", NpgsqlDbType.Boolean) { Value = tail?.Invalid ?? false });
                },
                reader =>
                {
                    var count = reader.GetInt64(0);
                    if (reader.IsDBNull(5) || reader.GetDouble(5) <= 0)
                    {
                        result = new RawStatistics(count, null, null, null, null);
                        return;
                    }

                    var seconds = reader.GetDouble(5);
                    var mean = reader.GetDouble(3) / seconds;
                    var variance = Math.Max(0, reader.GetDouble(4) / seconds - mean * mean);
                    result = new RawStatistics(count, ToDouble((float)reader.GetDouble(1)), ToDouble((float)reader.GetDouble(2)), mean, Math.Sqrt(variance));
                },
                cancellationToken);
            return result;
        }

        /// <summary>
        /// How long a bool tag was high, low and without signal between start and end, how often it changed and how long
        /// its high and low times lasted. A stored value holds until the next one is stored, but only when that one is
        /// valid: before an invalid value (NaN or an invalid state) the signal is lost, so the time from the last valid
        /// value to the invalid one counts as no signal, like the gap that a chart shows there. The first segment belongs
        /// to the value that holds at the start (the last one within the lookback before it, none: not counted). Nothing
        /// is known about the future, so the range is cut at now, and the last value of the tag holds for the hold time
        /// at most (see SignalLostFrom); the rest of the range is no signal. A high or low time is a run of segments
        /// of that state, so a value that is stored again does not end it; the shortest ones are only taken from the runs
        /// that began and ended with a change in the range. The database does the work in one pass.
        /// </summary>
        public BoolDurations GetBoolDurations(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            if (!TryCapRange(startTime, endTime, out var start, out var end))
            {
                return new BoolDurations(0, 0, 0);
            }

            var (head, first, tail) = RowsAround(tagId, start, end, lookback, cancellationToken);
            var hasRows = first != null && first.Time < end;
            if (head == null && !hasRows)
            {
                return new BoolDurations(0, 0, 0);
            }

            // After the last value stopped holding there is no signal; the segments are calculated up to that time
            var rangeEnd = end;
            var lostFrom = SignalLostFrom(tagId, start, end, hold, head, first, tail, cancellationToken);
            if (lostFrom != null)
            {
                end = lostFrom.Value;
                if (end <= start)
                {
                    return new BoolDurations(0, 0, (rangeEnd - start).TotalSeconds);
                }
            }

            // The row before the range and the one after it join the rows of the range as rows of their own, which
            // lie just before the start and at the end, so the segments of the first and last row know their neighbours
            var rows = (head == null
                    ? ""
                    : "SELECT (@start - interval '1 millisecond') AS time, @headValue AS value, @headInv AS inv, 0 AS ord UNION ALL ")
                + "SELECT time, value, (value IS NULL OR state = @invalid) AS inv, 1 AS ord "
                + $"FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end"
                + (tail == null ? "" : " UNION ALL SELECT @end AS time, NULL::real AS value, @tailInv AS inv, 2 AS ord");
            // kind: 1 high, 0 low, 2 no signal. A run is a group of neighbouring segments of one kind; a change is where
            // a run of a state follows a run of the other one
            var sql =
                $"WITH s AS ({rows}), "
                + "seg AS (SELECT t0, t1, kind FROM (SELECT time AS t0, ord, coalesce(lead(time) OVER w, @end) AS t1, "
                + "CASE WHEN inv OR coalesce(lead(inv) OVER w, false) THEN 2 WHEN value >= 0.5 THEN 1 ELSE 0 END AS kind "
                + "FROM s WINDOW w AS (ORDER BY time)) x WHERE ord < 2), "
                + "flagged AS (SELECT t0, t1, kind, CASE WHEN kind IS DISTINCT FROM lag(kind) OVER (ORDER BY t0) THEN 1 ELSE 0 END AS flag FROM seg), "
                + "grouped AS (SELECT kind, t0, t1, sum(flag) OVER (ORDER BY t0) AS grp FROM flagged), "
                + "run AS (SELECT kind, grp, sum(extract(epoch FROM (t1 - greatest(t0, @start))))::float8 AS secs FROM grouped GROUP BY kind, grp), "
                + "nb AS (SELECT kind, secs, lag(kind) OVER (ORDER BY grp) AS pk, lead(kind) OVER (ORDER BY grp) AS nk FROM run) "
                + "SELECT coalesce(sum(secs) FILTER (WHERE kind = 1), 0), coalesce(sum(secs) FILTER (WHERE kind = 0), 0), "
                + "coalesce(sum(secs) FILTER (WHERE kind = 2), 0), count(*) FILTER (WHERE kind < 2 AND pk < 2), "
                + "max(secs) FILTER (WHERE kind = 1), min(secs) FILTER (WHERE kind = 1 AND pk < 2 AND nk < 2), "
                + "max(secs) FILTER (WHERE kind = 0), min(secs) FILTER (WHERE kind = 0 AND pk < 2 AND nk < 2) FROM nb";

            BoolDurations result = new(0, 0, 0);
            StreamRows(
                sql,
                p =>
                {
                    AddRange(p, tagId, start, end);
                    p.AddWithValue("invalid", (short)ValueState.Invalid);
                    p.Add(new NpgsqlParameter("headValue", NpgsqlDbType.Real) { Value = head?.Value ?? 0f });
                    p.Add(new NpgsqlParameter("headInv", NpgsqlDbType.Boolean) { Value = head?.Invalid ?? false });
                    p.Add(new NpgsqlParameter("tailInv", NpgsqlDbType.Boolean) { Value = tail?.Invalid ?? false });
                },
                reader =>
                {
                    double? Seconds(int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);
                    result = new BoolDurations(
                        reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt64(3),
                        Seconds(4), Seconds(5), Seconds(6), Seconds(7));
                },
                cancellationToken);
            return lostFrom == null ? result : result with { InvalidSeconds = result.InvalidSeconds + (rangeEnd - end).TotalSeconds };
        }

        /// <summary>
        /// How long a data tag had no signal between start and end: NaN or an invalid state, which holds until the next
        /// value is stored, and the time from the last valid value to an invalid one (see GetBoolDurations). The first
        /// segment belongs to the value that holds at the start (the last one within the lookback before it); nothing is
        /// known about the future, so the range is cut at now, and the last value of the tag holds for the hold time at
        /// most (see SignalLostFrom): after that there is no signal. The database only looks at the invalid values and
        /// their neighbours, so a long range of a fast tag stays cheap. Null for a tag without uid.
        /// </summary>
        public double? GetInvalidSeconds(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            if (!TryCapRange(startTime, endTime, out var start, out var end))
            {
                return 0;
            }

            var (head, first, tail) = RowsAround(tagId, start, end, lookback, cancellationToken);
            var hasRows = first != null && first.Time < end;
            double seconds = 0;

            if (hasRows)
            {
                // Each invalid row holds until the next row; with a valid row before it, the time from that row is lost too.
                // Both neighbours are searched within the range only, so the plan holds the chunks of the range: a next
                // row after the end counts as the end, and before the first row of the range the row before it is the
                // head. A neighbour without a fixed bound on one side planned every chunk of archiveraw (statistics of the
                // measuring cursors read for up to a minute on the Raspberry Pi, 2026-10-31).
                StreamRows(
                    "SELECT coalesce(sum(own), 0) + coalesce(sum(pre), 0) FROM ("
                    + "SELECT extract(epoch FROM (coalesce(nx.time, @end) - r.time))::float8 AS own, "
                    + "CASE WHEN pv.time IS NOT NULL THEN (CASE WHEN NOT (pv.value IS NULL OR pv.state = @invalid) "
                    + "THEN extract(epoch FROM (r.time - pv.time))::float8 ELSE 0 END) "
                    + "WHEN @headValid THEN extract(epoch FROM (r.time - @start))::float8 ELSE 0 END AS pre "
                    + $"FROM {RawTable} r "
                    + $"LEFT JOIN LATERAL (SELECT n.time FROM {RawTable} n WHERE n.tag_id = r.tag_id AND n.time > r.time "
                    + "AND n.time >= @start AND n.time < @end ORDER BY n.time LIMIT 1) nx ON true "
                    + $"LEFT JOIN LATERAL (SELECT p.time, p.value, p.state FROM {RawTable} p WHERE p.tag_id = r.tag_id "
                    + "AND p.time < r.time AND p.time >= @start AND p.time < @end ORDER BY p.time DESC LIMIT 1) pv ON true "
                    + "WHERE r.tag_id = @tagId AND r.time >= @start AND r.time < @end AND (r.value IS NULL OR r.state = @invalid)) x",
                    p =>
                    {
                        AddRange(p, tagId, start, end);
                        p.AddWithValue("headValid", head is { Invalid: false });
                        p.AddWithValue("invalid", (short)ValueState.Invalid);
                    },
                    reader => seconds = reader.GetDouble(0),
                    cancellationToken);
            }

            // The value that holds at the start was invalid: until the first row in the range, or else to the end
            if (head is { Invalid: true })
            {
                seconds += ((hasRows ? first.Time : end) - start).TotalSeconds;
            }

            // The next row after the range is invalid and the one before it is valid: that one is lost up to the end
            if (tail is { Invalid: true })
            {
                var before = LastRowBetween(tagId, start - lookback, end, cancellationToken);
                if (before is { Invalid: false })
                {
                    seconds += (end - (before.Time > start ? before.Time : start)).TotalSeconds;
                }
            }

            // The last value of the tag stopped holding: no signal from then to the end of the range
            var lostFrom = SignalLostFrom(tagId, start, end, hold, head, first, tail, cancellationToken);
            if (lostFrom != null)
            {
                seconds += (end - (lostFrom.Value > start ? lostFrom.Value : start)).TotalSeconds;
            }

            return seconds;
        }

        /// <summary>
        /// The time from which a tag has no signal because nothing was stored after its last row: a value holds until
        /// the next one is stored, and the newest one for the hold time at most (a data source stores a row at least
        /// every cycle, so a tag that stopped delivering, e.g. after the loss of its connection, has no signal even
        /// though no invalid row marks it yet). Null when a row follows the range, when the last row is invalid (there
        /// is no signal from it on anyway), when the tag has no row before the end, and when the last row still holds at
        /// the end of the range.
        /// </summary>
        private DateTime? SignalLostFrom(int tagId, DateTime start, DateTime end, TimeSpan hold, Neighbour head, Neighbour first, Neighbour tail, CancellationToken cancellationToken)
        {
            if (tail != null)
            {
                return null;
            }

            var last = first != null && first.Time < end ? LastRowBetween(tagId, start, end, cancellationToken) : head;
            if (last == null || last.Invalid)
            {
                return null;
            }

            var lostFrom = last.Time + hold;
            return lostFrom < end ? lostFrom : null;
        }

        // The range in archive precision, cut at now: nothing is known about the future. False when nothing is left.
        private bool TryCapRange(DateTime startTime, DateTime endTime, out DateTime start, out DateTime end)
        {
            start = ToDbTime(startTime);
            end = ToDbTime(endTime);
            var now = ToDbTime(UtcNow());
            if (end > now)
            {
                end = now;
            }
            return end > start;
        }

        /// <summary>A stored row of a tag, reduced to what the durations need.</summary>
        private sealed record Neighbour(DateTime Time, bool Invalid, bool High, float Value);

        // The rows around a range: the last one before it, the first one in or after it, and the first one at or after its
        // end (the first one itself when the range has no row). Each is searched within the lookback.
        private (Neighbour Head, Neighbour First, Neighbour Tail) RowsAround(int tagId, DateTime start, DateTime end, TimeSpan lookback, CancellationToken cancellationToken)
        {
            var head = LastRowBetween(tagId, start - lookback, start, cancellationToken);
            var first = FirstRowBetween(tagId, start, start + lookback, cancellationToken);
            var tail = first == null || first.Time >= end ? first : FirstRowBetween(tagId, end, end + lookback, cancellationToken);
            return (head, first, tail);
        }

        // [from, to), searched with fixed bounds by NearestRawRow, which takes (after, until]: the times are whole milliseconds
        private Neighbour LastRowBetween(int tagId, DateTime from, DateTime to, CancellationToken cancellationToken)
            => ToNeighbour(NearestRawRow(tagId, last: true, from - Millisecond, to - Millisecond, cancellationToken));

        private Neighbour FirstRowBetween(int tagId, DateTime from, DateTime to, CancellationToken cancellationToken)
            => ToNeighbour(NearestRawRow(tagId, last: false, from - Millisecond, to - Millisecond, cancellationToken));

        private static Neighbour ToNeighbour((DateTime Time, float? Value, short State)? row)
        {
            if (row is not { } found)
            {
                return null;
            }

            var invalid = found.Value == null || found.State == (short)ValueState.Invalid;
            var value = invalid ? 0f : found.Value.Value;
            return new Neighbour(found.Time, invalid, value >= 0.5f && !invalid, value);
        }

        /// <summary>
        /// The values of the level in the range. Invalid states clear the points at their time and
        /// leave a point without value that marks the gap.
        /// </summary>
        public List<PlotPoint> GetPlotPoints(int dataTagId, CompressionType compression, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
            => ReadPlotPoints(dataTagId, TableOf(compression), startTime, endTime, 0, cancellationToken, null);

        public List<PlotPoint> GetRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken = default, Action<long> onRead = null)
            => limit > 0 ? ReadPlotPoints(dataTagId, RawTable, startTime, endTime, limit, cancellationToken, onRead) : [];

        public async IAsyncEnumerable<PlotPoint> StreamRawPlotPoints(
            int dataTagId,
            DateTime startTime,
            DateTime endTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                yield break;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = ReadTimeoutSeconds;
            command.CommandText = $"SELECT time, value, state FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end ORDER BY time";
            AddRange(command.Parameters, tagId, startTime, endTime);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // The same points as ReadPlotPoints, one value per time and an invalid state clearing the values within a
            // millisecond of it, but only the points of the last millisecond are held: an older one cannot change any more
            List<(long Millis, PlotPoint Point)> pending = [];
            long? lastInvalid = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                var millis = ToMillis(reader.GetDateTime(0));
                var done = 0;
                while (done < pending.Count && pending[done].Millis < millis - 1)
                {
                    yield return pending[done++].Point;
                }
                pending.RemoveRange(0, done);

                var isInvalid = reader.GetInt16(2) == (short)ValueState.Invalid;
                if (isInvalid)
                {
                    lastInvalid = millis;
                    foreach (var (_, point) in pending)
                    {
                        point.Raw = null;
                    }
                }
                if (!isInvalid && reader.IsDBNull(1))
                {
                    continue;
                }

                var current = pending.Count > 0 && pending[^1].Millis == millis ? pending[^1].Point : null;
                if (current == null)
                {
                    current = new PlotPoint { X = millis.FromUnixMilliseconds().ToUnixSeconds() };
                    pending.Add((millis, current));
                }
                current.Raw = millis - lastInvalid <= 1 || reader.IsDBNull(1) ? null : ToDouble(reader.GetFloat(1));
            }

            foreach (var (_, point) in pending)
            {
                yield return point;
            }
        }

        // Rows between two reports of the rows read: often enough for a progress bar, rare enough to cost nothing
        private const int ReadReportRows = 10_000;

        private List<PlotPoint> ReadPlotPoints(int dataTagId, string table, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken, Action<long> onRead)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return [];
            }

            var values = new Dictionary<long, PlotPoint>();
            List<long> invalid = [];
            long read = 0;
            // The records are taken over as they arrive, so only the plot points are held
            StreamRows(
                $"SELECT time, value, state FROM {table} WHERE tag_id = @tagId AND time >= @start AND time < @end ORDER BY time"
                + (limit > 0 ? " LIMIT @limit" : ""),
                p =>
                {
                    AddRange(p, tagId, startTime, endTime);
                    if (limit > 0)
                    {
                        p.AddWithValue("limit", limit);
                    }
                },
                reader =>
                {
                    var millis = ToMillis(reader.GetDateTime(0));
                    if (reader.GetInt16(2) == (short)ValueState.Invalid)
                    {
                        invalid.Add(millis);
                    }
                    if (!reader.IsDBNull(1))
                    {
                        GetOrAddPoint(values, millis).Raw = ToDouble(reader.GetFloat(1));
                    }
                    if (++read % ReadReportRows == 0)
                    {
                        onRead?.Invoke(read);
                    }
                },
                cancellationToken);
            onRead?.Invoke(read);

            return GetPlotPointsResult(values, invalid);
        }

        /// <summary>
        /// The M4 points of the raw values per bucket, chosen by the database in one pass with the
        /// TimescaleDB aggregates first() and last(): the first, the last, the smallest and the
        /// largest valid value with their times, like DataCompression.M4Buckets on the raw values,
        /// and the first and the last invalid state of each bucket as gaps. Only the M4 points travel,
        /// however many raw values the range holds.
        /// </summary>
        public List<PlotPoint> GetRawM4PlotPoints(int dataTagId, DateTime startTime, DateTime endTime, double origin, double bucketSeconds, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId) || !(bucketSeconds > 0))
            {
                return [];
            }

            const string bucket = "floor((extract(epoch FROM time) - @origin) / @bucket)";
            void Bind(NpgsqlParameterCollection p)
            {
                AddRange(p, tagId, startTime, endTime);
                p.AddWithValue("origin", origin);
                p.AddWithValue("bucket", bucketSeconds);
                p.AddWithValue("invalid", (short)ValueState.Invalid);
            }

            var values = new Dictionary<long, PlotPoint>();
            List<long> invalid = [];
            StreamRows(
                "SELECT min(time), first(value, time), max(time), last(value, time), "
                + "first(time, value), min(value), last(time, value), max(value) "
                + $"FROM (SELECT time, value, {bucket} AS b FROM {RawTable} "
                + "WHERE tag_id = @tagId AND time >= @start AND time < @end AND value IS NOT NULL AND state <> @invalid) v "
                + "GROUP BY b",
                Bind,
                reader =>
                {
                    for (var column = 0; column < 8; column += 2)
                    {
                        GetOrAddPoint(values, ToMillis(reader.GetDateTime(column))).Raw = ToDouble(reader.GetFloat(column + 1));
                    }
                },
                cancellationToken);
            StreamRows(
                $"SELECT min(time), max(time) FROM (SELECT time, {bucket} AS b FROM {RawTable} "
                + "WHERE tag_id = @tagId AND time >= @start AND time < @end AND state = @invalid) g "
                + "GROUP BY b",
                Bind,
                reader =>
                {
                    invalid.Add(ToMillis(reader.GetDateTime(0)));
                    invalid.Add(ToMillis(reader.GetDateTime(1)));
                },
                cancellationToken);

            return GetPlotPointsResult(values, invalid);
        }

        private static PlotPoint GetOrAddPoint(Dictionary<long, PlotPoint> values, long millis)
        {
            if (!values.TryGetValue(millis, out var point))
            {
                point = new PlotPoint { X = millis.FromUnixMilliseconds().ToUnixSeconds() };
                values.Add(millis, point);
            }
            return point;
        }

        private static List<PlotPoint> GetPlotPointsResult(Dictionary<long, PlotPoint> values, List<long> invalid)
        {
            // An invalid state clears the values at its time. The sorted times are searched, not all
            // values per state, which would grow with values * states.
            var keys = values.Keys.ToArray();
            Array.Sort(keys);
            foreach (var millis in invalid)
            {
                (long x1, long x2) = (millis - 1, millis + 1);
                var i = Array.BinarySearch(keys, x1);
                for (i = i < 0 ? ~i : i; i < keys.Length && keys[i] <= x2; i++)
                {
                    values[keys[i]].Raw = null;
                }

                // Without a value at the timestamp a null point marks the gap
                if (!values.ContainsKey(millis))
                {
                    values.Add(millis, new PlotPoint { X = millis.FromUnixMilliseconds().ToUnixSeconds() });
                }
            }

            return values.Values.OrderBy(v => v.X).ToList();
        }

        /// <summary>
        /// The last raw value within the minute before the time, null when it is NaN.
        /// </summary>
        public PlotPoint GetPlotPointAt(int dataTagId, DateTime dateTime)
        {
            var point = new PlotPoint { X = dateTime.ToUnixSeconds() };
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return point;
            }

            StreamRows(
                $"SELECT time, value, state FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end "
                + "ORDER BY time DESC LIMIT 1",
                p => AddRange(p, tagId, dateTime.AddSeconds(-60), dateTime),
                reader => point = reader.IsDBNull(1)
                    ? null
                    : new PlotPoint
                    {
                        X = ToMillis(reader.GetDateTime(0)).FromUnixMilliseconds().ToUnixSeconds(),
                        Raw = reader.GetInt16(2) > 0 ? ToDouble(reader.GetFloat(1)) : null
                    });
            return point;
        }

        /// <summary>
        /// The raw value that holds at the time, read from the archive as it is stored: the last one at or before it
        /// within the lookback, if the next one stored is valid (a value before an invalid one is lost, like the gap a
        /// chart shows there). The newest value of the tag holds for the hold time only, so a source that stopped
        /// delivering has no value. Null when there is no row and for a time in the future; Raw is null when that value
        /// is NaN, invalid or lost.
        /// </summary>
        public PlotPoint GetRawValueAt(int dataTagId, DateTime dateTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
        {
            if (!_tags.TryGet(dataTagId, out var tagId))
            {
                return null;
            }

            var end = ToDbTime(dateTime);
            var now = ToDbTime(UtcNow());
            // Nothing is known about the future
            if (end > now)
            {
                return null;
            }

            // The last row at or before the time within the lookback, and the one stored after it (which lies after the
            // time), each searched with fixed bounds. The next row without an upper bound and the lookback of 30 days at
            // once probed every chunk of archiveraw and decompressed the compressed ones: six reads of the measuring
            // cursors blocked the Raspberry Pi for 22 minutes (2026-10-31).
            var row = NearestRawRow(tagId, last: true, end - lookback, end, cancellationToken);
            if (row == null)
            {
                return null;
            }

            var (time, value, state) = row.Value;
            var invalid = value == null || state == (short)ValueState.Invalid;
            var next = invalid ? null : NearestRawRow(tagId, last: false, end, now, cancellationToken);
            // A value holds until the next one is stored, if that is valid. The newest one of the tag has no next one: it
            // holds for the hold time from its time, then the source has stopped delivering.
            var holds = !invalid && (next != null
                ? !(next.Value.Value == null || next.Value.State == (short)ValueState.Invalid)
                : end - time <= hold);
            return new PlotPoint
            {
                X = ToMillis(time).FromUnixMilliseconds().ToUnixSeconds(),
                Raw = holds ? ToDouble(value.Value) : null
            };
        }

        // The ranges next to a time in which a raw row is searched in archiveraw, before the counters take over
        private static readonly TimeSpan[] NearSearchWindows = [TimeSpan.FromHours(1), TimeSpan.FromDays(1)];
        private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// The last (or first) raw row of the tag in (after, until] with UTC time and value (null for NaN). Searched in
        /// the hour and the day next to until (after), then in the rest of the range by the raw value counters, every
        /// statement with fixed bounds.
        /// </summary>
        private (DateTime Time, float? Value, short State)? NearestRawRow(
            int tagId, bool last, DateTime after, DateTime until, CancellationToken cancellationToken)
        {
            var reached = TimeSpan.Zero;
            foreach (var window in NearSearchWindows)
            {
                var (from, to) = last
                    ? (Max(after, until - window), until - reached)
                    : (after + reached, Min(until, after + window));
                if (from < to)
                {
                    var row = ReadRawRow(tagId, last, from, to, cancellationToken);
                    if (row != null)
                    {
                        return row;
                    }
                }
                reached = window;
            }

            var (restFrom, restTo) = last ? (after, until - reached) : (after + reached, until);
            if (restFrom >= restTo)
            {
                return null;
            }
            // CountedRawTime takes [from, until): the times are whole milliseconds
            var time = CountedRawTime(tagId, last, restFrom + Millisecond, restTo + Millisecond, cancellationToken);
            if (time == null)
            {
                return null;
            }
            var utc = ToDbTime(time.Value);
            return ReadRawRow(tagId, last, utc - Millisecond, utc, cancellationToken);
        }

        /// <summary>The last (or first) raw row of the tag in (after, until].</summary>
        private (DateTime Time, float? Value, short State)? ReadRawRow(
            int tagId, bool last, DateTime after, DateTime until, CancellationToken cancellationToken)
        {
            (DateTime, float?, short)? row = null;
            StreamRows(
                $"SELECT time, value, state FROM {RawTable} WHERE tag_id = @tagId AND time > @start AND time <= @end "
                + $"ORDER BY time {(last ? "DESC" : "ASC")} LIMIT 1",
                p => AddRange(p, tagId, after, until),
                reader => row = (
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    reader.IsDBNull(1) ? null : reader.GetFloat(1),
                    reader.GetInt16(2)),
                cancellationToken);
            return row;
        }
        #endregion

        #region database size, metadata and retention
        /// <summary>
        /// The size of the whole database on disk (configuration, archive, process events, indexes, TOAST).
        /// Excludes the WAL, which lies outside the database directory.
        /// </summary>
        public long? GetDatabaseDiskSizeBytes()
        {
            try
            {
                using var connection = _dataSource.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_database_size(current_database())";
                return (long)command.ExecuteScalar();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read the database disk size");
                return null;
            }
        }

        /// <summary>
        /// The TimescaleDB and PostgreSQL versions and the retention of the archive in seconds
        /// (0 = forever).
        /// </summary>
        public (string Version, long? RetentionSeconds) ReadMetadata()
        {
            using var connection = _dataSource.OpenConnection();
            string version = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'), current_setting('server_version')";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var timescale = reader.IsDBNull(0) ? "-" : reader.GetString(0);
                    version = $"TimescaleDB {timescale}, PostgreSQL {reader.GetString(1).Split(' ')[0]}";
                }
            }

            return (version, ReadRetentionSeconds(connection, null));
        }

        public void SetRetentionSeconds(long everySeconds)
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
                foreach (var table in RetentionTables)
                {
                    using (var remove = connection.CreateCommand())
                    {
                        remove.Transaction = transaction;
                        remove.CommandText = $"SELECT remove_retention_policy('{table}', if_exists => true)";
                        remove.ExecuteNonQuery();
                    }

                    if (everySeconds > 0)
                    {
                        using var add = connection.CreateCommand();
                        add.Transaction = transaction;
                        add.CommandText = $"SELECT add_retention_policy('{table}', make_interval(secs => @seconds))";
                        add.Parameters.AddWithValue("seconds", (double)everySeconds);
                        add.ExecuteNonQuery();
                    }
                }

                var confirmed = ReadRetentionSeconds(connection, transaction);
                if (confirmed != everySeconds)
                {
                    throw new InvalidOperationException("TimescaleDB did not confirm the new retention.");
                }

                transaction.Commit();
            }

            SetCachedRetentionSeconds(everySeconds);
            DropExpiredChunks(connection, everySeconds);
        }

        /// <summary>
        /// Drops the chunks older than the retention. The Timescale jobs do the same, but the
        /// background workers are capped on the small boards, so the service enforces it as well.
        /// </summary>
        public int ApplyRetention()
        {
            using var connection = _dataSource.OpenConnection();
            var seconds = ReadRetentionSeconds(connection, null);
            return seconds > 0 ? DropExpiredChunks(connection, seconds) : 0;
        }

        private static int DropExpiredChunks(NpgsqlConnection connection, long seconds)
        {
            if (seconds <= 0)
            {
                return 0;
            }

            var dropped = 0;
            foreach (var table in RetentionTables)
            {
                using var command = connection.CreateCommand();
                command.CommandTimeout = ReadTimeoutSeconds;
                command.CommandText = $"SELECT count(*) FROM drop_chunks('{table}', older_than => make_interval(secs => @seconds))";
                command.Parameters.AddWithValue("seconds", (double)seconds);
                dropped += Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            }
            return dropped;
        }

        /// <summary>
        /// The retention of the raw values in seconds, 0 for forever (no retention policy).
        /// </summary>
        private static long ReadRetentionSeconds(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT extract(epoch FROM (config->>'drop_after')::interval)::bigint "
                + "FROM timescaledb_information.jobs "
                + "WHERE proc_name = 'policy_retention' AND hypertable_name = @table LIMIT 1";
            command.Parameters.AddWithValue("table", RawTable);
            var raw = command.ExecuteScalar();
            return raw == null || raw is DBNull ? 0 : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        #endregion

        #endregion

        #region common
        /// <summary>
        /// Runs the query and hands every row over as it arrives, without collecting the result.
        /// A cancelled token cancels the query on the server.
        /// </summary>
        private void StreamRows(string sql, Action<NpgsqlParameterCollection> bind, Action<NpgsqlDataReader> onRow, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandTimeout = ReadTimeoutSeconds;
            command.CommandText = sql;
            bind(command.Parameters);
            ReadObserver?.Invoke(command);
            try
            {
                using var registration = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    onRow(reader);
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException("The archive read was cancelled.", ex, cancellationToken);
            }
        }

        private static void AddRange(NpgsqlParameterCollection parameters, int tagId, DateTime startTime, DateTime endTime)
        {
            parameters.AddWithValue("tagId", tagId);
            parameters.Add(TimeParameter("start", startTime));
            parameters.Add(TimeParameter("end", endTime));
        }

        private static NpgsqlParameter TimeParameter(string name, DateTime time)
            => new(name, NpgsqlDbType.TimestampTz) { Value = ToDbTime(time) };

        /// <summary>
        /// UTC with millisecond precision, the precision of the archive times (as in Influx). Local and
        /// unspecified times are local times.
        /// </summary>
        internal static DateTime ToDbTime(DateTime time)
        {
            var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        internal static DateTime FromDbTime(DateTime utc)
            => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

        private static long ToMillis(DateTime utc) => FromDbTime(utc).ToUnixMilliseconds();

        /// <summary>
        /// The double with the shortest digits of the stored real (100.62626 instead of the widened
        /// 100.62625885009766), as Influx returned the written text. Keeps the chart JSON short.
        /// </summary>
        internal static double ToDouble(float value)
        {
            Span<char> text = stackalloc char[32];
            return value.TryFormat(text, out var length, default, CultureInfo.InvariantCulture)
                && double.TryParse(text[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : value;
        }

        /// <summary>
        /// The archivetag ids of the uids; uids without one get a new id.
        /// </summary>
        private IReadOnlyDictionary<string, int> ResolveArchiveIds(IReadOnlyCollection<string> uids)
        {
            var array = uids.ToArray();
            using var connection = _dataSource.OpenConnection();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO archivetag (guid) SELECT unnest(@guids) ON CONFLICT (guid) DO NOTHING";
                insert.Parameters.Add(new NpgsqlParameter("guids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = array });
                insert.ExecuteNonQuery();
            }

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT guid, id FROM archivetag WHERE guid = ANY(@guids)";
            select.Parameters.Add(new NpgsqlParameter("guids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = array });
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetInt32(1);
            }
            return result;
        }
        #endregion
    }
}
