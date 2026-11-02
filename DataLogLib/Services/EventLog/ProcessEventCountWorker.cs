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

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Counts the alarms that were stored before schema 17 into the hourly alarm counts (see ProcessEventStore.Counts.cs),
    /// from the newest hour back to the oldest event, and ends when it got there. It reads every row once (on the Raspberry
    /// Pi 1.1 million rows a week, 3.9 GB for 30 days), so it slows down while the device is busy (LoadPacer,
    /// BULK_WORK_MAX_LOAD) and the window grows while steps are quick.
    /// </summary>
    internal sealed class ProcessEventCountWorker(ProcessEventStore store, LoadPacer pacer)
    {
        // The schema is upgraded after the event log starts
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        // A write that began before schema 17 appeared counts nothing; it ends long before this
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan OldestRefresh = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ProgressLog = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FastStep = TimeSpan.FromSeconds(0.5);
        private const int MaxStepHours = 24;

        private readonly ProcessEventStore _store = store;
        private readonly LoadPacer _pacer = pacer;

        public void Start()
        {
            new Thread(() => DatabaseLimits.Background(WorkerLoop)) { IsBackground = true, Name = "ProcessEventCount" }.Start();
        }

        private void WorkerLoop()
        {
            Thread.Sleep(StartupDelay);
            var prepared = false;
            var hours = 1;
            var oldestReadAt = DateTime.MinValue;
            var lastProgressLog = DateTime.MinValue;
            DateTime? oldest = null;

            while (true)
            {
                try
                {
                    if (!prepared)
                    {
                        while (!_store.CountsAvailable())
                        {
                            Thread.Sleep(RetryDelay);
                        }

                        // A write that began before schema 17 appeared has to end before live_from_id is set
                        Thread.Sleep(SettleDelay);
                        _store.PrepareCountBackfill();
                        prepared = true;
                    }

                    if (DateTime.UtcNow - oldestReadAt > OldestRefresh)
                    {
                        oldest = _store.ReadOldestEventTime();
                        oldestReadAt = DateTime.UtcNow;
                    }

                    var step = _store.BackfillCountsStep(hours, oldest);
                    if (step.Before == null && !step.Done)
                    {
                        Thread.Sleep(RetryDelay);
                        continue;
                    }

                    if (step.Done)
                    {
                        Log.Information("Alarm counts complete from {From:u}", step.Before ?? DateTime.UtcNow);
                        return;
                    }

                    if (DateTime.UtcNow - lastProgressLog > ProgressLog)
                    {
                        Log.Information("Alarm counts filled back to {From:u}, steps of {Hours} hours, the last took {Seconds:0.0} s",
                            step.Before.Value, hours, step.Took.TotalSeconds);
                        lastProgressLog = DateTime.UtcNow;
                    }

                    hours = step.Took > SlowStep ? Math.Max(1, hours / 2)
                        : step.Took < FastStep ? Math.Min(MaxStepHours, hours * 2)
                        : hours;
                    _pacer.Step();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Alarm counts failed");
                    Thread.Sleep(RetryDelay);
                }
            }
        }
    }
}
