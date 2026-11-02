#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

using System.Text;

namespace S7CommPlusDriver.Symbols
{
    /// <summary>One level of a symbolic path: a member name with optional array indexes.</summary>
    public sealed class PathToken
    {
        public string Name;
        public int[] Indexes;   // null when the level has no index, [a] or [a,b,...] otherwise

        public override string ToString() => SymbolPath.FormatName(Name) + SymbolPath.FormatIndexes(Indexes);
    }

    /// <summary>
    /// Symbolic paths in TIA Portal notation, e.g. <c>"DB Name".Motor[3].Speed</c> or <c>"Tag_1"</c>.
    /// Names are quoted when they are not plain identifiers; the first level (block or tag) is always quoted.
    /// </summary>
    public static class SymbolPath
    {
        public static List<PathToken> Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FormatException("Empty symbol");
            }
            var tokens = new List<PathToken>();
            int pos = 0;
            string s = path.Trim();
            while (pos < s.Length)
            {
                string name;
                if (s[pos] == '"')
                {
                    int end = s.IndexOf('"', pos + 1);
                    if (end < 0) throw new FormatException($"Missing closing quote in '{path}'");
                    name = s.Substring(pos + 1, end - pos - 1);
                    pos = end + 1;
                }
                else
                {
                    int start = pos;
                    while (pos < s.Length && s[pos] != '.' && s[pos] != '[') pos++;
                    name = s.Substring(start, pos - start).Trim();
                }
                if (name.Length == 0) throw new FormatException($"Empty name in '{path}'");
                var token = new PathToken { Name = name };
                if (pos < s.Length && s[pos] == '[')
                {
                    int end = s.IndexOf(']', pos);
                    if (end < 0) throw new FormatException($"Missing ']' in '{path}'");
                    string[] parts = s.Substring(pos + 1, end - pos - 1).Split(',');
                    token.Indexes = new int[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (!int.TryParse(parts[i].Trim(), out token.Indexes[i]))
                        {
                            throw new FormatException($"Invalid array index '{parts[i]}' in '{path}'");
                        }
                    }
                    pos = end + 1;
                }
                tokens.Add(token);
                if (pos < s.Length)
                {
                    if (s[pos] != '.') throw new FormatException($"Unexpected '{s[pos]}' in '{path}'");
                    pos++;
                    if (pos >= s.Length) throw new FormatException($"Path ends with '.' in '{path}'");
                }
            }
            return tokens;
        }

        public static bool IsPlainIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }

        public static string FormatName(string name) => IsPlainIdentifier(name) ? name : "\"" + name + "\"";

        public static string FormatRoot(string name) => "\"" + name + "\"";

        public static string FormatIndexes(int[] indexes)
        {
            if (indexes == null || indexes.Length == 0) return "";
            var sb = new StringBuilder("[");
            for (int i = 0; i < indexes.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(indexes[i]);
            }
            return sb.Append(']').ToString();
        }

        /// <summary>Normalized form (quoted root, quoted special member names) used as tag address.</summary>
        public static string Normalize(string path)
        {
            var tokens = Parse(path);
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append('.');
                sb.Append(i == 0 ? FormatRoot(tokens[i].Name) : FormatName(tokens[i].Name));
                sb.Append(FormatIndexes(tokens[i].Indexes));
            }
            return sb.ToString();
        }
    }
}
