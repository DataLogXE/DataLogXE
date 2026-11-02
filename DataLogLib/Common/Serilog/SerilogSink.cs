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

using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace DataLogLib.Common.Serilog
{
    #region construction
    public class SerilogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new ConcurrentQueue<LogEvent>();

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            while (Events.Count >= DataLogLib.Services.EventLog.EventLogConstants.SinkMaxQueue
                && Events.TryDequeue(out _))
            {
            }

            Events.Enqueue(logEvent);
        }
    }
    #endregion
}
