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

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DataLogLib.Services.EventLog;
using Serilog;

namespace DataLogService.DataSource.OpcUa
{
    /// <summary>
    /// Restores the open OPC UA alarms from the archive before the alarm subscription starts.
    /// The seed never throws and its reads are bounded by a timeout: a slow or unavailable archive
    /// only delays the alarm subscription, it never blocks or breaks it. The events still queued for
    /// the archive are written first, outside the timeout: on a Raspberry Pi that write took most of
    /// the 15 s at a start, when all sources write at once, and the read then timed out although it
    /// takes well under a second. A seed that timed out is never done later (the live alarms take
    /// over), so a read that times out is tried once more.
    /// </summary>
    internal sealed class OpcUaAlarmSeeder
    {
        // A log line carries the failure reason, not the whole stack trace.
        private const int MaxReasonChars = 200;

        private readonly int _sourceId;
        private readonly string _sourceName;
        private readonly string _logPrefix;
        private readonly AlarmTracker _tracker;
        private readonly Func<IEventLogAccess> _access;
        private readonly Action<EventLogRecord> _rememberAlarmClass;
        private readonly Action<EventLogRecord> _enqueue;
        // A reconnect can start a new subscription while an older start still seeds.
        private readonly object _seedGate = new();

        public OpcUaAlarmSeeder(
            int sourceId,
            string sourceName,
            string logPrefix,
            AlarmTracker tracker,
            Func<IEventLogAccess> access,
            Action<EventLogRecord> rememberAlarmClass,
            Action<EventLogRecord> enqueue)
        {
            _sourceId = sourceId;
            _sourceName = sourceName;
            _logPrefix = logPrefix;
            _tracker = tracker;
            _access = access;
            _rememberAlarmClass = rememberAlarmClass;
            _enqueue = enqueue;
        }

        /// <summary>
        /// Seeds the open alarms once. Returns false when the archive is not ready, the query
        /// timed out or it failed, so the next alarm subscription build can try again.
        /// </summary>
        public bool TrySeed(TimeSpan timeout)
        {
            lock (_seedGate)
            {
                return TrySeedLocked(timeout);
            }
        }

        private bool TrySeedLocked(TimeSpan timeout)
        {
            if (_tracker.ArchiveSeeded)
            {
                return true;
            }

            if (_tracker.HasLiveState)
            {
                _tracker.SeedOpenAlarms([], null);
                Log.Information(
                    _logPrefix + "OpcUaClient: Alarm archive seed skipped, live alarm state is already present",
                    _sourceName, _sourceId);
                return true;
            }

            var access = _access?.Invoke();
            if (access == null)
            {
                Log.Warning(_logPrefix + "OpcUaClient: Alarm archive seed skipped, event log is not ready", _sourceName, _sourceId);
                return false;
            }

            var clock = Stopwatch.StartNew();
            try
            {
                access.Flush();
            }
            catch (Exception ex)
            {
                Log.Warning(_logPrefix + "OpcUaClient: Alarm archive seed could not write the queued events first: {Reason}",
                    _sourceName, _sourceId, Reason(ex));
            }
            var flushMs = clock.ElapsedMilliseconds;

            for (var attempt = 1; ; attempt++)
            {
                switch (TryRead(access, timeout, flushMs, attempt))
                {
                    case true:
                        return true;
                    case false:
                        return false;
                }
            }
        }

