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
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Serilog;

namespace DataLogLib.Services.DataAccess
{
    internal static class PostgresSchemaUpgrade
    {
        public const int CurrentVersion = 18;

        // The version 001-baseline.sql creates. The upgrades below it were merged into the baseline (0.0.249), so a
        // database below it cannot be upgraded; upgrades/<n>-to-<n+1>.sql start here.
        public const int BaselineVersion = 18;
        public const string VersionKey = "VERSION";
        private const long SchemaLockKey = 0x444C5845;

        private static readonly Regex UpgradeFileName = new(
            @"^(?<from>\d+)-to-(?<to>\d+)\.sql$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static void Apply(NpgsqlConnection connection, string scriptsFolder)
        {
            Apply(connection, scriptsFolder, CurrentVersion);
        }

        internal static void Apply(NpgsqlConnection connection, string scriptsFolder, int targetVersion)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (targetVersion < BaselineVersion || targetVersion > CurrentVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(targetVersion));
            }

            if (string.IsNullOrWhiteSpace(scriptsFolder) || !Directory.Exists(scriptsFolder))
            {
                throw new InvalidOperationException($"PostgreSQL script folder was not found: {scriptsFolder}");
            }

            using (var lockCmd = connection.CreateCommand())
            {
                // No command timeout here and for the scripts: the app cannot start without its schema, so waiting for
                // another instance's upgrade or for a lock (a Timescale job right after the start of the database) beats
                // failing after Npgsql's 30 s, which ended the first start of 0.0.241 on the Pi.
                lockCmd.CommandText = "SELECT pg_advisory_lock(@key)";
                lockCmd.CommandTimeout = 0;
                lockCmd.Parameters.AddWithValue("key", SchemaLockKey);
                lockCmd.ExecuteNonQuery();
            }

            try
            {
                var baselinePath = Path.Combine(scriptsFolder, "001-baseline.sql");
                if (!File.Exists(baselinePath))
                {
                    throw new InvalidOperationException($"PostgreSQL baseline script was not found: {baselinePath}");
                }

                var version = TryReadVersion(connection);
                if (version == 0)
                {
                    ApplyBaseline(connection, baselinePath);
                    version = ReadVersion(connection, null);
                }

                if (version < BaselineVersion)
                {
                    throw new InvalidOperationException(
                        $"Database schema version {version} is older than the baseline of this application ({BaselineVersion}) " +
                        "and cannot be upgraded. Start with an empty database.");
                }

                if (version > CurrentVersion)
                {
                    throw new InvalidOperationException(
                        $"Database schema version {version} is newer than this application ({CurrentVersion}).");
                }

                if (version >= targetVersion)
                {
                    Log.Information("PostgreSQL schema version {SchemaVersion} is current", version);
                    return;
                }

                var upgradesFolder = Path.Combine(scriptsFolder, "upgrades");
                var scripts = Directory.Exists(upgradesFolder)
                    ? LoadUpgradeScripts(upgradesFolder, targetVersion)
                    : new Dictionary<(int From, int To), string>();

                Log.Information(
                    "PostgreSQL schema version {SchemaVersion} will upgrade to {TargetVersion}",
                    version,
                    targetVersion);

                while (version < targetVersion)
                {
                    var next = version + 1;
                    if (!scripts.TryGetValue((version, next), out var scriptPath))
                    {
                        throw new InvalidOperationException(
                            $"Database schema upgrade from version {version} to {next} was not found.");
                    }

                    ApplyUpgradeScript(connection, scriptPath, version, next);
                    version = next;
                }
            }
            finally
            {
                using var unlockCmd = connection.CreateCommand();
                unlockCmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                unlockCmd.Parameters.AddWithValue("key", SchemaLockKey);
                unlockCmd.ExecuteNonQuery();
            }
        }

