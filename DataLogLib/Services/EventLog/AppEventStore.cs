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
    /// policies compress it after a day and keep it two weeks. Read by the log of a data source and the system log.
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
            return QueryAppLog(new AppLogQuery
            {
                SourceId = query.SourceId,
                BeforeUtc = query.BeforeUtc,
                AfterUtc = query.AfterUtc,
                MinLevel = query.MinLevel,
                Text = query.Text,
                Limit = query.Limit
            }, cancellationToken);
        }

        /// <summary>
        /// The application log, newest first, paged like <see cref="QuerySourceLog"/>; every filter is optional, so
        /// without one it is the log of the whole system.
        /// </summary>
        public SourceLogResult QueryAppLog(AppLogQuery query, CancellationToken cancellationToken = default)
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
            command.Parameters.AddWithValue("@from", NpgsqlDbType.TimestampTz, fromUtc);
            command.Parameters.AddWithValue("@to", NpgsqlDbType.TimestampTz, toUtc);
            where += FilterSql(query, command);
            command.CommandText = $"SELECT {ReadColumns} FROM {TableName} WHERE {where} ORDER BY time DESC LIMIT @fetch";
            command.Parameters.AddWithValue("@fetch", limit + 1);
            using (var registration = cancellationToken.Register(command.Cancel))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Items.Add(ReadRecord(reader));
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

        /// <summary>
        /// Every event from <paramref name="fromUtc"/> to <paramref name="toUtc"/> that matches the filters of
        /// <paramref name="query"/> (its paging fields are ignored), oldest first. Read one window at a time, so an
        /// export never decompresses more than a day at once and never holds more than one row.
        /// </summary>
        public async IAsyncEnumerable<EventLogRecord> ExportAppLog(
            AppLogQuery query,
            DateTime fromUtc,
            DateTime toUtc,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var oldestUtc = DateTime.UtcNow - Retention;
            if (fromUtc < oldestUtc)
            {
                fromUtc = oldestUtc;
            }

            for (var windowFrom = fromUtc; windowFrom < toUtc; windowFrom += SourceLogWindow)
            {
                var windowTo = Min(windowFrom + SourceLogWindow, toUtc);
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@from", NpgsqlDbType.TimestampTz, windowFrom);
                command.Parameters.AddWithValue("@to", NpgsqlDbType.TimestampTz, windowTo);
                var where = "time >= @from AND time < @to" + FilterSql(query, command);
                command.CommandText = $"SELECT {ReadColumns} FROM {TableName} WHERE {where} ORDER BY time";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    yield return ReadRecord(reader);
                }
            }
        }

        private const string ReadColumns =
            "time, level, kind, component, source_id, affected_source_id, source_type, message, template, exception, props, correlation_id";

        /// <summary>The filters of the query as " AND ..." conditions, with their parameters added to the command.</summary>
        private static string FilterSql(AppLogQuery query, NpgsqlCommand command)
        {
            var where = "";
            if (query.SourceId.HasValue)
            {
                where += " AND source_id = @source";
                command.Parameters.AddWithValue("@source", query.SourceId.Value);
            }

            var levels = LevelsFrom(query.MinLevel);
            if (levels != null)
            {
                where += " AND level = ANY(@levels)";
                command.Parameters.AddWithValue("@levels", levels);
            }

            if (!string.IsNullOrWhiteSpace(query.Component))
            {
                where += " AND component = @component";
                command.Parameters.AddWithValue("@component", query.Component.Trim());
            }

            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                where += " AND correlation_id = @correlation";
                command.Parameters.AddWithValue("@correlation", query.CorrelationId.Trim());
            }

            var text = EventTextFilter.Parse(query.Text);
            if (!text.IsEmpty)
            {
                where += " AND (message ILIKE @search OR exception ILIKE @search)";
                command.Parameters.AddWithValue("@search", text.LikePattern);
            }

            return where;
        }

        private static EventLogRecord ReadRecord(NpgsqlDataReader reader)
        {
            return new EventLogRecord
            {
                TimeUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                Level = reader.GetString(1),
                Kind = reader.GetString(2),
                Component = reader.GetString(3),
                SourceId = reader.GetInt32(4),
                AffectedSourceId = reader.GetInt32(5),
                SourceType = reader.GetString(6),
                Message = reader.GetString(7),
                Template = reader.GetString(8),
                Exception = reader.GetString(9),
                Props = reader.GetString(10),
                CorrelationId = reader.GetString(11)
            };
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

    /// <summary>A read of the application log; every filter is optional.</summary>
    public sealed class AppLogQuery
    {
        /// <summary>Only the events of this data source; 0 for the events of the application itself.</summary>
        public int? SourceId { get; set; }
        public DateTime? BeforeUtc { get; set; }
        public DateTime? AfterUtc { get; set; }
        public string MinLevel { get; set; }
        public string Component { get; set; }
        public string CorrelationId { get; set; }
        public string Text { get; set; }
        public int Limit { get; set; } = EventLogConstants.DefaultQueryLimit;

        /// <summary>Throws an <see cref="ArgumentException"/> for an unknown level.</summary>
        public void Validate()
        {
            AppEventStore.LevelsFrom(MinLevel);
        }
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
