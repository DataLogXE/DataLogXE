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

namespace DataLogLib.OpcUaNodeset
{
    internal enum NodesetTagKind : byte
    {
        Other = 0,
        Variable,
        NamespaceUris,
        Uri,
        Alias,
        DataType,
        Reference,
        DisplayName
    }

    internal enum NodesetEventKind : byte
    {
        Start,
        End,
        Text
    }

    internal struct NodesetEvent
    {
        public NodesetEventKind Kind;
        public NodesetTagKind Tag;
        public bool IsEmpty;
        public string NodeId;
        public string DataType;
        public string Alias;
        public string ReferenceType;
        public string IsForward;
        public string Text;
        // A variable: the parent, the browse name and, for an array (ValueRank 1 or more), its ArrayDimensions.
        public string ParentNodeId;
        public string BrowseName;
        public bool IsArray;
        public string ArrayDimensions;
    }

    internal sealed class NodesetByteScanner : IDisposable
    {
        private static readonly byte[] LeafMarker = ";s="u8.ToArray();
        private static readonly byte[] ArrayMarker = ";s=V_"u8.ToArray();

        private readonly FileStream _stream;
        private readonly CancellationToken _cancellation;
        private byte[] _buffer;
        private int _index;
        private int _length;
        private long _baseOffset;
        private long _lastReport;
        private int _tagsSinceCancel;
        private bool _wantText;
        private bool _captureAttributes = true;

        public NodesetByteScanner(string path, CancellationToken cancellation)
        {
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                XmlImportLimits.FileBufferBytes,
                FileOptions.SequentialScan);
            _buffer = new byte[XmlImportLimits.FileBufferBytes];
            _cancellation = cancellation;
            Fill();
            if (_length >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
            {
                _index = 3;
            }
        }

        public long BytesRead => _baseOffset + _index;

        public Action<long> OnBytes { get; set; }

        public bool WantText
        {
            get => _wantText;
            set => _wantText = value;
        }

        public bool TryRead(out NodesetEvent item)
        {
            item = default;
            while (true)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (_index >= _length && !Fill())
                {
                    return false;
                }

                if (_buffer[_index] != (byte)'<')
                {
                    if (_wantText)
                    {
                        item.Kind = NodesetEventKind.Text;
                        item.Text = ReadText();
                        return true;
                    }

                    SkipText();
                    continue;
                }

                if (!TryReadTag(out item))
                {
                    continue;
                }

                return true;
            }
        }

        public void SkipRestOfElement()
        {
            var depth = 1;
            var capture = _captureAttributes;
            var wantText = _wantText;
            _captureAttributes = false;
            _wantText = false;
            try
            {
                while (depth > 0)
                {
                    if (++_tagsSinceCancel >= 64)
                    {
                        _tagsSinceCancel = 0;
                        _cancellation.ThrowIfCancellationRequested();
                        ReportBytes(false);
                    }

                    if (!TryRead(out var item))
                    {
                        throw new InvalidDataException("The XML file ended inside an element.");
                    }

                    if (item.Kind == NodesetEventKind.End)
                    {
                        depth--;
                    }
                    else if (item.Kind == NodesetEventKind.Start && !item.IsEmpty)
                    {
                        depth++;
                    }
                }
            }
            finally
            {
                _captureAttributes = capture;
                _wantText = wantText;
            }

            ReportBytes(false);
        }

