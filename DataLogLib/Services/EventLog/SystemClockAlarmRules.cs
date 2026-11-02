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

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Decides when the system watch reports the clock of a data source as off: the clock difference stays above the
    /// accepted one for <see cref="RaiseDelayMs"/> (a start or a load peak does not raise), and the alarm goes once
    /// the difference is clearly back below it, so a clock near the limit does not toggle the alarm.
    /// </summary>
    public static class SystemClockAlarmRules
    {
        public const int RaiseDelayMs = 30_000;
        public const int MinHysteresisMs = 500;

        /// <summary>Whether the clock of a source the connection watch monitors is watched too.</summary>
        public static bool ShouldWatch(DataSourceModel dataSource)
        {
            return SystemConnectionAlarmRules.ShouldMonitor(dataSource)
                && dataSource.ClockMonitor
                && dataSource.ClockWarnMs > 0;
        }

        public static bool IsOff(long clockOffsetMs, int clockWarnMs)
        {
            return Math.Abs(clockOffsetMs) > clockWarnMs;
        }

        /// <summary>Back below the accepted difference by 10 %, at least <see cref="MinHysteresisMs"/>.</summary>
        public static bool IsBack(long clockOffsetMs, int clockWarnMs)
        {
            return Math.Abs(clockOffsetMs) <= clockWarnMs - Math.Max(MinHysteresisMs, clockWarnMs / 10);
        }
    }
}
