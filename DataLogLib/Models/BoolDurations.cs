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
    /// How long a bool data tag held each state in a time range, in seconds. A value holds until the next one is
    /// stored, if that is valid; the time before an invalid value counts as no signal, and so does the time after the
    /// last value of the tag once its hold time has passed. Time before the first known value and time after now is not
    /// counted.
    /// </summary>
    public sealed record BoolDurations(
        /// <summary>The value was 1.</summary>
        double HighSeconds,
        /// <summary>The value was 0.</summary>
        double LowSeconds,
        /// <summary>The value was NaN or invalid, or the signal was lost before such a value was stored.</summary>
        double InvalidSeconds,
        /// <summary>How often the value changed between 0 and 1 in the range; a change over a lost signal is not counted.</summary>
        long Changes = 0,
        /// <summary>The longest time the value stayed high, in seconds; null without one. It may be cut by the range.</summary>
        double? LongestHighSeconds = null,
        /// <summary>The shortest time the value stayed high, only of the times that began and ended with a change in the range.</summary>
        double? ShortestHighSeconds = null,
        double? LongestLowSeconds = null,
        double? ShortestLowSeconds = null);
}
