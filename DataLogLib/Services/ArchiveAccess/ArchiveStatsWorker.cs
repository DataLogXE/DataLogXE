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
using Serilog;
using System.Diagnostics;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Records archive ingest rate from the insert worker and writes the values archived
    /// per minute into the archive. The total archive value count is the sum of these
    /// counts, so retention reduces the total with the stored data.
    /// </summary>
    public class ArchiveStatsWorker
    {
        #region public const
        public const int VALUE_COUNT_SUM_CYCLE_MS = 60000;
        #endregion

        #region public events
        public event EventHandler<(DateTime, long)> OnValueCountPerSec;
        public event EventHandler<(DateTime, double)> OnValueCountSumMio;
        public event EventHandler<(DateTime, double)> OnDatabaseDiskSizeGb;
        #endregion

        #region private fields
        private IArchive _archive;
        private ArchiveInsertWorker _insertWorker;
        private DateTime _minuteStart;
        private long _minuteCount;
        #endregion

        #region internal methods
        internal void Init(IArchive archive, ArchiveInsertWorker insertWorker)
        {
            _archive = archive;
            _insertWorker = insertWorker;
            _minuteStart = TruncateToMinute(DateTime.Now);
            new Thread(Worker) { IsBackground = true }.Start();
        }
        #endregion

        #region private methods
        private void Worker(object obj)
        {
            Log.Information("Startup worker thread: Archive statistics");
            Thread.Sleep(Consts.ARCHIVE_STATS_CYLE_MS);

            while (true)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var now = DateTime.Now;
                    var nowMinute = TruncateToMinute(now);
                    if (nowMinute > _minuteStart)
                    {
                        FlushClosedMinute();
                        _minuteStart = nowMinute;
                        _minuteCount = 0;
                    }

                    var n = _insertWorker.TakeInsertedCount();
                    _minuteCount += n;
                    var rate = _insertWorker.InsertRatePerSec();
                    if (rate != null)
                    {
                        DoOnValueCountPerSec(now, (long)Math.Round(rate.GetValueOrDefault()));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An exception collecting archive statistics was thrown!");
                }

                // Sleep and keep the cycle time on track
                var sleep = Consts.ARCHIVE_STATS_CYLE_MS - (int)sw.ElapsedMilliseconds;
                sleep = Math.Clamp(sleep, 1, Consts.ARCHIVE_STATS_CYLE_MS);

                Log.Debug("ArchiveStats runtime={ArchiveStatsExecutedTime}ms " +
                    "sleep={ArchiveStatsSleepTime}ms",
                    sw.ElapsedMilliseconds, sleep);

                Thread.Sleep(sleep);
            }
        }

        private void FlushClosedMinute()
        {
            var endTime = _minuteStart.AddMinutes(1);
            var count1Minute = _minuteCount;

            var totalSumN = _archive.GetValueCountSum();
            if (totalSumN != null)
            {
                var total = totalSumN.GetValueOrDefault() + count1Minute;
                Log.Debug("ArchiveStats total={ArchiveStatsTotal}", total);
                DoOnValueCountSumMio(endTime, total / 1000000.0);
            }

            _archive.InsertValueCount(endTime, count1Minute);

            var diskSizeBytes = _archive.GetDatabaseDiskSizeBytes();
            if (diskSizeBytes != null)
            {
                var gb = diskSizeBytes.GetValueOrDefault() / (1024.0 * 1024.0 * 1024.0);
                DoOnDatabaseDiskSizeGb(endTime, gb);
            }

            Log.Debug("ArchiveStats time={ArchiveStatsTime} minuteCount={ArchiveStatsCount1Min}",
                _minuteStart, count1Minute);
        }

        private static DateTime TruncateToMinute(DateTime time)
        {
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);
        }

        private void DoOnValueCountPerSec(DateTime dateTime, long value)
        {
            OnValueCountPerSec?.Invoke(this, (dateTime, value));
        }

        private void DoOnValueCountSumMio(DateTime dateTime, double value)
        {
            OnValueCountSumMio?.Invoke(this, (dateTime, value));
        }

        private void DoOnDatabaseDiskSizeGb(DateTime dateTime, double value)
        {
            OnDatabaseDiskSizeGb?.Invoke(this, (dateTime, value));
        }
        #endregion
    }
}
