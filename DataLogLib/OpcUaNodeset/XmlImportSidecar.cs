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
using DataLogLib.Services;
using DataLogLib.Services.DataAccess;
using Npgsql;
using NpgsqlTypes;

namespace DataLogLib.OpcUaNodeset
{
    internal static class XmlImportSidecar
    {
        private const string CopyCommand =
            "COPY xmlimportstage (jobid, address, name, displayname, datatype, typeid) FROM STDIN (FORMAT BINARY)";

        public static void Write(
            Guid jobId,
            BlockingCollection<List<XmlImportRow>> batches,
            ConcurrentBag<List<XmlImportRow>> pool,
            XmlImportCounters counters,
            XmlImportTypeGraph types,
            Action<string> setPhase,
            CancellationToken cancellationToken)
        {
            // The child inherits the environment of the app: DATABASE_URL or the POSTGRES_* variables of compose.
            var connectionString = DataAccessLib.Models.DataConnectionInfo.FromEnvironment();

            using var connection = new NpgsqlConnection(connectionString);
            // A busy device (checkpoint, autovacuum after a large import) may not accept a connection within the
            // 15 s of Npgsql; transient errors are retried for a while.
            var clock = Stopwatch.StartNew();
            DatabaseStartupWait.Run(connection.Open, XmlImportLimits.ConnectWait, () => clock.Elapsed, Thread.Sleep);

            Execute(connection, "DELETE FROM xmlimportstage WHERE jobid = @jobId", jobId, cancellationToken);
            Execute(connection, "DELETE FROM xmlimporttypemap WHERE jobid = @jobId", jobId, cancellationToken);

            var pacer = new LoadPacer(LoadPacer.MaxLoadFromEnvironment());
            var pendingTypes = new HashSet<string>(StringComparer.Ordinal);
            var pendingOverflow = false;
            NpgsqlBinaryImporter importer = null;
            var rowsInCopy = 0;
            var copyStarted = Stopwatch.StartNew();
            try
            {
                foreach (var batch in batches.GetConsumingEnumerable(cancellationToken))
                {
                    if (importer == null)
                    {
                        importer = connection.BeginBinaryImport(CopyCommand);
                        rowsInCopy = 0;
                        copyStarted.Restart();
                    }

                    var written = WriteRows(importer, jobId, batch, pendingTypes, ref pendingOverflow);
                    counters.AddAccepted(written);
                    rowsInCopy += written;
                    OpcUaNodesetImportParser.Return(pool, batch);

                    // Parsing and the COPY slow down while the device is busy; the reader waits on the full queue.
                    pacer.Step(cancellationToken);
                    counters.SetSlowed(pacer.Slowed);

                    if (rowsInCopy >= XmlImportLimits.CommitRows ||
                        copyStarted.ElapsedMilliseconds >= XmlImportLimits.CommitMs)
                    {
                        CompleteCopy(ref importer);
                        counters.TickWork();
                    }
                }

                if (importer != null)
                {
                    CompleteCopy(ref importer);
                    counters.TickWork();
                }
            }
            finally
            {
                // Disposing an importer without Complete() cancels the running COPY.
                importer?.Dispose();
            }

            // The statistics still describe the stage of the previous import, whose job id is gone. With them the
            // planner expects a handful of rows for this job and joins the stage to itself in a nested loop
            // (570 000 rows: hours instead of a second).
            Analyze(connection, cancellationToken);

            counters.SetSlowed(false);
            setPhase("resolve");
            using (new ProgressScope(counters, advancePhaseDone: false))
            {
                Resolve(connection, jobId, types, counters, pendingTypes, pendingOverflow, cancellationToken);
            }

            setPhase("finalize");
            using (new ProgressScope(counters, advancePhaseDone: true))
            {
                FinalizeTable(connection, jobId, counters, cancellationToken);
            }

            var accepted = ExecuteScalar(
                connection,
                "SELECT COUNT(*) FROM xmlimportstage WHERE jobid = @jobId",
                jobId,
                cancellationToken);
            counters.SetAccepted(Convert.ToInt32(accepted));
        }

        private static void CompleteCopy(ref NpgsqlBinaryImporter importer)
        {
            importer.Complete();
            importer.Dispose();
            importer = null;
        }

        private static int WriteRows(
            NpgsqlBinaryImporter importer,
            Guid jobId,
            List<XmlImportRow> batch,
            HashSet<string> pendingTypes,
            ref bool pendingOverflow)
        {
            var written = 0;
            foreach (var row in batch)
            {
                var address = row.Address ?? "";
                importer.StartRow();
                importer.Write(jobId, NpgsqlDbType.Uuid);
                importer.Write(address, NpgsqlDbType.Text);
                importer.Write(string.IsNullOrWhiteSpace(row.Name) ? address : row.Name, NpgsqlDbType.Text);
                var displayName = XmlImportTypeGraph.NormalizeDisplayName(row.DisplayName);
                if (displayName == null)
                {
                    importer.WriteNull();
                }
                else
                {
                    importer.Write(displayName, NpgsqlDbType.Text);
                }

                importer.Write(row.DataType ?? "", NpgsqlDbType.Text);
                importer.Write(row.TypeId, NpgsqlDbType.Integer);
                written++;

                if (row.TypeId == 0 && !pendingOverflow)
                {
                    var key = row.DataType ?? "";
                    if (pendingTypes.Count < XmlImportLimits.PendingTypeCap)
                    {
                        pendingTypes.Add(key);
                    }
                    else if (!pendingTypes.Contains(key))
                    {
                        pendingOverflow = true;
                        pendingTypes.Clear();
                    }
                }
            }

            return written;
        }

