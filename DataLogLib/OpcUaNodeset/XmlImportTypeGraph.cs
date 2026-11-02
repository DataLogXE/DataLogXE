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
    internal sealed class XmlImportTypeGraph
    {
        private static readonly HashSet<string> BoolTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "BOOL", "Boolean"
        };

        private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "REAL", "LREAL", "BYTE",
            "TIME", "LTIME", "S5TIME", "DATE", "TOD", "LTOD", "CHAR", "WCHAR", "Enumeration",
            "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double"
        };

        // Text and object types never give a number (the OPC UA source parses every value as one): not imported, like
        // in the browse of a connected server and the S7 browse, so a new import removes the ones imported before
        private static readonly HashSet<string> RejectedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "STRING", "WSTRING", "DateTime", "LocalizedText", "Guid", "ByteString", "XmlElement",
            "NodeId", "ExpandedNodeId", "StatusCode", "QualifiedName", "DataValue", "DiagnosticInfo"
        };

        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _cache = new(StringComparer.Ordinal);

        public List<string> Namespaces { get; } = new(8);
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SuperTypes { get; } = new(StringComparer.Ordinal);

        public int Resolve(string dataType, bool final)
        {
            if (string.IsNullOrEmpty(dataType))
            {
                return 0;
            }

            if (_cache.TryGetValue(dataType, out var cached))
            {
                return cached;
            }

            if (NumberTypes.Contains(dataType))
            {
                _cache[dataType] = 2;
                return 2;
            }

            if (BoolTypes.Contains(dataType))
            {
                _cache[dataType] = 1;
                return 1;
            }

            if (RejectedTypes.Contains(dataType))
            {
                _cache[dataType] = 0;
                return 0;
            }

            var nodeId = dataType;
            if (Aliases.TryGetValue(dataType, out var aliased))
            {
                nodeId = aliased;
            }

            _seen.Clear();
            var pending = false;
            var result = 0;
            for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(nodeId); depth++)
            {
                if (!_seen.Add(nodeId))
                {
                    result = 0;
                    break;
                }

                var builtin = BuiltinTypeId(nodeId);
                if (builtin == -1)
                {
                    result = 0;
                    break;
                }

                if (builtin > 0)
                {
                    result = builtin;
                    break;
                }

                if (!SuperTypes.TryGetValue(nodeId, out var superType))
                {
                    if (!final)
                    {
                        pending = true;
                    }

                    result = 0;
                    break;
                }

                nodeId = superType;
            }

            if (!pending)
            {
                _cache[dataType] = result;
            }

            return pending ? -1 : result;
        }

        public string ToAddress(string nodeId)
        {
            if (Namespaces.Count == 0 ||
                nodeId.Length < 4 ||
                !nodeId.StartsWith("ns=", StringComparison.Ordinal))
            {
                return nodeId;
            }

            var indexStart = 3;
            var indexEnd = indexStart;
            while (indexEnd < nodeId.Length && char.IsDigit(nodeId[indexEnd]))
            {
                indexEnd++;
            }

            if (indexEnd == indexStart)
            {
                return nodeId;
            }

            if (!int.TryParse(
                    nodeId.AsSpan(indexStart, indexEnd - indexStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var nsIndex) ||
                nsIndex < 1 ||
                nsIndex > Namespaces.Count)
            {
                return nodeId;
            }

            return string.Concat("nsu=", Namespaces[nsIndex - 1], nodeId.AsSpan(indexEnd));
        }

        public static string ToName(string address)
        {
            var separator = address.IndexOf(";s=", StringComparison.Ordinal);
            if (separator < 0 || separator + 3 >= address.Length)
            {
                return address;
            }

            var identifier = address[(separator + 3)..];
            if (identifier.IndexOf('"') >= 0)
            {
                identifier = identifier.Replace("\"", "");
            }

            return string.IsNullOrWhiteSpace(identifier) ? address : identifier;
        }

        public static string NormalizeDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > XmlImportLimits.MaxDisplayNameChars)
            {
                return trimmed.Substring(0, XmlImportLimits.MaxDisplayNameChars);
            }

            return trimmed;
        }

        private static int BuiltinTypeId(string nodeId)
        {
            if (nodeId == "i=22" || nodeId == "i=127")
            {
                return -1;
            }

            if (nodeId == "i=1")
            {
                return 1;
            }

            if (!nodeId.StartsWith("i=", StringComparison.Ordinal))
            {
                return 0;
            }

            if (!int.TryParse(nodeId.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return 0;
            }

            if (id >= 2 && id <= 11)
            {
                return 2;
            }

            // String, DateTime, Guid, ByteString, XmlElement, NodeId, ExpandedNodeId, StatusCode, QualifiedName,
            // LocalizedText, DataValue and DiagnosticInfo, see RejectedTypes
            if (id is (>= 12 and <= 21) or 23 or 25)
            {
                return -1;
            }

            // BaseDataType (the value decides), Number, Integer, UInteger, Enumeration (an Int32)
            if (id is 24 or 26 or 27 or 28 or 29)
            {
                return 2;
            }

            return 0;
        }
    }
}
