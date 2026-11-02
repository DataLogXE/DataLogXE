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

using System.Buffers.Binary;
using System.Globalization;
using Serilog;

namespace DataLogLib.Services.EventLog
{
    /// <summary>
    /// Records of any length on disk in the order they came, for what the database cannot take in time (the process
    /// events, see EventLogWorker). Like the journal of the archive values (ArchiveJournal), but each record is its length
    /// (4 bytes) and its bytes: the records are appended to segment files (prefix-000001.jnl, ...), read back in order,
    /// and after each chunk that reached the database the read position (a byte offset) is stored next to the segment
    /// (.pos), so a restart goes on where it stopped. A segment read to its end is deleted, and a segment is never
    /// appended to after a restart: a record cut off by a crash only ends its segment.
    /// </summary>
    internal sealed class RecordJournal
    {
        private const string Extension = ".jnl";
        private const string PositionExtension = ".pos";
        private const int HeaderSize = 4;
        // A length beyond this is a damaged record, not a real one
        private const int MaxRecordBytes = 16 * 1024 * 1024;

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly string _prefix;
        private readonly long _maxBytes;
        private readonly long _segmentBytes;
        // The segments not yet read to their end, oldest first
        private readonly List<long> _segments = [];
        private FileStream _writer;
        private long _writerSegment;
        private long _writerBytes;
        // Byte offset in the oldest segment up to which the records reached the database
        private long _readOffset;
        // The end offsets of the records the last Peek returned, for Commit
        private readonly List<long> _peekEnds = [];
        private long _pendingBytes;
        private long _pendingCount;

        public RecordJournal(string folder, string prefix, long maxBytes, long segmentBytes = 4L * 1024 * 1024)
        {
            _folder = folder;
            _prefix = prefix + "-";
            _maxBytes = maxBytes;
            _segmentBytes = Math.Max(HeaderSize + 1, segmentBytes);
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(folder, _prefix + "*" + Extension))
            {
                if (long.TryParse(Path.GetFileNameWithoutExtension(file)[_prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    _segments.Add(number);
                }
            }
            _segments.Sort();
            _writerSegment = _segments.Count > 0 ? _segments[^1] : 0;
            if (_segments.Count > 0)
            {
                _readOffset = ReadPosition(_segments[0]);
                for (var i = 0; i < _segments.Count; i++)
                {
                    var (count, bytes) = Scan(_segments[i], i == 0 ? _readOffset : 0);
                    _pendingCount += count;
                    _pendingBytes += bytes;
                }
                if (_pendingCount > 0)
                {
                    Log.Warning("Journal {Prefix} holds {Count} records from before the start; they are written to the database",
                        prefix, _pendingCount);
                }
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _pendingCount == 0;
                }
            }
        }

        /// <summary>The records that have not reached the database.</summary>
        public long Count
        {
            get
            {
                lock (_lock)
                {
                    return _pendingCount;
                }
            }
        }

        /// <summary>
        /// Appends the records at the end. Returns how many did not fit within the size limit and were not written (the
        /// newest ones). Throws when the disk does not take them.
        /// </summary>
        public int Append(IReadOnlyList<byte[]> records)
        {
            lock (_lock)
            {
                var written = 0;
                var header = new byte[HeaderSize];
                try
                {
                    foreach (var record in records)
                    {
                        var size = HeaderSize + record.Length;
                        if (_pendingBytes + size > _maxBytes)
                        {
                            break;
                        }
                        if (_writer == null || _writerBytes >= _segmentBytes)
                        {
                            OpenNextSegment();
                        }

                        BinaryPrimitives.WriteInt32LittleEndian(header, record.Length);
                        _writer.Write(header);
                        _writer.Write(record);
                        _writerBytes += size;
                        _pendingBytes += size;
                        _pendingCount++;
                        written++;
                    }
                    // To the operating system, so the reader sees the records and they survive a crash of the app
                    _writer?.Flush();
                }
                catch
                {
                    // A record may be cut off: the segment ends here, and only its whole records count
                    try
                    {
                        _writer?.Dispose();
                    }
                    catch (IOException)
                    {
                    }
                    _writer = null;
                    Recount();
                    throw;
                }
                return records.Count - written;
            }
        }

