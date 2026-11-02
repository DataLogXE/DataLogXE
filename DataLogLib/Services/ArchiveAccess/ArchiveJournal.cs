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
using DataLogLib.Common;
using DataLogLib.Models;
using Serilog;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// The archive values that the database cannot take in time, on disk in the order they came, so a full insert
    /// buffer no longer drops them (on 2026-10-31 a blocked database cost ~114 000 values on the Raspberry Pi). The
    /// values are appended to segment files of fixed records (journal-000001.jnl, ...); the insert worker reads them
    /// back in order, and after each chunk that reached the database the read position is stored next to the segment
    /// (.pos), so a restart goes on where it stopped and writes no value twice. A segment that is read to its end is
    /// deleted. A segment is never appended to after a restart: a record cut off by a crash only ends its segment.
    /// </summary>
    internal sealed class ArchiveJournal
    {
        // Tag id (4), UTC ticks (8), value (8), state (2), compression level (2)
        internal const int RecordSize = 24;
        private const string Prefix = "journal-";
        private const string Extension = ".jnl";
        private const string PositionExtension = ".pos";
        private const long DefaultSegmentBytes = 16L * 1024 * 1024;

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly long _maxBytes;
        private readonly long _segmentBytes;
        // The segments not yet read to their end, oldest first
        private readonly List<long> _segments = [];
        private FileStream _writer;
        private long _writerSegment;
        private long _writerBytes;
        // Records of the oldest segment that reached the database
        private long _readRecords;
        // Bytes of all segments minus the records that reached the database
        private long _pendingBytes;

        public ArchiveJournal(string folder, long maxBytes, long segmentBytes = DefaultSegmentBytes)
        {
            _folder = folder;
            _maxBytes = maxBytes;
            _segmentBytes = Math.Max(RecordSize, segmentBytes - segmentBytes % RecordSize);
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(folder, Prefix + "*" + Extension))
            {
                if (long.TryParse(Path.GetFileNameWithoutExtension(file)[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    _segments.Add(number);
                }
            }
            _segments.Sort();
            _writerSegment = _segments.Count > 0 ? _segments[^1] : 0;
            if (_segments.Count > 0)
            {
                _readRecords = ReadPosition(_segments[0]);
                _pendingBytes = _segments.Sum(s => Records(s) * RecordSize) - _readRecords * RecordSize;
                Log.Warning("Archive journal holds {Count} values from before the start; they are written to the archive",
                    _pendingBytes / RecordSize);
            }
        }

        /// <summary>The folder of the journal: journal in the data folder (DATA_PATH, default /app/data, a volume in Docker).</summary>
        public static string DefaultFolder
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("DATA_PATH");
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Environment.GetEnvironmentVariable("DATABASE_PATH");
                }

                if (string.IsNullOrWhiteSpace(root))
                {
                    root = "/app/data";
                }

                return Path.Combine(root, "journal");
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _pendingBytes == 0;
                }
            }
        }

        /// <summary>The values in the journal that have not reached the database.</summary>
        public long Count
        {
            get
            {
                lock (_lock)
                {
                    return _pendingBytes / RecordSize;
                }
            }
        }

        /// <summary>
        /// Appends the values at the end. Returns how many did not fit within the size limit and were not written (the
        /// newest ones). Throws when the disk does not take them.
        /// </summary>
        public int Append(IReadOnlyList<ArchiveValue> values)
        {
            lock (_lock)
            {
                var fit = (int)Math.Min(values.Count, Math.Max(0, (_maxBytes - _pendingBytes) / RecordSize));
                var record = new byte[RecordSize];
                try
                {
                    for (var i = 0; i < fit; i++)
                    {
                        if (_writer == null || _writerBytes >= _segmentBytes)
                        {
                            OpenNextSegment();
                        }

                        Encode(values[i], record);
                        _writer.Write(record);
                        _writerBytes += RecordSize;
                        _pendingBytes += RecordSize;
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
                    _pendingBytes = _segments.Sum(s => Records(s) * RecordSize) - _readRecords * RecordSize;
                    throw;
                }
                return values.Count - fit;
            }
        }

        /// <summary>The next values in order, at most max, all from the oldest segment; empty when the journal is.</summary>
        public List<ArchiveValue> Peek(int max)
        {
            lock (_lock)
            {
                List<ArchiveValue> values = [];
                while (_segments.Count > 0 && values.Count == 0)
                {
                    var segment = _segments[0];
                    var available = Records(segment) - _readRecords;
                    if (available <= 0)
                    {
                        // Read to its end; the segment the writer appends to stays until it is full
                        if (segment == _writerSegment && _writer != null && _writerBytes < _segmentBytes)
                        {
                            return values;
                        }
                        RemoveOldest();
                        continue;
                    }

                    var count = (int)Math.Min(max, available);
                    var buffer = new byte[count * RecordSize];
                    using (var stream = new FileStream(SegmentPath(segment), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Position = _readRecords * RecordSize;
                        stream.ReadExactly(buffer);
                    }
                    for (var i = 0; i < count; i++)
                    {
                        values.Add(Decode(buffer.AsSpan(i * RecordSize, RecordSize)));
                    }
                }
                return values;
            }
        }

        /// <summary>
        /// Marks the first count values (as returned by Peek) as written to the database. When the journal is empty
        /// afterwards, its files are deleted.
        /// </summary>
        public void Commit(int count)
        {
            lock (_lock)
            {
                if (_segments.Count == 0 || count <= 0)
                {
                    return;
                }

                _readRecords += count;
                _pendingBytes -= (long)count * RecordSize;
                if (_pendingBytes <= 0)
                {
                    // All written: start over without files
                    _writer?.Dispose();
                    _writer = null;
                    while (_segments.Count > 0)
                    {
                        RemoveOldest();
                    }
                    _pendingBytes = 0;
                    return;
                }

                if (_readRecords >= Records(_segments[0]) && _segments[0] != _writerSegment)
                {
                    RemoveOldest();
                }
                else
                {
                    WritePosition(_segments[0], _readRecords);
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
            _readRecords = _segments.Count > 0 ? ReadPosition(_segments[0]) : 0;
        }

        // Whole records only: a record cut off by a crash is not read
        private long Records(long segment)
            => segment == _writerSegment && _writer != null ? _writerBytes / RecordSize : new FileInfo(SegmentPath(segment)).Length / RecordSize;

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

        private void WritePosition(long segment, long records)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, records);
            var temp = PositionPath(segment) + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, PositionPath(segment), overwrite: true);
        }

        private string SegmentPath(long segment) => Path.Combine(_folder, Prefix + segment.ToString("D6", CultureInfo.InvariantCulture) + Extension);

        private string PositionPath(long segment) => Path.ChangeExtension(SegmentPath(segment), PositionExtension);

        private static void Encode(ArchiveValue value, Span<byte> record)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record, value.Id);
            BinaryPrimitives.WriteInt64LittleEndian(record[4..], value.Time.ToUniversalTime().Ticks);
            BinaryPrimitives.WriteDoubleLittleEndian(record[12..], value.Value);
            BinaryPrimitives.WriteInt16LittleEndian(record[20..], (short)value.State);
            BinaryPrimitives.WriteInt16LittleEndian(record[22..], (short)value.Compression);
        }

        private static ArchiveValue Decode(ReadOnlySpan<byte> record) => new()
        {
            Id = BinaryPrimitives.ReadInt32LittleEndian(record),
            Time = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(record[4..]), DateTimeKind.Utc).ToLocalTime(),
            Value = BinaryPrimitives.ReadDoubleLittleEndian(record[12..]),
            State = (ValueState)BinaryPrimitives.ReadInt16LittleEndian(record[20..]),
            Compression = (CompressionType)BinaryPrimitives.ReadInt16LittleEndian(record[22..])
        };
    }
}
