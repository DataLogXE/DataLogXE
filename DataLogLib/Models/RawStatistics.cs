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

namespace DataLogLib.Models
{
    /// <summary>
    /// The statistics of a data tag in a time range. Count is the number of valid values stored in the range. The
    /// other members describe the signal: a value holds until the next one is stored (if that is valid), so the value
    /// that was stored before the range counts for the time it still held in it, and the mean and the standard
    /// deviation are weighted by the time each value held; the last value of the tag holds for its hold time at most.
    /// They are null when no valid value held in the range.
    /// </summary>
    public sealed record RawStatistics(
        long Count,
        double? Min,
        double? Max,
        /// <summary>The mean over time.</summary>
        double? Mean,
        /// <summary>The standard deviation over time.</summary>
        double? StdDev);
}
