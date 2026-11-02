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

using DataLogApi.Dtos;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services;
using DataLogService;
using Serilog;

namespace DataLogApi.Services
{
    public class DataTagAppService
    {
        public const int DefaultListLimit = 150;
        public const int MaxListLimit = 500;
        public const int MaxActualValueIds = 128;
        public const int MaxMutationIds = 1000;
        public const double MaxPlotRangeSeconds = 30 * 24 * 3600;
        // The chart gets the M4 points (first, min, max, last) of one bucket per pixel,
        // which draws the curve pixel-exact. The width is the drawing area in device pixels
        // (high resolution screens have 2 or 3 per CSS pixel). It is clamped, so the points are bounded.
        public const int DefaultPlotWidth = 1500;
        public const int MinPlotWidth = 120;
        public const int MaxPlotWidth = 8000;
        // Maximum number of archive values read for one chart request before the M4 reduction
        public const double MaxPlotReadPoints = 60000;
        // The rows of the raw data of a data tag that the UI loads for an Excel export: the rows of an Excel sheet
        // without its header. The CSV export streams the raw data and has no limit.
        public const int MaxExportRows = 1_048_575;
        // The data tags of one streamed raw data CSV export, as many as a chart holds and more
        public const int MaxCsvExportTags = 64;
        // A level window must fit into a pixel, otherwise the shape within the window is lost. Then
        // more values are read (a level window down to a fifth of a pixel, or the raw values when
        // even 1 s is wider than a pixel), but never more than the read limit. ARCHIVE_PLOT_READ_LIMIT
        // sets it; without it, small devices (Raspberry Pi, up to SmallDeviceMemoryBytes) use
        // MaxPlotReadPoints and accept charts that are not pixel exact on wide screens.
        public const double DefaultPlotReadLimit = 200000;
        public const long SmallDeviceMemoryBytes = DataLogLib.Services.DataAccess.DatabaseLimits.SmallDeviceMemoryBytes;
        // The preview reads about one point per this many pixels, a few hundred to a few thousand
        public const int PreviewPixelsPerPoint = 2;

        private readonly ServiceCtrl _service;
        private readonly double _plotReadLimit;
        private readonly object _archiveStartLock = new();
        private DateTime? _archiveStart;
        private DateTime _archiveStartFetchedAtUtc = DateTime.MinValue;
        // The last archive start read (unix seconds, boxed), for the preview that must not wait for the query
        private object _knownArchiveStart;

        public DataTagAppService(ServiceCtrl service)
        {
            _service = service;
            _plotReadLimit = GetPlotReadLimit(service.Configuration?["ARCHIVE_PLOT_READ_LIMIT"],
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            Log.Information("Chart read limit {Limit} values per plot", _plotReadLimit);
        }

        /// <summary>
        /// The configured read limit, otherwise one that fits the memory of the device (the container limit or the
        /// physical memory).
        /// </summary>
        internal static double GetPlotReadLimit(string text, long availableMemoryBytes)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return availableMemoryBytes > 0 && availableMemoryBytes <= SmallDeviceMemoryBytes
                    ? MaxPlotReadPoints
                    : DefaultPlotReadLimit;
            }
            if (!int.TryParse(text, out var limit) || limit < MaxPlotReadPoints)
            {
                Log.Error("Invalid ARCHIVE_PLOT_READ_LIMIT {Value} (at least {Min}), using {Default}", text, MaxPlotReadPoints, DefaultPlotReadLimit);
                return DefaultPlotReadLimit;
            }
            return limit;
        }

