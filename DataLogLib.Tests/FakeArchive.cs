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

using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services.ArchiveAccess;
using Microsoft.Extensions.Configuration;

namespace DataLogLib.Tests
{
    /// <summary>
    /// In-memory archive for the compression tests: raw values and written compression levels per
    /// uid (tag id n has the uid TAGn), with the hour coverage computed like the archive query.
    /// </summary>
    internal sealed class FakeArchive : IArchive
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<ArchiveValue>> _raw = [];
        private readonly Dictionary<string, List<ArchiveValue>> _compressed = [];
        private DateTime? _verifiedUntil;

        /// <summary>Confirmed writes throw while true (archive outage).</summary>
        public bool FailWrites { get; set; }
        /// <summary>Every write throws while true (archive outage).</summary>
        public bool FailInserts { get; set; }
        public List<(string Uid, DateTime Start, DateTime End)> CoverageReads { get; } = [];
        public List<(string Uid, DateTime Start, DateTime End)> RawReads { get; } = [];

        public static string Uid(int tagId) => $"TAG{tagId}";

        public void AddRaw(string uid, IEnumerable<ArchiveValue> values)
        {
            lock (_lock)
            {
                if (!_raw.TryGetValue(uid, out var list))
                {
                    list = [];
                    _raw.Add(uid, list);
                }
                list.AddRange(values);
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        }

        public List<ArchiveValue> Compressed(string uid, CompressionType? level = null)
        {
            lock (_lock)
            {
                return _compressed.TryGetValue(uid, out var list)
                    ? list.Where(v => level == null || v.Compression == level).ToList()
                    : [];
            }
        }

        private void AddCompressed(string uid, IEnumerable<ArchiveValue> values)
        {
            if (!_compressed.TryGetValue(uid, out var list))
            {
                list = [];
                _compressed.Add(uid, list);
            }
            list.AddRange(values);
        }

        public void InsertValues(List<ArchiveValue> values)
        {
            if (FailInserts)
            {
                throw new IOException("archive outage");
            }
            lock (_lock)
            {
                foreach (var group in values.GroupBy(v => v.Id))
                {
                    var raw = group.Where(v => v.Compression == CompressionType.RAW).ToList();
                    if (raw.Count > 0)
                    {
                        AddRaw(Uid(group.Key), raw);
                    }
                    AddCompressed(Uid(group.Key), group.Where(v => v.Compression != CompressionType.RAW));
                }
            }
        }

        public void InsertValuesAndWait(List<ArchiveValue> values)
        {
            if (FailWrites)
            {
                throw new IOException("archive outage");
            }
            InsertValues(values);
        }

        public void InsertCompressedValuesAndWait(string uid, List<ArchiveValue> values)
        {
            if (FailWrites)
            {
                throw new IOException("archive outage");
            }
            lock (_lock)
            {
                CompressedWrites++;
                AddCompressed(uid, values);
            }
        }

        public bool TryGetUid(int dataTagId, out string uid)
        {
            uid = Uid(dataTagId);
            return true;
        }

        public List<(string Uid, DateTime First)> GetRawUids(DateTime startTime, DateTime endTime)
        {
            lock (_lock)
            {
                return _raw
                    .Select(r => (r.Key, Values: r.Value.Where(v => v.Time >= startTime && v.Time < endTime).ToList()))
                    .Where(r => r.Values.Count > 0)
                    .Select(r => (r.Key, r.Values[0].Time))
                    .ToList();
            }
        }

        public void ReadRawValues(string uid, DateTime startTime, DateTime endTime, Action<ArchiveValue> onValue)
        {
            List<ArchiveValue> values;
            lock (_lock)
            {
                RawReads.Add((uid, startTime, endTime));
                values = _raw.TryGetValue(uid, out var list)
                    ? list.Where(v => v.Time >= startTime && v.Time < endTime)
                        .Select(v => new ArchiveValue { Value = v.Value, State = v.State, Time = v.Time })
                        .ToList()
                    : [];
            }
            values.ForEach(onValue);
        }

        public List<(string Uid, CompressionType Compression, DateTime Last)> GetLastTimes(DateTime startTime, DateTime endTime)
        {
            lock (_lock)
            {
                var raw = _raw.SelectMany(r => r.Value.Select(v => (Uid: r.Key, v.Compression, v.Time)));
                var compressed = _compressed.SelectMany(c => c.Value.Select(v => (Uid: c.Key, v.Compression, v.Time)));
                return raw.Concat(compressed)
                    .Where(v => v.Time >= startTime && v.Time < endTime)
                    .GroupBy(v => (v.Uid, v.Compression))
                    .Select(g => (g.Key.Uid, g.Key.Compression, g.Max(v => v.Time)))
                    .ToList();
            }
        }

        /// <summary>Number of confirmed writes of compressed values.</summary>
        public int CompressedWrites { get; private set; }

        public List<HourCoverage> GetHourCoverage(string uid, DateTime startTime, DateTime endTime)
        {
            lock (_lock)
            {
                CoverageReads.Add((uid, startTime, endTime));
                static DateTime HourOf(DateTime t) => CompressionLevels.WindowStart(t, CompressionLevels.Coarsest);

                var level = Compressed(uid, CompressionLevels.Coarsest.Type)
                    .Where(v => v.Time >= startTime && v.Time < endTime)
                    .GroupBy(v => HourOf(v.Time))
                    .ToDictionary(g => g.Key, g => (First: g.Min(v => v.Time), Last: g.Max(v => v.Time)));

                return (_raw.TryGetValue(uid, out var list) ? list : [])
                    .Where(v => v.Time >= startTime && v.Time < endTime)
                    .GroupBy(v => HourOf(v.Time))
                    .Select(g => new HourCoverage(g.Key, g.Min(v => v.Time), g.Max(v => v.Time),
                        level.TryGetValue(g.Key, out var l) ? l.First : null,
                        level.TryGetValue(g.Key, out l) ? l.Last : null,
                        g.Count()))
                    .ToList();
            }
        }

        public DateTime? GetCompressionVerifiedUntil() => _verifiedUntil;
        public void SetCompressionVerifiedUntil(DateTime time)
        {
            _verifiedUntil = time;
            VerifiedHistory.Add(time);
        }

        /// <summary>Every time written as verified, in order.</summary>
        public List<DateTime> VerifiedHistory { get; } = [];

        public DateTime? GetArchiveEndTime(int dataTagId) => null;
        public DateTime? GetArchiveStartTime() => null;
        public List<PlotPoint> GetPlotPoints(int dataTagId, CompressionType compression, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default) => [];
        public List<PlotPoint> GetRawM4PlotPoints(int dataTagId, DateTime startTime, DateTime endTime, double origin, double bucketSeconds, CancellationToken cancellationToken = default) => [];
        public List<PlotPoint> GetRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken = default, Action<long> onRead = null) => [];
        public IAsyncEnumerable<PlotPoint> StreamRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<PlotPoint>();
        public long? GetRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default) => null;
        public RawStatistics GetRawStatistics(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default) => null;
        public BoolDurations GetBoolDurations(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default) => null;
        public double? GetInvalidSeconds(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default) => null;
        public long? GetValueCountSum() => null;
        public long? GetDatabaseDiskSizeBytes() => null;
        public void Init(IConfiguration configuration) { }
        public void InsertValueCount(DateTime time, long count) { }
    }
}
