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

namespace DataLogLib.Common
{
    /// <summary>
    /// How long the last stored value of a tag holds when no value was stored after it. The tag stores a row at
    /// least every cycle (a data source stores the last value again after <see cref="Consts.ARCHIVE_VALUE_MIN_MS"/>),
    /// so a value without a successor that is older than this is no longer a signal: the source stopped
    /// delivering, e.g. because its connection was lost, and nothing marks that in the archive yet.
    /// </summary>
    public static class ArchiveHold
    {
        public static TimeSpan For(int cycleMs)
            => TimeSpan.FromMilliseconds(Math.Max(Consts.ARCHIVE_VALUE_HOLD_MS, 2L * Math.Max(cycleMs, 0)));
    }
}
