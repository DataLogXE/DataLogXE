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

namespace DataLogLib.Services.DataAccess
{
    internal sealed class DataTagNameFilter
    {
        private static readonly DataTagNameFilter None = new(null, null);

        private readonly string _contains;
        private readonly bool _containsIsDigits;
        private readonly Regex _regex;

        private DataTagNameFilter(string contains, Regex regex)
        {
            _contains = contains;
            _containsIsDigits = contains != null && IsAllDigits(contains);
            _regex = regex;
        }

        public bool IsEmpty => _contains == null && _regex == null;

        public static DataTagNameFilter Parse(string q)
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

            if (!HasWildcard(trimmed))
            {
                return new DataTagNameFilter(trimmed, null);
            }

            return new DataTagNameFilter(null, BuildRegex(trimmed));
        }

        public bool Matches(string name)
        {
            if (IsEmpty)
            {
                return true;
            }

            name ??= "";
            if (_contains != null)
            {
                return name.Contains(_contains, StringComparison.OrdinalIgnoreCase);
            }

            return _regex.IsMatch(name);
        }

        public bool MatchesId(int id)
        {
            if (IsEmpty || id < 0)
            {
                return IsEmpty;
            }

            if (_contains != null)
            {
                return _containsIsDigits && IdContainsDigits(id, _contains);
            }

            return _regex.IsMatch(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static bool IsAllDigits(string value)
        {
            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        private static bool IdContainsDigits(int id, string digits)
        {
            Span<char> buf = stackalloc char[10];
            var length = 0;
            var value = id;
            if (value == 0)
            {
                buf[0] = '0';
                length = 1;
            }
            else
            {
                while (value > 0 && length < buf.Length)
                {
                    buf[length++] = (char)('0' + (value % 10));
                    value /= 10;
                }
            }

            var need = digits.Length;
            if (need > length)
            {
                return false;
            }

            for (var start = 0; start <= length - need; start++)
            {
                var found = true;
                for (var j = 0; j < need; j++)
                {
                    if (buf[length - 1 - (start + j)] != digits[j])
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasWildcard(string q)
        {
            foreach (var c in q)
            {
                if (c == '*' || c == '?')
                {
                    return true;
                }
            }

            return false;
        }

        private static Regex BuildRegex(string q)
        {
            var pattern = new StringBuilder(q.Length * 2);
            foreach (var c in q)
            {
                switch (c)
                {
                    case '*':
                        pattern.Append(".*");
                        break;
                    case '?':
                        pattern.Append('.');
                        break;
                    default:
                        pattern.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            return new Regex(
                pattern.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
    }
}
