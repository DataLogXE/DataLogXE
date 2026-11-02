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
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// The application log (every event but the process events) in the appevent hypertable. The Timescale
    /// policies compress it after a day and keep it two weeks. Read only by the log of a data source.
    /// </summary>
    internal sealed class AppEventStore
    {
        public const string TableName = "appevent";

        /// <summary>One read of the log of a data source spans at most this much time, so it never decompresses two weeks.</summary>
        public static readonly TimeSpan SourceLogWindow = TimeSpan.FromDays(1);
        public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

        private static readonly string[] LevelOrder = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
        private static readonly string[] ProblemLevels = ["Warning", "Error", "Fatal"];

        private const string Columns =
            "time, level, kind, component, source_id, affected_source_id, source_type, message, template, "
            + "exception, props, correlation_id";

        private readonly DataAccess.PostgresPools _dataSource;

        public AppEventStore(IConfiguration configuration)
        {
            var info = new DataConnectionInfo(configuration);
            _dataSource = DataAccess.PostgresDataSources.Get(info.ConnectionString);
        }

        /// <summary>Writes the events and throws when the database does not accept them.</summary>
        public void InsertEventPoints(IReadOnlyList<EventLogPoint> points)
        {
            if (points == null || points.Count == 0)
            {
                return;
            }

            using var connection = _dataSource.OpenIngestConnection();
            using var writer = connection.BeginBinaryImport($"COPY {TableName} ({Columns}) FROM STDIN (FORMAT BINARY)");
            foreach (var point in points)
            {
                writer.StartRow();
                var time = point.TimeUtc.Kind == DateTimeKind.Local
                    ? point.TimeUtc.ToUniversalTime()
                    : DateTime.SpecifyKind(point.TimeUtc, DateTimeKind.Utc);
                writer.Write(time, NpgsqlDbType.TimestampTz);
                writer.Write(point.Level ?? "", NpgsqlDbType.Text);
                writer.Write(point.Kind ?? "", NpgsqlDbType.Text);
                writer.Write(point.Component ?? "", NpgsqlDbType.Text);
                writer.Write(point.SourceId, NpgsqlDbType.Integer);
                writer.Write(point.AffectedSourceId, NpgsqlDbType.Integer);
                writer.Write(point.SourceType ?? "", NpgsqlDbType.Text);
                writer.Write(point.Message ?? "", NpgsqlDbType.Text);
                writer.Write(point.Template ?? "", NpgsqlDbType.Text);
                writer.Write(point.Exception ?? "", NpgsqlDbType.Text);
                writer.Write(point.Props ?? "", NpgsqlDbType.Text);
                writer.Write(point.CorrelationId?.Trim() ?? "", NpgsqlDbType.Text);
            }

            writer.Complete();
        }

        /// <summary>
        /// The events of a data source, newest first: older than <see cref="SourceLogQuery.BeforeUtc"/> (back to one
        /// window before it) or, for the live view, newer than <see cref="SourceLogQuery.AfterUtc"/>.
        /// </summary>
        public SourceLogResult QuerySourceLog(SourceLogQuery query, CancellationToken cancellationToken = default)
        {
            var limit = Math.Clamp(query.Limit, 1, EventLogConstants.MaxQueryLimit);
            var nowUtc = DateTime.UtcNow;
            var toUtc = query.AfterUtc.HasValue ? nowUtc.AddMinutes(1) : Min(query.BeforeUtc ?? nowUtc.AddMinutes(1), nowUtc.AddMinutes(1));
            var oldestUtc = nowUtc - Retention;
            var fromUtc = query.AfterUtc ?? toUtc - SourceLogWindow;
            if (fromUtc < oldestUtc)
            {
                fromUtc = oldestUtc;
            }

            var result = new SourceLogResult();
            if (fromUtc >= toUtc)
            {
                return result;
            }

            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            var where = query.AfterUtc.HasValue ? "time > @from AND time < @to" : "time >= @from AND time < @to";
            command.Parameters.AddWithValue("@source", query.SourceId);
            command.Parameters.AddWithValue("@from", NpgsqlDbType.TimestampTz, fromUtc);
            command.Parameters.AddWithValue("@to", NpgsqlDbType.TimestampTz, toUtc);
            var levels = LevelsFrom(query.MinLevel);
            if (levels != null)
            {
                where += " AND level = ANY(@levels)";
                command.Parameters.AddWithValue("@levels", levels);
            }

            var text = EventTextFilter.Parse(query.Text);
            if (!text.IsEmpty)
            {
                where += " AND (message ILIKE @search OR exception ILIKE @search)";
                command.Parameters.AddWithValue("@search", text.LikePattern);
            }

            command.CommandText = "SELECT time, level, component, source_type, message, exception, props "
                + $"FROM {TableName} WHERE source_id = @source AND {where} ORDER BY time DESC LIMIT @fetch";
            command.Parameters.AddWithValue("@fetch", limit + 1);
            using (var registration = cancellationToken.Register(command.Cancel))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Items.Add(new EventLogRecord
                    {
                        TimeUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                        Level = reader.GetString(1),
                        Kind = "datasource",
                        Component = reader.GetString(2),
                        SourceId = query.SourceId,
                        SourceType = reader.GetString(3),
                        Message = reader.GetString(4),
                        Exception = reader.GetString(5),
                        Props = reader.GetString(6)
                    });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (result.Items.Count > limit)
            {
                result.Items.RemoveRange(limit, result.Items.Count - limit);
                result.Truncated = true;
            }

            if (!query.AfterUtc.HasValue)
            {
                // A full page goes on before its oldest event, a short one before the window; nothing is older than the retention
                if (result.Truncated)
                {
                    result.NextBeforeUtc = result.Items[^1].TimeUtc;
                }
                else if (fromUtc > oldestUtc)
                {
                    result.NextBeforeUtc = fromUtc;
                }
            }

            return result;
        }

        /// <summary>Warnings and errors of every data source since <paramref name="fromUtc"/>, by source and level.</summary>
        public List<SourceLogCount> QuerySourceLogCounts(DateTime fromUtc, CancellationToken cancellationToken = default)
        {
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = "SELECT source_id, level, COUNT(*) "
                + $"FROM {TableName} WHERE time >= @from AND source_id > 0 AND level = ANY(@levels) GROUP BY source_id, level";
            command.Parameters.AddWithValue("@from", NpgsqlDbType.TimestampTz, fromUtc);
            command.Parameters.AddWithValue("@levels", ProblemLevels);
            var counts = new List<SourceLogCount>();
            using var registration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts.Add(new SourceLogCount(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return counts;
        }

        /// <summary>The levels from <paramref name="minLevel"/> up; null for all of them.</summary>
        internal static string[] LevelsFrom(string minLevel)
        {
            if (string.IsNullOrWhiteSpace(minLevel))
            {
                return null;
            }

            var index = Array.FindIndex(LevelOrder, level => string.Equals(level, minLevel.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new ArgumentException("Invalid level.");
            }

            return index == 0 ? null : LevelOrder[index..];
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }

    public sealed class SourceLogQuery
    {
        public int SourceId { get; set; }
        public DateTime? BeforeUtc { get; set; }
        public DateTime? AfterUtc { get; set; }
        public string MinLevel { get; set; }
        public string Text { get; set; }
        public int Limit { get; set; } = EventLogConstants.DefaultQueryLimit;
    }

    public sealed class SourceLogResult
    {
        public List<EventLogRecord> Items { get; } = [];
        /// <summary>More events than the limit in the range: the live view missed some and reloads.</summary>
        public bool Truncated { get; set; }
        /// <summary>Where the next older page starts; null at the end of the retention.</summary>
        public DateTime? NextBeforeUtc { get; set; }
    }

    public sealed record SourceLogCount(int SourceId, string Level, long Count);
}
