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
using Serilog;

namespace DataLogService.DataSource.System.Telegraf
{
    internal class TelegrafSwapParser : BaseParser
    {
        #region construction
        public TelegrafSwapParser(Action<TelegrafValue> onValue) : base(onValue) { }
        #endregion

        #region internal methods
        internal void OnMessage(string message)
        {
            ParseMessage(message);
        }
        #endregion

        #region private methods
        private void ParseMessage(string message)
        {
            try
            {
                var dateTime = GetDateTime(message, out var valueString);

                var pairs = ParsePairs(valueString);

                foreach (var pair in pairs)
                {
                    ParsePair(dateTime, pair.Key, pair.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An exception was thrown!");
            }
        }

        private void ParsePair(DateTime dateTime, string key, string valueString)
        {
            double value;
            switch (key)
            {
                case "total":
                    value = GetValue(valueString, GIGABYTE);
                    DoOnValue($"swap_total_gb".ToPascalCase(), value, dateTime);
                    break;

                case "used":
                    value = GetValue(valueString, GIGABYTE);
                    DoOnValue($"swap_used_gb".ToPascalCase(), value, dateTime);
                    break;

                case "free":
                    value = GetValue(valueString, GIGABYTE);
                    DoOnValue($"swap_free_gb".ToPascalCase(), value, dateTime);
                    break;

                case "used_percent":
                    value = GetValue(valueString);
                    DoOnValue($"swap_used_percent".ToPascalCase(), value, dateTime);
                    break;
            }

        }
        #endregion
    }
}

