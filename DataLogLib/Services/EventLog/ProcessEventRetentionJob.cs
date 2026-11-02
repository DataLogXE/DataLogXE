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
    /// Drops expired process event chunks once an hour. The Timescale retention job does the same,
    /// but the background workers are capped on the small boards, so it may never get a slot.
    /// </summary>
    internal sealed class ProcessEventRetentionJob
    {
        private readonly ProcessEventStore _store;

        public ProcessEventRetentionJob(ProcessEventStore store)
        {
            _store = store;
        }

        public void Start()
        {
            new Thread(() => DatabaseLimits.Background(WorkerLoop)) { IsBackground = true, Name = "ProcessEventRetention" }.Start();
        }

        private void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    var dropped = _store.ApplyRetention();
                    if (dropped > 0)
                    {
                        Log.Information("Process event retention dropped {Count} chunks", dropped);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Process event retention failed");
                }

                Thread.Sleep(TimeSpan.FromHours(1));
            }
        }
    }
}
