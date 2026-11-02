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
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Reflection;
using Xunit;

namespace DataLogLib.Tests
{
    [CollectionDefinition(PostgresTestConfiguration.Collection, DisableParallelization = true)]
    public sealed class PostgresCollection
    {
    }

    internal static class PostgresTestConfiguration
    {
        /// <summary>Tests sharing the test database must not run in parallel.</summary>
        public const string Collection = "Postgres";

        public static IConfiguration Create(string folder = null)
        {
            var settings = new Dictionary<string, string>();
            var connectionString = TestPostgres.ConnectionString;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                settings["DATABASE_URL"] = connectionString;
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                settings["DATA_PATH"] = folder;
            }

            return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        }

        public static bool IsAvailable => !string.IsNullOrWhiteSpace(TestPostgres.ConnectionString);

        public static void EnsureBaselineApplied()
        {
            if (!IsAvailable)
            {
                return;
            }

            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var connection = dataSource.OpenConnection();
            var scriptsFolder = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "Templates",
                "postgres");
            if (!Directory.Exists(scriptsFolder))
            {
                scriptsFolder = Path.Combine(FindRepoRoot(), "DataLogLib", "Templates", "postgres");
            }

            PostgresSchemaUpgrade.Apply(connection, scriptsFolder);
        }

        private static string FindRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "DataLogXE.slnx")))
            {
                dir = Directory.GetParent(dir)?.FullName;
            }

            return dir ?? AppContext.BaseDirectory;
        }
    }
}
