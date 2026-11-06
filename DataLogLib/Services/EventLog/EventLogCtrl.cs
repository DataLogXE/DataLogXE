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
using DataLogLib.Common.Serilog;
using Microsoft.Extensions.Configuration;

namespace DataLogLib.Services.EventLog
{
    public class EventLogCtrl : IEventLogAccess, IEventLogWriter
    {
        private EventLogWorker _worker;
        private ProcessEventStore _store;
        private AppEventStore _appStore;
        private ProcessEventRetentionJob _retention;

        public void Init(IConfiguration configuration, SerilogSink sink)
        {
            _store = new ProcessEventStore(configuration);
            _appStore = new AppEventStore(configuration);
            _worker = new EventLogWorker(
                sink,
                _store,
                _appStore,
                configuration);
            _worker.Start();
            _retention = new ProcessEventRetentionJob(_store);
            _retention.Start();
            new ProcessEventCountWorker(_store, new LoadPacer(LoadPacer.MaxLoadFromEnvironment(), work: "Alarm counts")).Start();
        }

        /// <summary>Retention of the process events in seconds, 0 for forever.</summary>
        public long GetRetentionSeconds()
        {
            if (_store == null)
            {
                throw new InvalidOperationException("Event log is unavailable.");
            }

            return _store.ReadRetentionSeconds();
        }

        public long SetRetentionSeconds(long everySeconds)
        {
            if (_store == null)
            {
                throw new InvalidOperationException("Event log is unavailable.");
            }

            return _store.SetRetentionSeconds(everySeconds);
        }

        public void Enqueue(EventLogRecord record)
        {
            // A row far in the future would push the times of every later row behind it.
            if (record != null && !ClockGuard.Admit(record.TimeUtc, "alarms"))
            {
                return;
            }
            _worker?.EnqueueDirect(record);
        }

        public EventLogQueryResult QueryEvents(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryEvents(query, null, cancellationToken);
        }

        public EventLogQueryResult QueryEvents(
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return new EventLogQueryResult();
            }

            if (query.SortColumn is EventSortColumn column)
            {
                var limit = Math.Clamp(query.Limit, 1, EventLogConstants.MaxQueryLimit);
                var sorted = _store.LoadSortedIds(query, column, progress, cancellationToken);
                var ids = sorted.Ids;
                var start = Math.Max(0, query.Offset);
                var take = Math.Max(0, Math.Min(limit, ids.Length - start));
                progress?.Report("Loading alarms", null, ids.Length);
                return new EventLogQueryResult
                {
                    Items = take == 0 ? [] : _store.LoadRows(query, ids.AsSpan(start, take).ToArray(), cancellationToken),
                    HasMore = start + take < ids.Length,
                    NextCursor = null,
                    Total = ids.Length,
                    SortedOf = sorted.Total
                };
            }

            progress?.Report("Loading alarms");
            return _store.QueryEvents(query, cancellationToken);
        }

        public EventLogSummaryResult QueryEventSummary(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryEventSummary(query, null, cancellationToken);
        }

        public EventLogSummaryResult QueryEventSummary(
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return new EventLogSummaryResult();
            }

            return _store.QueryEventSummary(query, progress, cancellationToken);
        }

        public long QueryEventCount(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryEventCount(query, null, cancellationToken);
        }

        public long QueryEventCount(EventLogQuery query, IQueryProgress progress, CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return 0;
            }

            return _store.QueryEventCount(query, progress, cancellationToken);
        }

        private static readonly TimeSpan AlarmStatesTtl = TimeSpan.FromSeconds(30);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Expires, IReadOnlyList<string> States)> _alarmStatesCache = new();

        /// <summary>
        /// States present in the range, cached for a short time per minute-aligned range and source set.
        /// </summary>
        public IReadOnlyList<string> QueryAlarmStates(EventLogQuery query, CancellationToken cancellationToken = default)
        {
            return QueryAlarmStates(query, null, cancellationToken);
        }

        public IReadOnlyList<string> QueryAlarmStates(
            EventLogQuery query,
            IQueryProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return [];
            }

            var fromTicks = query.FromUtc.Ticks / TimeSpan.TicksPerMinute;
            var toTicks = query.ToUtc.Ticks / TimeSpan.TicksPerMinute;
            var sources = string.Join(",", (query.SourceIds ?? []).OrderBy(id => id));
            var classes = string.Join("\u001f", query.ClassLabels ?? []);
            var key = $"{fromTicks}|{toTicks}|{sources}|{query.SourceId}|{classes}";
            var now = DateTime.UtcNow;
            if (_alarmStatesCache.TryGetValue(key, out var hit) && hit.Expires > now)
            {
                return hit.States;
            }

            var states = _store.QueryAlarmStates(query, progress, cancellationToken);
            var normalized = states
                .Where(AlarmStateValues.IsKnown)
                .Select(AlarmStateValues.Base)
                .Distinct()
                .ToList();
            if (_alarmStatesCache.Count > 64)
            {
                _alarmStatesCache.Clear();
            }

            _alarmStatesCache[key] = (now + AlarmStatesTtl, normalized);
            return normalized;
        }

        public EventLogQueryResult QueryAlarmHistory(AlarmHistoryQuery query, CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return new EventLogQueryResult();
            }

            return _store.QueryAlarmHistory(query, cancellationToken);
        }

        public AlarmCountResult QueryAlarmCounts(AlarmCountQuery query, CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return new AlarmCountResult { Counts = new long[Math.Max(0, (query.Edges?.Count ?? 0) - 1)] };
            }

            return _store.QueryAlarmCounts(query, cancellationToken);
        }

        public EventLogQueryResult QueryOpenAlarms(int sourceId, CancellationToken cancellationToken = default)
        {
            if (_store == null)
            {
                return new EventLogQueryResult();
            }

            return _store.QueryOpenAlarms(sourceId, cancellationToken);
        }

        public EventLogQueryResult QueryOpenAlarmLookback(
            int sourceId,
            IReadOnlyList<string> correlationIds,
            CancellationToken cancellationToken = default)
        {
            if (_store == null || correlationIds == null || correlationIds.Count == 0)
            {
                return new EventLogQueryResult();
            }

            return _store.QueryOpenAlarmLookback(sourceId, correlationIds, cancellationToken);
        }

        public SourceLogResult QuerySourceLog(SourceLogQuery query, CancellationToken cancellationToken = default)
        {
            if (_appStore == null)
            {
                return new SourceLogResult();
            }

            return _appStore.QuerySourceLog(query, cancellationToken);
        }

        public SourceLogResult QueryAppLog(AppLogQuery query, CancellationToken cancellationToken = default)
        {
            if (_appStore == null)
            {
                return new SourceLogResult();
            }

            return _appStore.QueryAppLog(query, cancellationToken);
        }

        public IAsyncEnumerable<EventLogRecord> ExportAppLog(
            AppLogQuery query, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        {
            if (_appStore == null)
            {
                return AsyncEnumerable.Empty<EventLogRecord>();
            }

            return _appStore.ExportAppLog(query, fromUtc, toUtc, cancellationToken);
        }

        public List<SourceLogCount> QuerySourceLogCounts(DateTime fromUtc, CancellationToken cancellationToken = default)
        {
            if (_appStore == null)
            {
                return [];
            }

            return _appStore.QuerySourceLogCounts(fromUtc, cancellationToken);
        }

        public void Flush()
        {
            _worker?.Flush();
        }
    }
}
