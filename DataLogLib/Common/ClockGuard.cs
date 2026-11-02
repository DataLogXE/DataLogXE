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

using System.Globalization;
using Serilog;

namespace DataLogLib.Common
{
    /// <summary>
    /// The limit for device clocks that are ahead. A time more than <see cref="MaxAheadMs"/> in the future is never
    /// taken (values and alarms of every source): the event log gives its rows rising times, so one row far in the
    /// future would push the times of every later row of all sources behind it. A source that takes the device times
    /// as they are refuses a device whose clock is ahead by more than this, and connects again only once the clock
    /// is back below it by <see cref="RejoinHysteresisMs"/>. 3 h also lets a clock off by a daylight saving or time
    /// zone mistake connect.
    /// </summary>
    public static class ClockGuard
    {
        public const long MaxAheadMs = 3 * 60 * 60 * 1000;
        public const long RejoinHysteresisMs = 10 * 60 * 1000;

        private static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);
        private static readonly object DropLock = new();
        private static readonly Dictionary<string, (long Count, DateTime LoggedUtc)> Drops = new(StringComparer.Ordinal);

        /// <summary>Whether the time lies more than <see cref="MaxAheadMs"/> after now (local and UTC times).</summary>
        public static bool IsTooFarAhead(DateTime time, DateTime nowUtc)
        {
            return time.ToUniversalTime() > nowUtc.AddMilliseconds(MaxAheadMs);
        }

        public static bool IsTooFarAhead(DateTime time) => IsTooFarAhead(time, DateTime.UtcNow);

        /// <summary>
        /// Whether a source that takes the device times as they are refuses the device: its clock is ahead by more
        /// than the limit, after a refusal by more than the limit minus the hysteresis.
        /// </summary>
        public static bool RefuseConnection(long deviceAheadMs, bool refusedBefore)
        {
            return deviceAheadMs > (refusedBefore ? MaxAheadMs - RejoinHysteresisMs : MaxAheadMs);
        }

        /// <summary>The text of a refused or dropped connection.</summary>
        public static string RefusalText(long deviceAheadMs)
        {
            var hours = (deviceAheadMs / 3_600_000.0).ToString("0.0", CultureInfo.InvariantCulture);
            return $"Device clock is {hours} h ahead (at most {MaxAheadMs / 3_600_000} h "
                + "while the device times are taken as they are), connection refused";
        }

        /// <summary>
        /// False for a time too far ahead, which the caller then drops; the drops are logged at most once a minute
        /// per kind ("values", "alarms").
        /// </summary>
        public static bool Admit(DateTime time, string kind)
        {
            var nowUtc = DateTime.UtcNow;
            if (!IsTooFarAhead(time, nowUtc))
            {
                return true;
            }

            long count;
            lock (DropLock)
            {
                Drops.TryGetValue(kind, out var drop);
                drop.Count++;
                if (nowUtc - drop.LoggedUtc < DropLogInterval)
                {
                    Drops[kind] = drop;
                    return false;
                }
                count = drop.Count;
                Drops[kind] = (0, nowUtc);
            }
            Log.Warning("ClockGuard: {Count} {Kind} more than {Hours} h in the future dropped (latest {Time:o})",
                count, kind, MaxAheadMs / 3_600_000, time.ToUniversalTime());
            return false;
        }
    }
}
