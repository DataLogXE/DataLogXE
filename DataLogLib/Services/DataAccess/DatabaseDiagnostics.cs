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

namespace DataLogLib.Services.DataAccess
{
    /// <summary>
    /// The state of the database for the support bundle: versions, sizes, compression and the background jobs with
    /// their last errors. Each part is read on its own, so one that fails (named in <c>errors</c>) leaves the others.
    /// </summary>
    public static class DatabaseDiagnostics
    {
        private static readonly (string Name, string Sql)[] Queries =
        [
            ("versions",
                "SELECT version() AS postgres, (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb') AS timescaledb, "
                + "pg_database_size(current_database()) AS database_bytes, now() AS server_time, "
                + "pg_postmaster_start_time() AS server_started"),
            ("hypertables",
                "SELECT h.hypertable_name, h.num_chunks, h.compression_enabled, "
                + "hypertable_size(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass) AS total_bytes, "
                + "(SELECT count(*) FROM timescaledb_information.chunks c WHERE c.hypertable_name = h.hypertable_name AND c.is_compressed) AS compressed_chunks, "
                + "(SELECT min(range_start) FROM timescaledb_information.chunks c WHERE c.hypertable_name = h.hypertable_name) AS oldest_chunk "
                + "FROM timescaledb_information.hypertables h ORDER BY h.hypertable_name"),
            ("jobs",
                "SELECT j.job_id, j.application_name, j.proc_name, j.hypertable_name, j.schedule_interval::text AS schedule_interval, "
                + "s.last_run_status, s.last_run_started_at, s.last_successful_finish, s.last_run_duration::text AS last_run_duration, "
                + "s.next_start, s.total_runs, s.total_failures "
                + "FROM timescaledb_information.jobs j LEFT JOIN timescaledb_information.job_stats s ON s.job_id = j.job_id "
                + "ORDER BY j.job_id"),
            ("job_errors",
                "SELECT job_id, proc_name, start_time, finish_time, sqlerrcode, err_message "
                + "FROM timescaledb_information.job_errors ORDER BY start_time DESC LIMIT 50"),
            ("connections",
                "SELECT application_name, state, wait_event_type, count(*) AS count, max(now() - query_start)::text AS longest_query "
                + "FROM pg_stat_activity WHERE datname = current_database() GROUP BY 1, 2, 3 ORDER BY 4 DESC"),
            ("settings",
                "SELECT name, setting, unit FROM pg_settings WHERE name IN ('shared_buffers', 'max_connections', "
                + "'max_worker_processes', 'timescaledb.max_background_workers', 'checkpoint_timeout', 'max_wal_size', "
                + "'work_mem', 'effective_cache_size', 'wal_compression') ORDER BY name")
        ];

        public static Dictionary<string, object> Read(IConfiguration configuration, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            PostgresPools pools;
            try
            {
                pools = PostgresDataSources.Get(new DataConnectionInfo(configuration).ConnectionString);
            }
            catch (Exception ex)
            {
                result["errors"] = new Dictionary<string, string> { ["connection"] = ex.Message };
                return result;
            }

            foreach (var (name, sql) in Queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var connection = pools.OpenConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.CommandTimeout = 30;
                    using var registration = cancellationToken.Register(command.Cancel);
                    using var reader = command.ExecuteReader();
                    var rows = new List<Dictionary<string, object>>();
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>(StringComparer.Ordinal);
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }

                        rows.Add(row);
                    }

                    result[name] = rows;
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    errors[name] = Common.Serilog.LogRedaction.Redact(ex.Message);
                }
            }

            if (errors.Count > 0)
            {
                result["errors"] = errors;
            }

            return result;
        }
    }
}
