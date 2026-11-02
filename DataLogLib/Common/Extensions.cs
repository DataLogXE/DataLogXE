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

namespace DataLogLib.Common
{
    public static class Extensions
    {
        #region datetime extensions
        public static long ToUnixMilliseconds(this DateTime dateTime)
        {
            try
            {
                return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static DateTime FromUnixMilliseconds(this long unixMillis)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMillis).LocalDateTime;
        }

        public static double ToUnixSeconds(this DateTime dateTime)
        {
            try
            {
                return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds() / 1000.0;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        public static DateTime FromUnixSeconds(this double unixSeconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(unixSeconds * 1000.0)).LocalDateTime;
        }

        public static DateTime ToDateTimeFromUnixNanoSeconds(this long unixTimestampInNanoSeconds)
        {
            // Convert nanoseconds to ticks
            long ticks = unixTimestampInNanoSeconds / 100; // 1 tick = 100 nanoseconds

            // Create Unix epoch base date
            DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Create DateTime object
            return epoch.AddTicks(ticks).ToLocalTime();
        }
        #endregion

        #region string extensions
        public static string ToPascalCase(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            // Split the string by spaces or other delimiters
            var words = str.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

            // Capitalize the first letter of each word and combine them
            return string.Concat(words.Select(word => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(word.ToLower())));
        }
        #endregion

        #region dictionary extensions
        public static void AddRangeOverride<TKey, TValue>(this IDictionary<TKey, TValue> dic, IDictionary<TKey, TValue> dicToAdd)
        {
            dicToAdd.ToList().ForEach(x => dic[x.Key] = x.Value);
        }

        public static void AddRangeNewOnly<TKey, TValue>(this IDictionary<TKey, TValue> dic, IDictionary<TKey, TValue> dicToAdd)
        {
            dicToAdd.ToList().ForEach(x => { if (!dic.ContainsKey(x.Key)) dic.Add(x.Key, x.Value); });
        }

        public static void AddRange<TKey, TValue>(this IDictionary<TKey, TValue> dic, IDictionary<TKey, TValue> dicToAdd)
        {
            dicToAdd.ToList().ForEach(x => dic.Add(x.Key, x.Value));
        }
        #endregion

        #region double extension
        /// <summary>
        /// Checks if the difference between two double values is within a specified percentage.
        /// </summary>
        /// <param name="a">The first double value (invoking instance).</param>
        /// <param name="b">The second double value to compare.</param>
        /// <param name="percent">The percentage threshold within which the two values should differ.</param>
        /// <returns>True if the difference is within the specified percentage, otherwise false.</returns>
        public static bool IsWithinPercent(this double a, double b, double percent)
        {
           return Math.Abs(a - b) <= (percent / 100) * Math.Max(a, b);
        }
        #endregion
    }
}
