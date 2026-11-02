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
using System.Net.Sockets;
using Xunit;

namespace DataLogLib.Tests
{
    public class DatabaseStartupWaitTests
    {
        // A refused connection, as Npgsql reports it
        private static NpgsqlException Refused() =>
            new("Failed to connect to 127.0.0.1:5432", new SocketException((int)SocketError.ConnectionRefused));

        // PostgreSQL while it recovers from its WAL
        private static PostgresException Recovering() =>
            new("the database system is not yet accepting connections", "FATAL", "FATAL", PostgresErrorCodes.CannotConnectNow);

        private static PostgresException WrongPassword() =>
            new("password authentication failed", "FATAL", "FATAL", PostgresErrorCodes.InvalidPassword);

        // The clock only moves when the wait sleeps
        private sealed class FakeWait
        {
            private TimeSpan _now = TimeSpan.Zero;

            public List<TimeSpan> Sleeps { get; } = [];
            public int Attempts { get; private set; }

            public void Run(Func<int, Exception> failure, TimeSpan timeout)
            {
                DatabaseStartupWait.Run(
                    () =>
                    {
                        Attempts++;
                        var error = failure(Attempts);
                        if (error != null)
                        {
                            throw error;
                        }
                    },
                    timeout,
                    () => _now,
                    delay =>
                    {
                        Sleeps.Add(delay);
                        _now += delay;
                    });
            }
        }

        private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
                .Build();

        [Fact]
        public void Run_returns_at_once_when_the_database_is_available()
        {
            var wait = new FakeWait();

            wait.Run(_ => null, TimeSpan.FromMinutes(10));

            Assert.Equal(1, wait.Attempts);
            Assert.Empty(wait.Sleeps);
        }

        [Fact]
        public void Run_retries_a_refused_connection_with_growing_delays()
        {
            var wait = new FakeWait();

            wait.Run(attempt => attempt <= 6 ? Refused() : null, TimeSpan.FromMinutes(10));

            Assert.Equal(7, wait.Attempts);
            Assert.Equal([1, 2, 4, 8, 10, 10], wait.Sleeps.Select(s => (int)s.TotalSeconds));
        }

        [Fact]
        public void Run_retries_while_the_database_recovers()
        {
            var wait = new FakeWait();

            wait.Run(attempt => attempt <= 3 ? Recovering() : null, TimeSpan.FromMinutes(10));

            Assert.Equal(4, wait.Attempts);
            Assert.Equal(3, wait.Sleeps.Count);
        }

        [Fact]
        public void Run_throws_the_last_error_when_the_waiting_time_is_over()
        {
            var wait = new FakeWait();

            var error = Assert.Throws<NpgsqlException>(() => wait.Run(_ => Refused(), TimeSpan.FromSeconds(30)));

            Assert.IsType<SocketException>(error.InnerException);
            Assert.Equal(6, wait.Attempts);
            Assert.True(wait.Sleeps.Sum(s => s.TotalSeconds) <= 30);
        }

        [Fact]
        public void Run_does_not_wait_when_the_timeout_is_zero()
        {
            var wait = new FakeWait();

            Assert.Throws<NpgsqlException>(() => wait.Run(_ => Refused(), TimeSpan.Zero));

            Assert.Equal(1, wait.Attempts);
            Assert.Empty(wait.Sleeps);
        }

        [Fact]
        public void Run_does_not_retry_an_error_that_does_not_pass_by_itself()
        {
            var wait = new FakeWait();

            var error = Assert.Throws<PostgresException>(() => wait.Run(_ => WrongPassword(), TimeSpan.FromMinutes(10)));

            Assert.Equal(PostgresErrorCodes.InvalidPassword, error.SqlState);
            Assert.Equal(1, wait.Attempts);
            Assert.Empty(wait.Sleeps);
        }

        [Fact]
        public void Run_does_not_retry_an_error_that_is_not_a_connection_error()
        {
            var wait = new FakeWait();

            Assert.Throws<InvalidOperationException>(() =>
                wait.Run(_ => new InvalidOperationException("bug"), TimeSpan.FromMinutes(10)));

            Assert.Equal(1, wait.Attempts);
        }

        [Fact]
        public void Run_with_a_configuration_gives_up_on_a_port_nobody_listens_on()
        {
            // Port 1 refuses at once; the real Npgsql error has to count as a connection error that is retried
            var configuration = Configuration(
                ("DATABASE_URL", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2"),
                (DatabaseStartupWait.TimeoutKey, "2"));

            var started = DateTime.UtcNow;
            Assert.ThrowsAny<NpgsqlException>(() => DatabaseStartupWait.Run(configuration));

            Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(0.9), "the wait did not retry");
        }

        [Theory]
        [InlineData("30", 30)]
        [InlineData("0", 0)]
        [InlineData("600", 600)]
        public void GetTimeout_reads_the_seconds(string text, int seconds)
        {
            var timeout = DatabaseStartupWait.GetTimeout(Configuration((DatabaseStartupWait.TimeoutKey, text)));

            Assert.Equal(TimeSpan.FromSeconds(seconds), timeout);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-5")]
        [InlineData("1.5")]
        public void GetTimeout_uses_the_default_for_a_missing_or_invalid_value(string text)
        {
            var timeout = DatabaseStartupWait.GetTimeout(Configuration((DatabaseStartupWait.TimeoutKey, text)));

            Assert.Equal(DatabaseStartupWait.DefaultTimeout, timeout);
        }
    }
}
