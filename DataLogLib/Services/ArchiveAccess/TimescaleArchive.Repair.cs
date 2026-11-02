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

using DataLogLib.Common;
using DataLogLib.Models;
using Npgsql;
using NpgsqlTypes;
using Serilog;
using System.Globalization;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Confirmed writes of the compressed values and the reads that repair the compression levels
    /// from the raw values (see CompressionRepairWorker). The repair works with the uid of the data
    /// tags, so it also covers archived tags that no longer exist.
    /// </summary>
    public partial class TimescaleArchive
    {
        // Time up to which the compression levels were checked against the raw values (unix seconds)
        private const string CompressionVerifiedKey = "COMPRESSION_VERIFIED_UNTIL";

        #region confirmed writes
        public void InsertCompressedValuesAndWait(string uid, List<ArchiveValue> values)
        {
            var tagId = _tags.GetId(uid);
            Write([], values.Select(v => (tagId, v)).ToList());
        }
        #endregion

        #region repair reads
        public bool TryGetUid(int dataTagId, out string uid)
        {
            return _tags.TryGetUid(dataTagId, out uid);
        }

        /// <summary>
        /// Looks up the first raw value per archived tag, so every tag reads only the start of its
        /// batches instead of the whole range.
        /// </summary>
        public List<(string Uid, DateTime First)> GetRawUids(DateTime startTime, DateTime endTime)
        {
            List<(string, DateTime)> result = [];
            StreamRows(
                "SELECT t.guid, f.time FROM archivetag t CROSS JOIN LATERAL ("
                + $"SELECT time FROM {RawTable} r WHERE r.tag_id = t.id AND r.time >= @start AND r.time < @end "
                + "ORDER BY r.time LIMIT 1) f",
                p =>
                {
                    p.Add(TimeParameter("start", startTime));
                    p.Add(TimeParameter("end", endTime));
                },
                reader => result.Add((reader.GetString(0), FromDbTime(reader.GetDateTime(1)))));
            return result;
        }

        /// <summary>
        /// Streams the raw values of the tag in time order without holding them in memory. A value
        /// without a number (NULL) is passed with the value NaN.
        /// </summary>
        public void ReadRawValues(string uid, DateTime startTime, DateTime endTime, Action<ArchiveValue> onValue)
        {
            var tagId = _tags.GetId(uid);
            StreamRows(
                $"SELECT time, value, state FROM {RawTable} WHERE tag_id = @tagId AND time >= @start AND time < @end ORDER BY time",
                p => AddRange(p, tagId, startTime, endTime),
                reader => onValue(new ArchiveValue
                {
                    Time = FromDbTime(reader.GetDateTime(0)),
                    Value = reader.IsDBNull(1) ? double.NaN : reader.GetFloat(1),
                    State = (ValueState)reader.GetInt16(2)
                }));
        }

        /// <summary>
        /// The time of the last value of every tag in every level within the range.
        /// </summary>
        public List<(string Uid, CompressionType Compression, DateTime Last)> GetLastTimes(DateTime startTime, DateTime endTime)
        {
            List<(string, CompressionType, DateTime)> result = [];
            foreach (var level in CompressionLevels.All)
            {
                StreamRows(
                    "SELECT t.guid, l.time FROM archivetag t CROSS JOIN LATERAL ("
                    + $"SELECT time FROM {TableOf(level.Type)} v WHERE v.tag_id = t.id AND v.time >= @start AND v.time < @end "
                    + "ORDER BY v.time DESC LIMIT 1) l",
                    p =>
                    {
                        p.Add(TimeParameter("start", startTime));
                        p.Add(TimeParameter("end", endTime));
                    },
                    reader => result.Add((reader.GetString(0), level.Type, FromDbTime(reader.GetDateTime(1)))));
            }
            return result;
        }

        /// <summary>
        /// Returns per local hour the first and last time of the raw values and of the coarsest
        /// level. A complete hour of the level keeps the first and the last raw value of the hour.
        /// The range must start and end on a local hour.
        /// </summary>
        public List<HourCoverage> GetHourCoverage(string uid, DateTime startTime, DateTime endTime)
        {
            var tagId = _tags.GetId(uid);

            // The windows start on the local hours, also in time zones with a half hour offset
            var offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(startTime).TotalMinutes;
            var origin = new DateTime(1970, 1, 1, 0, ((-offsetMinutes) % 60 + 60) % 60, 0, DateTimeKind.Utc);

            static DateTime HourOf(DateTime local) =>
                CompressionLevels.WindowStart(local, CompressionLevels.Coarsest);

            Dictionary<DateTime, (DateTime RawFirst, DateTime RawLast, long Count)> raw = [];
            Dictionary<DateTime, (DateTime First, DateTime Last)> level = [];
            void Read(string table, bool count, Action<DateTime, DateTime, long> onHour) => StreamRows(
                $"SELECT min(time), max(time), {(count ? "count(*)" : "0::bigint")} FROM {table} "
                + "WHERE tag_id = @tagId AND time >= @start AND time < @end "
                + "GROUP BY time_bucket(INTERVAL '1 hour', time, @origin)",
                p =>
                {
                    AddRange(p, tagId, startTime, endTime);
                    p.Add(new NpgsqlParameter("origin", NpgsqlDbType.TimestampTz) { Value = origin });
                },
                reader => onHour(FromDbTime(reader.GetDateTime(0)), FromDbTime(reader.GetDateTime(1)), reader.GetInt64(2)));

            // The raw value count per hour sizes the repair reads
            Read(RawTable, true, (first, last, n) => raw[HourOf(first)] = (first, last, n));
            Read(TableOf(CompressionLevels.Coarsest.Type), false, (first, last, _) => level[HourOf(first)] = (first, last));

            return raw
                .OrderBy(h => h.Key)
                .Select(h => new HourCoverage(h.Key, h.Value.RawFirst, h.Value.RawLast,
                    level.TryGetValue(h.Key, out var l) ? l.First : null,
                    level.TryGetValue(h.Key, out l) ? l.Last : null,
                    h.Value.Count))
                .ToList();
        }

        public DateTime? GetCompressionVerifiedUntil()
        {
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM parameter WHERE key = @key";
            command.Parameters.AddWithValue("key", CompressionVerifiedKey);
            return command.ExecuteScalar() is string text
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                : null;
        }

        public void SetCompressionVerifiedUntil(DateTime time)
        {
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO parameter (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value";
            command.Parameters.AddWithValue("key", CompressionVerifiedKey);
            command.Parameters.AddWithValue("value",
                new DateTimeOffset(time).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            Log.Information("Compression levels verified until {Time}", time);
        }
        #endregion
    }
}