        public DataTagListPageDto GetPage(
            int dataSourceId,
            string q,
            bool activeOnly,
            int offset,
            int limit,
            string sort1,
            string dir1,
            string sort2,
            string dir2)
        {
            var source = GetSourceOrThrow(dataSourceId);
            if (offset < 0)
            {
                throw new ArgumentException("Offset must be 0 or greater.");
            }

            if (limit <= 0)
            {
                limit = DefaultListLimit;
            }

            limit = Math.Min(limit, MaxListLimit);

            var sorts = ParseSorts(sort1, dir1, sort2, dir2);
            var info = _service.GetDataSourceInfo(dataSourceId);
            var page = _service.DataAccess.DataTagsGetPage(
                dataSourceId,
                activeOnly ? 1 : null,
                q,
                offset,
                limit,
                sorts,
                LiveSort(sorts, info));
            if (page.Preparing)
            {
                return new DataTagListPageDto
                {
                    DataSourceId = source.Id,
                    DataSourceName = source.Name,
                    DataSourceState = SourceStateName(info),
                    DataSourceStateId = SourceStateId(info),
                    DataSourceType = ((DataSourceType)source.TypeId).ToString(),
                    Cycles = GetCycles(dataSourceId),
                    Total = 0,
                    Offset = offset,
                    Limit = limit,
                    Preparing = true,
                    PreparePercent = page.PreparePercent,
                    Items = []
                };
            }

            return new DataTagListPageDto
            {
                DataSourceId = source.Id,
                DataSourceName = source.Name,
                DataSourceState = SourceStateName(info),
                DataSourceStateId = SourceStateId(info),
                DataSourceType = ((DataSourceType)source.TypeId).ToString(),
                Cycles = GetCycles(dataSourceId),
                Total = page.Total,
                Offset = offset,
                Limit = limit,
                Items = ToItems(source, page.Items)
            };
        }

        public DataTagActualValuesDto GetActualValues(int dataSourceId, DataTagIdsRequestDto dto)
        {
            GetSourceOrThrow(dataSourceId);
            var info = _service.GetDataSourceInfo(dataSourceId);
            var ids = NormalizeIds(dto?.Ids, MaxActualValueIds, "actual values");
            if (ids.Count == 0)
            {
                return new DataTagActualValuesDto
                {
                    DataSourceState = SourceStateName(info),
                    DataSourceStateId = SourceStateId(info)
                };
            }

            var owned = _service.DataAccess.DataTagsGetByIds(dataSourceId, ids);
            var ownedIds = owned.Select(t => t.Id).ToList();
            var values = _service.ArchiveAccess.GetActualValues(ownedIds);

            return new DataTagActualValuesDto
            {
                DataSourceState = SourceStateName(info),
                DataSourceStateId = SourceStateId(info),
                Values = values.Select(v => new DataTagActualValueDto
                {
                    Id = v.Id,
                    Value = v.Value,
                    Time = v.Time,
                    State = v.State.ToString()
                }).ToList(),
                Issues = _service.GetTagIssues(dataSourceId, ownedIds)
                    .Select(kv => new DataTagIssueDto { Id = kv.Key, Issue = kv.Value })
                    .ToList()
            };
        }

        public DataTagMutationResultDto SetActive(int dataSourceId, DataTagActiveWriteDto dto)
        {
            var source = GetSourceOrThrow(dataSourceId);
            var writable = GetWritableTags(dataSourceId, dto?.Ids);
            if (writable.Count == 0)
            {
                throw new ArgumentException("No writable data tags were selected.");
            }

            var changed = dto.IsActive ? WithoutNotLoggable(source, writable) : writable;
            _service.DataAccess.DataTagsUpdateActive(changed.Select(t => t.Id).ToList(), dto.IsActive ? 1 : 0);
            foreach (var tag in changed)
            {
                tag.IsActive = dto.IsActive ? 1 : 0;
            }

            return new DataTagMutationResultDto
            {
                Updated = ToItems(source, writable),
                NotActivated = writable.Count - changed.Count
            };
        }

        public DataTagDetailsListDto GetDetails(int dataSourceId, DataTagIdsRequestDto dto)
        {
            var source = GetSourceOrThrow(dataSourceId);
            var ids = NormalizeIds(dto?.Ids, MaxMutationIds, "data tags");
            return new DataTagDetailsListDto
            {
                Cycles = GetCycles(dataSourceId),
                Items = _service.DataAccess.DataTagDetailsGetByIds(dataSourceId, ids).Select(tag => new DataTagDetailsDto
                {
                    Id = tag.Id,
                    Guid = tag.Guid ?? "",
                    Name = tag.Name ?? "",
                    DisplayName = tag.DisplayName ?? "",
                    Address = tag.Address ?? "",
                    Comment = DataTagDescription.Resolve(tag.Description, source.S7Locale),
                    Type = Enum.IsDefined(typeof(DataTagType), tag.TypeId) ? ((DataTagType)tag.TypeId).ToString() : nameof(DataTagType.Unknown),
                    Cycle = tag.Cycle,
                    IsActive = tag.IsActive != 0,
                    IsSys = tag.IsSys != 0,
                    CreatedAt = tag.CreatedAt == default ? null : tag.CreatedAt
                }).ToList()
            };
        }