        private static void Resolve(
            NpgsqlConnection connection,
            Guid jobId,
            XmlImportTypeGraph types,
            XmlImportCounters counters,
            HashSet<string> pendingTypes,
            bool pendingOverflow,
            CancellationToken cancellationToken)
        {
            IReadOnlyCollection<string> pending;
            if (pendingOverflow)
            {
                pending = LoadDistinctPendingTypes(connection, jobId, cancellationToken);
            }
            else
            {
                pending = pendingTypes;
            }

            counters.SetPhaseProgress(0, pending.Count);
            var dataTypes = new List<string>(pending.Count);
            var typeIds = new List<int>(pending.Count);
            var done = 0L;
            foreach (var value in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = types.Resolve(value, true);
                if (resolved > 0)
                {
                    dataTypes.Add(value ?? "");
                    typeIds.Add(resolved);
                }

                done++;
                if ((done & 63) == 0 || done == pending.Count)
                {
                    counters.SetPhaseProgress(done, pending.Count);
                    counters.TickWork();
                }
            }

            if (dataTypes.Count > 0)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO xmlimporttypemap (jobid, datatype, typeid) " +
                    "SELECT @jobId, m.datatype, m.typeid FROM unnest(@dataTypes, @typeIds) AS m(datatype, typeid) " +
                    "ON CONFLICT DO NOTHING";
                insert.Parameters.AddWithValue("jobId", jobId);
                insert.Parameters.AddWithValue("dataTypes", dataTypes.ToArray());
                insert.Parameters.AddWithValue("typeIds", typeIds.ToArray());
                ExecuteCancellable(insert, cancellationToken);
            }

            counters.SetPhaseProgress(pending.Count, pending.Count);
        }

        private static List<string> LoadDistinctPendingTypes(
            NpgsqlConnection connection,
            Guid jobId,
            CancellationToken cancellationToken)
        {
            var list = new List<string>(1024);
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT DISTINCT datatype FROM xmlimportstage WHERE jobid = @jobId AND typeid = 0";
            select.Parameters.AddWithValue("jobId", jobId);
            using var registration = cancellationToken.Register(select.Cancel);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }

            return list;
        }

        private static void FinalizeTable(
            NpgsqlConnection connection,
            Guid jobId,
            XmlImportCounters counters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageCount = Convert.ToInt64(ExecuteScalar(
                connection,
                "SELECT COUNT(*) FROM xmlimportstage WHERE jobid = @jobId",
                jobId,
                cancellationToken));
            counters.SetPhaseProgress(0, stageCount);

            Execute(
                connection,
                "UPDATE xmlimportstage s SET typeid = m.typeid " +
                "FROM xmlimporttypemap m " +
                "WHERE s.jobid = @jobId AND s.typeid = 0 AND m.jobid = @jobId AND m.datatype = s.datatype",
                jobId,
                cancellationToken);
            Execute(connection, "DELETE FROM xmlimportstage WHERE jobid = @jobId AND typeid <= 0", jobId, cancellationToken);
            // One pass in address order instead of a join of the stage with itself, whose plan depends on statistics.
            // The first row of an address (lowest ctid) stays, as before.
            Execute(
                connection,
                "DELETE FROM xmlimportstage WHERE jobid = @jobId AND ctid = ANY(ARRAY(" +
                "SELECT ctid FROM (" +
                "SELECT ctid, address, lag(address) OVER (ORDER BY address, ctid) AS previous " +
                "FROM xmlimportstage WHERE jobid = @jobId) ordered " +
                "WHERE address = previous))",
                jobId,
                cancellationToken);
            Execute(connection, "DELETE FROM xmlimporttypemap WHERE jobid = @jobId", jobId, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            counters.SetPhaseProgress(stageCount, stageCount);
        }

        private static void Execute(NpgsqlConnection connection, string sql, Guid jobId, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("jobId", jobId);
            ExecuteCancellable(command, cancellationToken);
        }

        private static void Analyze(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ANALYZE xmlimportstage";
            command.CommandTimeout = 0;
            ExecuteCancellable(command, cancellationToken);
        }

        private static object ExecuteScalar(NpgsqlConnection connection, string sql, Guid jobId, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("jobId", jobId);
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.Register(command.Cancel);
            var value = TranslateCancel(() => command.ExecuteScalar(), cancellationToken);
            return value;
        }

        private static void ExecuteCancellable(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var registration = cancellationToken.Register(command.Cancel);
            TranslateCancel(() => command.ExecuteNonQuery(), cancellationToken);
        }

        private static T TranslateCancel<T>(Func<T> action, CancellationToken cancellationToken)
        {
            try
            {
                return action();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled &&
                                               cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class ProgressScope : IDisposable
        {
            private readonly XmlImportCounters _counters;
            private readonly bool _advancePhaseDone;
            private readonly Timer _heartbeat;

            public ProgressScope(XmlImportCounters counters, bool advancePhaseDone)
            {
                _counters = counters;
                _advancePhaseDone = advancePhaseDone;
                _heartbeat = new Timer(_ =>
                {
                    _counters.TickWork();
                    if (_advancePhaseDone)
                    {
                        var total = _counters.PhaseTotal;
                        if (total > 0 && _counters.PhaseDone < total)
                        {
                            _counters.AddPhaseDone(Math.Max(1L, total / 500));
                        }
                    }
                }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            }

            public void Dispose()
            {
                _heartbeat.Dispose();
            }
        }
    }
}
