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
using System.Globalization;

namespace DataLogService.DataSource.System.Telegraf
{
    internal class BaseParser
    {
        #region const
        protected const double GIGABYTE = 1024 * 1024 * 1024;
        #endregion

        #region private fields
        private readonly Action<TelegrafValue> _onValue;
        #endregion

        #region construction
        public BaseParser(Action<TelegrafValue> onValue)
        {
            _onValue = onValue;
        }
        #endregion

        #region protected methods
        protected void DoOnValue(string name, double value, DateTime time)
        {
            var tgv = new TelegrafValue()
            {
                Name = name,
                Value = value,
                Time = time
            };
            _onValue(tgv);
        }

        protected DateTime GetDateTime(string message, out string valueString)
        {
            int lastSpaceIndex = message.LastIndexOf(' ');

            if (lastSpaceIndex >= 0)
            {
                valueString = message.Substring(0, lastSpaceIndex).Trim();

                string dateTimeString = message.Substring(lastSpaceIndex + 1);
                long unixNanos = long.Parse(dateTimeString);
                var dt = unixNanos.ToDateTimeFromUnixNanoSeconds();
                return dt;
            }
            else
            {
                throw new Exception($"Could not parse datetime");
            }
        }

        protected double GetValue(string valueString, double divisor = 1.0)
        {
            // Line protocol numbers use '.' and mark integers with 'i' and unsigned integers with 'u'.
            if (valueString.EndsWith('i') || valueString.EndsWith('u'))
            {
                valueString = valueString[..^1];
            }

            var value = double.Parse(valueString, NumberStyles.Float, CultureInfo.InvariantCulture);
            return value / divisor;
        }

        /// <summary>
        /// Splits "measurement,tag=v,... field=v,..." (timestamp already removed) into tags and fields.
        /// Tags and fields are separated by an unescaped space; pairs by unescaped commas. Splitting the
        /// whole text at commas only would glue the last tag to the first field and lose that field.
        /// Fields win over tags with the same key.
        /// </summary>
        protected static Dictionary<string, string> ParsePairs(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var sections = SplitUnescaped(text, ' ');
            for (var s = 0; s < sections.Count && s < 2; s++)
            {
                var items = SplitUnescaped(sections[s], ',');
                // The first item of the first section is the measurement name.
                for (var i = s == 0 ? 1 : 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var eq = IndexOfUnescaped(item, '=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var key = Unescape(item[..eq]).Trim();
                    var value = item[(eq + 1)..].Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    {
                        value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
                    }
                    else
                    {
                        value = Unescape(value);
                    }

                    result[key] = value;
                }
            }

            return result;
        }

        private static List<string> SplitUnescaped(string text, char separator)
        {
            var parts = new List<string>();
            var start = 0;
            var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == separator && !inQuotes)
                {
                    if (i > start)
                    {
                        parts.Add(text[start..i]);
                    }

                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                parts.Add(text[start..]);
            }

            return parts;
        }

        private static int IndexOfUnescaped(string text, char value)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == value)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unescape(string text)
        {
            return text.Replace("\\ ", " ").Replace("\\,", ",").Replace("\\=", "=");
        }
        #endregion
    }
}
