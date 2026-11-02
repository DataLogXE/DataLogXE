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

using Serilog;
using DataLogLib.Services.DataAccess;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Keeps the raw value counters of the archive (schema 12, see TimescaleArchive.Counts.cs): once a minute it counts
    /// the closed minutes (roll-up), in between it counts the older raw values back to backfillDays before now or the
    /// oldest raw chunk (backfill). A step counts its window without the count lock and holds it only to insert the
    /// counts, which holds the writes of the raw values back, so a backfill window is sized to hold it 0.1 to 0.5 s;
    /// the backfill slows down while the device is busy (LoadPacer, BULK_WORK_MAX_LOAD). Before the backfill reached a
    /// range, its raw values are counted in archiveraw as before. The backfill starts 5 minutes after the service and with
    /// small windows: on the Raspberry Pi the insert buffer rose to 43 000 values (dropped from 50 000 on) while the data
    /// sources connected, the compression repair ran its startup check and the backfill started with 30 minutes.
    /// A step that held the writes back for more than a second is logged.
    /// </summary>
    internal sealed class ArchiveCountWorker(TimescaleArchive archive, int backfillDays, LoadPacer pacer)
    {
        internal const int DefaultBackfillDays = 30;

        // The schema is upgraded after the archive starts
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        // After the start of the data sources and the startup check of the compression repair (2 minutes)
        private static readonly TimeSpan BackfillDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RollUpInterval = TimeSpan.FromMinutes(1);
        // The minute before the current one is counted as well; the values of the current one are still arriving
        private static readonly TimeSpan RollUpDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TargetRefresh = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ProgressLog = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MinStep = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FirstStep = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxStep = TimeSpan.FromHours(6);
        private static readonly TimeSpan SlowLock = TimeSpan.FromSeconds(0.5);
        private static readonly TimeSpan FastLock = TimeSpan.FromSeconds(0.1);
        // A step that holds the writes back longer is logged
        private static readonly TimeSpan LongLock = TimeSpan.FromSeconds(1);

        private readonly TimescaleArchive _archive = archive;
        private readonly int _backfillDays = Math.Max(0, backfillDays);
        private readonly LoadPacer _pacer = pacer;

        public void Start()
        {
            new Thread(() => DatabaseLimits.Background(WorkerLoop)) { IsBackground = true, Name = "ArchiveCount" }.Start();
        }

        private void WorkerLoop()
        {
            var backfillAt = DateTime.UtcNow + BackfillDelay;
            Thread.Sleep(StartupDelay);
            var step = FirstStep;
            var maxLock = TimeSpan.Zero;
            var nextRollUp = DateTime.MinValue;
            var targetReadAt = DateTime.MinValue;
            var lastProgressLog = DateTime.MinValue;
            DateTime? target = null;
            var backfillDone = _backfillDays == 0;

            while (true)
            {
                try
                {
                    if (DateTime.UtcNow >= nextRollUp)
                    {
                        if (!_archive.RollUpCounts(_archive.UtcNow() - RollUpDelay, out var rollUpLock))
                        {
                            // Schema 12 is not there yet
                            Thread.Sleep(RetryDelay);
                            continue;
                        }
                        LogLongLock("roll-up", rollUpLock);
                        nextRollUp = DateTime.UtcNow + RollUpInterval;
                    }

                    if (backfillDone || DateTime.UtcNow < backfillAt)
                    {
                        Sleep(nextRollUp - DateTime.UtcNow);
                        continue;
                    }

                    if (target == null || DateTime.UtcNow - targetReadAt > TargetRefresh)
                    {
                        target = BackfillTarget();
                        targetReadAt = DateTime.UtcNow;
                    }

                    var from = _archive.BackfillCountsStep(target.Value, step, out var lockTime);
                    LogLongLock($"backfill of {step.TotalMinutes:0} minutes", lockTime);
                    if (lockTime > maxLock)
                    {
                        maxLock = lockTime;
                    }
                    if (from == null)
                    {
                        Thread.Sleep(RetryDelay);
                        continue;
                    }

                    if (from <= target)
                    {
                        Log.Information("Archive value counters complete from {From:u}", from.Value);
                        backfillDone = true;
                        continue;
                    }

                    if (DateTime.UtcNow - lastProgressLog > ProgressLog)
                    {
                        Log.Information("Archive value counters filled back to {From:u}, until {Target:u}, steps of {Step} minutes, "
                            + "writes held back {Lock:0.000} s at most", from.Value, target.Value, step.TotalMinutes, maxLock.TotalSeconds);
                        lastProgressLog = DateTime.UtcNow;
                        maxLock = TimeSpan.Zero;
                    }

                    // The counts of a step are inserted under the lock: the window is sized to hold it briefly
                    step = lockTime > SlowLock ? Max(MinStep, step / 2)
                        : lockTime < FastLock ? Min(MaxStep, step * 2)
                        : step;
                    _pacer.Step();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Archive value counters failed");
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        /// <summary>
        /// Back to backfillDays before now, but not before the oldest raw values.
        /// </summary>
        private DateTime BackfillTarget()
        {
            var target = _archive.UtcNow().AddDays(-_backfillDays);
            var oldest = _archive.GetOldestRawChunkStart();
            return oldest == null || oldest > _archive.UtcNow() ? _archive.UtcNow() : Max(target, oldest.Value);
        }

        private static void LogLongLock(string step, TimeSpan lockTime)
        {
            if (lockTime > LongLock)
            {
                Log.Warning("Archive value counters: the {Step} held the archive writes back for {Seconds:0.0} s", step, lockTime.TotalSeconds);
            }
        }

        private static void Sleep(TimeSpan time)
        {
            if (time > TimeSpan.Zero)
            {
                Thread.Sleep(time);
            }
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }
}
