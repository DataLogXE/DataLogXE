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

using DataLogLib.Common;
using DataLogLib.Models;
using Serilog;
using System.Diagnostics;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// This class collects all inserted raw data and reduces it per time window
    /// to the M4 points (first, min, max, last). The windows of 1 s (C001) are built
    /// from the raw values, the coarser ones in a lossless cascade (C005 ... C3600,
    /// see CompressionLevels) and all levels are inserted into the archive.
    /// Various statistical values such as the compression time and the number 
    /// of raw and compression values are made available via event.
    /// </summary>
    public class CompressionWorker
    {
        #region public events
        public event EventHandler<long> OnArchiveCompTimeMs;
        public event EventHandler<int> OnArchiveCompCountRaw;
        public event EventHandler<int> OnArchiveCompCount1Sec;
        #endregion

        #region private fields
        // Maximum number of compressed values kept for a retry after a failed archive insert
        private const int MAX_PENDING_VALUES = 100000;

        private IArchive _archive;
        // Gets the values that were not written (dropped or failed), to repair their time ranges
        private Action<List<ArchiveValue>> _onLost;
        private readonly object _mapLock = new();
        // Serializes the worker cycle and Flush, both change the cascade buffers.
        private readonly object _compressionGate = new();
        private Dictionary<int, List<ArchiveValue>> _compressionMap = [];
        private List<ArchiveValue> _pendingValues = [];
        private int _rawCount = 0;
        private int _comp1sCount = 0;

        // Latest raw value time and when it was seen. Windows are closed by this watermark,
        // which keeps advancing with the wall clock while no values arrive.
        private long _latestRawTicks = long.MinValue;
        private readonly Stopwatch _sinceLatestRaw = new();

        // Cascade levels: C001 collects the raw values, every other level the points of the
        // previous level per tag. A level emits them reduced as soon as a window is complete.
        private readonly CompressionCascade _cascade = new();
        #endregion

        #region internal methods
        internal void Init(IArchive archive, ArchiveInsertWorker insertWorker, Action<List<ArchiveValue>> onLost = null)
        {
            _archive = archive;
            _onLost = onLost;
            insertWorker.OnArchiveInsert += InsertWorker_OnArchiveInsert;
            new Thread(Worker) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Writes the open windows of all levels as window parts. The open windows are only held in
        /// memory, so call this on shutdown after the raw values were flushed. After a restart the
        /// rest of a window is written as a further part; the union of the M4 points of both parts
        /// still contains the first, min, max and last point of the whole window.
        /// </summary>
        internal void Flush()
        {
            lock (_compressionGate)
            {
                DoCompression(closeAll: true);
            }
        }
        #endregion

        #region private methods
        private void InsertWorker_OnArchiveInsert(object sender, List<ArchiveValue> values)
        {
            lock (_mapLock)
            {
                values.ForEach(value =>
                {
                    if (!_compressionMap.TryGetValue(value.Id, out var l))
                    {
                        l = [];
                        _compressionMap.Add(value.Id, l);
                    }
                    l.Add(value);
                });
            }
        }

        private void Worker(object obj)
        {
            Log.Information("Startup worker thread: Archive value compression");
            Thread.Sleep(Consts.ARCHIVE_COMPRESSION_CYLE_MS);

            while (true)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Execute compression task
                    lock (_compressionGate)
                    {
                        DoCompression();
                    }

                    // Fire stats events
                    DoOnArchiveCompTimeMs(sw.ElapsedMilliseconds);
                    DoOnArchiveCompCountRaw(_rawCount);
                    DoOnArchiveCompCount1Sec(_comp1sCount);

                    // Log debug
                    Log.Debug("CompressionWorker " +
                        "raw={ArchiveCompRaw} " +
                        "comp={ArchiveComp1s}",
                        _rawCount, _comp1sCount);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An exception during data compression was thrown!");
                }
                finally
                {
                    _rawCount = 0;
                    _comp1sCount = 0;
                }

                // Sleep and keep the cycle time on track
                var sleep = Consts.ARCHIVE_COMPRESSION_CYLE_MS - (int)sw.ElapsedMilliseconds;
                sleep = Math.Clamp(sleep, 1, Consts.ARCHIVE_COMPRESSION_CYLE_MS);

                Log.Debug("CompressionWorker runtime={ArchiveCompExecutedTime}ms " +
                    "sleep={ArchiveCompSleepTime}ms",
                    sw.ElapsedMilliseconds, sleep);

                Thread.Sleep(sleep);
            }
        }

        #region data compression
        /// <summary>
        /// Reduces the collected values through all levels and inserts the closed windows.
        /// </summary>
        /// <param name="closeAll">True closes all windows, also the open ones (shutdown).</param>
        private void DoCompression(bool closeAll = false)
        {
            // Take over the collected values. Swapping the map keeps the lock short.
            Dictionary<int, List<ArchiveValue>> compressionMap;
            lock (_mapLock)
            {
                compressionMap = _compressionMap;
                _compressionMap = [];
            }

            // Values of a failed insert in a previous cycle are retried first
            List<ArchiveValue> pending = _pendingValues;
            _pendingValues = [];

            // All raw values of this cycle feed the first level
            List<ArchiveValue> raw = [];
            foreach (var values in compressionMap.Values)
            {
                _rawCount += values.Count;
                raw.AddRange(values);
            }

            // Reduce the complete windows of every level, each level feeds the next one
            var watermark = GetWatermark(raw);
            if (closeAll)
            {
                watermark = long.MaxValue;
            }
            List<ArchiveValue> lost = [];
            var compressed = _cascade.Compress(raw, watermark, lost);
            _comp1sCount += compressed.Count(v => v.Compression == CompressionType.C001);
            ReportLost(lost);

            var cv = pending;
            cv.AddRange(compressed);
            if (cv.Count == 0)
            {
                return;
            }

            // Insert compressed values into the archive. The insert waits for the archive, so a
            // failed write is noticed and retried in the next cycle.
            try
            {
                _archive.InsertValuesAndWait(cv);
            }
            catch
            {
                // Keep the values for the next cycle. If too many are waiting, the oldest are
                // dropped and their time ranges are repaired from the raw values later.
                if (cv.Count > MAX_PENDING_VALUES)
                {
                    ReportLost(cv.GetRange(0, cv.Count - MAX_PENDING_VALUES));
                    _pendingValues = cv.GetRange(cv.Count - MAX_PENDING_VALUES, MAX_PENDING_VALUES);
                }
                else
                {
                    _pendingValues = cv;
                }
                throw;
            }
        }

        /// <summary>
        /// Returns the time up to which windows are complete: the latest raw value time, advanced
        /// by the wall clock while nothing arrives (so the last windows are closed after a stop),
        /// minus one cycle for values that arrive late.
        /// </summary>
        private long GetWatermark(List<ArchiveValue> raw)
        {
            foreach (var value in raw)
            {
                if (value.Time.Ticks > _latestRawTicks)
                {
                    _latestRawTicks = value.Time.Ticks;
                    _sinceLatestRaw.Restart();
                }
            }

            if (_latestRawTicks == long.MinValue)
            {
                return long.MinValue;
            }

            return _latestRawTicks
                + _sinceLatestRaw.Elapsed.Ticks
                - Consts.ARCHIVE_COMPRESSION_CYLE_MS * TimeSpan.TicksPerMillisecond;
        }

        private void ReportLost(List<ArchiveValue> lost)
        {
            if (lost.Count == 0 || _onLost == null)
            {
                return;
            }

            try
            {
                _onLost(lost);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reporting {Count} lost compression values failed", lost.Count);
            }
        }
        #endregion

        #region events
        public void DoOnArchiveCompTimeMs(long value)
        {
            OnArchiveCompTimeMs?.Invoke(this, value);
        }

        public void DoOnArchiveCompCountRaw(int value)
        {
            OnArchiveCompCountRaw?.Invoke(this, value);
        }

        public void DoOnArchiveCompCount1Sec(int value)
        {
            OnArchiveCompCount1Sec?.Invoke(this, value);
        }
        #endregion
        #endregion
    }
}