        public string ReadFirstDisplayName()
        {
            var depth = 1;
            var displayDepth = 0;
            var seen = false;
            string displayName = null;
            StringBuilder builder = null;
            var capture = _captureAttributes;
            var wantText = _wantText;
            _captureAttributes = false;
            _wantText = false;
            try
            {
                while (depth > 0)
                {
                    if (++_tagsSinceCancel >= 64)
                    {
                        _tagsSinceCancel = 0;
                        _cancellation.ThrowIfCancellationRequested();
                        ReportBytes(false);
                    }

                    if (!TryRead(out var item))
                    {
                        throw new InvalidDataException("The XML file ended inside an element.");
                    }

                    if (displayDepth > 0)
                    {
                        if (item.Kind == NodesetEventKind.Text)
                        {
                            AppendDisplayName(ref builder, item.Text);
                        }
                        else if (item.Kind == NodesetEventKind.End)
                        {
                            depth--;
                            displayDepth--;
                            if (displayDepth == 0)
                            {
                                displayName = FinishDisplayName(builder);
                                builder = null;
                            }
                        }
                        else if (item.Kind == NodesetEventKind.Start && !item.IsEmpty)
                        {
                            depth++;
                            displayDepth++;
                        }

                        _wantText = displayDepth == 1;
                        continue;
                    }

                    if (item.Kind == NodesetEventKind.End)
                    {
                        depth--;
                        continue;
                    }

                    if (item.Kind != NodesetEventKind.Start)
                    {
                        continue;
                    }

                    if (!seen && depth == 1 && item.Tag == NodesetTagKind.DisplayName)
                    {
                        seen = true;
                        if (!item.IsEmpty)
                        {
                            depth++;
                            displayDepth = 1;
                            _wantText = true;
                        }

                        continue;
                    }

                    if (!item.IsEmpty)
                    {
                        depth++;
                    }
                }
            }
            finally
            {
                _captureAttributes = capture;
                _wantText = wantText;
            }

            ReportBytes(false);
            return displayName;
        }

        private static void AppendDisplayName(ref StringBuilder builder, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            builder ??= new StringBuilder();
            if (builder.Length >= XmlImportLimits.MaxDisplayNameChars)
            {
                return;
            }

            var room = XmlImportLimits.MaxDisplayNameChars - builder.Length;
            if (text.Length <= room)
            {
                builder.Append(text);
                return;
            }

            builder.Append(text.AsSpan(0, room));
        }