        /// <summary>The next records in order, at most max, all from the oldest segment; empty when the journal is.</summary>
        public List<byte[]> Peek(int max)
        {
            lock (_lock)
            {
                List<byte[]> records = [];
                _peekEnds.Clear();
                while (_segments.Count > 0 && records.Count == 0)
                {
                    var segment = _segments[0];
                    var end = SegmentEnd(segment);
                    if (_readOffset >= end)
                    {
                        // Read to its end; the segment the writer appends to stays until it is full
                        if (segment == _writerSegment && _writer != null && _writerBytes < _segmentBytes)
                        {
                            return records;
                        }
                        RemoveOldest();
                        continue;
                    }

                    using var stream = new FileStream(SegmentPath(segment), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Position = _readOffset;
                    var header = new byte[HeaderSize];
                    while (records.Count < max && stream.Position + HeaderSize <= end)
                    {
                        stream.ReadExactly(header);
                        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                        if (length < 0 || length > MaxRecordBytes || stream.Position + length > end)
                        {
                            // Cut off or damaged: the rest of the segment is no record
                            break;
                        }
                        var record = new byte[length];
                        stream.ReadExactly(record);
                        records.Add(record);
                        _peekEnds.Add(stream.Position);
                    }

                    if (records.Count == 0)
                    {
                        // Only a cut-off record is left in this segment
                        if (segment == _writerSegment && _writer != null)
                        {
                            return records;
                        }
                        Log.Warning("Journal segment {Segment} ends with a cut-off record, which is skipped", SegmentPath(segment));
                        RemoveOldest();
                    }
                }
                return records;
            }
        }

        /// <summary>
        /// Marks the first count records of the last Peek as written to the database. When the journal is empty
        /// afterwards, its files are deleted.
        /// </summary>
        public void Commit(int count)
        {
            lock (_lock)
            {
                if (_segments.Count == 0 || count <= 0 || count > _peekEnds.Count)
                {
                    return;
                }

                var newOffset = _peekEnds[count - 1];
                _pendingBytes -= newOffset - _readOffset;
                _pendingCount -= count;
                _readOffset = newOffset;
                _peekEnds.Clear();
                if (_pendingCount <= 0)
                {
                    // All written: start over without files
                    _writer?.Dispose();
                    _writer = null;
                    while (_segments.Count > 0)
                    {
                        RemoveOldest();
                    }
                    _pendingCount = 0;
                    _pendingBytes = 0;
                    return;
                }

                if (_readOffset >= SegmentEnd(_segments[0]) && _segments[0] != _writerSegment)
                {
                    RemoveOldest();
                }
                else
                {
                    WritePosition(_segments[0], _readOffset);
                }
            }
        }

        private void OpenNextSegment()
        {
            _writer?.Dispose();
            _writerSegment++;
            _writer = new FileStream(SegmentPath(_writerSegment), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
            _writerBytes = 0;
            _segments.Add(_writerSegment);
        }

        private void RemoveOldest()
        {
            var segment = _segments[0];
            if (segment == _writerSegment && _writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
            File.Delete(SegmentPath(segment));
            File.Delete(PositionPath(segment));
            _segments.RemoveAt(0);
            _readOffset = _segments.Count > 0 ? ReadPosition(_segments[0]) : 0;
        }

        private void Recount()
        {
            _pendingCount = 0;
            _pendingBytes = 0;
            for (var i = 0; i < _segments.Count; i++)
            {
                var (count, bytes) = Scan(_segments[i], i == 0 ? _readOffset : 0);
                _pendingCount += count;
                _pendingBytes += bytes;
            }
        }

        // The whole records of the segment from the offset on: a record cut off by a crash is not counted
        private (long Count, long Bytes) Scan(long segment, long from)
        {
            long count = 0;
            var offset = from;
            try
            {
                using var stream = new FileStream(SegmentPath(segment), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var header = new byte[HeaderSize];
                stream.Position = from;
                while (stream.Position + HeaderSize <= stream.Length)
                {
                    stream.ReadExactly(header);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > MaxRecordBytes || stream.Position + length > stream.Length)
                    {
                        break;
                    }
                    stream.Position += length;
                    offset = stream.Position;
                    count++;
                }
            }
            catch (FileNotFoundException)
            {
            }
            return (count, offset - from);
        }

        private long SegmentEnd(long segment)
            => segment == _writerSegment && _writer != null ? _writerBytes : new FileInfo(SegmentPath(segment)).Length;

        private long ReadPosition(long segment)
        {
            try
            {
                var bytes = File.ReadAllBytes(PositionPath(segment));
                return bytes.Length == 8 ? Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(bytes)) : 0;
            }
            catch (FileNotFoundException)
            {
                return 0;
            }
        }

        private void WritePosition(long segment, long offset)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, offset);
            var temp = PositionPath(segment) + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, PositionPath(segment), overwrite: true);
        }

        private string SegmentPath(long segment) => Path.Combine(_folder, _prefix + segment.ToString("D6", CultureInfo.InvariantCulture) + Extension);

        private string PositionPath(long segment) => Path.ChangeExtension(SegmentPath(segment), PositionExtension);
    }
}