        private static void ApplyBaseline(NpgsqlConnection connection, string baselinePath)
        {
            Log.Information("Applying PostgreSQL baseline schema ({Script})", Path.GetFileName(baselinePath));
            var script = File.ReadAllText(baselinePath);
            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in SplitStatements(script))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = statement;
                    cmd.CommandTimeout = 0;
                    cmd.ExecuteNonQuery();
                }

                var version = ReadVersion(connection, transaction);
                if (version != BaselineVersion)
                {
                    throw new InvalidOperationException($"Baseline did not set {VersionKey} to {BaselineVersion}.");
                }

                transaction.Commit();
                Log.Information("Applied PostgreSQL baseline schema ({Script})", Path.GetFileName(baselinePath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PostgreSQL baseline schema failed ({Script})", Path.GetFileName(baselinePath));
                transaction.Rollback();
                throw;
            }
        }

        private static Dictionary<(int From, int To), string> LoadUpgradeScripts(string upgradesFolder, int targetVersion)
        {
            var scripts = new Dictionary<(int From, int To), string>();
            foreach (var path in Directory.GetFiles(upgradesFolder, "*.sql"))
            {
                var match = UpgradeFileName.Match(Path.GetFileName(path));
                if (!match.Success)
                {
                    continue;
                }

                var from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
                var to = int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture);
                if (to != from + 1)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL upgrade {Path.GetFileName(path)} must raise the schema by one version.");
                }

                scripts[(from, to)] = path;
            }

            for (var from = BaselineVersion; from < targetVersion; from++)
            {
                if (!scripts.ContainsKey((from, from + 1)))
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL upgrade from version {from} to {from + 1} was not found.");
                }
            }

            return scripts;
        }

        private static void ApplyUpgradeScript(
            NpgsqlConnection connection,
            string scriptPath,
            int fromVersion,
            int toVersion)
        {
            var scriptName = Path.GetFileName(scriptPath);
            Log.Information(
                "Applying PostgreSQL schema upgrade {FromVersion} to {ToVersion} ({Script})",
                fromVersion,
                toVersion,
                scriptName);

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in SplitStatements(File.ReadAllText(scriptPath)))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = statement;
                    cmd.CommandTimeout = 0;
                    cmd.ExecuteNonQuery();
                }

                var upgraded = ReadVersion(connection, transaction);
                if (upgraded != toVersion)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL upgrade {fromVersion} to {toVersion} did not set {VersionKey} to {toVersion}.");
                }

                transaction.Commit();
                Log.Information(
                    "Applied PostgreSQL schema upgrade {FromVersion} to {ToVersion} ({Script})",
                    fromVersion,
                    toVersion,
                    scriptName);
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "PostgreSQL schema upgrade {FromVersion} to {ToVersion} failed ({Script})",
                    fromVersion,
                    toVersion,
                    scriptName);
                transaction.Rollback();
                throw;
            }
        }

        private static int TryReadVersion(NpgsqlConnection connection)
        {
            using var exists = connection.CreateCommand();
            exists.CommandText =
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'parameter')";
            if (!Convert.ToBoolean(exists.ExecuteScalar()))
            {
                return 0;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM parameter WHERE key = @key";
            cmd.Parameters.AddWithValue("key", VersionKey);
            var value = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(value) ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                return 0;
            }

            return version;
        }

        private static List<string> SplitStatements(string script)
        {
            var statements = new List<string>();
            var current = new StringBuilder();
            foreach (var rawLine in script.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (current.Length > 0)
                {
                    current.Append('\n');
                }

                current.Append(line);
                if (line.EndsWith(';'))
                {
                    statements.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                throw new InvalidOperationException("PostgreSQL script has an unterminated statement.");
            }

            return statements;
        }

        private static int ReadVersion(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }

            cmd.CommandText = "SELECT value FROM parameter WHERE key = @key";
            cmd.Parameters.AddWithValue("key", VersionKey);
            var value = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(value) ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException("Database schema version is missing or invalid.");
            }

            return version;
        }
    }
}
