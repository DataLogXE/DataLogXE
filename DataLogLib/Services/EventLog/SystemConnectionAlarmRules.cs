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

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Decides which data sources the system connection watch monitors and how each state
    /// affects raise/clear of SystemAlarm occurrences.
    /// </summary>
    public static class SystemConnectionAlarmRules
    {
        public enum AlarmCycleAction
        {
            None = 0,
            Raise = 1,
            Clear = 2
        }

        public static bool ShouldMonitor(DataSourceModel dataSource)
        {
            if (dataSource == null || dataSource.IsActive == 0)
            {
                return false;
            }

            var type = (DataSourceType)dataSource.TypeId;
            return type == DataSourceType.OpcUa || type == DataSourceType.S7CommPlus;
        }

        public static AlarmCycleAction Decide(DataSourceState state)
        {
            switch (state)
            {
                case DataSourceState.Error:
                    return AlarmCycleAction.Raise;
                case DataSourceState.Connected:
                case DataSourceState.Inactive:
                    return AlarmCycleAction.Clear;
                default:
                    return AlarmCycleAction.None;
            }
        }
    }
}
