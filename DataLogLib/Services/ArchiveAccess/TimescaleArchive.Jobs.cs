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

        /// <summary>
        /// Pauses the compression and retention policies of TimescaleDB while the load guard is tense or critical, and
        /// schedules them again once it is normal. They run as background workers in the database, outside the pools of
        /// the app: a compression of a chunk reads and rewrites it on the SD card. Critical also cancels a compression
        /// that runs; it loses only the chunk it was at, which the next run compresses. Policies that were unscheduled
        /// before (by hand) are left as they are.
        /// </summary>
        public void ApplyLoadLevel(LoadLevel level)
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
                    if (level == LoadLevel.Critical)
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
            }
        }

        private void PauseJobs()
        {
            using var connection = _dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The ids are stored before the jobs are paused, in one transaction
            command.CommandText = "WITH jobs AS (SELECT job_id FROM timescaledb_information.jobs "
                + "WHERE proc_name IN ('policy_compression', 'policy_retention') AND scheduled), "
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
            command.CommandText = "SELECT count(pg_cancel_backend(pid)) FROM pg_stat_activity "
                + "WHERE application_name LIKE 'Compression Policy%' OR backend_type LIKE 'Compression Policy%'";
            var cancelled = Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            if (cancelled > 0)
            {
                Log.Warning("A running compression of TimescaleDB was cancelled by the load guard");
            }
        }
    }
}