        // True when seeded, false when it failed for good, null after a first timeout (read once more)
        private bool? TryRead(IEventLogAccess access, TimeSpan timeout, long flushMs, int attempt)
        {
            var clock = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var items = access.QueryOpenAlarms(_sourceId, cts.Token)?.Items ?? [];
                var incomplete = CollectIncomplete(items);
                var history = QueryBackfill(access, incomplete, cts.Token);
                var notes = AlarmTracker.CompleteOpenRows(items, history);
                LogNotes(notes);

                var superseded = new List<EventLogRecord>();
                var count = _tracker.SeedOpenAlarms(items, superseded);
                foreach (var record in superseded)
                {
                    _rememberAlarmClass?.Invoke(record);
                    _enqueue?.Invoke(record);
                    Log.Information(
                        _logPrefix + "OpcUaClient: Alarm seed closed older state={AlarmState} message={Message} correlation={Correlation} time={Time} duration_ms={Duration}",
                        _sourceName, _sourceId, record.AlarmState, record.Message, record.CorrelationId,
                        record.TimeUtc.ToString("o", CultureInfo.InvariantCulture), record.DurationMs);
                }

                var missing = notes.Count(note => note.StillMissing);
                Log.Information(
                    _logPrefix + "OpcUaClient: Alarm archive seed open={Count} closed_older={Closed} backfill={Backfill} restored={Restored} missing={Missing} write_ms={FlushMs} read_ms={ReadMs}",
                    _sourceName, _sourceId, count, superseded.Count, incomplete.Count, notes.Count - missing, missing,
                    flushMs, clock.ElapsedMilliseconds);
                return true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && attempt == 1)
            {
                Log.Information(
                    _logPrefix + "OpcUaClient: Alarm archive seed read timed out after {TimeoutMs} ms (write_ms={FlushMs}), reading once more",
                    _sourceName, _sourceId, (long)timeout.TotalMilliseconds, flushMs);
                return null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Log.Warning(
                    _logPrefix + "OpcUaClient: Alarm archive seed timed out twice after {TimeoutMs} ms (write_ms={FlushMs}), "
                    + "the open alarms of the archive are not restored in this session",
                    _sourceName, _sourceId, (long)timeout.TotalMilliseconds, flushMs);
                return false;
            }
            catch (Exception ex) when (IsArchiveUnavailable(ex))
            {
                Log.Warning(
                    _logPrefix + "OpcUaClient: Alarm archive seed failed, archive unavailable: {Reason}",
                    _sourceName, _sourceId, Reason(ex));
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, _logPrefix + "OpcUaClient: Alarm archive seed failed", _sourceName, _sourceId);
                return false;
            }
        }

        private static List<string> CollectIncomplete(IReadOnlyList<EventLogRecord> items)
        {
            var incomplete = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in items)
            {
                if (!AlarmTracker.NeedsOccurrenceBackfill(row) || !seen.Add(row.CorrelationId))
                {
                    continue;
                }

                if (incomplete.Count >= EventLogConstants.MaxTrackedConditions)
                {
                    break;
                }

                incomplete.Add(row.CorrelationId);
            }

            return incomplete;
        }

        // The open rows are still worth seeding without their raise time, so a failed
        // backfill only logs. A timeout cancels the whole seed.
        private IReadOnlyList<EventLogRecord> QueryBackfill(
            IEventLogAccess access,
            IReadOnlyList<string> incomplete,
            CancellationToken cancellationToken)
        {
            if (incomplete.Count == 0)
            {
                return [];
            }

            Log.Information(
                _logPrefix + "OpcUaClient: Alarm seed backfill {Count} open occurrences",
                _sourceName, _sourceId, incomplete.Count);
            try
            {
                var history = access.QueryOpenAlarmLookback(_sourceId, incomplete, cancellationToken)?.Items ?? [];
                Log.Information(
                    _logPrefix + "OpcUaClient: Alarm seed backfill read {Rows} points",
                    _sourceName, _sourceId, history.Count);
                return history;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning(
                    _logPrefix + "OpcUaClient: Alarm seed backfill failed: {Reason}",
                    _sourceName, _sourceId, Reason(ex));
                return [];
            }
        }

        private void LogNotes(IReadOnlyList<AlarmTracker.AlarmSeedNote> notes)
        {
            foreach (var note in notes)
            {
                var since = note.ActiveSinceUtc.HasValue
                    ? note.ActiveSinceUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                    : "";
                if (note.StillMissing)
                {
                    Log.Warning(
                        _logPrefix + "OpcUaClient: Alarm seed missing state={AlarmState} message={Message} correlation={Correlation} active_since={ActiveSince} duration_ms={Duration}",
                        _sourceName, _sourceId, note.AlarmState, note.Message, note.CorrelationId, since, note.DurationMs);
                }
                else
                {
                    Log.Information(
                        _logPrefix + "OpcUaClient: Alarm seed restored state={AlarmState} message={Message} correlation={Correlation} active_since={ActiveSince} duration_ms={Duration} filled_since={FilledSince} filled_duration={FilledDuration}",
                        _sourceName, _sourceId, note.AlarmState, note.Message, note.CorrelationId, since, note.DurationMs, note.FilledActiveSince, note.FilledDuration);
                }
            }
        }

        // Network failures and timeouts are expected while the database restarts. Anything else is
        // a defect and keeps its stack trace.
        private static bool IsArchiveUnavailable(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is HttpRequestException
                    or TimeoutException
                    or OperationCanceledException
                    or SocketException
                    or IOException)
                {
                    return true;
                }
            }

            return false;
        }

        // The outer message names the timeout, the base message names the transport failure.
        private static string Reason(Exception ex)
        {
            var outer = ex.Message;
            var inner = ex.GetBaseException().Message;
            var message = string.IsNullOrWhiteSpace(inner) || inner == outer
                ? outer
                : $"{outer} ({inner})";
            if (string.IsNullOrWhiteSpace(message))
            {
                return ex.GetType().Name;
            }

            var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return singleLine.Length > MaxReasonChars ? singleLine[..MaxReasonChars] : singleLine;
        }
    }
}
