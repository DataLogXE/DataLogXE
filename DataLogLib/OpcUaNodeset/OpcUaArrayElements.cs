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
using System.Text.RegularExpressions;

namespace DataLogLib.OpcUaNodeset
{
    /// <summary>
    /// The tags of the elements of an OPC UA array variable, the same for the browse of a connected server and the
    /// XML import: address "index=&lt;i,j&gt;;&lt;array address&gt;" (an IndexRange, 0 based), name and display name of
    /// the array with the index the PLC shows ("[1]" for Array[1..10] when the server names its element nodes so).
    /// </summary>
    public static class OpcUaArrayElements
    {
        public const string IndexPrefix = "index=";

        /// <summary>Elements of one array that become tags, like the MaxArrayLength quota of the client.</summary>
        public const int MaxElements = 65535;

        private static readonly Regex ElementIndexes =
            new(@"(?:^|\[)(-?\d+(?:,-?\d+)*)\]?$", RegexOptions.CultureInvariant);

        public static string Address(string arrayAddress, IReadOnlyList<int> indexes)
        {
            return IndexPrefix + FormatIndexes(indexes, null) + ";" + arrayAddress;
        }

        /// <summary>The indexes as the name shows them: each shifted by the lower bound of its dimension.</summary>
        public static string FormatIndexes(IReadOnlyList<int> indexes, IReadOnlyList<int> lowerBounds)
        {
            var parts = new string[indexes.Count];
            for (var i = 0; i < indexes.Count; i++)
            {
                var lower = lowerBounds != null && i < lowerBounds.Count ? lowerBounds[i] : 0;
                parts[i] = (indexes[i] + lower).ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// The indexes in the name of an element node: "x[1]", or only the index like the S7-1500 names them
        /// ("1", "1,2"); null for any other name or another number of dimensions.
        /// </summary>
        public static int[] ParseIndexes(string name, int rank)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var match = ElementIndexes.Match(name);
            if (!match.Success || name.EndsWith(']') != name.Contains('['))
            {
                return null;
            }

            var parts = match.Groups[1].Value.Split(',');
            if (parts.Length != rank)
            {
                return null;
            }

            var indexes = new int[rank];
            for (var d = 0; d < rank; d++)
            {
                if (!int.TryParse(parts[d], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out indexes[d]))
                {
                    return null;
                }
            }

            return indexes;
        }

        /// <summary>The identifier of an element node as servers with element nodes name it: array + "[i,j]".</summary>
        public static string ElementIdentifier(string arrayIdentifier, IReadOnlyList<int> indexes)
        {
            return arrayIdentifier + "[" + FormatIndexes(indexes, null) + "]";
        }

        /// <summary>
        /// Calls <paramref name="element"/> for every element, the last dimension fastest, at most
        /// <see cref="MaxElements"/>; true when the array has more and was cut.
        /// </summary>
        public static bool ForEach(IReadOnlyList<int> dimensions, Action<int[]> element)
        {
            var total = 1L;
            foreach (var dim in dimensions)
            {
                total *= dim;
            }

            var count = (int)Math.Min(total, MaxElements);
            var indexes = new int[dimensions.Count];
            for (var n = 0; n < count; n++)
            {
                element(indexes);
                for (var d = dimensions.Count - 1; d >= 0; d--)
                {
                    if (++indexes[d] < dimensions[d])
                    {
                        break;
                    }

                    indexes[d] = 0;
                }
            }

            return total > MaxElements;
        }
    }
}