        private static string FinishDisplayName(StringBuilder builder)
        {
            return XmlImportTypeGraph.NormalizeDisplayName(builder?.ToString());
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        public static bool IsLeafNodeId(ReadOnlySpan<byte> nodeId)
        {
            var index = nodeId.IndexOf(LeafMarker);
            if (index < 0 || index + LeafMarker.Length >= nodeId.Length)
            {
                return false;
            }

            return nodeId.IndexOf(ArrayMarker) < 0;
        }

        public static bool IsArray(ReadOnlySpan<byte> arrayDimensions)
        {
            var start = 0;
            var end = arrayDimensions.Length;
            while (start < end && arrayDimensions[start] is (byte)' ' or (byte)'\t')
            {
                start++;
            }

            while (end > start && arrayDimensions[end - 1] is (byte)' ' or (byte)'\t')
            {
                end--;
            }

            if (end <= start)
            {
                return false;
            }

            return end - start != 1 || arrayDimensions[start] != (byte)'0';
        }

        private bool TryReadTag(out NodesetEvent item)
        {
            item = default;
            if (_index >= _length && !Fill())
            {
                return false;
            }

            if (_buffer[_index] != (byte)'<')
            {
                if (_wantText)
                {
                    item.Kind = NodesetEventKind.Text;
                    item.Text = ReadText();
                    return true;
                }

                SkipText();
                return false;
            }

            var tagStart = _index;
            if (!LoadThroughTagEnd(ref tagStart))
            {
                throw new InvalidDataException("The XML file ended inside a tag.");
            }

            var tag = _buffer.AsSpan(tagStart, _index - tagStart);
            if (tag.Length >= 2 && tag[1] == (byte)'?')
            {
                return false;
            }

            if (tag.Length >= 4 && tag[1] == (byte)'!' && (tag[2] == (byte)'-' || tag[2] == (byte)'['))
            {
                return false;
            }

            var cursor = 1;
            var isEnd = false;
            if (cursor < tag.Length && tag[cursor] == (byte)'/')
            {
                isEnd = true;
                cursor++;
            }

            while (cursor < tag.Length && IsSpace(tag[cursor]))
            {
                cursor++;
            }

            var nameStart = cursor;
            while (cursor < tag.Length && !IsSpace(tag[cursor]) && tag[cursor] != (byte)'/' && tag[cursor] != (byte)'>')
            {
                cursor++;
            }

            var name = tag.Slice(nameStart, cursor - nameStart);
            var colon = name.LastIndexOf((byte)':');
            if (colon >= 0)
            {
                name = name[(colon + 1)..];
            }

            var isEmpty = false;
            var kind = KindOf(name);
            string nodeId = null;
            string dataType = null;
            string alias = null;
            string referenceType = null;
            string isForward = null;
            ReadOnlySpan<byte> nodeIdRaw = default;
            ReadOnlySpan<byte> dataTypeRaw = default;
            ReadOnlySpan<byte> arrayRaw = default;
            var sawArray = false;
            ReadOnlySpan<byte> parentRaw = default;
            ReadOnlySpan<byte> browseNameRaw = default;
            var valueRank = -1;

            while (cursor < tag.Length)
            {
                while (cursor < tag.Length && IsSpace(tag[cursor]))
                {
                    cursor++;
                }

                if (cursor >= tag.Length || tag[cursor] == (byte)'>')
                {
                    break;
                }

                if (tag[cursor] == (byte)'/')
                {
                    isEmpty = true;
                    cursor++;
                    continue;
                }

                var attrStart = cursor;
                while (cursor < tag.Length && tag[cursor] != (byte)'=' && !IsSpace(tag[cursor]) && tag[cursor] != (byte)'>' && tag[cursor] != (byte)'/')
                {
                    cursor++;
                }

                var attrName = tag.Slice(attrStart, cursor - attrStart);
                while (cursor < tag.Length && IsSpace(tag[cursor]))
                {
                    cursor++;
                }

                if (cursor >= tag.Length || tag[cursor] != (byte)'=')
                {
                    continue;
                }

                cursor++;
                while (cursor < tag.Length && IsSpace(tag[cursor]))
                {
                    cursor++;
                }

                if (cursor >= tag.Length || (tag[cursor] != (byte)'"' && tag[cursor] != (byte)'\''))
                {
                    continue;
                }

                var quote = tag[cursor++];
                var valueStart = cursor;
                while (cursor < tag.Length && tag[cursor] != quote)
                {
                    cursor++;
                }

                var value = tag.Slice(valueStart, cursor - valueStart);
                if (cursor < tag.Length && tag[cursor] == quote)
                {
                    cursor++;
                }

                if (isEnd || kind == NodesetTagKind.Other || !_captureAttributes)
                {
                    continue;
                }

                if (attrName.SequenceEqual("NodeId"u8))
                {
                    nodeIdRaw = value;
                }
                else if (attrName.SequenceEqual("DataType"u8))
                {
                    dataTypeRaw = value;
                }
                else if (attrName.SequenceEqual("ArrayDimensions"u8))
                {
                    arrayRaw = value;
                    sawArray = true;
                }
                else if (attrName.SequenceEqual("ValueRank"u8))
                {
                    valueRank = int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var rank) ? rank : -1;
                }
                else if (attrName.SequenceEqual("ParentNodeId"u8))
                {
                    parentRaw = value;
                }
                else if (attrName.SequenceEqual("BrowseName"u8))
                {
                    browseNameRaw = value;
                }
                else if (attrName.SequenceEqual("Alias"u8))
                {
                    alias = Decode(value);
                }
                else if (attrName.SequenceEqual("ReferenceType"u8))
                {
                    referenceType = Decode(value);
                }
                else if (attrName.SequenceEqual("IsForward"u8))
                {
                    isForward = Decode(value);
                }
            }

            if (kind == NodesetTagKind.Variable && !isEnd)
            {
                if (!IsLeafNodeId(nodeIdRaw))
                {
                    nodeId = null;
                    dataType = null;
                }
                else
                {
                    nodeId = Decode(nodeIdRaw);
                    dataType = Decode(dataTypeRaw);
                    item.ParentNodeId = parentRaw.IsEmpty ? null : Decode(parentRaw);
                    item.BrowseName = browseNameRaw.IsEmpty ? null : Decode(browseNameRaw);
                    item.IsArray = valueRank >= 1 || (sawArray && IsArray(arrayRaw));
                    item.ArrayDimensions = item.IsArray && sawArray ? Decode(arrayRaw) : null;
                }
            }
            else if (kind == NodesetTagKind.DataType && !isEnd)
            {
                nodeId = Decode(nodeIdRaw);
            }

            item.Kind = isEnd ? NodesetEventKind.End : NodesetEventKind.Start;
            item.Tag = kind;
            item.IsEmpty = isEmpty || isEnd;
            item.NodeId = nodeId;
            item.DataType = dataType;
            item.Alias = alias;
            item.ReferenceType = referenceType;
            item.IsForward = isForward;
            return true;
        }

