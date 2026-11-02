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

namespace DataLogLib.OpcUaNodeset
{
    /// <summary>
    /// The arrays of a nodeset file, imported like the browse of a connected server imports them: an array of numbers
    /// or bools gives one tag per element (<see cref="OpcUaArrayElements"/>) and its element nodes are no tags of
    /// their own; the element nodes of other arrays (structures: their members) stay. The file lists an array before
    /// its element nodes, but the types may follow later, so the decision waits for the end of the file.
    /// </summary>
    internal sealed class XmlImportArrays
    {
        private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);
        private readonly List<ArrayNode> _arrays = [];
        private readonly Dictionary<string, ArrayNode> _byId = new(StringComparer.Ordinal);
        private readonly List<ElementNode> _elements = [];

        public void AddNode(string nodeId)
        {
            _nodes.Add(nodeId);
        }

        public void AddArray(string nodeId, string arrayDimensions, string browseName, XmlImportRow row)
        {
            var array = new ArrayNode(nodeId, ParseDimensions(arrayDimensions), row) { BrowseName = browseName };
            _arrays.Add(array);
            _byId[nodeId] = array;
        }

        /// <summary>Holds back a node named like an element of its parent ("parent[1]"); false for any other node.</summary>
        public bool TryDefer(string nodeId, string parentNodeId, string browseName, bool isArray, XmlImportRow row)
        {
            if (string.IsNullOrEmpty(parentNodeId)
                || nodeId.Length <= parentNodeId.Length + 2
                || !nodeId.StartsWith(parentNodeId, StringComparison.Ordinal)
                || nodeId[parentNodeId.Length] != '['
                || nodeId[^1] != ']')
            {
                return false;
            }

            _elements.Add(new ElementNode(nodeId, parentNodeId, nodeId[parentNodeId.Length..], browseName, isArray, row));
            return true;
        }

        public List<XmlImportRow> Finish(XmlImportTypeGraph types, List<KeyValuePair<string, string>> renames)
        {
            var rows = new List<XmlImportRow>();
            foreach (var element in _elements)
            {
                if (_byId.TryGetValue(element.Parent, out var array) && array.FirstElement == null)
                {
                    array.FirstElement = element;
                }
            }

            // Element nodes of an array of numbers: covered by its element tags, also when they are arrays
            // themselves (an Array of DT is a matrix of bytes; its element node is an array of 8 bytes).
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in _elements)
            {
                if (_byId.TryGetValue(element.Parent, out var array) && IsLoggable(types, array))
                {
                    covered.Add(element.NodeId);
                    continue;
                }

                if (!element.IsArray && !string.IsNullOrEmpty(element.Row.Address))
                {
                    rows.Add(element.Row);
                }
            }

            foreach (var array in _arrays)
            {
                if (array.Dimensions == null || string.IsNullOrEmpty(array.Row.Address) || !IsLoggable(types, array)
                    || covered.Contains(array.NodeId))
                {
                    continue;
                }

                var bounds = LowerBounds(array);
                var name = array.Row.Name;
                var displayName = array.Row.DisplayName ?? WithoutNamespace(array.BrowseName) ?? name;
                var typeId = types.Resolve(array.Row.DataType, true);
                OpcUaArrayElements.ForEach(array.Dimensions, indexes =>
                {
                    var address = OpcUaArrayElements.Address(array.Row.Address, indexes);
                    var text = "[" + OpcUaArrayElements.FormatIndexes(indexes, bounds) + "]";
                    rows.Add(new XmlImportRow
                    {
                        Address = address,
                        Name = name + text,
                        DisplayName = XmlImportTypeGraph.NormalizeDisplayName(displayName + text),
                        DataType = array.Row.DataType,
                        TypeId = typeId
                    });
                    if (bounds != null)
                    {
                        // The element node of the server: tags imported or browsed before keep their id and history.
                        renames?.Add(new KeyValuePair<string, string>(array.Row.Address + text, address));
                    }
                });
            }

            return rows;
        }

        private static bool IsLoggable(XmlImportTypeGraph types, ArrayNode array)
        {
            var kind = types.Resolve(array.Row.DataType, true);
            return kind is 1 or 2;
        }

        // Like the browse: the name of the first element node gives the first index, taken only when the node one
        // before it is unknown in every dimension and the last element exists.
        private int[] LowerBounds(ArrayNode array)
        {
            var first = array.FirstElement;
            if (first == null)
            {
                return null;
            }

            var dims = array.Dimensions;
            var indexes = OpcUaArrayElements.ParseIndexes(WithoutNamespace(first.BrowseName), dims.Length);
            if (indexes == null || first.Suffix != "[" + OpcUaArrayElements.FormatIndexes(indexes, null) + "]")
            {
                return null;
            }

            var last = new int[dims.Length];
            for (var d = 0; d < dims.Length; d++)
            {
                last[d] = indexes[d] + dims[d] - 1;
                var before = (int[])indexes.Clone();
                before[d]--;
                if (_nodes.Contains(OpcUaArrayElements.ElementIdentifier(array.NodeId, before)))
                {
                    return null;
                }
            }

            return _nodes.Contains(OpcUaArrayElements.ElementIdentifier(array.NodeId, last)) ? indexes : null;
        }

        private static int[] ParseDimensions(string arrayDimensions)
        {
            if (string.IsNullOrWhiteSpace(arrayDimensions))
            {
                return null;
            }

            var parts = arrayDimensions.Split(',');
            var dims = new int[parts.Length];
            for (var d = 0; d < parts.Length; d++)
            {
                // 0 is a length that is not fixed: without a server there is no value to take it from.
                if (!int.TryParse(parts[d].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out dims[d]) || dims[d] <= 0)
                {
                    return null;
                }
            }

            return dims;
        }

        // "3:0" -> "0": a BrowseName of a nodeset carries its namespace index.
        private static string WithoutNamespace(string browseName)
        {
            if (string.IsNullOrEmpty(browseName))
            {
                return null;
            }

            var colon = browseName.IndexOf(':');
            return colon > 0 && browseName[..colon].All(char.IsAsciiDigit) ? browseName[(colon + 1)..] : browseName;
        }

        private sealed class ArrayNode(string nodeId, int[] dimensions, XmlImportRow row)
        {
            public string NodeId { get; } = nodeId;
            public int[] Dimensions { get; } = dimensions;
            public XmlImportRow Row { get; } = row;
            public string BrowseName { get; set; }
            public ElementNode FirstElement { get; set; }
        }

        private sealed record ElementNode(
            string NodeId, string Parent, string Suffix, string BrowseName, bool IsArray, XmlImportRow Row);
    }
}
