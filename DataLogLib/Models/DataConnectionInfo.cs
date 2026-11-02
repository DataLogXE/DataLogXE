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

using Microsoft.Extensions.Configuration;

namespace DataAccessLib.Models
{
    internal class DataConnectionInfo
    {
        public DataConnectionInfo(IConfiguration configuration)
        {
            ConnectionString = configuration["DATABASE_URL"] ?? configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                ConnectionString = FromPostgresSettings(name => configuration[name]);
            }

            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                throw new Exception("Configuration error: Missing DATABASE_URL or POSTGRES_* configuration");
            }
        }

        public string ConnectionString { get; }

        /// <summary>
        /// The connection of the app from the environment: DATABASE_URL, else the POSTGRES_* variables, as the app
        /// builds it. The XML import child process has no configuration of its own.
        /// </summary>
        public static string FromEnvironment()
        {
            var url = Environment.GetEnvironmentVariable("DATABASE_URL");
            return string.IsNullOrWhiteSpace(url) ? FromPostgresSettings(Environment.GetEnvironmentVariable) : url;
        }

        private static string FromPostgresSettings(Func<string, string> setting)
        {
            var host = setting("POSTGRES_HOST") ?? "localhost";
            var port = setting("POSTGRES_PORT") ?? "5432";
            var database = setting("POSTGRES_DB") ?? "datalogxe";
            var user = setting("POSTGRES_USER") ?? "datalogxe";
            var password = setting("POSTGRES_PASSWORD") ?? "";
            return $"Host={host};Port={port};Database={database};Username={user};Password={password};Maximum Pool Size=14";
        }

        public override string ToString()
        {
            return "PostgreSQL data connection (credentials omitted)";
        }
    }
}
