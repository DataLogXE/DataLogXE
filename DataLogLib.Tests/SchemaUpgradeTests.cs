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
    public class SchemaUpgradeTests
    {
        [Fact]
        public void Baseline_creates_the_current_schema_on_an_empty_database()
        {
            WithEmptyDatabase((connection, scriptsFolder) =>
            {
                PostgresSchemaUpgrade.Apply(connection, scriptsFolder);

                Assert.Equal(PostgresSchemaUpgrade.BaselineVersion.ToString(), Scalar(connection,
                    "SELECT value FROM parameter WHERE key = 'VERSION'"));
                // The system tags without ArchiveDiskSizeGb (4), which the System data source replaced at its start
                Assert.Equal("1,2,3,5,6,7", Scalar(connection, "SELECT string_agg(id::text, ',' ORDER BY id) FROM datatag"));
                Assert.Equal("mincycle=0", Scalar(connection,
                    "SELECT string_agg(key || '=' || value, ',') FROM datasourceproperty WHERE datasourceid = 1"));
                Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM processeventcountstate WHERE live_from_id IS NULL"));
                // An empty counted range at the minute of the installation
                Assert.Equal("1", Scalar(connection,
                    "SELECT count(DISTINCT value) FROM parameter WHERE key IN ('ARCHIVE_COUNT_FROM', 'ARCHIVE_COUNT_UNTIL')"));
                Assert.Equal("True", Scalar(connection,
                    "SELECT abs(value::bigint - extract(epoch FROM now())) < 120 AND value::bigint % 60 = 0 "
                    + "FROM parameter WHERE key = 'ARCHIVE_COUNT_FROM'"));
                // One retention of 90 days for the archive tables and the process events, two weeks for the log
                Assert.Equal("14", Scalar(connection,
                    "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' "
                    + "AND hypertable_name <> 'appevent' AND config->>'drop_after' = '90 days'"));
                Assert.Equal("14 days", Scalar(connection,
                    "SELECT config->>'drop_after' FROM timescaledb_information.jobs "
                    + "WHERE proc_name = 'policy_retention' AND hypertable_name = 'appevent'"));
                Assert.Equal("archivec001=02:00:00,archivec005=12:00:00,archivec010=12:00:00,archiveraw=00:30:00", Scalar(connection,
                    "SELECT string_agg(hypertable_name || '=' || time_interval, ',' ORDER BY hypertable_name) "
                    + "FROM timescaledb_information.dimensions "
                    + "WHERE hypertable_name IN ('archiveraw', 'archivec001', 'archivec005', 'archivec010')"));
                Assert.Equal("source_id", Scalar(connection,
                    "SELECT attname FROM timescaledb_information.compression_settings "
                    + "WHERE hypertable_name = 'appevent' AND segmentby_column_index = 1"));
                Assert.Throws<PostgresException>(() => Execute(connection,
                    "INSERT INTO datatag (guid, datasourceid, name, address) VALUES ('00000000000000000000000000000001', 1, 'dup', 'dup')"));
                Assert.Throws<PostgresException>(() => Execute(connection,
                    "INSERT INTO appuser (name, passwordhash, role, securitystamp) VALUES ('a', 'h', 'guest', 's')"));

                // A second start finds the schema current
                PostgresSchemaUpgrade.Apply(connection, scriptsFolder);
                Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM datasource"));
            });
        }

        [Fact]
        public void Database_below_the_baseline_is_not_upgraded()
        {
            WithEmptyDatabase((connection, scriptsFolder) =>
            {
                Execute(connection, "CREATE TABLE parameter (key text NOT NULL PRIMARY KEY, value text)");
                Execute(connection, $"INSERT INTO parameter (key, value) VALUES ('VERSION', '{PostgresSchemaUpgrade.BaselineVersion - 1}')");

                var ex = Assert.Throws<InvalidOperationException>(() => PostgresSchemaUpgrade.Apply(connection, scriptsFolder));
                Assert.Contains("older than the baseline", ex.Message);
            });
        }

        private static void WithEmptyDatabase(Action<NpgsqlConnection, string> test)
        {
            var connectionString = TestPostgres.ConnectionString;
            if (connectionString == null)
            {
                return;
            }

            using var admin = new NpgsqlConnection(connectionString);
            admin.Open();
            var databaseName = "datalogxe_test_" + Guid.NewGuid().ToString("N");
            using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                create.ExecuteNonQuery();
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName };
                using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Open();
                test(connection, Path.Combine(RepoRoot(), "DataLogLib", "Templates", "postgres"));
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
                drop.ExecuteNonQuery();
            }
        }

        private static void Execute(NpgsqlConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static string Scalar(NpgsqlConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar());
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "DataLogXE.slnx")))
            {
                dir = Directory.GetParent(dir)?.FullName;
            }

            return dir ?? throw new InvalidOperationException("Repository root was not found.");
        }
    }

    internal static class TestPostgres
    {
        public static string ConnectionString =>
            Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL");
    }
}