        public DataTagMutationResultDto Update(int dataSourceId, DataTagUpdateDto dto)
        {
            if (dto == null)
            {
                throw new ArgumentException("The request body is missing.");
            }

            var source = GetSourceOrThrow(dataSourceId);
            if (dto.IsActive == null && dto.Cycle == null)
            {
                throw new ArgumentException("Nothing to change.");
            }

            if (dto.Cycle.HasValue && !GetCycles(dataSourceId).Contains(dto.Cycle.Value))
            {
                throw new ArgumentException("Please choose a valid cycle.");
            }

            var writable = GetWritableTags(dataSourceId, dto.Ids);
            if (writable.Count == 0)
            {
                throw new ArgumentException("No writable data tags were selected.");
            }

            int? active = dto.IsActive.HasValue ? (dto.IsActive.Value ? 1 : 0) : null;
            var activated = active == 1 ? WithoutNotLoggable(source, writable) : writable;
            var notActivated = writable.Except(activated).ToList();
            _service.DataAccess.DataTagsUpdateActiveAndCycle(activated.Select(t => t.Id).ToList(), active, dto.Cycle);
            if (dto.Cycle.HasValue && notActivated.Count > 0)
            {
                _service.DataAccess.DataTagsUpdateCycle(notActivated.Select(t => t.Id).ToList(), dto.Cycle.Value);
            }
            foreach (var tag in writable)
            {
                if (active.HasValue && activated.Contains(tag))
                {
                    tag.IsActive = active.Value;
                }
                tag.Cycle = dto.Cycle ?? tag.Cycle;
            }

            return new DataTagMutationResultDto
            {
                Updated = ToItems(source, writable),
                NotActivated = notActivated.Count
            };
        }

        private static void CheckPlotRange(double fromUnix, double toUnix)
        {
            if (double.IsNaN(fromUnix) || double.IsNaN(toUnix) || double.IsInfinity(fromUnix) || double.IsInfinity(toUnix))
            {
                throw new ArgumentException("The time range is invalid.");
            }

            if (fromUnix >= toUnix)
            {
                throw new ArgumentException("The time range must have a start before the end.");
            }

            if (toUnix - fromUnix > MaxPlotRangeSeconds)
            {
                throw new ArgumentException("The time range cannot exceed 30 days.");
            }
        }

