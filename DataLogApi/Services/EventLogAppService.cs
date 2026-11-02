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
using DataLogLib.Services;
using DataLogLib.Services.EventLog;
using DataLogService;

namespace DataLogApi.Services
{
    public class EventLogAppService
    {
        public const int DefaultLimit = EventLogConstants.DefaultQueryLimit;
        public const int MaxLimit = EventLogConstants.MaxQueryLimit;
        // The alarm counts: at most this many bucket edges over at most this span
        private const int MaxCountEdges = 200;
        private static readonly TimeSpan MaxCountRange = TimeSpan.FromDays(32);

        private readonly EventLogCtrl _eventLog;
        private readonly ServiceCtrl _service;

        public EventLogAppService(EventLogCtrl eventLog, ServiceCtrl service)
        {
            _eventLog = eventLog;
            _service = service;
        }

        public EventLogPageDto GetPage(
            double? fromMs,
            double? toMs,
            string level,
            string kind,
            int? sourceId,
            string sourceIds,
            string component,
            string state,
            string q,
            int? limit,
            string cursor,
            int? offset,
            string sort1 = null,
            string dir1 = null,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null,
            string classes = null)
        {
            var pageLimit = limit.GetValueOrDefault(DefaultLimit);
            if (pageLimit <= 0)
            {
                pageLimit = DefaultLimit;
            }

            pageLimit = Math.Min(pageLimit, MaxLimit);
            var pageOffset = Math.Max(0, offset.GetValueOrDefault());
            var query = BuildQuery(fromMs, toMs, level, kind, sourceId, sourceIds, component, state, q, pageLimit, cursor, pageOffset, classes);
            ApplySort(query, sort1, dir1);
            var result = _eventLog.QueryEvents(query, progress, cancellationToken);
            return new EventLogPageDto
            {
                From = query.FromUtc,
                To = query.ToUtc,
                Limit = pageLimit,
                HasMore = result.HasMore,
                NextCursor = result.NextCursor,
                Total = result.Total,
                SortedOf = result.SortedOf,
                Items = result.Items.Select(ToDto).ToList()
            };
        }

        public EventLogSummaryDto GetSummary(
            double? fromMs,
            double? toMs,
            string level,
            string kind,
            int? sourceId,
            string sourceIds,
            string component,
            string state,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            var query = BuildQuery(fromMs, toMs, level, kind, sourceId, sourceIds, component, state, q: null, limit: DefaultLimit, cursor: null);
            var summary = _eventLog.QueryEventSummary(query, progress, cancellationToken);
            return new EventLogSummaryDto
            {
                From = query.FromUtc,
                To = query.ToUtc,
                Total = summary.Total,
                ActiveAlarms = summary.ActiveAlarms,
                CountByLevel = summary.CountByLevel
            };
        }

        public EventLogCountDto GetCount(
            double? fromMs,
            double? toMs,
            string level,
            string kind,
            int? sourceId,
            string sourceIds,
            string component,
            string state,
            string q,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null,
            string classes = null)
        {
            var query = BuildQuery(fromMs, toMs, level, kind, sourceId, sourceIds, component, state, q, limit: DefaultLimit, cursor: null, classes: classes);
            return new EventLogCountDto
            {
                Total = _eventLog.QueryEventCount(query, progress, cancellationToken)
            };
        }

        public List<string> GetAlarmStates(
            double? fromMs,
            double? toMs,
            string sourceIds,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null,
            string classes = null)
        {
            var query = BuildQuery(fromMs, toMs, null, null, null, sourceIds, null, null, null, limit: DefaultLimit, cursor: null, classes: classes);
            return _eventLog.QueryAlarmStates(query, progress, cancellationToken).ToList();
        }

        public EventLogPageDto GetAlarmHistory(
            string correlationId,
            double? fromMs,
            double? toMs,
            int? sourceId,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                throw new ArgumentException("Correlation id is required.");
            }

            var toUtc = ParseToUtc(toMs);
            var fromUtc = ParseFromUtc(fromMs, toUtc);
            if (fromUtc >= toUtc)
            {
                throw new ArgumentException("Invalid time range.");
            }

