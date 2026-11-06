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
using Serilog;

namespace DataLogLib.Services.ArchiveAccess
{
    public partial class TimescaleArchive
    {
        // The jobs the load guard paused, so a restart while they were paused schedules them again
        internal const string PausedJobsKey = "LOAD_GUARD_PAUSED_JOBS";

        private readonly object _jobsLock = new();

        // The compression jobs the load guard cancelled, started again at once when it is normal (a cancel counts as a
        // failure, after which TimescaleDB waits the retry period, an hour for the raw values)
        private readonly HashSet<int> _cancelledJobs = [];

        /// <summary>
        /// Pauses the compression and retention policies of TimescaleDB while the load guard is tense or critical, and
        /// schedules them again once it is normal. They run as background workers in the database, outside the pools of
        /// the app: a compression of a chunk reads and rewrites it on the SD card. A policy that runs is not paused:
        /// TimescaleDB stops a running job that is unscheduled, and the compression of a large chunk raises the load
        /// itself, so it would be stopped again on every run and no chunk would ever be compressed (seen on an IOT2050,
        /// 22 runs in a row stopped after about 2 minutes). It is paused once it is done; call this again while the
        /// level lasts. A running compression is cancelled only when the recording is at risk; it loses only the chunk
        /// it was at, and it starts again as soon as the guard is normal. Policies that were unscheduled before (by
        /// hand) are left as they are.
        /// </summary>
        public void ApplyLoadLevel(LoadLevel level, bool recordingAtRisk = false)
        {
            lock (_jobsLock)
            {
                try
                {
                    if (level == LoadLevel.Normal)
                    {
                        ResumePausedJobs();
                        return;
                    }

                    PauseJobs();
                    if (level == LoadLevel.Critical && recordingAtRisk)
                    {
                        CancelRunningCompression();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "The TimescaleDB policies could not be paused or scheduled again");
                }
            }
        }

        /// <summary>Schedules the policies again that the load guard paused, also those of the last run of the app.</summary>
        public void ResumePausedJobs()
        {
            lock (_jobsLock)
            {
                using var connection = _dataSource.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "WITH paused AS (SELECT unnest(string_to_array(value, ','))::int AS job_id FROM parameter WHERE key = @key AND value <> '') "
                    + "SELECT count(*) FROM paused p JOIN timescaledb_information.jobs j ON j.job_id = p.job_id, "
                    + "LATERAL alter_job(p.job_id, scheduled => true)";
                command.Parameters.AddWithValue("key", PausedJobsKey);
                var resumed = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                using var clear = connection.CreateCommand();
                clear.CommandText = "DELETE FROM parameter WHERE key = @key";
                clear.Parameters.AddWithValue("key", PausedJobsKey);
                clear.ExecuteNonQuery();
                if (resumed > 0)
                {
                    Log.Information("TimescaleDB policies scheduled again: {Count}", resumed);
                }

                if (_cancelledJobs.Count > 0)
                {
                    using var restart = connection.CreateCommand();
                    restart.CommandText = "SELECT count(*) FROM timescaledb_information.jobs j, "
                        + "LATERAL alter_job(j.job_id, next_start => now()) WHERE j.job_id = ANY(@ids) AND j.scheduled";
                    restart.Parameters.AddWithValue("ids", _cancelledJobs.ToArray());
                    var restarted = Convert.ToInt64(restart.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                    _cancelledJobs.Clear();
                    if (restarted > 0)
                    {
                        Log.Information("TimescaleDB compression cancelled by the load guard starts again: {Count}", restarted);
                    }
                }
            }
        }

        private void PauseJobs()
        {
            using var connection = _dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The ids are stored before the jobs are paused, in one transaction; a job that runs is left scheduled
            command.CommandText = "WITH jobs AS (SELECT j.job_id FROM timescaledb_information.jobs j "
                + "WHERE j.proc_name IN ('policy_compression', 'policy_retention') AND j.scheduled "
                + "AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.application_name = j.application_name)), "
                + "stored AS (INSERT INTO parameter (key, value) "
                + "SELECT @key, coalesce(string_agg(job_id::text, ','), '') FROM (SELECT job_id FROM jobs UNION "
                + "SELECT unnest(string_to_array(value, ','))::int FROM parameter WHERE key = @key AND value <> '') ids "
                + "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value) "
                + "SELECT count(*) FROM jobs, LATERAL alter_job(jobs.job_id, scheduled => false)";
            command.Parameters.AddWithValue("key", PausedJobsKey);
            var paused = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            transaction.Commit();
            if (paused > 0)
            {
                Log.Information("TimescaleDB policies paused by the load guard: {Count}", paused);
            }
        }

        private void CancelRunningCompression()
        {
            using var connection = _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            // The cancel in the select list of the materialized match: in a WHERE the planner may push it down to the scan
            // of pg_stat_activity, where it would cancel every session
            command.CommandText = "WITH running AS MATERIALIZED (SELECT j.job_id, a.pid FROM timescaledb_information.jobs j "
                + "JOIN pg_stat_activity a ON a.application_name = j.application_name WHERE j.proc_name = 'policy_compression') "
                + "SELECT job_id FROM running WHERE (SELECT pg_cancel_backend(running.pid))";
            using var reader = command.ExecuteReader();
            var cancelled = 0;
            while (reader.Read())
            {
                _cancelledJobs.Add(reader.GetInt32(0));
                cancelled++;
            }
            if (cancelled > 0)
            {
                Log.Warning("A running compression of TimescaleDB was cancelled by the load guard, the recording is at risk");
            }
        }
    }
}