        public PlotPointsDto GetPlotPoints(
            int dataSourceId,
            int tagId,
            double fromUnix,
            double toUnix,
            int? width = null,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            GetSourceOrThrow(dataSourceId);

            CheckPlotRange(fromUnix, toUnix);

            if (toUnix - fromUnix < 0.5)
            {
                throw new ArgumentException("The time range is too small.");
            }

            var tag = _service.DataAccess.DataTagDetailsGet(dataTagId: tagId, dataSourceId: dataSourceId).FirstOrDefault();
            if (tag == null)
            {
                throw new KeyNotFoundException($"Data tag {tagId} was not found.");
            }

            var xMin = fromUnix.FromUnixSeconds();
            var xMax = toUnix.FromUnixSeconds();
            var requestDiff = (xMax - xMin).TotalMilliseconds;

            if (requestDiff < 120 * 1000)
            {
                xMin = xMin.AddMinutes(-1);
                xMax = xMax.AddMinutes(1);
            }

            xMin = xMin.AddMilliseconds(requestDiff / -2);
            xMax = xMax.AddMilliseconds(requestDiff / 2);
            requestDiff = (xMax - xMin).TotalMilliseconds;

            // One M4 bucket per pixel over the requested range (the padding gets the same density).
            // The buckets start at the chart start, so they lie exactly on the pixels.
            var rangeSeconds = requestDiff / 1000.0;
            var buckets = Math.Clamp(width ?? DefaultPlotWidth, MinPlotWidth, MaxPlotWidth)
                * rangeSeconds / (toUnix - fromUnix);
            var bucketSeconds = rangeSeconds / buckets;
            var maxPlotPoints = 4 * buckets;

            // Raw values as long as they fit, otherwise a level whose window fits into a pixel. Parts
            // without points of the level (open windows at the live end, missing windows) are read
            // from finer sources. Only when no source fits, the database picks the M4 points per pixel
            // from the raw values.
            var reader = new PlotSourceReader(
                (start, end) =>
                {
                    progress?.Report("Counting archive values");
                    return _service.ArchiveAccess.GetRawValueCount(tagId, start, end, cancellationToken);
                },
                (compression, start, end) =>
                {
                    progress?.Report("Loading chart points");
                    return _service.ArchiveAccess.GetPlotPoints(tagId, compression, start, end, cancellationToken);
                },
                (start, end) =>
                {
                    progress?.Report("Reducing archive values");
                    return _service.ArchiveAccess.GetRawM4PlotPoints(tagId, start, end, fromUnix, bucketSeconds, cancellationToken);
                },
                bucketSeconds,
                MaxPlotReadPoints,
                _plotReadLimit);
            var segments = reader.Read(xMin, xMax);

            // The M4 points per pixel, if there are more points than pixels can show
            var reduce = segments.Where(s => !s.Reduced).Sum(s => s.Points.Count) > maxPlotPoints;
            List<PlotPoint> points = [];
            foreach (var segment in segments)
            {
                points.AddRange(reduce && !segment.Reduced
                    ? DataCompression.M4Buckets(segment.Points, fromUnix, bucketSeconds)
                    : segment.Points);
            }

            // Reported is the coarsest source of the range
            var compression = segments
                .Select(s => s.Compression)
                .DefaultIfEmpty(CompressionType.RAW)
                .MaxBy(c => CompressionLevels.All.First(l => l.Type == c).Seconds);
            var reduced = reduce || segments.Any(s => s.Reduced);
            return BuildPlotPointsDto(
                tag, points, compression, reduced ? bucketSeconds : 0, GetCachedArchiveStart()?.ToUnixSeconds());
        }

        /// <summary>
        /// A coarse preview of the range, drawn grayed while the exact points of GetPlotPoints load:
        /// the points of a compression level with about one point per PreviewPixelsPerPoint pixels,
        /// without the padding of the range, without counting and without raw values (PlotPreviewReader).
        /// </summary>
        public PlotPointsDto GetPlotPreview(
            int dataSourceId,
            int tagId,
            double fromUnix,
            double toUnix,
            int? width = null,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            GetSourceOrThrow(dataSourceId);

            CheckPlotRange(fromUnix, toUnix);

            if (toUnix - fromUnix < 0.5)
            {
                throw new ArgumentException("The time range is too small.");
            }

            var tag = _service.DataAccess.DataTagDetailsGet(dataTagId: tagId, dataSourceId: dataSourceId).FirstOrDefault();
            if (tag == null)
            {
                throw new KeyNotFoundException($"Data tag {tagId} was not found.");
            }

            // Local times, like the windows of the levels are aligned; nothing is stored after now
            var start = fromUnix.FromUnixSeconds();
            var end = toUnix.FromUnixSeconds();
            var now = DateTime.Now;
            if (end > now)
            {
                end = now;
            }

            var maxPoints = (double)Math.Clamp(width ?? DefaultPlotWidth, MinPlotWidth, MaxPlotWidth) / PreviewPixelsPerPoint;
            var reader = new PlotPreviewReader(
                (compression, from, to) =>
                {
                    progress?.Report("Loading preview");
                    return _service.ArchiveAccess.GetPlotPoints(tagId, compression, from, to, cancellationToken);
                },
                maxPoints);
            var segments = reader.Read(start, end);

            var points = segments.SelectMany(s => s.Points).ToList();
            // Reported is the level of the range, the first and coarsest one
            var compression = segments.Select(s => s.Compression).DefaultIfEmpty(CompressionLevels.Coarsest.Type).First();
            // The archive start as known: reading it may take a while and waits for a full load that reads it
            return BuildPlotPointsDto(tag, points, compression, 0, Volatile.Read(ref _knownArchiveStart) as double?);
        }

