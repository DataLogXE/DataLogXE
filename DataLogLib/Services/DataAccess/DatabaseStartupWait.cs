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
using Serilog;
using System.Diagnostics;
using System.Globalization;

namespace DataLogLib.Services.DataAccess
{
    /// <summary>
    /// Waits at startup until PostgreSQL accepts connections. After a restart of the host the database
    /// can recover from its WAL for minutes (a Raspberry Pi 3 replays about 4 MB/s), while the restart
    /// policy of Docker starts the service at once and does not honor depends_on. A refused connection
    /// is therefore expected at startup and must not end the service.
    /// </summary>
    public static class DatabaseStartupWait
    {
        /// <summary>Seconds to wait for the database at startup, 0 = do not wait.</summary>
        public const string TimeoutKey = "DATABASE_WAIT_SECONDS";

        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Blocks until a connection to the configured database can be opened. A refused connection, a
        /// timeout and the states of PostgreSQL that pass by themselves (starting up, recovery, shutting
        /// down) are retried; anything else (wrong password, missing database) and the end of the
        /// waiting time throw the error, so the process ends and the restart policy starts it again.
        /// </summary>
        public static void Run(IConfiguration configuration)
        {
            var info = new DataConnectionInfo(configuration);
            var dataSource = PostgresDataSources.Get(info.ConnectionString);
            var clock = Stopwatch.StartNew();
            Run(
                () =>
                {
                    using var connection = dataSource.OpenConnection();
                },
                GetTimeout(configuration),
                () => clock.Elapsed,
                Thread.Sleep);
        }

        internal static void Run(Action connect, TimeSpan timeout, Func<TimeSpan> elapsed, Action<TimeSpan> sleep)
        {
            var delay = FirstDelay;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    connect();
                    if (attempt > 1)
                    {
                        Log.Information("Database available after {Seconds} s", (int)elapsed().TotalSeconds);
                    }

                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (elapsed() + delay > timeout)
                    {
                        Log.Error("Database not available after {Seconds} s, giving up: {Reason}", (int)elapsed().TotalSeconds, Describe(ex));
                        throw;
                    }

                    Log.Warning("Database not available yet, retry in {Seconds} s: {Reason}", delay.TotalSeconds, Describe(ex));
                    sleep(delay);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
                }
            }
        }

        internal static TimeSpan GetTimeout(IConfiguration configuration)
        {
            var text = configuration[TimeoutKey];
            if (string.IsNullOrWhiteSpace(text))
            {
                return DefaultTimeout;
            }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                Log.Error("Invalid {Key} {Value}, using {Default} s", TimeoutKey, text, (int)DefaultTimeout.TotalSeconds);
                return DefaultTimeout;
            }
            return TimeSpan.FromSeconds(seconds);
        }

        // PostgreSQL answers 57P03 (cannot_connect_now) while it starts up, recovers or shuts down
        private static bool IsTransient(Exception ex) => ex is NpgsqlException { IsTransient: true };

        private static string Describe(Exception ex)
        {
            if (ex is PostgresException postgres)
            {
                return $"{postgres.SqlState} {postgres.MessageText}";
            }
            return ex.InnerException == null ? ex.Message : $"{ex.Message}: {ex.InnerException.Message}";
        }
    }
}