        private string ReadText()
        {
            var builder = new StringBuilder();
            while (_index < _length || Fill())
            {
                if (_buffer[_index] == (byte)'<')
                {
                    break;
                }

                var start = _index;
                while (_index < _length && _buffer[_index] != (byte)'<')
                {
                    _index++;
                }

                AppendDecoded(builder, _buffer.AsSpan(start, _index - start));
                if (builder.Length > XmlImportLimits.MaxTextChars)
                {
                    SkipText();
                    return "";
                }
            }

            return builder.ToString();
        }

        private void SkipText()
        {
            while (_index < _length || Fill())
            {
                if (_buffer[_index] == (byte)'<')
                {
                    return;
                }

                var next = _buffer.AsSpan(_index, _length - _index).IndexOf((byte)'<');
                if (next < 0)
                {
                    _index = _length;
                    ReportBytes(false);
                    continue;
                }

                _index += next;
                return;
            }
        }

        private bool LoadThroughTagEnd(ref int tagStart)
        {
            EnsurePrefix(ref tagStart, 9);
            var available = _length - tagStart;
            if (available >= 4 &&
                _buffer[tagStart + 1] == (byte)'!' &&
                _buffer[tagStart + 2] == (byte)'-' &&
                _buffer[tagStart + 3] == (byte)'-')
            {
                return LoadUntil(ref tagStart, "-->"u8);
            }

            if (available >= 2 && _buffer[tagStart + 1] == (byte)'?')
            {
                return LoadUntil(ref tagStart, "?>"u8);
            }

            if (available >= 9 && _buffer.AsSpan(tagStart, 9).SequenceEqual("<![CDATA["u8))
            {
                return LoadUntil(ref tagStart, "]]>"u8);
            }

            var inQuote = false;
            byte quote = 0;

            _index = tagStart + 1;
            while (true)
            {
                if (_index >= _length)
                {
                    if (!RefillKeeping(ref tagStart))
                    {
                        return false;
                    }
                }

                var current = _buffer[_index++];
                if (inQuote)
                {
                    if (current == quote)
                    {
                        inQuote = false;
                    }

                    continue;
                }

                if (current is (byte)'"' or (byte)'\'')
                {
                    inQuote = true;
                    quote = current;
                    continue;
                }

                if (current == (byte)'>')
                {
                    return true;
                }
            }
        }

        private bool LoadUntil(ref int tagStart, ReadOnlySpan<byte> marker)
        {
            _index = tagStart;
            while (true)
            {
                if (_length - _index < marker.Length)
                {
                    if (!RefillKeeping(ref tagStart))
                    {
                        return false;
                    }

                    if (_length - _index < marker.Length)
                    {
                        return false;
                    }
                }

                var found = _buffer.AsSpan(_index, _length - _index).IndexOf(marker);
                if (found >= 0)
                {
                    _index += found + marker.Length;
                    return true;
                }

                var keep = Math.Max(tagStart, _length - marker.Length + 1);
                _index = keep;
                if (!RefillKeeping(ref tagStart))
                {
                    return false;
                }
            }
        }

        private bool RefillKeeping(ref int keepFrom)
        {
            if (keepFrom > 0)
            {
                var keep = _length - keepFrom;
                if (keep > 0)
                {
                    Buffer.BlockCopy(_buffer, keepFrom, _buffer, 0, keep);
                }

                _baseOffset += keepFrom;
                _index -= keepFrom;
                _length = keep;
                keepFrom = 0;
            }

            if (_length == _buffer.Length)
            {
                if (_buffer.Length >= XmlImportLimits.MaxTagBytes)
                {
                    throw new InvalidDataException("The XML file contains an element the importer cannot read.");
                }

                var grown = new byte[Math.Min(_buffer.Length * 2, XmlImportLimits.MaxTagBytes)];
                Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
                _buffer = grown;
            }

            var read = _stream.Read(_buffer, _length, _buffer.Length - _length);
            if (read <= 0)
            {
                return false;
            }

            _length += read;
            ReportBytes(false);
            return true;
        }