        private static PlotPointsDto BuildPlotPointsDto(
            DataTagDetailModel tag,
            List<PlotPoint> points,
            CompressionType compression,
            double reductionSeconds,
            double? archiveStart)
        {
            var count = points.Count;
            var times = new List<double>(count);
            var raw = new List<double?>(count);
            for (var i = 0; i < count; i++)
            {
                times.Add(points[i].X);
                raw.Add(points[i].Raw);
            }

            return new PlotPointsDto
            {
                DataTagId = tag.Id,
                Name = tag.Name ?? "",
                IsBool = tag.TypeId == (int)DataTagType.Bool,
                Compression = compression.ToString(),
                CompressionSeconds = CompressionLevels.All.First(l => l.Type == compression).Seconds,
                ReductionSeconds = reductionSeconds,
                ArchiveStart = archiveStart,
                Times = times,
                Raw = raw
            };
        }

        /// <summary>The number of raw values of the tag between two times, for the estimate of an export.</summary>
        public RawValueCountDto GetRawValueCount(
            int dataSourceId,
            int tagId,
            double fromUnix,
            double toUnix,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            var tag = GetExportTag(dataSourceId, tagId, fromUnix, toUnix);
            progress?.Report("Counting archive values");
            return new RawValueCountDto
            {
                DataTagId = tag.Id,
                Count = _service.ArchiveAccess.GetRawValueCount(tagId, fromUnix.FromUnixSeconds(), toUnix.FromUnixSeconds(), cancellationToken)
            };
        }

        /// <summary>
        /// About how many raw values the tag has between two times, from the counters of the archive only (milliseconds),
        /// for the cost class of a read before it starts; null when it is not known.
        /// </summary>
        public long? EstimateRawRows(int dataSourceId, int tagId, double fromUnix, double toUnix, CancellationToken cancellationToken = default)
        {
            if (double.IsNaN(fromUnix) || double.IsNaN(toUnix) || fromUnix >= toUnix)
            {
                return null;
            }

            GetSourceOrThrow(dataSourceId);
            return _service.ArchiveAccess.EstimateRawValueCount(tagId, fromUnix.FromUnixSeconds(), toUnix.FromUnixSeconds(), cancellationToken);
        }

        /// <summary>
        /// The raw values of the tag between two times for an export, at most limit rows (MaxExportRows at most), so a
        /// long range cannot take the memory of a small device; Truncated tells that more rows exist.
        /// </summary>
        public RawValuesDto GetRawValues(
            int dataSourceId,
            int tagId,
            double fromUnix,
            double toUnix,
            int? limit = null,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            var tag = GetExportTag(dataSourceId, tagId, fromUnix, toUnix);
            var max = Math.Clamp(limit ?? MaxExportRows, 1, MaxExportRows);
            progress?.Report("Loading raw values", null, 0);
            // One row more than asked tells whether the range holds more; the count of the rows read so far lets the
            // export show how far it got
            var points = _service.ArchiveAccess.GetRawPlotPoints(
                tagId, fromUnix.FromUnixSeconds(), toUnix.FromUnixSeconds(), max + 1, cancellationToken,
                read => progress?.Report("Loading raw values", null, read));
            var truncated = points.Count > max;
            if (truncated)
            {
                points.RemoveRange(max, points.Count - max);
            }
            return new RawValuesDto
            {
                DataTagId = tag.Id,
                Name = tag.Name ?? "",
                IsBool = tag.TypeId == (int)DataTagType.Bool,
                Truncated = truncated,
                Times = points.Select(p => p.X).ToList(),
                Raw = points.Select(p => p.Raw).ToList()
            };
        }

        /// <summary>
        /// The data tags of a streamed raw data CSV export ("data source id:data tag id" each, in the order of the file),
        /// checked with the range before the first byte is written, so a wrong request still gets an error.
        /// </summary>
        public List<RawCsvTag> GetRawCsvTags(IReadOnlyList<string> tags, double fromUnix, double toUnix)
        {
            if (tags == null || tags.Count == 0)
            {
                throw new ArgumentException("At least one data tag is needed.");
            }
            if (tags.Count > MaxCsvExportTags)
            {
                throw new ArgumentException($"At most {MaxCsvExportTags} data tags can be exported at once.");
            }

            List<RawCsvTag> result = [];
            foreach (var value in tags)
            {
                var parts = value?.Split(':');
                if (parts is not { Length: 2 } || !int.TryParse(parts[0], out var dataSourceId) || !int.TryParse(parts[1], out var tagId))
                {
                    throw new ArgumentException($"The data tag '{value}' is invalid.");
                }
                var tag = GetExportTag(dataSourceId, tagId, fromUnix, toUnix);
                result.Add(new RawCsvTag(tag.Id, GetSourceOrThrow(dataSourceId).Name ?? "", tag.Name ?? ""));
            }
            return result;
        }

