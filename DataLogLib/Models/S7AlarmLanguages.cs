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
    /// Languages of the S7 alarm texts with their Windows language ids, as the PLC keys the texts
    /// (the app runs with invariant globalization, so CultureInfo cannot resolve them).
    /// </summary>
    public static class S7AlarmLanguages
    {
        public const int English = 0x0409;

        public static readonly IReadOnlyDictionary<string, int> ById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = 0x0409,
            ["de-DE"] = 0x0407,
            ["fr-FR"] = 0x040C,
            ["it-IT"] = 0x0410,
            ["es-ES"] = 0x0C0A,
            ["nl-NL"] = 0x0413,
            ["pl-PL"] = 0x0415,
            ["cs-CZ"] = 0x0405,
            ["hu-HU"] = 0x040E,
            ["sv-SE"] = 0x041D,
            ["pt-BR"] = 0x0416,
            ["ru-RU"] = 0x0419,
            ["tr-TR"] = 0x041F,
            ["zh-CN"] = 0x0804,
            ["ja-JP"] = 0x0411,
            ["ko-KR"] = 0x0412
        };

        /// <summary>The language id of a locale such as "de-DE"; English for an empty or unknown one.</summary>
        public static int LanguageId(string locale)
        {
            return !string.IsNullOrWhiteSpace(locale) && ById.TryGetValue(locale.Trim(), out var id) ? id : English;
        }

        /// <summary>The locale in its canonical spelling, or null when it is not in the list.</summary>
        public static string Normalize(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale))
            {
                return "";
            }
            foreach (var key in ById.Keys)
            {
                if (string.Equals(key, locale.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
            return null;
        }
    }
}
