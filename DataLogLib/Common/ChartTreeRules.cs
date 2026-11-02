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

namespace DataLogLib.Common
{
    /// <summary>
    /// Rules of the chart tree (folders and charts below the root), kept free of the database so they can be tested.
    /// </summary>
    public static class ChartTreeRules
    {
        /// <summary>
        /// Root and folders hold charts and folders; a chart holds data tags only.
        /// </summary>
        public static bool IsContainer(ChartItemType type)
        {
            return type == ChartItemType.Root || type == ChartItemType.Folder;
        }

        /// <summary>
        /// True if moving the node below the new parent puts it into its own subtree. Walks up from the new
        /// parent, so a loop in the stored data (the root points at itself) ends the walk instead of hanging.
        /// </summary>
        public static bool WouldCreateCycle(IReadOnlyDictionary<int, int> parentById, int nodeId, int newParentId)
        {
            var visited = new HashSet<int>();
            var current = newParentId;
            while (visited.Add(current))
            {
                if (current == nodeId)
                {
                    return true;
                }

                if (!parentById.TryGetValue(current, out var parent) || parent == current)
                {
                    return false;
                }

                current = parent;
            }

            // A loop above the new parent that does not contain the node
            return false;
        }

        /// <summary>
        /// Compares two names for sorting: letters without regard to case, runs of digits by their value (2 before 10).
        /// Done by hand, because the app runs without culture data (invariant globalization), where a culture
        /// comparison is ordinal and knows no numeric order.
        /// </summary>
        public static int CompareNames(string a, string b)
        {
            a ??= "";
            b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    var startA = i;
                    var startB = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i]))
                    {
                        i++;
                    }

                    while (j < b.Length && char.IsAsciiDigit(b[j]))
                    {
                        j++;
                    }

                    // The longer number without leading zeros is the bigger one; of equal length the digits decide
                    var digitsA = a.AsSpan(startA, i - startA).TrimStart('0');
                    var digitsB = b.AsSpan(startB, j - startB).TrimStart('0');
                    var byValue = digitsA.Length != digitsB.Length
                        ? digitsA.Length.CompareTo(digitsB.Length)
                        : digitsA.CompareTo(digitsB, StringComparison.Ordinal);
                    if (byValue != 0)
                    {
                        return byValue;
                    }
                }
                else
                {
                    var byChar = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (byChar != 0)
                    {
                        return byChar;
                    }

                    i++;
                    j++;
                }
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>
        /// The positive ids without duplicates, in the order they came.
        /// </summary>
        public static List<int> NormalizeIds(IEnumerable<int> ids)
        {
            var seen = new HashSet<int>();
            var result = new List<int>();
            foreach (var id in ids ?? [])
            {
                if (id > 0 && seen.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }
    }
}