        private bool Fill()
        {
            var keep = _index;
            return RefillKeeping(ref keep) && _index < _length;
        }

        private void EnsurePrefix(ref int tagStart, int count)
        {
            while (_length - tagStart < count)
            {
                if (!RefillKeeping(ref tagStart))
                {
                    return;
                }
            }
        }

        private void ReportBytes(bool force)
        {
            var read = BytesRead;
            if (!force && read - _lastReport < XmlImportLimits.FileBufferBytes)
            {
                return;
            }

            _lastReport = read;
            OnBytes?.Invoke(read);
        }

        private static NodesetTagKind KindOf(ReadOnlySpan<byte> name)
        {
            if (name.SequenceEqual("UAVariable"u8))
            {
                return NodesetTagKind.Variable;
            }

            if (name.SequenceEqual("NamespaceUris"u8))
            {
                return NodesetTagKind.NamespaceUris;
            }

            if (name.SequenceEqual("Uri"u8))
            {
                return NodesetTagKind.Uri;
            }

            if (name.SequenceEqual("Alias"u8))
            {
                return NodesetTagKind.Alias;
            }

            if (name.SequenceEqual("UADataType"u8))
            {
                return NodesetTagKind.DataType;
            }

            if (name.SequenceEqual("Reference"u8))
            {
                return NodesetTagKind.Reference;
            }

            if (name.SequenceEqual("DisplayName"u8))
            {
                return NodesetTagKind.DisplayName;
            }

            return NodesetTagKind.Other;
        }

        private static bool IsSpace(byte value)
        {
            return value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        }

        private static string Decode(ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty)
            {
                return "";
            }

            if (utf8.IndexOf((byte)'&') < 0)
            {
                return Encoding.UTF8.GetString(utf8);
            }

            var builder = new StringBuilder(utf8.Length);
            AppendDecoded(builder, utf8);
            return builder.ToString();
        }

        private static void AppendDecoded(StringBuilder builder, ReadOnlySpan<byte> utf8)
        {
            var start = 0;
            for (var i = 0; i < utf8.Length; i++)
            {
                if (utf8[i] != (byte)'&')
                {
                    continue;
                }

                if (i > start)
                {
                    builder.Append(Encoding.UTF8.GetString(utf8.Slice(start, i - start)));
                }

                var semi = utf8[i..].IndexOf((byte)';');
                if (semi < 2)
                {
                    builder.Append('&');
                    start = i + 1;
                    continue;
                }

                var token = utf8.Slice(i + 1, semi - 1);
                if (!TryEntity(token, out var decoded))
                {
                    builder.Append('&');
                    start = i + 1;
                    continue;
                }

                builder.Append(decoded);
                i += semi;
                start = i + 1;
            }

            if (start < utf8.Length)
            {
                builder.Append(Encoding.UTF8.GetString(utf8[start..]));
            }
        }

        private static bool TryEntity(ReadOnlySpan<byte> token, out char decoded)
        {
            decoded = '\0';
            if (token.SequenceEqual("amp"u8))
            {
                decoded = '&';
                return true;
            }

            if (token.SequenceEqual("lt"u8))
            {
                decoded = '<';
                return true;
            }

            if (token.SequenceEqual("gt"u8))
            {
                decoded = '>';
                return true;
            }

            if (token.SequenceEqual("quot"u8))
            {
                decoded = '"';
                return true;
            }

            if (token.SequenceEqual("apos"u8))
            {
                decoded = '\'';
                return true;
            }

            if (token.Length > 1 && token[0] == (byte)'#')
            {
                var number = token[1] == (byte)'x'
                    ? token[2..]
                    : token[1..];
                var hex = token[1] == (byte)'x';
                if (int.TryParse(
                        Encoding.ASCII.GetString(number),
                        hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var code) &&
                    code > 0 &&
                    code <= 0xFFFF)
                {
                    decoded = (char)code;
                    return true;
                }
            }

            return false;
        }
    }
}