        /// <summary>The raw values of the tag between two times as they are read, for the streamed CSV export.</summary>
        public IAsyncEnumerable<PlotPoint> StreamRawValues(int tagId, double fromUnix, double toUnix, CancellationToken cancellationToken)
            => _service.ArchiveAccess.StreamRawPlotPoints(tagId, fromUnix.FromUnixSeconds(), toUnix.FromUnixSeconds(), cancellationToken);

        private DataTagDetailModel GetExportTag(int dataSourceId, int tagId, double fromUnix, double toUnix)
        {
            GetSourceOrThrow(dataSourceId);
            CheckPlotRange(fromUnix, toUnix);
            return _service.DataAccess.DataTagDetailsGet(dataTagId: tagId, dataSourceId: dataSourceId).FirstOrDefault()
                ?? throw new KeyNotFoundException($"Data tag {tagId} was not found.");
        }

        /// <summary>The statistics of the raw values of the tag between two times, calculated by the archive.</summary>
        public DataTagStatisticsDto GetStatistics(
            int dataSourceId,
            int tagId,
            double fromUnix,
            double toUnix,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            GetSourceOrThrow(dataSourceId);

            CheckPlotRange(fromUnix, toUnix);

            var tag = _service.DataAccess.DataTagDetailsGet(dataTagId: tagId, dataSourceId: dataSourceId).FirstOrDefault();
            if (tag == null)
            {
                throw new KeyNotFoundException($"Data tag {tagId} was not found.");
            }

            progress?.Report("Calculating statistics");
            var start = fromUnix.FromUnixSeconds();
            var end = toUnix.FromUnixSeconds();
            // The value that holds at the start is the last one within the lookback before it
            var lookback = TimeSpan.FromSeconds(MaxPlotRangeSeconds);
            // The last value of the tag holds for the hold time at most, then the source has stopped delivering
            var hold = ArchiveHold.For(tag.Cycle);
            var stats = _service.ArchiveAccess.GetRawStatistics(tagId, start, end, lookback, hold, cancellationToken);
            var isBool = tag.TypeId == (int)DataTagType.Bool;
            var durations = isBool
                ? _service.ArchiveAccess.GetBoolDurations(tagId, start, end, lookback, hold, cancellationToken)
                : null;
            var invalidSeconds = isBool
                ? durations?.InvalidSeconds
                : _service.ArchiveAccess.GetInvalidSeconds(tagId, start, end, lookback, hold, cancellationToken);

            return new DataTagStatisticsDto
            {
                DataTagId = tag.Id,
                Name = tag.Name ?? "",
                IsBool = isBool,
                From = fromUnix,
                To = toUnix,
                Count = stats?.Count ?? 0,
                Min = stats?.Min,
                Max = stats?.Max,
                Mean = stats?.Mean,
                StdDev = stats?.StdDev,
                HighSeconds = durations?.HighSeconds,
                LowSeconds = durations?.LowSeconds,
                SignalChanges = durations?.Changes,
                LongestHighSeconds = durations?.LongestHighSeconds,
                ShortestHighSeconds = durations?.ShortestHighSeconds,
                LongestLowSeconds = durations?.LongestLowSeconds,
                ShortestLowSeconds = durations?.ShortestLowSeconds,
                InvalidSeconds = invalidSeconds
            };
        }

