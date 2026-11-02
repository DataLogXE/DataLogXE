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

using System.Text.Json;

namespace DataLogLib.Models
{
    /// <summary>
    /// The description of a data tag in all languages the source delivers, stored as JSON object culture -> text
    /// (e.g. {"de-DE":"Motor an","en-US":"Motor on"}); S7CommPlus fills it with the comments of the PLC variables.
    /// </summary>
    public static class DataTagDescription
    {
        /// <summary>The JSON of the texts sorted by culture, null when there is no text.</summary>
        public static string ToJson(IEnumerable<KeyValuePair<string, string>> texts)
        {
            if (texts == null)
            {
                return null;
            }
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in texts)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    sorted[kv.Key] = kv.Value;
                }
            }
            return sorted.Count == 0 ? null : JsonSerializer.Serialize(sorted);
        }

        /// <summary>The texts of a stored description; empty for null or text that is not such a JSON object.</summary>
        public static IReadOnlyDictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
            {
                return result;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        result[p.Name] = p.Value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not a description of ours: no text.
            }
            return result;
        }

        /// <summary>
        /// The text in exactly the given language (empty locale = English), without falling back to another one:
        /// a comment that is not translated stays empty.
        /// </summary>
        public static string Resolve(string json, string locale)
        {
            var language = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
            return Parse(json).TryGetValue(language, out var text) ? text ?? "" : "";
        }
    }
}
