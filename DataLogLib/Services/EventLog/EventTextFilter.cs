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

using System.Text;
using System.Text.RegularExpressions;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Alarm search text, matched like the data tag filter and the active alarm view
    /// (datalog-ui/src/textFilter.ts): case-insensitive "contains", <c>*</c> and <c>?</c> as wildcards.
    /// </summary>
    internal sealed class EventTextFilter
    {
        private static readonly EventTextFilter None = new(null, null);

        private readonly Regex _regex;

        private EventTextFilter(string likePattern, Regex regex)
        {
            LikePattern = likePattern;
            _regex = regex;
        }

        public bool IsEmpty => LikePattern == null;

        /// <summary>ILIKE pattern with the default escape character <c>\</c>, wrapped in <c>%</c>.</summary>
        public string LikePattern { get; }

        public static EventTextFilter Parse(string q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return None;
            }

            var trimmed = q.Trim().Replace("\"", "");
            if (trimmed.Length == 0)
            {
                return None;
            }

            var like = new StringBuilder(trimmed.Length + 8).Append('%');
            var regex = new StringBuilder(trimmed.Length * 2);
            foreach (var c in trimmed)
            {
                switch (c)
                {
                    case '*':
                        like.Append('%');
                        regex.Append(".*");
                        break;
                    case '?':
                        like.Append('_');
                        regex.Append('.');
                        break;
                    case '%':
                    case '_':
                    case '\\':
                        like.Append('\\').Append(c);
                        regex.Append(Regex.Escape(c.ToString()));
                        break;
                    default:
                        like.Append(c);
                        regex.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            like.Append('%');
            return new EventTextFilter(
                like.ToString(),
                new Regex(
                    regex.ToString(),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking));
        }

        public bool Matches(string text)
        {
            return IsEmpty || _regex.IsMatch(text ?? "");
        }

        /// <summary>Stored alarm states whose raw value or table label matches the filter.</summary>
        public List<string> MatchingAlarmStates()
        {
            var states = new List<string>();
            foreach (var basis in AlarmStateValues.KnownBases)
            {
                foreach (var state in new[] { basis, basis + AlarmStateValues.UnconfirmedSuffix })
                {
                    if (Matches(state) || Matches(EventLogLabels.AlarmState(state, "")))
                    {
                        states.Add(state);
                    }
                }
            }

            return states;
        }
    }
}