        /// <summary>
        /// The raw value of the tag that holds at the time, read from the archive, not from the plot points. No value
        /// in the future, when the signal was lost before the next value, and when the newest value is older than the
        /// hold time of the tag.
        /// </summary>
        public RawValueAtDto GetRawValueAt(int dataSourceId, int tagId, double unixSeconds, CancellationToken cancellationToken = default)
        {
            GetSourceOrThrow(dataSourceId);
            if (double.IsNaN(unixSeconds) || double.IsInfinity(unixSeconds))
            {
                throw new ArgumentException("The time is invalid.");
            }

            var tag = _service.DataAccess.DataTagDetailsGet(dataTagId: tagId, dataSourceId: dataSourceId).FirstOrDefault();
            if (tag == null)
            {
                throw new KeyNotFoundException($"Data tag {tagId} was not found.");
            }

            var point = _service.ArchiveAccess.GetRawValueAt(
                tagId,
                unixSeconds.FromUnixSeconds(),
                TimeSpan.FromSeconds(MaxPlotRangeSeconds),
                ArchiveHold.For(tag.Cycle),
                cancellationToken);
            return new RawValueAtDto { DataTagId = tag.Id, Time = point?.X, Value = point?.Raw };
        }

        public DataTagMutationResultDto SetCycle(int dataSourceId, DataTagCycleWriteDto dto)
        {
            if (dto == null)
            {
                throw new ArgumentException("The request body is missing.");
            }

            var source = GetSourceOrThrow(dataSourceId);
            var cycles = GetCycles(dataSourceId);
            if (!cycles.Contains(dto.Cycle))
            {
                throw new ArgumentException("Please choose a valid cycle.");
            }

            var writable = GetWritableTags(dataSourceId, dto?.Ids);
            if (writable.Count == 0)
            {
                throw new ArgumentException("No writable data tags were selected.");
            }

            _service.DataAccess.DataTagsUpdateCycle(writable.Select(t => t.Id).ToList(), dto.Cycle);
            foreach (var tag in writable)
            {
                tag.Cycle = dto.Cycle;
            }

            return new DataTagMutationResultDto
            {
                Updated = ToItems(source, writable)
            };
        }

        private DateTime? GetCachedArchiveStart()
        {
            lock (_archiveStartLock)
            {
                if (_archiveStartFetchedAtUtc != DateTime.MinValue
                    && DateTime.UtcNow - _archiveStartFetchedAtUtc < TimeSpan.FromMinutes(5))
                {
                    return _archiveStart;
                }

                try
                {
                    _archiveStart = _service.ArchiveAccess.GetArchiveStartTime();
                    _archiveStartFetchedAtUtc = DateTime.UtcNow;
                    Volatile.Write(ref _knownArchiveStart, _archiveStart?.ToUnixSeconds());
                }
                catch
                {
                    if (_archiveStartFetchedAtUtc == DateTime.MinValue)
                    {
                        _archiveStartFetchedAtUtc = DateTime.UtcNow;
                    }
                }

                return _archiveStart;
            }
        }

        private DataSourceModel GetSourceOrThrow(int id)
        {
            try
            {
                return _service.DataAccess.DataSourceGetById(id);
            }
            catch (InvalidOperationException)
            {
                throw new KeyNotFoundException($"Data source {id} was not found.");
            }
        }

        private List<int> GetCycles(int dataSourceId)
        {
            var info = _service.GetDataSourceInfo(dataSourceId);
            if (info?.Cycles != null && info.Cycles.Count > 0)
            {
                return info.Cycles;
            }

            return [1000];
        }

        /// <summary>The tags that may be activated (see DataTagActivation); throws when none of them may.</summary>
        private List<DataTagListItemModel> WithoutNotLoggable(DataSourceModel source, List<DataTagListItemModel> tags)
        {
            var refused = _service.DataAccess.DataTagDetailsGetByIds(source.Id, tags.Select(t => t.Id).ToList())
                .Where(t => !DataTagActivation.CanActivate((DataSourceType)source.TypeId, (DataTagType)t.TypeId))
                .Select(t => t.Id)
                .ToHashSet();
            if (refused.Count == 0)
            {
                return tags;
            }
            if (refused.Count == tags.Count)
            {
                throw new ArgumentException(DataTagActivation.NotLoggableText);
            }
            return tags.Where(t => !refused.Contains(t.Id)).ToList();
        }

        private List<DataTagListItemModel> GetWritableTags(int dataSourceId, List<int> ids)
        {
            var normalized = NormalizeIds(ids, MaxMutationIds, "data tags");
            if (normalized.Count == 0)
            {
                return [];
            }

            return _service.DataAccess.DataTagsGetByIds(dataSourceId, normalized)
                .Where(t => t.IsSys == 0)
                .ToList();
        }

