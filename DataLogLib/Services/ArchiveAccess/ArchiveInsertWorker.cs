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

using DataLogLib.Models;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// This class collects the archive values in a list and inserts them into the
    /// archive in a separate thread. Values of a failed write are written with the next try.
    /// When the list is full, the values go to the journal on disk (see ArchiveJournal) instead of being dropped,
    /// and new values follow them there until the journal is written, so the values reach the archive in order.
    /// </summary>
    public class ArchiveInsertWorker
    {
        #region internal events
        internal event EventHandler<List<ArchiveValue>> OnArchiveInsert;
        #endregion

        #region public const
        public const int BUFFER_MAX_SIZE = 50000;
        public const int BUFFER_WARN_SIZE = 20000;
        public const int MAX_ARCHIVE_CHUNK_SIZE = 10000;
        #endregion

        #region private fields
        private const int CATCHUP_SLEEP_MS = 100;
        private const int IDLE_SLEEP_MS = 1000;
        // Wait after a failed write, the archive is down or restarting
        private const int RETRY_SLEEP_MS = 5000;
        // Values per second over the chunks of the last 10 s, 0 after 10 s without a write
        private const double RATE_WINDOW_S = 10.0;
        private const double RATE_IDLE_S = 10.0;
        private IArchive _archive;
        private ArchiveJournal _journal;
        // Values of the journal from before the start are written once the archive resolves the data tags
        private volatile bool _replay = true;
        private bool _journaling;
        private DateTime _lastJournalLogUtc = DateTime.MinValue;
        private readonly List<ArchiveValue> _insertBuffer = [];
        // Serializes the worker and Flush so chunks reach the archive in order and only once.
        private readonly object _writeGate = new();
        private readonly ConcurrentDictionary<int, ArchiveValue> _actualValues = new();
        private long _insertedCount;
        private readonly IngestRate _rate = new(RATE_WINDOW_S, RATE_IDLE_S);
        private long _droppedCount;
        private DateTime _lastDropLogUtc = DateTime.MinValue;
        #endregion

        #region internal methods
        internal void Init(IArchive archive, ArchiveJournal journal = null)
        {
            _archive = archive;
            _journal = journal;
            _replay = journal == null || journal.IsEmpty;
            _journaling = !_replay;
            new Thread(Worker) { IsBackground = true }.Start();
        }

        /// <summary>Lets the worker write the values that the journal held at the start. Call once the archive resolves the data tags.</summary>
        internal void StartReplay()
        {
            _replay = true;
        }

        /// <summary>The values in the journal on disk that have not reached the archive.</summary>
        internal long JournalCount => _journal?.Count ?? 0;

        /// <summary>The share of the insert buffer in use (0..1) for the load guard; 1 while the journal holds values.</summary>
        internal double Backlog
        {
            get
            {
                if (_journal != null && !_journal.IsEmpty)
                {
                    return 1;
                }

                lock (_insertBuffer)
                {
                    return (double)_insertBuffer.Count / BUFFER_MAX_SIZE;
                }
            }
        }

        internal void InsertValues(List<ArchiveValue> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            lock (_insertBuffer)
            {
                // While the journal holds values, new ones follow them there
                if (_insertBuffer.Count < BUFFER_MAX_SIZE && (_journal == null || _journal.IsEmpty))
                {
                    _insertBuffer.AddRange(values);
                    return;
                }

                if (_journal != null)
                {
                    ToJournal(values);
                    return;
                }

                // Keep what is already buffered and drop only the new values, so an overflow does not
                // throw away the oldest unwritten data as well.
                CountDropped(values.Count);
            }
        }

        /// <summary>
        /// Writes everything still buffered. Called on shutdown after the data sources stopped.
        /// Gives up when the archive does not accept the values.
        /// </summary>
        internal void Flush()
        {
            // Waits for a write of the worker thread, so its values are written on return as well
            lock (_writeGate)
            {
                while (true)
                {
                    int remaining;
                    lock (_insertBuffer)
                    {
                        remaining = _insertBuffer.Count;
                    }

                    if (remaining == 0)
                    {
                        return;
                    }

                    try
                    {
                        InsertNextChunk();
                    }
                    catch (Exception ex)
                    {
                        if (_journal == null)
                        {
                            Log.Error(ex, "Archive insert on shutdown failed, {Count} values are not written", remaining);
                            return;
                        }

                        // Written after the restart, behind the values the journal holds already
                        Log.Error(ex, "Archive insert on shutdown failed, {Count} values go to the journal", remaining);
                        lock (_insertBuffer)
                        {
                            ToJournal(_insertBuffer);
                            _insertBuffer.Clear();
                        }
                        return;
                    }
                }
            }
        }

        internal List<ArchiveValue> GetActualValues(List<int> ids)
        {
            var result = new List<ArchiveValue>();
            foreach (var id in ids)
            {
                if (_actualValues.TryGetValue(id, out var v))
                {
                    result.Add(v);
                }
            }
            return result;
        }

        internal bool TryGetActual(int id, out double value, out int state, out long timeUtcTicks)
        {
            if (_actualValues.TryGetValue(id, out var actual) && actual != null)
            {
                value = actual.Value;
                state = (int)actual.State;
                timeUtcTicks = actual.Time.ToUniversalTime().Ticks;
                return true;
            }

            value = 0;
            state = 0;
            timeUtcTicks = 0;
            return false;
        }

        internal long TakeInsertedCount()
        {
            return Interlocked.Exchange(ref _insertedCount, 0);
        }

        /// <summary>
        /// Values written per second over the last chunks, null before the second write.
        /// </summary>
        internal double? InsertRatePerSec()
        {
            return _rate.Rate((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }
        #endregion

        #region private methods
        private void Worker(object obj)
        {
            Log.Information("Startup worker thread: Archive value inserter");

            while (true)
            {
                var sleep = IDLE_SLEEP_MS;
                try
                {
                    InsertNextChunk();
                    lock (_insertBuffer)
                    {
                        if (_insertBuffer.Count > MAX_ARCHIVE_CHUNK_SIZE || (_replay && _journal is { IsEmpty: false }))
                        {
                            sleep = CATCHUP_SLEEP_MS;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An exception during data insert was thrown!");
                    sleep = RETRY_SLEEP_MS;
                }

                Thread.Sleep(sleep);
            }
        }

        private void InsertNextChunk()
        {
            lock (_writeGate)
            {
                var sw = Stopwatch.StartNew();
                List<ArchiveValue> archiveBuffer = [];
                double takeS;

                lock (_insertBuffer)
                {
                    takeS = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                    if (_insertBuffer.Count > BUFFER_WARN_SIZE)
                    {
                        Log.Warning("Archive insert buffer high level {Count} values", _insertBuffer.Count);
                    }

                    if (_insertBuffer.Count > MAX_ARCHIVE_CHUNK_SIZE)
                    {
                        // Read first n values from insert buffer
                        archiveBuffer.AddRange(_insertBuffer.Take(MAX_ARCHIVE_CHUNK_SIZE));
                        _insertBuffer.RemoveRange(0, MAX_ARCHIVE_CHUNK_SIZE);
                    }
                    else
                    {
                        // Copy insert buffer complete
                        archiveBuffer.AddRange(_insertBuffer);
                        _insertBuffer.Clear();
                    }
                }

                // The journal holds the values that came after the buffer was full: written once the buffer is
                var fromJournal = false;
                if (archiveBuffer.Count == 0 && _replay && _journal != null)
                {
                    archiveBuffer = _journal.Peek(MAX_ARCHIVE_CHUNK_SIZE);
                    fromJournal = archiveBuffer.Count > 0;
                }

                try
                {
                    _archive.InsertValues(archiveBuffer);
                }
                catch
                {
                    // The values are written with the next chunk; only written values reach the
                    // actual values and the compression. Those of the journal stay there.
                    if (!fromJournal)
                    {
                        Requeue(archiveBuffer);
                    }
                    throw;
                }

                if (fromJournal)
                {
                    _journal.Commit(archiveBuffer.Count);
                    LogJournal();
                }
                UpdateActualValues(archiveBuffer);
                DoOnArchiveInsert(archiveBuffer);

                if (archiveBuffer.Count > 0)
                {
                    Interlocked.Add(ref _insertedCount, archiveBuffer.Count);
                }
                _rate.Add(takeS, archiveBuffer.Count);

                Log.Debug("ArchiveInsertWorker count={ArchiveInsertTime} time={ArchiveInsertMillis}ms",
                    archiveBuffer.Count, sw.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Puts the values of a failed write back in front of the buffer. Above the buffer limit the
        /// newest values are dropped, like new values on an overflow.
        /// </summary>
        private void Requeue(List<ArchiveValue> values)
        {
            lock (_insertBuffer)
            {
                _insertBuffer.InsertRange(0, values);
                if (_journal != null)
                {
                    // New values go to the journal while the buffer is full, so it holds a chunk more at most
                    return;
                }

                var excess = _insertBuffer.Count - BUFFER_MAX_SIZE;
                if (excess > 0)
                {
                    _insertBuffer.RemoveRange(BUFFER_MAX_SIZE, excess);
                    CountDropped(excess);
                }
            }
        }

        /// <summary>
        /// Appends the values to the journal; those that do not fit within its size limit or that the disk does not take
        /// are dropped. Call within the buffer lock.
        /// </summary>
        private void ToJournal(List<ArchiveValue> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            if (!_journaling)
            {
                _journaling = true;
                Log.Warning("Archive insert buffer full ({Count} values): new values go to the journal on disk", _insertBuffer.Count);
            }

            try
            {
                var dropped = _journal.Append(values);
                if (dropped > 0)
                {
                    CountDropped(dropped);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Archive journal write failed");
                CountDropped(values.Count);
            }
        }

        /// <summary>Logs the values left in the journal at most every 10 s, and when it is written.</summary>
        private void LogJournal()
        {
            var left = _journal.Count;
            var now = DateTime.UtcNow;
            if (left == 0)
            {
                lock (_insertBuffer)
                {
                    if (_journaling && _journal.IsEmpty)
                    {
                        _journaling = false;
                        Log.Information("Archive journal written to the archive");
                    }
                }
            }
            else if ((now - _lastJournalLogUtc).TotalSeconds >= 10)
            {
                _lastJournalLogUtc = now;
                Log.Information("Archive journal: {Count} values left to write", left);
            }
        }

        /// <summary>
        /// Counts values dropped on an overflow, logged at most every 10 s. Call within the buffer lock.
        /// </summary>
        private void CountDropped(int count)
        {
            _droppedCount += count;
            var now = DateTime.UtcNow;
            if ((now - _lastDropLogUtc).TotalSeconds >= 10)
            {
                Log.Error("Archive insert buffer overflow! Dropped {Count} new values to protect the system", _droppedCount);
                _droppedCount = 0;
                _lastDropLogUtc = now;
            }
        }

        private void UpdateActualValues(List<ArchiveValue> values)
        {
            foreach (var value in values)
            {
                _actualValues[value.Id] = value;
            }
        }

        private void DoOnArchiveInsert(List<ArchiveValue> values)
        {
            OnArchiveInsert?.Invoke(this, values);
        }
        #endregion
    }
}
