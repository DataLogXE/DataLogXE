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

using System.Text.Json;
using DataLogLib.Common.Serilog;
using DataLogLib.Services.ArchiveAccess;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using System;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Writes the events of the log and the process events (alarms) to the database in batches. The process events
    /// that the database does not take (an outage, a lock, a full pool) go to a journal on disk (DATA_PATH/journal,
    /// events-*.jnl, up to 200 MB) instead of being dropped after 10 000; while it holds events, new ones follow them
    /// there, and each flush writes it back in order. The events of the log itself are diagnostics: they are kept for
    /// a retry up to 10 000 and dropped beyond, as before.
    /// </summary>
    internal sealed class EventLogWorker
    {
        internal const long JournalMaxBytes = 200L * 1024 * 1024;
        // Process events written back from the journal at most in one flush, so the batches of the log keep their turn
        private const int JournalChunk = 500;
        private const int JournalChunksPerFlush = 20;
        private static readonly TimeSpan JournalLogInterval = TimeSpan.FromMinutes(1);

        private readonly SerilogSink _sink;
        private readonly Action<IReadOnlyList<EventLogPoint>> _insertProcess;
        private readonly Action<IReadOnlyList<EventLogPoint>> _insertApp;
        private readonly RecordJournal _journal;
        private readonly List<EventLogPoint> _batch = [];
        private DateTime _lastJournalLogUtc = DateTime.MinValue;
        private bool _journaling;
        // Process events whose Postgres write failed, without a journal. Retried on the next flush.
        private readonly List<EventLogPoint> _processRetry = [];
        private readonly List<EventLogPoint> _appRetry = [];
        // Flush() runs on caller threads, for example the alarm seed, while the worker fills the batch.
        private readonly object _batchLock = new();
        private readonly object _directLock = new();
        private readonly List<EventLogRecord> _directPending = [];
        private readonly LogEventLevel _minLevel;
        private readonly bool _enabled;
        private DateTime _lastFlushUtc = DateTime.UtcNow;

        public EventLogWorker(
            SerilogSink sink,
            ProcessEventStore processStore,
            AppEventStore appStore,
            IConfiguration configuration)
            : this(sink, processStore.InsertEventPoints, appStore.InsertEventPoints, OpenJournal(), configuration)
        {
        }

        internal EventLogWorker(
            SerilogSink sink,
            Action<IReadOnlyList<EventLogPoint>> insertProcess,
            Action<IReadOnlyList<EventLogPoint>> insertApp,
            RecordJournal journal,
            IConfiguration configuration)
        {
            _sink = sink;
            _insertProcess = insertProcess;
            _insertApp = insertApp;
            _journal = journal;
            _journaling = journal is { IsEmpty: false };
            _enabled = !string.Equals(configuration["EVENT_LOG_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);
            _minLevel = ParseMinLevel(configuration["EVENT_LOG_MIN_LEVEL"]);
        }

        public void Start()
        {
            if (!_enabled)
            {
                Log.Information("Event log is disabled");
            }

            new Thread(WorkerLoop) { IsBackground = true, Name = "EventLogWorker" }.Start();
        }

        public void EnqueueDirect(EventLogRecord record)
        {
            if (!_enabled || record == null)
            {
                return;
            }

            lock (_directLock)
            {
                if (_directPending.Count < EventLogConstants.SinkMaxQueue)
                {
                    _directPending.Add(record);
                    return;
                }
            }

            // The worker is stuck in a write: a process event goes to the journal instead of being dropped (the
            // journal keeps its own order; the open occurrences take the newest point of each, whatever the order)
            if (_journal != null && IsProcessEvent(record))
            {
                try
                {
                    _journal.Append([JsonSerializer.SerializeToUtf8Bytes(record.ToPoint())]);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Process event journal cannot be written");
                }
            }
        }

        public void Flush()
        {
            if (!_enabled)
            {
                return;
            }

            // Records queued by the shutdown itself (closed system alarms) are still in the direct queue
            // and the sink; take them along before the final write.
            DrainDirectPending();
            DrainSink();
            FlushBatch(force: true);
        }

        private void WorkerLoop()
        {
            Log.Information("Startup worker thread: Event log inserter");

            while (true)
            {
                try
                {
                    if (!_enabled)
                    {
                        while (_sink.Events.TryDequeue(out _))
                        {
                        }

                        Thread.Sleep(500);
                        continue;
                    }

                    DrainDirectPending();
                    DrainSink();
                    FlushBatch(force: false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Event log worker failed");
                }

                Thread.Sleep(100);
            }
        }

        private void DrainDirectPending()
        {
            List<EventLogRecord> pending = null;
            lock (_directLock)
            {
                if (_directPending.Count > 0)
                {
                    pending = [.. _directPending];
                    _directPending.Clear();
                }
            }

            if (pending == null)
            {
                return;
            }

            foreach (var record in pending)
            {
                AcceptRecord(record);
            }
        }

        private void DrainSink()
        {
            while (_sink.Events.TryDequeue(out var logEvent))
            {
                if (logEvent.Level < _minLevel)
                {
                    continue;
                }

                var record = EventLogMapper.FromLogEvent(logEvent);
                AcceptRecord(record);
            }
        }

        private void AcceptRecord(EventLogRecord record)
        {
            if (!IsProcessEvent(record) && EventLogMapper.ShouldSkipAppEvent(record))
            {
                return;
            }

            var point = record.ToPoint();
            lock (_batchLock)
            {
                _batch.Add(point);
                if (_batch.Count >= EventLogConstants.WorkerBatchSize)
                {
                    FlushBatch(force: true);
                }
            }
        }

        private void FlushBatch(bool force)
        {
            lock (_batchLock)
            {
                if (_batch.Count == 0 && _processRetry.Count == 0 && _appRetry.Count == 0 && (_journal == null || _journal.IsEmpty))
                {
                    return;
                }

                var elapsedMs = (DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds;
                if (!force && elapsedMs < EventLogConstants.WorkerFlushIntervalMs)
                {
                    return;
                }

                var points = _batch.ToList();
                _batch.Clear();
                _lastFlushUtc = DateTime.UtcNow;
                var process = new List<EventLogPoint>(_processRetry);
                _processRetry.Clear();
                var application = new List<EventLogPoint>(_appRetry);
                _appRetry.Clear();
                foreach (var point in points)
                {
                    (IsProcessEvent(point.Measurement) ? process : application).Add(point);
                }

                // Holding the lock keeps the writes ordered.
                WriteEvents(application, _insertApp, _appRetry, EventLogMapper.AppEventWriteFailed);
                if (_journal == null)
                {
                    WriteEvents(process, _insertProcess, _processRetry, "Process event write failed");
                }
                else
                {
                    WriteProcessEvents(process);
                }
            }
        }

        /// <summary>The events still in the journal on disk.</summary>
        internal long JournalCount => _journal?.Count ?? 0;

        private static RecordJournal OpenJournal()
        {
            var folder = ArchiveJournal.DefaultFolder;
            try
            {
                return new RecordJournal(folder, "events", JournalMaxBytes);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Event journal in {Folder} is not available; process events beyond 10 000 are dropped while the database is down", folder);
                return null;
            }
        }

        // Called under the batch lock: the journal first, so the events reach the database in the order they came
        private void WriteProcessEvents(List<EventLogPoint> points)
        {
            if (!_journal.IsEmpty)
            {
                ToJournal(points);
                points = [];
            }

            if (!WriteJournal())
            {
                ToJournal(points);
                return;
            }

            if (points.Count == 0)
            {
                return;
            }

            try
            {
                _insertProcess(points);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Process event write failed, {Count} events go to the journal on disk", points.Count);
                ToJournal(points);
            }
        }

        // Writes the journal back in chunks; true when it is empty afterwards
        private bool WriteJournal()
        {
            for (var chunk = 0; chunk < JournalChunksPerFlush && !_journal.IsEmpty; chunk++)
            {
                var records = _journal.Peek(JournalChunk);
                if (records.Count == 0)
                {
                    break;
                }

                List<EventLogPoint> points;
                try
                {
                    points = [.. records.Select(r => JsonSerializer.Deserialize<EventLogPoint>(r))];
                }
                catch (JsonException ex)
                {
                    // A damaged record cannot be written; it is skipped with its chunk rather than blocking the journal
                    Log.Error(ex, "{Count} process events in the journal cannot be read and are skipped", records.Count);
                    _journal.Commit(records.Count);
                    continue;
                }

                try
                {
                    _insertProcess(points);
                }
                catch (Exception ex)
                {
                    if (DateTime.UtcNow - _lastJournalLogUtc >= JournalLogInterval)
                    {
                        _lastJournalLogUtc = DateTime.UtcNow;
                        Log.Warning("Process event journal: {Count} events wait for the database ({Reason})", _journal.Count, ex.Message);
                    }
                    return false;
                }
                _journal.Commit(records.Count);
            }

            if (_journal.IsEmpty)
            {
                if (_journaling)
                {
                    _journaling = false;
                    Log.Information("Process event journal written to the database");
                }
                return true;
            }

            if (DateTime.UtcNow - _lastJournalLogUtc >= JournalLogInterval)
            {
                _lastJournalLogUtc = DateTime.UtcNow;
                Log.Information("Process event journal: {Count} events left to write", _journal.Count);
            }
            return false;
        }

        private void ToJournal(List<EventLogPoint> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            if (!_journaling)
            {
                _journaling = true;
                Log.Warning("The database does not take the process events: they go to the journal on disk");
            }

            try
            {
                var dropped = _journal.Append([.. points.Select(p => JsonSerializer.SerializeToUtf8Bytes(p))]);
                if (dropped > 0)
                {
                    Log.Error("Process event journal is full ({MaxMb} MB): {Count} events dropped", JournalMaxBytes >> 20, dropped);
                }
            }
            catch (Exception ex)
            {
                // The disk does not take them: kept in memory for a retry, as without a journal
                Log.Error(ex, "Process event journal cannot be written");
                var keep = points.Count > EventLogConstants.SinkMaxQueue
                    ? points.GetRange(points.Count - EventLogConstants.SinkMaxQueue, EventLogConstants.SinkMaxQueue)
                    : points;
                _processRetry.AddRange(keep);
            }
        }

        private static void WriteEvents(
            List<EventLogPoint> points,
            Action<IReadOnlyList<EventLogPoint>> insert,
            List<EventLogPoint> retry,
            string failedMessage)
        {
            if (points.Count == 0)
            {
                return;
            }

            try
            {
                insert(points);
            }
            catch (Exception ex)
            {
                // Postgres restart or network drop: keep the newest events for the next flush.
                var keep = points.Count > EventLogConstants.SinkMaxQueue
                    ? points.GetRange(points.Count - EventLogConstants.SinkMaxQueue, EventLogConstants.SinkMaxQueue)
                    : points;
                retry.AddRange(keep);
                Log.Error(
                    ex,
                    failedMessage + ", {Count} events kept for retry, {Dropped} dropped",
                    keep.Count,
                    points.Count - keep.Count);
            }
        }

        private static bool IsProcessEvent(EventLogRecord record)
        {
            return IsProcessEvent(record.Measurement);
        }

        private static bool IsProcessEvent(string measurement)
        {
            return string.Equals(measurement, EventLogConstants.ProcessMeasurementName, StringComparison.Ordinal);
        }

        private static LogEventLevel ParseMinLevel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LogEventLevel.Information;
            }

            return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
                ? level
                : LogEventLevel.Information;
        }
    }
}
