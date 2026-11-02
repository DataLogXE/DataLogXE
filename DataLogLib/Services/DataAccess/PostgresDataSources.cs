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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace DataLogLib.Services.DataAccess
{
    /// <summary>What a database connection is used for. Each kind has a pool of its own with its own limits.</summary>
    public enum PostgresWork
    {
        /// <summary>The runtime of the data sources, the compression levels and the writes of the API, without limits as before.</summary>
        Default,
        /// <summary>Archive values, alarms and events: a pool that the reads cannot take away.</summary>
        Ingest,
        /// <summary>The reads of the UI and the API, stopped by the database after the statement timeout.</summary>
        Ui,
        /// <summary>
        /// Work that can wait (compression repair, the counters of archive values and alarms, retention): a small pool,
        /// so it cannot take the connections of the other work, and a statement timeout of 10 minutes.
        /// </summary>
        Background
    }

    /// <summary>
    /// The pools and limits of the database connections, chosen by the memory of the device: a small device (Raspberry
    /// Pi, up to <see cref="SmallDeviceMemoryBytes"/>) gets the small ones. On 2026-10-31 six slow reads of the measuring
    /// cursors held 6 of the 14 connections that all work shared, and ran on for 22 minutes after their requests had
    /// timed out: the inserts waited for a connection and the insert buffer dropped ~114 000 values.
    /// </summary>
    public static class DatabaseLimits
    {
        public const long SmallDeviceMemoryBytes = 2L * 1024 * 1024 * 1024;

        private static readonly AsyncLocal<WorkScope> Ambient = new();

        /// <summary>The limits of this device (the container limit or the physical memory).</summary>
        public static DatabaseProfile Profile { get; } = DatabaseProfile.For(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

        /// <summary>
        /// The connections opened by the stores within the scope (also in the tasks started within it) come from the
        /// pool of the work, except the writes of values, alarms and events, which always use the ingest pool. A
        /// statement that runs on such a connection when the token is cancelled is cancelled in the database, also one
        /// of a store that takes no token. A task that outlives the scope (a timer of a service created in a request)
        /// uses the default pool again.
        /// </summary>
        public static IDisposable Use(PostgresWork work, CancellationToken cancellationToken = default)
        {
            var scope = new WorkScope(work, cancellationToken, Ambient.Value);
            Ambient.Value = scope;
            return scope;
        }

        /// <summary>Runs the loop of a background worker with the connections of the background pool.</summary>
        public static void Background(Action work)
        {
            using (Use(PostgresWork.Background))
            {
                work();
            }
        }

        internal static WorkScope Current => Ambient.Value is { IsActive: true } scope ? scope : null;

        internal sealed class WorkScope(PostgresWork work, CancellationToken token, WorkScope outer) : IDisposable
        {
            private volatile bool _disposed;

            public PostgresWork Work { get; } = work;
            public CancellationToken Token { get; } = token;
            public bool IsActive => !_disposed;

            public void Dispose()
            {
                _disposed = true;
                if (ReferenceEquals(Ambient.Value, this))
                {
                    Ambient.Value = outer;
                }
            }
        }
    }

    /// <summary>Pool sizes and limits of the database connections and of the queries of the UI for a device.</summary>
    public sealed record DatabaseProfile(
        string Name,
        int IngestConnections,
        int UiConnections,
        int DefaultConnections,
        int BackgroundConnections,
        TimeSpan UiStatementTimeout,
        int UiWorkMemMb,
        TimeSpan IngestLockTimeout,
        TimeSpan BackgroundStatementTimeout,
        long JournalMaxBytes,
        int SmallQuerySlots,
        int MediumQuerySlots,
        int LargeQuerySlots,
        long LargeQueryRows)
    {
        // A Raspberry Pi has 1 GB for the database, the app and Telegraf: 16 connections in all. The query slots take
        // 4 of the 5 UI connections, so the other reads of the UI (dashboard, lists) get one. The journal of the archive
        // values (24 bytes each) holds ~45 million values on it, 15 hours at 800 values/s. A count(*) of 8 million raw
        // values took 1.4-5 s on the Pi, of 40 million 27-46 s (2026-10-14).
        public static readonly DatabaseProfile Small = new("small device", 4, 5, 4, 3, TimeSpan.FromSeconds(60), 4, TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10), 1L << 30, 2, 1, 1, 2_000_000);
        public static readonly DatabaseProfile Large = new("PC", 8, 16, 6, 6, TimeSpan.FromSeconds(120), 16, TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10), 5L << 30, 8, 4, 2, 10_000_000);

        public static DatabaseProfile For(long availableMemoryBytes)
            => availableMemoryBytes > 0 && availableMemoryBytes <= DatabaseLimits.SmallDeviceMemoryBytes ? Small : Large;

        public int TotalConnections => IngestConnections + UiConnections + DefaultConnections + BackgroundConnections;
    }

    /// <summary>
    /// The pools of one database: OpenConnection takes a connection from the pool of the current work (see
    /// <see cref="DatabaseLimits.Use"/>), OpenIngestConnection from the ingest pool.
    /// </summary>
    internal sealed class PostgresPools
    {
        private readonly NpgsqlDataSource _default;
        private readonly NpgsqlDataSource _ingest;
        private readonly NpgsqlDataSource _ui;
        private readonly NpgsqlDataSource _background;

        public PostgresPools(NpgsqlDataSource defaultSource, NpgsqlDataSource ingest, NpgsqlDataSource ui, NpgsqlDataSource background)
        {
            _default = defaultSource;
            _ingest = ingest;
            _ui = ui;
            _background = background;
        }

        /// <summary>Called with each connection opened from these pools; for tests that watch the statements.</summary>
        internal Action<NpgsqlConnection> Opened { get; set; }

        public NpgsqlDataSource this[PostgresWork work] => work switch
        {
            PostgresWork.Ingest => _ingest,
            PostgresWork.Ui => _ui,
            PostgresWork.Background => _background,
            _ => _default
        };

        public NpgsqlConnection OpenConnection()
        {
            var scope = DatabaseLimits.Current;
            var waited = Stopwatch.GetTimestamp();
            var connection = this[scope?.Work ?? PostgresWork.Default].OpenConnection();
            ReportWait(scope, waited);
            Opened?.Invoke(connection);
            if (!Watched(scope))
            {
                return connection;
            }
            var marker = NextMarker(scope);
            using (var command = MarkCommand(connection, marker))
            {
                command.ExecuteNonQuery();
            }
            return Watch(connection, scope, marker);
        }

        public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var scope = DatabaseLimits.Current;
            var waited = Stopwatch.GetTimestamp();
            var connection = await this[scope?.Work ?? PostgresWork.Default].OpenConnectionAsync(cancellationToken);
            ReportWait(scope, waited);
            Opened?.Invoke(connection);
            if (!Watched(scope))
            {
                return connection;
            }
            var marker = NextMarker(scope);
            await using (var command = MarkCommand(connection, marker))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return Watch(connection, scope, marker);
        }

        // The background pool is small on purpose: its work waits for each other, which is no load
        private static void ReportWait(DatabaseLimits.WorkScope scope, long waited)
        {
            if (scope?.Work != PostgresWork.Background)
            {
                LoadGuard.Shared.ReportPoolWait(Stopwatch.GetElapsedTime(waited));
            }
        }

        // The wait for a connection tells the load guard that the database does not keep up
        public NpgsqlConnection OpenIngestConnection()
        {
            var waited = Stopwatch.GetTimestamp();
            var connection = _ingest.OpenConnection();
            LoadGuard.Shared.ReportPoolWait(Stopwatch.GetElapsedTime(waited));
            Opened?.Invoke(connection);
            return connection;
        }

        /// <summary>
        /// Cancels the statement that runs on the connection when the token of the scope is cancelled, until the
        /// connection goes back to its pool. The cancel request comes from a connection of the default pool, which the
        /// work of the scope cannot have taken. It names the backend by its process id and the application name that
        /// marks this connection: Npgsql hands the backend to the next request before the connection reports that it
        /// closed, and a cancel by the process id alone stopped the statement of that request ("57014: canceling
        /// statement due to user request" in the chart editor or the tag list on the Pi, 0.0.245). The pool resets
        /// the name (RESET ALL) before the next statement on that backend.
        /// </summary>
        private NpgsqlConnection Watch(NpgsqlConnection connection, DatabaseLimits.WorkScope scope, string marker)
        {
            var processId = connection.ProcessID;
            var open = true;
            CancellationTokenRegistration registration = default;
            void OnStateChange(object sender, System.Data.StateChangeEventArgs e)
            {
                if (e.CurrentState != System.Data.ConnectionState.Open)
                {
                    open = false;
                    connection.StateChange -= OnStateChange;
                    registration.Dispose();
                }
            }
            connection.StateChange += OnStateChange;
            registration = scope.Token.Register(
                () => _ = Task.Run(() => CancelBackend(processId, marker, () => Volatile.Read(ref open))));
            return connection;
        }

        private static long _markers;

        private static bool Watched(DatabaseLimits.WorkScope scope) => scope != null && scope.Token.CanBeCanceled;

        private static string NextMarker(DatabaseLimits.WorkScope scope) => string.Create(CultureInfo.InvariantCulture,
            $"{PostgresDataSources.ApplicationName} {scope.Work.ToString().ToLowerInvariant()} #{Interlocked.Increment(ref _markers)}");

        private static NpgsqlCommand MarkCommand(NpgsqlConnection connection, string marker)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT set_config('application_name', @name, false)";
            command.Parameters.AddWithValue("name", marker);
            return command;
        }

        internal void CancelBackend(int processId, string marker, Func<bool> stillOpen)
        {
            try
            {
                using var connection = _default.OpenConnection();
                // Back in the pool meanwhile: its statement has ended, and the next one belongs to other work
                if (!stillOpen())
                {
                    return;
                }
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(pg_cancel_backend(pid)) FROM pg_stat_activity "
                    + "WHERE pid = @pid AND application_name = @name";
                command.Parameters.AddWithValue("pid", processId);
                command.Parameters.AddWithValue("name", marker);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not cancel the statement of database process {ProcessId}", processId);
            }
        }

        public void Clear()
        {
            _default.Clear();
            _ingest.Clear();
            _ui.Clear();
            _background.Clear();
        }
    }

    /// <summary>
    /// The pools per connection string, shared by every store in the process. PostgreSQL runs with max_connections=40
    /// (compose.yaml), which holds the pools of a PC (36) plus a few for maintenance; separate pools per store would
    /// exhaust it under load. The pool sizes of the profile replace a Maximum Pool Size of the connection string.
    /// </summary>
    internal static class PostgresDataSources
    {
        private const int DefaultMaxAutoPrepare = 32;
        internal const string ApplicationName = "DataLogXE";
        private static readonly TimeSpan BackgroundPoolWait = TimeSpan.FromMinutes(2);

        // Auto-prepared statements switch to a generic plan after five executions. On a hypertable that
        // plan covers every chunk (no parameter values to exclude them with) and locks each chunk and its
        // index: ~1 550 locks for archiveraw with 775 chunks, more than the whole lock table of the Pi
        // (max_locks_per_transaction 64 x max_connections 20), so every other statement failed with
        // 53200 "out of shared memory" (2026-10-25, start of 0.0.209). Custom plans exclude the chunks
        // outside the bound time range at planning, as unprepared statements do. Set on every pool.
        private const string PlanCacheOption = "-c plan_cache_mode=force_custom_plan";

        private static readonly ConcurrentDictionary<string, Lazy<PostgresPools>> Sources = new(StringComparer.Ordinal);

        public static PostgresPools Get(string connectionString)
        {
            return Sources.GetOrAdd(
                connectionString,
                key => new Lazy<PostgresPools>(() => Create(key, DatabaseLimits.Profile))).Value;
        }

        internal static PostgresPools Create(string connectionString, DatabaseProfile profile)
        {
            Serilog.Log.Information(
                "Database pools for a {Profile}: ingest {Ingest}, UI {Ui} (statement timeout {Timeout} s, work_mem {WorkMem} MB), "
                + "background {Background} (statement timeout {BackgroundTimeout} min), other {Default}",
                profile.Name, profile.IngestConnections, profile.UiConnections, profile.UiStatementTimeout.TotalSeconds,
                profile.UiWorkMemMb, profile.BackgroundConnections, profile.BackgroundStatementTimeout.TotalMinutes, profile.DefaultConnections);
            var ui = (int)profile.UiStatementTimeout.TotalMilliseconds;
            return new PostgresPools(
                Build(connectionString, ApplicationName, profile.DefaultConnections, PlanCacheOption),
                // A write that waits for a lock (a chunk being compressed, a count step) fails and is written again
                // with the next batch, instead of holding its connection
                Build(connectionString, ApplicationName + " ingest", profile.IngestConnections,
                    $"{PlanCacheOption} -c lock_timeout={(int)profile.IngestLockTimeout.TotalMilliseconds}"),
                // The database itself stops a read of the UI after the timeout, whatever the app does meanwhile
                Build(connectionString, ApplicationName + " ui", profile.UiConnections,
                    $"{PlanCacheOption} -c statement_timeout={ui} -c work_mem={profile.UiWorkMemMb}MB"),
                // The workers take a connection each; one that finds the pool busy waits for it instead of failing
                Build(connectionString, ApplicationName + " background", profile.BackgroundConnections,
                    $"{PlanCacheOption} -c statement_timeout={(int)profile.BackgroundStatementTimeout.TotalMilliseconds}",
                    BackgroundPoolWait));
        }

        private static NpgsqlDataSource Build(string connectionString, string applicationName, int maxPoolSize, string options,
            TimeSpan? poolWait = null)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            var settings = builder.ConnectionStringBuilder;
            if (settings.MaxAutoPrepare == 0)
            {
                settings.MaxAutoPrepare = DefaultMaxAutoPrepare;
            }
            settings.Options = string.IsNullOrWhiteSpace(settings.Options) ? options : settings.Options + " " + options;
            settings.ApplicationName = applicationName;
            settings.MaxPoolSize = maxPoolSize;
            settings.MinPoolSize = Math.Min(settings.MinPoolSize, maxPoolSize);
            if (poolWait is TimeSpan wait && settings.Timeout < wait.TotalSeconds)
            {
                settings.Timeout = (int)wait.TotalSeconds;
            }

            return builder.Build();
        }
    }
}
