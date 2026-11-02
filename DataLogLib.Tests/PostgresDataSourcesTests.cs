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

using DataLogLib.Services.DataAccess;
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class PostgresDataSourcesTests
    {
        // A statement run more often than Npgsql's auto-prepare threshold must still plan only the chunks of
        // its time range: a generic plan locked every chunk and filled the lock table of the Pi (0.0.209).
        [Fact]
        public void RepeatedStatementOnHypertableLocksOnlyTheChunksOfItsRange()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var dataSource = PostgresDataSources.Get(TestPostgres.ConnectionString);
            using var connection = dataSource.OpenConnection();
            Execute(connection, "DROP TABLE IF EXISTS lockprobe");
            Execute(connection, "CREATE TABLE lockprobe (time timestamptz NOT NULL, tag_id int NOT NULL)");
            Execute(connection, "SELECT create_hypertable('lockprobe', 'time', chunk_time_interval => interval '1 hour')");
            Execute(connection, "CREATE INDEX ON lockprobe (tag_id, time)");
            Execute(connection,
                "INSERT INTO lockprobe SELECT t, 1 FROM generate_series(now() - interval '200 hours', now(), interval '1 hour') t");
            try
            {
                using var transaction = connection.BeginTransaction();
                for (var i = 0; i < 10; i++)
                {
                    using var command = new NpgsqlCommand(
                        "SELECT time FROM lockprobe WHERE tag_id = @tag AND time >= @from ORDER BY time DESC LIMIT 1",
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("tag", 1);
                    command.Parameters.AddWithValue("from", DateTime.UtcNow.AddHours(-2));
                    Assert.NotNull(command.ExecuteScalar());
                }

                using var locks = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE pid = pg_backend_pid() AND locktype = 'relation'",
                    connection,
                    transaction);
                var count = Convert.ToInt64(locks.ExecuteScalar());
                Assert.True(count < 30, $"{count} relation locks for a range of 2 of 201 chunks");
                transaction.Rollback();
            }
            finally
            {
                Execute(connection, "DROP TABLE lockprobe");
            }
        }

        // Short limits, so the tests do not wait for the real ones
        private static readonly DatabaseProfile TestProfile = DatabaseProfile.Small with
        {
            UiStatementTimeout = TimeSpan.FromSeconds(1)
        };

        [Fact]
        public void EachPoolHasItsOwnNameAndLimitsAndAllUseCustomPlans()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, DatabaseProfile.Small);
            const string Settings = "SELECT current_setting('application_name') || '|' || current_setting('statement_timeout') || '|' "
                + "|| current_setting('lock_timeout') || '|' || current_setting('plan_cache_mode')";
            using (var connection = pools.OpenConnection())
            {
                Assert.Equal("DataLogXE|0|0|force_custom_plan", Scalar(connection, Settings));
            }
            using (var connection = pools.OpenIngestConnection())
            {
                Assert.Equal("DataLogXE ingest|0|5s|force_custom_plan", Scalar(connection, Settings));
            }
            using (DatabaseLimits.Use(PostgresWork.Ui))
            using (var connection = pools.OpenConnection())
            {
                Assert.Equal("DataLogXE ui|1min|0|force_custom_plan", Scalar(connection, Settings));
                Assert.Equal("4MB", Scalar(connection, "SELECT current_setting('work_mem')"));
            }
            using (DatabaseLimits.Use(PostgresWork.Background))
            using (var connection = pools.OpenConnection())
            {
                Assert.Equal("DataLogXE background|10min|0|force_custom_plan", Scalar(connection, Settings));
            }
            Assert.Equal(16, DatabaseProfile.Small.TotalConnections);
            Assert.True(DatabaseProfile.Large.TotalConnections <= 40 - 4, "max_connections in compose.yaml leaves room for psql");
        }

        [Fact]
        public async Task BackgroundWorkWaitsForAConnectionOfItsSmallPool()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, DatabaseProfile.Small);
            // One more worker than the pool has connections: it waits until one is free, the inserts do not
            var started = System.Diagnostics.Stopwatch.StartNew();
            var workers = Enumerable.Range(0, DatabaseProfile.Small.BackgroundConnections + 1)
                .Select(_ => Task.Run(() => DatabaseLimits.Background(() =>
                {
                    using var connection = pools.OpenConnection();
                    Scalar(connection, "SELECT pg_sleep(1)::text");
                })))
                .ToList();
            await Task.Delay(300);
            using (var connection = pools.OpenIngestConnection())
            {
                Assert.Equal("1", Scalar(connection, "SELECT 1::text"));
            }
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"insert waited until {started.Elapsed}");
            await Task.WhenAll(workers);
            Assert.True(started.Elapsed >= TimeSpan.FromSeconds(2), $"all done after {started.Elapsed}");
        }

        [Fact]
        public void AReadOfTheUiEndsAtTheStatementTimeout()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, TestProfile);
            using var scope = DatabaseLimits.Use(PostgresWork.Ui);
            using var connection = pools.OpenConnection();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var error = Assert.Throws<PostgresException>(() => Scalar(connection, "SELECT pg_sleep(10)::text"));
            Assert.Equal(PostgresErrorCodes.QueryCanceled, error.SqlState);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"ended after {clock.Elapsed}");
        }

        [Fact]
        public void TheTokenOfTheScopeCancelsAStatementThatHasNoToken()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, DatabaseProfile.Small);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            int processId;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (DatabaseLimits.Use(PostgresWork.Ui, cts.Token))
            using (var connection = pools.OpenConnection())
            {
                processId = connection.ProcessID;
                // As a Dapper query of a store: a command without a token
                var error = Assert.Throws<PostgresException>(() => Scalar(connection, "SELECT pg_sleep(20)::text"));
                Assert.Equal(PostgresErrorCodes.QueryCanceled, error.SqlState);
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"ended after {clock.Elapsed}");
            using var check = pools.OpenIngestConnection();
            Assert.Equal("0", Scalar(check, $"SELECT count(*)::text FROM pg_stat_activity WHERE pid = {processId} AND state = 'active'"));
        }

        // A cancel that comes late, when the backend already runs the statement of the next request, must not stop
        // that statement: "57014: canceling statement due to user request" in the chart editor on the Pi (0.0.245)
        [Fact]
        public void ALateCancelLeavesTheNextStatementOnTheSameBackendAlone()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, DatabaseProfile.Small);
            using var cts = new CancellationTokenSource();
            int processId;
            string marker;
            using (DatabaseLimits.Use(PostgresWork.Ui, cts.Token))
            using (var connection = pools.OpenConnection())
            {
                processId = connection.ProcessID;
                marker = Scalar(connection, "SELECT current_setting('application_name')");
            }
            Assert.StartsWith("DataLogXE ui #", marker);

            using (DatabaseLimits.Use(PostgresWork.Ui))
            using (var connection = pools.OpenConnection())
            {
                Assert.Equal(processId, connection.ProcessID);
                Assert.Equal("DataLogXE ui", Scalar(connection, "SELECT current_setting('application_name')"));
                var next = Task.Run(() => Scalar(connection, "SELECT pg_sleep(1.5)::text"));
                Thread.Sleep(300);
                // As the cancel of the first request that found its connection still open
                pools.CancelBackend(processId, marker, () => true);
                Assert.Equal("", next.GetAwaiter().GetResult());
            }
        }

        [Fact]
        public void InsertsGetAConnectionWhileTheUiPoolIsFull()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, TestProfile);
            var busy = Enumerable.Range(0, TestProfile.UiConnections).Select(_ => Task.Run(() =>
            {
                using var scope = DatabaseLimits.Use(PostgresWork.Ui);
                using var connection = pools.OpenConnection();
                try
                {
                    Scalar(connection, "SELECT pg_sleep(5)::text");
                }
                catch (PostgresException)
                {
                    // The statement timeout of the test profile (1 s)
                }
            })).ToArray();
            Thread.Sleep(300);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var connection = pools.OpenIngestConnection())
            {
                Assert.Equal("1", Scalar(connection, "SELECT 1::text"));
            }
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"waited {clock.Elapsed}");
            Task.WaitAll(busy);
        }

        [Fact]
        public void WorkThatOutlivesItsScopeUsesTheDefaultPool()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            var pools = PostgresDataSources.Create(TestPostgres.ConnectionString, TestProfile);
            using var cts = new CancellationTokenSource();
            Task<string> later;
            using (DatabaseLimits.Use(PostgresWork.Ui, cts.Token))
            {
                // As a timer of a service that is created within a request
                later = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    using var connection = pools.OpenConnection();
                    return Scalar(connection, "SELECT current_setting('application_name')");
                });
            }
            cts.Cancel();
            Assert.Equal("DataLogXE", later.GetAwaiter().GetResult());
        }

        private static string Scalar(NpgsqlConnection connection, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection);
            return (string)command.ExecuteScalar();
        }

        private static void Execute(NpgsqlConnection connection, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }
    }
}
