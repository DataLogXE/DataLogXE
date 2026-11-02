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

using DataLogLib.OpcUaNodeset;
using Opc.Ua;
using System.Globalization;

namespace OpcUaSimpleClientApi
{
    /// <summary>
    /// The address of an OPC UA data tag: an ExpandedNodeId, or one element of an array variable as
    /// "index=&lt;NumericRange&gt;;&lt;ExpandedNodeId&gt;". An array is one variable with an array value (OPC UA Part 3); its
    /// element is read with an IndexRange (Part 4, NumericRange: one index per dimension, comma separated). No
    /// ExpandedNodeId starts with "index=", so the prefix never takes a plain node address apart.
    /// </summary>
    public static class OpcUaTagAddress
    {
        public const string IndexPrefix = OpcUaArrayElements.IndexPrefix;

        public static string Format(string nodeAddress, IReadOnlyList<int> indexes) =>
            OpcUaArrayElements.Address(nodeAddress, indexes);

        /// <summary>
        /// Splits an address into the node and the IndexRange of one element (null for a plain node). False for an
        /// index prefix without a valid range of single indexes.
        /// </summary>
        public static bool TryParse(string address, out string nodeAddress, out string indexRange)
        {
            nodeAddress = address;
            indexRange = null;
            if (address == null || !address.StartsWith(IndexPrefix, StringComparison.Ordinal))
            {
                return address != null;
            }

            var end = address.IndexOf(';', IndexPrefix.Length);
            if (end < 0 || end + 1 >= address.Length)
            {
                return false;
            }

            var range = address[IndexPrefix.Length..end];
            if (!IsElementRange(range))
            {
                return false;
            }

            nodeAddress = address[(end + 1)..];
            indexRange = range;
            return true;
        }

        // One element: a single index per dimension, no "a:b" ranges.
        private static bool IsElementRange(string range)
        {
            if (string.IsNullOrEmpty(range))
            {
                return false;
            }

            foreach (var part in range.Split(','))
            {
                if (part.Length == 0 || !part.All(char.IsAsciiDigit)
                    || !uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }
            }

            return ServiceResult.IsGood(NumericRange.Validate(range, out _));
        }

        /// <summary>
        /// The value of one element read with an IndexRange: the server returns an array (or matrix) of one element.
        /// False when the value is not exactly one element.
        /// </summary>
        public static bool TryUnwrapElement(Variant value, out object element, out TypeInfo typeInfo)
        {
            element = null;
            typeInfo = value.TypeInfo;
            Array elements;
            if (value.Value is Matrix matrix)
            {
                elements = matrix.Elements;
            }
            else if (value.TypeInfo != null && value.TypeInfo.ValueRank >= ValueRanks.OneDimension && value.Value is Array array)
            {
                elements = array;
            }
            else if (value.TypeInfo != null && value.TypeInfo.ValueRank == ValueRanks.Scalar)
            {
                // A server that returns the element as a scalar.
                element = value.Value;
                return true;
            }
            else
            {
                return false;
            }

            if (elements == null || elements.Length != 1)
            {
                return false;
            }

            element = elements.GetValue(0);
            if (element is Variant inner)
            {
                element = inner.Value;
                typeInfo = inner.TypeInfo;
                return typeInfo == null || typeInfo.ValueRank == ValueRanks.Scalar;
            }

            typeInfo = new TypeInfo(value.TypeInfo?.BuiltInType ?? BuiltInType.Variant, ValueRanks.Scalar);
            return true;
        }
    }
}