            progress?.Report("Loading alarm history");
            var result = _eventLog.QueryAlarmHistory(new AlarmHistoryQuery
            {
                CorrelationId = correlationId.Trim(),
                SourceId = sourceId.GetValueOrDefault(),
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Limit = EventLogConstants.AlarmHistoryLimit
            }, cancellationToken);

            return new EventLogPageDto
            {
                From = fromUtc,
                To = toUtc,
                Limit = EventLogConstants.AlarmHistoryLimit,
                HasMore = false,
                NextCursor = null,
                Items = result.Items.Select(ToDto).ToList()
            };
        }

        /// <summary>
        /// How often the alarm of an occurrence entered the given state, per bucket between the edges (Unix ms on full
        /// hours, ascending), from the hourly alarm counts. The occurrence is found among the pending alarms, else by
        /// its point at the given time.
        /// </summary>
        public AlarmCountsDto GetAlarmCounts(
            string correlationId,
            int? sourceId,
            double? time,
            string state,
            string edges,
            CancellationToken cancellationToken = default,
            IQueryProgress progress = null)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                throw new ArgumentException("Correlation id is required.");
            }

            if (string.IsNullOrWhiteSpace(state) || !AlarmStateValues.IsKnown(state.Trim()))
            {
                throw new ArgumentException("Invalid alarm state.");
            }

            var bounds = ParseEdges(edges);
            var id = correlationId.Trim();
            var source = sourceId.GetValueOrDefault();
            progress?.Report("Finding the alarm");
            var reference = _service.GetActiveAlarms(null)
                .FirstOrDefault(item => item.CorrelationId == id && item.SourceId == source);
            if (reference == null && time is > 0)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds((long)time.Value).UtcDateTime;
                reference = _eventLog.QueryAlarmHistory(new AlarmHistoryQuery
                {
                    CorrelationId = id,
                    SourceId = source,
                    FromUtc = at.AddSeconds(-1),
                    ToUtc = at.AddSeconds(1),
                    Limit = 1
                }, cancellationToken).Items.FirstOrDefault();
            }

            progress?.Report("Counting the alarm");
            var result = _eventLog.QueryAlarmCounts(new AlarmCountQuery
            {
                Reference = reference,
                AlarmState = state.Trim(),
                Edges = bounds
            }, cancellationToken);
            return new AlarmCountsDto
            {
                Edges = bounds.ToArray(),
                Counts = result.Counts,
                CompleteFrom = result.CompleteFromUtc,
                Oldest = result.OldestUtc
            };
        }

        public EventLogPageDto GetActiveAlarms(string sourceIds)
        {
            var items = _service.GetActiveAlarms(ParseSourceIds(sourceIds));
            var nowUtc = DateTime.UtcNow;
            return new EventLogPageDto
            {
                From = nowUtc,
                To = nowUtc,
                Limit = items.Count,
                HasMore = false,
                NextCursor = null,
                Items = items.Select(ToDto).ToList()
            };
        }

        /// <summary>
        /// The log of a data source, newest first: one page older than <paramref name="before"/> (default now), or
        /// for the live view everything newer than <paramref name="after"/>.
        /// </summary>
        public SourceLogPageDto GetSourceLog(
            int sourceId,
            string before,
            string after,
            string level,
            string q,
            int? limit,
            CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
            {
                throw new ArgumentException("Use either before or after.");
            }

            var pageLimit = limit.GetValueOrDefault(MaxLimit);
            var result = _eventLog.QuerySourceLog(new SourceLogQuery
            {
                SourceId = sourceId,
                BeforeUtc = string.IsNullOrWhiteSpace(before) ? null : ParseCursorUtc(before),
                AfterUtc = string.IsNullOrWhiteSpace(after) ? null : ParseCursorUtc(after),
                MinLevel = NullIfEmpty(level),
                Text = NormalizeFilter(q),
                Limit = pageLimit <= 0 ? MaxLimit : Math.Min(pageLimit, MaxLimit)
            }, cancellationToken);
            return new SourceLogPageDto
            {
                Items = result.Items.Select(ToSourceLogDto).ToList(),
                Truncated = result.Truncated,
                NextBefore = result.NextBeforeUtc.HasValue ? ToCursor(result.NextBeforeUtc.Value) : null
            };
        }

        /// <summary>Warnings and errors of every data source in the last 24 hours.</summary>
        public List<SourceLogCountDto> GetSourceLogCounts(CancellationToken cancellationToken = default)
        {
            var counts = _eventLog.QuerySourceLogCounts(DateTime.UtcNow.AddHours(-24), cancellationToken);
            return counts
                .GroupBy(c => c.SourceId)
                .Select(g => new SourceLogCountDto
                {
                    SourceId = g.Key,
                    Warnings = g.Where(c => c.Level == "Warning").Sum(c => c.Count),
                    Errors = g.Where(c => c.Level != "Warning").Sum(c => c.Count)
                })
                .OrderBy(c => c.SourceId)
                .ToList();
        }

        private static SourceLogItemDto ToSourceLogDto(EventLogRecord record)
        {
            return new SourceLogItemDto
            {
                // The exact time as a cursor: the milliseconds of a JS date would repeat or skip events
                Time = ToCursor(record.TimeUtc),
                Level = record.Level,
                Component = record.Component,
                Message = record.Message,
                Exception = NullIfEmpty(record.Exception),
                Props = NullIfEmpty(record.Props)
            };
        }

        private static string ToCursor(DateTime timeUtc)
        {
            return DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.DateTimeFormatInfo.InvariantInfo) + "Z";
        }

        public List<AlarmClassDto> GetAlarmClasses()
        {
            return _service.GetAlarmClasses()
                .Select(item => new AlarmClassDto
                {
                    Name = item.Name ?? "",
                    Symbol = item.Symbol ?? ""
                })
                .ToList();
        }

        /// <summary>
        /// Applies the sort of the alarm table. An unknown column or direction keeps the storage
        /// order, like the tag list endpoint. The archive sorts on the labels the table renders, so
        /// the source names travel with the query.
        /// </summary>
        private void ApplySort(EventLogQuery query, string sort1, string dir1)
        {
            if (!EventSortColumns.TryParse(sort1, out var column)
                || !EventSortColumns.TryParseDirection(dir1, out var ascending))
            {
                return;
            }

            query.SortColumn = column;
            query.SortAscending = ascending;
            if (column == EventSortColumn.Source)
            {
                query.SourceNames = BuildSourceNames();
            }
        }

        private Dictionary<int, string> BuildSourceNames()
        {
            var names = new Dictionary<int, string>();
            foreach (var info in _service.GetDataSourceInfos())
            {
                if (info?.DataSource is { Id: > 0 })
                {
                    names[info.DataSource.Id] = info.DataSource.Name ?? "";
                }
            }

            return names;
        }

        private static EventLogQuery BuildQuery(
            double? fromMs,
            double? toMs,
            string level,
            string kind,
            int? sourceId,
            string sourceIds,
            string component,
            string state,
            string q,
            int limit,
            string cursor,
            int offset = 0,
            string classes = null)
        {
            var toUtc = ParseToUtc(toMs);
            var fromUtc = ParseFromUtc(fromMs, toUtc);
            if (fromUtc >= toUtc)
            {
                throw new ArgumentException("Invalid time range.");
            }

            DateTime? cursorUtc = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                cursorUtc = ParseCursorUtc(cursor);
            }

            var levels = ParseLevels(level);
            return new EventLogQuery
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Level = levels.Count == 1 ? levels[0] : null,
                Levels = levels.Count > 1 ? levels : null,
                Kind = NormalizeFilter(kind),
                SourceId = sourceId,
                SourceIds = ParseSourceIds(sourceIds),
                Component = NormalizeFilter(component),
                AlarmState = NormalizeAlarmState(state),
                ClassLabels = ParseClassLabels(classes),
                TextSearch = NormalizeFilter(q),
                Limit = limit,
                Offset = Math.Max(0, offset),
                CursorUtc = cursorUtc
            };
        }

        private static EventLogItemDto ToDto(EventLogRecord record)
        {
            return new EventLogItemDto
            {
                Time = record.TimeUtc,
                Level = record.Level,
                Kind = record.Kind,
                Component = record.Component,
                SourceId = record.SourceId,
                SourceType = record.SourceType,
                Message = record.Message,
                Template = record.Template,
                Exception = record.Exception,
                Props = record.Props,
                CorrelationId = record.CorrelationId,
                AlarmState = record.AlarmState,
                Severity = record.Severity,
                Active = record.Active,
                Acked = record.Acked,
                Retain = record.Retain,
                ActiveSince = record.ActiveSinceUtc,
                EventType = record.EventType,
                EventTypeName = NullIfEmpty(record.EventTypeName),
                AlarmClassSymbol = NullIfEmpty(record.AlarmClassSymbol),
                AlarmClassName = NullIfEmpty(record.AlarmClassName),
                TextColor = record.TextColor,
                BackColor = record.BackColor,
                Flashing = record.Flashing,
                DurationMs = record.DurationMs
            };
        }

        private static DateTime ParseToUtc(double? toMs)
        {
            if (toMs.HasValue && toMs.Value > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)toMs.Value).UtcDateTime;
            }

            return DateTime.UtcNow;
        }

        private static DateTime ParseFromUtc(double? fromMs, DateTime toUtc)
        {
            if (fromMs.HasValue && fromMs.Value > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)fromMs.Value).UtcDateTime;
            }

            return toUtc.AddHours(-24);
        }

        private static DateTime ParseCursorUtc(string cursor)
        {
            if (DateTime.TryParse(cursor, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            throw new ArgumentException("Invalid cursor.");
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string NormalizeFilter(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeAlarmState(string value)
        {
            var text = NormalizeFilter(value);
            if (text == null)
            {
                return null;
            }

            // Several states separated by commas
            var states = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (states.Length == 0 || !states.All(AlarmStateValues.IsKnown))
            {
                throw new ArgumentException("Invalid alarm state.");
            }

            return string.Join(",", states.Distinct(StringComparer.Ordinal));
        }

        /// <summary>The class labels as a JSON array of strings; null for none.</summary>
        private static List<string> ParseClassLabels(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                var labels = System.Text.Json.JsonSerializer.Deserialize<List<string>>(value)?
                    .Select(label => label?.Trim() ?? "")
                    .Where(label => label.Length > 0)
                    .Distinct()
                    .ToList();
                return labels is { Count: > 0 } ? labels : null;
            }
            catch (System.Text.Json.JsonException)
            {
                throw new ArgumentException("Invalid classes.");
            }
        }

        private static List<int> ParseSourceIds(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var ids = new List<int>();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out var id) || id <= 0)
                {
                    throw new ArgumentException("Invalid source.");
                }

                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            if (ids.Count > EventLogConstants.MaxSourceFilterCount)
            {
                throw new ArgumentException("Too many sources.");
            }

            return ids.Count == 0 ? null : ids;
        }

        private static List<DateTime> ParseEdges(string value)
        {
            var edges = new List<DateTime>();
            foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!long.TryParse(part, out var ms) || ms <= 0)
                {
                    throw new ArgumentException("Invalid edges.");
                }

                // The counts are kept per hour
                if (ms % 3_600_000 != 0)
                {
                    throw new ArgumentException("The edges must be on full hours.");
                }

                var edge = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                if (edges.Count > 0 && edge <= edges[^1])
                {
                    throw new ArgumentException("The edges must ascend.");
                }

                edges.Add(edge);
            }

            if (edges.Count < 2 || edges.Count > MaxCountEdges)
            {
                throw new ArgumentException($"Between 2 and {MaxCountEdges} edges are required.");
            }

            if (edges[^1] - edges[0] > MaxCountRange)
            {
                throw new ArgumentException("The edges span too long a time.");
            }

            return edges;
        }

        private static List<string> ParseLevels(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
    }
}