        private static List<int> NormalizeIds(List<int> ids, int max, string label)
        {
            if (ids == null || ids.Count == 0)
            {
                return [];
            }

            if (ids.Count > max)
            {
                throw new ArgumentException($"At most {max} {label} can be requested.");
            }

            return ids.Where(id => id > 0).Distinct().ToList();
        }

        private TagLiveSort LiveSort(List<DataTagSortLevel> sorts, DataSourceInfoModel info)
        {
            if (sorts == null || !sorts.Exists(level => level.Column is DataTagSortColumn.Value or DataTagSortColumn.Status))
            {
                return default;
            }

            var stateName = SourceStateName(info);
            var offline = stateName.Length > 0
                && !string.Equals(stateName, "Connected", StringComparison.OrdinalIgnoreCase);
            return new TagLiveSort(offline, DateTime.UtcNow, _service.ArchiveAccess);
        }

        private static List<DataTagSortLevel> ParseSorts(string sort1, string dir1, string sort2, string dir2)
        {
            var levels = new List<DataTagSortLevel>(2);
            TryAddSort(levels, sort1, dir1);
            TryAddSort(levels, sort2, dir2);
            return levels;
        }

        private static void TryAddSort(List<DataTagSortLevel> levels, string sort, string dir)
        {
            if (levels.Count >= 2 || !TryParseSortColumn(sort, out var column))
            {
                return;
            }

            if (levels.Exists(level => level.Column == column) || !TryParseSortDir(dir, out var ascending))
            {
                return;
            }

            levels.Add(new DataTagSortLevel(column, ascending));
        }

        private static bool TryParseSortColumn(string sort, out DataTagSortColumn column)
        {
            switch (sort?.Trim().ToLowerInvariant())
            {
                case "id":
                    column = DataTagSortColumn.Id;
                    return true;
                case "name":
                    column = DataTagSortColumn.Name;
                    return true;
                case "cycle":
                    column = DataTagSortColumn.Cycle;
                    return true;
                case "active":
                    column = DataTagSortColumn.Active;
                    return true;
                case "displayname":
                    column = DataTagSortColumn.DisplayName;
                    return true;
                case "address":
                    column = DataTagSortColumn.Address;
                    return true;
                case "comment":
                    column = DataTagSortColumn.Comment;
                    return true;
                case "value":
                    column = DataTagSortColumn.Value;
                    return true;
                case "status":
                    column = DataTagSortColumn.Status;
                    return true;
                default:
                    column = default;
                    return false;
            }
        }

        private static bool TryParseSortDir(string dir, out bool ascending)
        {
            switch (dir?.Trim().ToLowerInvariant())
            {
                case "asc":
                    ascending = true;
                    return true;
                case "desc":
                    ascending = false;
                    return true;
                default:
                    ascending = true;
                    return false;
            }
        }

        /// <summary>The rows with their comment in the language of the source (s7.locale), read for these ids only.</summary>
        private List<DataTagListItemDto> ToItems(DataSourceModel source, IReadOnlyList<DataTagListItemModel> tags)
        {
            // Rows of the list index carry their comment, rows read by id (mutation results) do not.
            var missing = tags.Where(t => t.Comment == null).Select(t => t.Id).ToList();
            var descriptions = missing.Count == 0
                ? new Dictionary<int, string>()
                : _service.DataAccess.DataTagDescriptionsGet(source.Id, missing);
            return tags.Select(tag => ToItem(tag, tag.Comment ??
                (descriptions.TryGetValue(tag.Id, out var description) ? DataTagDescription.Resolve(description, source.S7Locale) : ""))).ToList();
        }

        private static DataTagListItemDto ToItem(DataTagListItemModel tag, string comment)
        {
            return new DataTagListItemDto
            {
                Id = tag.Id,
                Name = tag.Name ?? "",
                DisplayName = tag.DisplayName ?? "",
                Address = tag.Address ?? "",
                Comment = comment ?? "",
                Cycle = tag.Cycle,
                IsActive = tag.IsActive != 0,
                IsSys = tag.IsSys != 0
            };
        }

        private static string SourceStateName(DataSourceInfoModel info)
        {
            return (info?.State ?? DataSourceState.Undefined).ToString();
        }

        private static int SourceStateId(DataSourceInfoModel info)
        {
            return (int)(info?.State ?? DataSourceState.Undefined);
        }
    }
}
