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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataLogLib.Services.EventLog;
using Opc.Ua;

namespace DataLogService.DataSource.OpcUa
{
    internal sealed class AlarmTracker
    {
        internal readonly struct AlarmEmit
        {
            public EventLogRecord Earlier { get; }
            public EventLogRecord Later { get; }

            public AlarmEmit(EventLogRecord earlier, EventLogRecord later)
            {
                Earlier = earlier;
                Later = later;
            }
        }

        private sealed class TrackedOccurrence
        {
            public string CorrelationId { get; set; } = "";
            public DateTime? ActiveSinceUtc { get; set; }
            public long? StandingDurationMs { get; set; }
            public string LastSignature { get; set; } = "";
            public DateTime LastSeenUtc { get; set; }
            public bool IsActive { get; set; }
            public string AlarmState { get; set; } = "";
            public DateTime AppliedUtc { get; set; }
            public byte[] EventId { get; set; }
            public OpcUaAlarmNotification Face { get; set; }
            public OpcUaAlarmNotification InactiveFace { get; set; }
        }

        private sealed class ParkedRemoval
        {
            public EventLogRecord Record { get; set; }
            public byte[] EventId { get; set; }
            public DateTime AppliedUtc { get; set; }
            public OpcUaAlarmNotification Face { get; set; }
        }

        private readonly int _sourceId;
        private readonly string _sourceName;
        private readonly string _sourceType;
        private readonly Dictionary<string, TrackedOccurrence> _byCorrelation = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _activeCorrelationByCondition = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ParkedRemoval> _parkedRemoval = new(StringComparer.Ordinal);
        private readonly HashSet<string> _suspect = new(StringComparer.Ordinal);
        private readonly HashSet<string> _refreshBaseline = new(StringComparer.Ordinal);
        private readonly HashSet<string> _refreshNewKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private bool _refreshing;
        private bool _refreshPending;
        private readonly Queue<OpcUaAlarmNotification> _pendingDuringRefresh = new();
        private bool _archiveSeeded;
        private int _refreshEpoch;
        private int _activeEpoch;
        private int _armedEpoch;
        private DateTime _refreshMarkUtc;
        private int _refreshOpen;
        private int _refreshConfirmed;
        private static readonly TimeSpan RefreshStampSlack = TimeSpan.FromSeconds(1);

        public AlarmTracker(int sourceId, string sourceName, string sourceType = "OpcUa")
        {
            _sourceId = sourceId;
            _sourceName = sourceName;
            _sourceType = sourceType;
        }

        // Props the tracker writes itself; any other key of a row came from ExtraProps.
        private static readonly HashSet<string> OwnPropKeys = new(StringComparer.Ordinal)
        {
            "ConditionId", "ConditionName", "BranchId", "SourceName", "Severity", "EventType", "DsName",
            "Active", "Acked", "Retain", "Enabled", "Confirmed", "Suppressed", "OutOfService",
            "SuppressedOrShelved", "ActiveSince", "LimitState"
        };

        internal Func<NodeId, string> LookupClassName { get; set; }
        internal Func<string, string> LookupConditionClassName { get; set; }
        internal Func<NodeId, string> LookupEventTypeName { get; set; }

        public List<EventLogRecord> CopyActiveRecords()
        {
            var nowUtc = DateTime.UtcNow;
            lock (_lock)
            {
                var items = new List<EventLogRecord>();
                foreach (var tracked in _byCorrelation.Values)
                {
                    if (!tracked.IsActive || tracked.Face == null)
                    {
                        continue;
                    }

                    var alarmState = string.IsNullOrEmpty(tracked.AlarmState)
                        ? AlarmStateValues.Incoming
                        : tracked.AlarmState;
                    var eventTimeUtc = tracked.LastSeenUtc == DateTime.MinValue
                        ? nowUtc
                        : tracked.LastSeenUtc;
                    items.Add(BuildRecord(
                        tracked.Face,
                        tracked.CorrelationId,
                        alarmState,
                        tracked.ActiveSinceUtc,
                        eventTimeUtc,
                        DurationSince(tracked.ActiveSinceUtc, nowUtc)));
                }

                return items;
            }
        }

        public AlarmEmit Process(OpcUaAlarmNotification notification)
        {
            if (notification == null
                || (notification.ConditionId == null && string.IsNullOrWhiteSpace(notification.ConditionName)))
            {
                return default;
            }

            ResolvePresentation(notification);
            var raiseClear = IsRaiseClearNotification(notification);
            ApplyRaiseClearState(notification);
            ClearListRemovalAck(notification);

            var alarmState = AlarmStateValues.Resolve(
                notification.Enabled,
                notification.OutOfService,
                notification.Suppressed,
                notification.SuppressedOrShelved,
                notification.Active,
                notification.Acked,
                notification.Confirmed);
            if (string.IsNullOrEmpty(alarmState))
            {
                return default;
            }

            alarmState = AlarmStateValues.AsWinccJournalState(alarmState, notification.Retain, raiseClear);

            var eventTimeUtc = notification.Time.ToUniversalTime();
            var conditionKey = BuildConditionKey(notification);
            var signature = BuildSignature(alarmState, notification);

            lock (_lock)
            {
                NoteRefreshContact(conditionKey);

                if (KeepCurrent(conditionKey, notification, eventTimeUtc, alarmState))
                {
                    return Finish(default);
                }

                if (alarmState == AlarmStateValues.Removed
                    && (_parkedRemoval.ContainsKey(conditionKey) || IsStaleRemoval(conditionKey, eventTimeUtc)))
                {
                    return Finish(default);
                }

                EventLogRecord closed = null;
                ParkedRemoval taken = null;
                if (notification.Active == true)
                {
                    if (_parkedRemoval.Remove(conditionKey, out var parked))
                    {
                        taken = parked;
                        closed = parked.Record;
                    }
                    else if (TryDetachInactive(conditionKey, out var detached))
                    {
                        closed = detached;
                    }
                }

                eventTimeUtc = OccurrenceTimeUtc(notification, conditionKey, eventTimeUtc);

                var activeSinceUtc = ResolveActiveSince(notification, conditionKey, eventTimeUtc, out var correlationId);

                if (!_byCorrelation.TryGetValue(correlationId, out var tracked))
                {
                    tracked = new TrackedOccurrence
                    {
                        CorrelationId = correlationId,
                        ActiveSinceUtc = activeSinceUtc
                    };
                    _byCorrelation[correlationId] = tracked;
                }
                else if (!tracked.ActiveSinceUtc.HasValue && activeSinceUtc.HasValue)
                {
                    tracked.ActiveSinceUtc = activeSinceUtc;
                }
                else
                {
                    activeSinceUtc = tracked.ActiveSinceUtc;
                }

                if (string.Equals(tracked.LastSignature, signature, StringComparison.Ordinal))
                {
                    tracked.LastSeenUtc = eventTimeUtc;
                    // A standing alarm keeps its signature. Class and colors still
                    // arrive on the next notification and must reach the active row.
                    ApplyPresentation(tracked.Face, notification);
                    ApplyPresentation(tracked.InactiveFace, notification);
                    if (taken != null)
                    {
                        _parkedRemoval[conditionKey] = taken;
                    }

                    return Finish(default);
                }

                tracked.LastSignature = signature;
                tracked.LastSeenUtc = eventTimeUtc;
                tracked.AppliedUtc = notification.Time.ToUniversalTime();
                tracked.EventId = notification.EventId;
                tracked.Face = CopyNotification(notification);
                eventTimeUtc = ClearTimeUtc(notification, eventTimeUtc, activeSinceUtc);
                var durationMs = ResolveStandingDuration(notification, eventTimeUtc, activeSinceUtc, tracked);
                tracked.IsActive = notification.Active == true;
                tracked.AlarmState = alarmState;
                if (alarmState == AlarmStateValues.IncomingOutgoing)
                {
                    tracked.InactiveFace = CopyNotification(notification);
                }

                TrimIfNeeded();

                if (notification.Retain == false && notification.Active != true)
                {
                    _byCorrelation.Remove(correlationId);
                    if (_activeCorrelationByCondition.TryGetValue(conditionKey, out var activeId)
                        && string.Equals(activeId, correlationId, StringComparison.Ordinal))
                    {
                        _activeCorrelationByCondition.Remove(conditionKey);
                    }
                }

                var record = BuildRecord(notification, correlationId, alarmState, activeSinceUtc, eventTimeUtc, durationMs);
                if (alarmState == AlarmStateValues.Removed)
                {
                    _parkedRemoval[conditionKey] = new ParkedRemoval
                    {
                        Record = record,
                        EventId = notification.EventId,
                        AppliedUtc = notification.Time.ToUniversalTime(),
                        Face = CopyNotification(notification)
                    };
                    return Finish(default);
                }

                if (closed != null)
                {
                    closed.TimeUtc = record.TimeUtc;
                    return Finish(new AlarmEmit(closed, record));
                }

                return Finish(new AlarmEmit(record, null));
            }
        }

        internal sealed class AlarmSeedNote
        {
            public bool StillMissing { get; init; }
            public bool FilledActiveSince { get; init; }
            public bool FilledDuration { get; init; }
            public string CorrelationId { get; init; } = "";
            public string AlarmState { get; init; } = "";
            public string Message { get; init; } = "";
            public DateTime? ActiveSinceUtc { get; init; }
            public long? DurationMs { get; init; }
        }

        internal sealed class AlarmRefreshReport
        {
            public int Open { get; set; }
            public int Confirmed { get; set; }
            public int Closed { get; set; }
            public int New { get; set; }
            // Closed alarms the tracker still held as active: their going was not received.
            public int Missed { get; set; }
            public bool Applied { get; set; }
            public AlarmRefreshFailure Failure { get; set; }
            public List<EventLogRecord> Closing { get; set; } = [];
        }

        internal enum AlarmRefreshFailure
        {
            None,
            Rejected,
            NoStart,
            NoEnd
        }

        // Part 9 section 4.5. Only an armed refresh may start, so a late
        // RefreshEnd from the previous subscription cannot close the new set.
        public void ResetForNewSubscription()
        {
            lock (_lock)
            {
                _refreshEpoch++;
                _refreshing = false;
                _refreshPending = false;
                _suspect.Clear();
            }
        }

        public bool TryArmRefresh()
        {
            lock (_lock)
            {
                if (_refreshing || _refreshPending)
                {
                    return false;
                }

                _refreshPending = true;
                _armedEpoch = _refreshEpoch;
                _refreshMarkUtc = DateTime.UtcNow;
                return true;
            }
        }

        public void DisarmRefresh()
        {
            lock (_lock)
            {
                if (_refreshing)
                {
                    return;
                }

                _refreshPending = false;
            }
        }

        // Without a completed refresh the client cannot tell which alarms still stand.
        // A server without RefreshStart closes every alarm, a missing RefreshEnd closes
        // the ones the server has not reported yet. A standing alarm returns on its next event.
        public AlarmRefreshReport ExpireRefresh(TimeSpan maxAge)
        {
            lock (_lock)
            {
                if ((!_refreshPending && !_refreshing) || DateTime.UtcNow - _refreshMarkUtc <= maxAge)
                {
                    return new AlarmRefreshReport();
                }

                if (_refreshing && _activeEpoch == _refreshEpoch)
                {
                    return FailRefreshLocked(AlarmRefreshFailure.NoEnd, _suspect.ToList(), DateTime.UtcNow);
                }

                return FailRefreshLocked(AlarmRefreshFailure.NoStart, OpenConditionKeys(), DateTime.UtcNow);
            }
        }

        public AlarmRefreshReport RejectRefresh()
        {
            lock (_lock)
            {
                return FailRefreshLocked(AlarmRefreshFailure.Rejected, OpenConditionKeys(), DateTime.UtcNow);
            }
        }

        private List<string> OpenConditionKeys()
        {
            return _activeCorrelationByCondition.Keys
                .Concat(_parkedRemoval.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private AlarmRefreshReport FailRefreshLocked(AlarmRefreshFailure failure, List<string> conditionKeys, DateTime failedUtc)
        {
            _refreshEpoch++;
            _refreshing = false;
            _refreshPending = false;
            _suspect.Clear();

            var closing = new List<EventLogRecord>();
            foreach (var conditionKey in conditionKeys)
            {
                if (TryCloseMissing(conditionKey, failedUtc, out var removed, out _) && removed != null)
                {
                    closing.Add(removed);
                }
            }

            return new AlarmRefreshReport
            {
                Open = conditionKeys.Count,
                Closed = closing.Count,
                Closing = closing,
                Failure = failure,
                Applied = true
            };
        }

        public bool BeginRefresh()
        {
            lock (_lock)
            {
                if (_refreshing || !_refreshPending || _armedEpoch != _refreshEpoch)
                {
                    return false;
                }

                _refreshing = true;
                _refreshPending = false;
                _activeEpoch = _refreshEpoch;
                _refreshMarkUtc = DateTime.UtcNow;
                _suspect.Clear();
                foreach (var conditionKey in _activeCorrelationByCondition.Keys)
                {
                    _suspect.Add(conditionKey);
                }

                foreach (var conditionKey in _parkedRemoval.Keys)
                {
                    _suspect.Add(conditionKey);
                }

                _refreshBaseline.Clear();
                foreach (var conditionKey in _suspect)
                {
                    _refreshBaseline.Add(conditionKey);
                }

                _refreshNewKeys.Clear();
                _refreshOpen = _suspect.Count;
                _refreshConfirmed = 0;
                return true;
            }
        }

        public AlarmRefreshReport EndRefresh(DateTime refreshUtc)
        {
            if (refreshUtc == DateTime.MinValue)
            {
                refreshUtc = DateTime.UtcNow;
            }

            var report = new AlarmRefreshReport();
            lock (_lock)
            {
                if (!_refreshing || _activeEpoch != _refreshEpoch)
                {
                    return report;
                }

                var closing = new List<EventLogRecord>();
                foreach (var conditionKey in _suspect)
                {
                    if (TryCloseMissing(conditionKey, refreshUtc, out var removed, out var wasActive) && removed != null)
                    {
                        closing.Add(removed);
                    }
                    if (wasActive)
                    {
                        report.Missed++;
                    }
                }

                report.Open = _refreshOpen;
                report.Confirmed = _refreshConfirmed;
                report.Closed = _suspect.Count;
                report.New = _refreshNewKeys.Count;
                report.Closing = closing;
                report.Applied = true;
                _suspect.Clear();
                _refreshing = false;
            }

            return report;
        }

        public bool RefreshBusy
        {
            get
            {
                lock (_lock)
                {
                    return _refreshing || _refreshPending;
                }
            }
        }

        public bool ArchiveSeeded
        {
            get
            {
                lock (_lock)
                {
                    return _archiveSeeded;
                }
            }
        }

        // Live notifications are newer than any archive row, so a seed must not replace them.
        public bool HasLiveState
        {
            get
            {
                lock (_lock)
                {
                    return _byCorrelation.Count > 0 || _parkedRemoval.Count > 0;
                }
            }
        }

        public int SeedOpenAlarms(IReadOnlyList<EventLogRecord> rows, ICollection<EventLogRecord> superseded)
        {
            lock (_lock)
            {
                _archiveSeeded = true;
                if (rows == null || rows.Count == 0 || _byCorrelation.Count > 0 || _parkedRemoval.Count > 0)
                {
                    return _activeCorrelationByCondition.Count;
                }

                var seeded = 0;
                foreach (var row in rows.OrderBy(item => item.TimeUtc))
                {
                    if (TrySeedRow(row, out var closed))
                    {
                        seeded++;
                    }

                    if (closed != null)
                    {
                        superseded?.Add(closed);
                    }
                }

                return seeded;
            }
        }

        internal static bool NeedsOccurrenceBackfill(EventLogRecord row)
        {
            if (row == null || string.IsNullOrEmpty(row.CorrelationId))
            {
                return false;
            }

            if (!row.ActiveSinceUtc.HasValue)
            {
                return true;
            }

            return row.AlarmState == AlarmStateValues.IncomingOutgoing
                && PositiveDuration(row.DurationMs) == null;
        }

        // The newest point keeps the state. Raise time and duration come from the
        // newest earlier point that still has them. Duration is clear minus raise.
        internal static List<AlarmSeedNote> CompleteOpenRows(
            IReadOnlyList<EventLogRecord> open,
            IReadOnlyList<EventLogRecord> history)
        {
            var notes = new List<AlarmSeedNote>();
            if (open == null || open.Count == 0)
            {
                return notes;
            }

            var byCorrelation = new Dictionary<string, List<EventLogRecord>>(StringComparer.Ordinal);
            if (history != null)
            {
                foreach (var row in history)
                {
                    if (row == null || string.IsNullOrEmpty(row.CorrelationId))
                    {
                        continue;
                    }

                    if (!byCorrelation.TryGetValue(row.CorrelationId, out var list))
                    {
                        list = [];
                        byCorrelation[row.CorrelationId] = list;
                    }

                    list.Add(row);
                }
            }

            foreach (var list in byCorrelation.Values)
            {
                list.Sort(static (left, right) => right.TimeUtc.CompareTo(left.TimeUtc));
            }

            foreach (var row in open)
            {
                if (!NeedsOccurrenceBackfill(row))
                {
                    continue;
                }

                var filledSince = false;
                var filledDuration = false;
                if (byCorrelation.TryGetValue(row.CorrelationId, out var older))
                {
                    if (!row.ActiveSinceUtc.HasValue)
                    {
                        foreach (var candidate in older)
                        {
                            if (!candidate.ActiveSinceUtc.HasValue)
                            {
                                continue;
                            }

                            row.ActiveSinceUtc = candidate.ActiveSinceUtc;
                            filledSince = true;
                            break;
                        }
                    }

                    if (row.AlarmState == AlarmStateValues.IncomingOutgoing
                        && PositiveDuration(row.DurationMs) == null)
                    {
                        foreach (var candidate in older)
                        {
                            var durationMs = PositiveDuration(candidate.DurationMs);
                            if (durationMs == null)
                            {
                                continue;
                            }

                            row.DurationMs = durationMs;
                            filledDuration = true;
                            break;
                        }

                        if (!filledDuration && row.ActiveSinceUtc.HasValue)
                        {
                            var computed = MillisBetween(row.ActiveSinceUtc, row.TimeUtc);
                            if (computed is > 0)
                            {
                                row.DurationMs = computed;
                                filledDuration = true;
                            }
                        }
                    }
                }

                var stillMissing = NeedsOccurrenceBackfill(row);
                if (!filledSince && !filledDuration && !stillMissing)
                {
                    continue;
                }

                notes.Add(new AlarmSeedNote
                {
                    StillMissing = stillMissing,
                    FilledActiveSince = filledSince,
                    FilledDuration = filledDuration,
                    CorrelationId = row.CorrelationId,
                    AlarmState = row.AlarmState ?? "",
                    Message = row.Message ?? "",
                    ActiveSinceUtc = row.ActiveSinceUtc,
                    DurationMs = row.DurationMs
                });
            }

            return notes;
        }

        // WinCC writes Removed on the next Incoming of the same alarm.
        // Duration stays the Incoming/Outgoing clear span. The OPC Retain=false
        // event often arrives one batch earlier; its Time is not the journal time.
        private bool IsStaleRemoval(string conditionKey, DateTime removalUtc)
        {
            if (!_activeCorrelationByCondition.TryGetValue(conditionKey, out var correlationId)
                || !_byCorrelation.TryGetValue(correlationId, out var tracked)
                || !tracked.IsActive
                || !tracked.ActiveSinceUtc.HasValue)
            {
                return false;
            }

            return removalUtc <= tracked.ActiveSinceUtc.Value;
        }

        private AlarmEmit Finish(AlarmEmit emit)
        {
            return emit;
        }

        private void NoteRefreshContact(string conditionKey)
        {
            if (!_refreshing || _activeEpoch != _refreshEpoch)
            {
                return;
            }

            if (_suspect.Remove(conditionKey))
            {
                _refreshConfirmed++;
                return;
            }

            if (!_refreshBaseline.Contains(conditionKey))
            {
                _refreshNewKeys.Add(conditionKey);
            }
        }

        // Siemens keeps one EventId for the alarm object and repeats it on ConditionRefresh.
        // The same id is a duplicate only while Active, Acked, Retain and the journal state
        // are unchanged. A repeated id that clears or acknowledges the alarm is applied.
        private bool KeepCurrent(
            string conditionKey,
            OpcUaAlarmNotification notification,
            DateTime eventTimeUtc,
            string alarmState)
        {
            if (_parkedRemoval.TryGetValue(conditionKey, out var parked))
            {
                if (SameSnapshot(parked.EventId, parked.Face, parked.Record?.AlarmState, notification, alarmState))
                {
                    return true;
                }

                if (_refreshing
                    && eventTimeUtc <= parked.AppliedUtc
                    && SameInterest(parked.Face, parked.Record?.AlarmState, notification, alarmState))
                {
                    return true;
                }
            }

            if (_activeCorrelationByCondition.TryGetValue(conditionKey, out var correlationId)
                && _byCorrelation.TryGetValue(correlationId, out var tracked))
            {
                if (SameSnapshot(tracked.EventId, tracked.Face, tracked.AlarmState, notification, alarmState))
                {
                    return true;
                }

                if (_refreshing
                    && tracked.AppliedUtc != DateTime.MinValue
                    && eventTimeUtc < tracked.AppliedUtc
                    && SameInterest(tracked.Face, tracked.AlarmState, notification, alarmState))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameSnapshot(
            byte[] eventId,
            OpcUaAlarmNotification face,
            string alarmState,
            OpcUaAlarmNotification notification,
            string nextState)
        {
            return SameEventId(eventId, notification.EventId)
                && SameInterest(face, alarmState, notification, nextState);
        }

        private static bool SameInterest(
            OpcUaAlarmNotification face,
            string alarmState,
            OpcUaAlarmNotification notification,
            string nextState)
        {
            if (face == null)
            {
                return false;
            }

            return face.Active == notification.Active
                && face.Acked == notification.Acked
                && face.Retain == notification.Retain
                && string.Equals(alarmState ?? "", nextState ?? "", StringComparison.Ordinal);
        }

        private bool TryCloseMissing(string conditionKey, DateTime refreshUtc, out EventLogRecord removed, out bool wasActive)
        {
            removed = null;
            wasActive = false;
            if (_parkedRemoval.Remove(conditionKey, out var parked))
            {
                DropCondition(conditionKey);
                removed = parked.Record;
                return removed != null;
            }

            if (!_activeCorrelationByCondition.TryGetValue(conditionKey, out var correlationId)
                || !_byCorrelation.TryGetValue(correlationId, out var tracked))
            {
                _activeCorrelationByCondition.Remove(conditionKey);
                return false;
            }

            wasActive = tracked.IsActive;
            var face = tracked.Face ?? tracked.InactiveFace;
            _byCorrelation.Remove(correlationId);
            _activeCorrelationByCondition.Remove(conditionKey);
            if (face == null)
            {
                return false;
            }

            face = CopyNotification(face);
            face.Active = false;
            face.Retain = false;
            face.Acked = false;
            removed = BuildRecord(
                face,
                correlationId,
                AlarmStateValues.Removed,
                tracked.ActiveSinceUtc,
                refreshUtc,
                tracked.StandingDurationMs);
            return true;
        }

        private void DropCondition(string conditionKey)
        {
            if (!_activeCorrelationByCondition.TryGetValue(conditionKey, out var correlationId))
            {
                return;
            }

            _activeCorrelationByCondition.Remove(conditionKey);
            _byCorrelation.Remove(correlationId);
        }

        private static bool SameEventId(byte[] left, byte[] right)
        {
            return left != null
                && right != null
                && left.Length > 0
                && left.AsSpan().SequenceEqual(right);
        }

        private bool TrySeedRow(EventLogRecord row, out EventLogRecord superseded)
        {
            superseded = null;
            if (row == null
                || (row.AlarmState != AlarmStateValues.Incoming
                    && row.AlarmState != AlarmStateValues.IncomingOutgoing))
            {
                return false;
            }

            var face = FaceFromProps(row);
            if (face == null)
            {
                return false;
            }

            var conditionKey = BuildConditionKey(face);
            var correlationId = string.IsNullOrEmpty(row.CorrelationId)
                ? BuildCorrelationId(_sourceId, conditionKey, row.ActiveSinceUtc ?? DateTime.MinValue)
                : row.CorrelationId;
            if (_activeCorrelationByCondition.TryGetValue(conditionKey, out var existingId)
                && _byCorrelation.TryGetValue(existingId, out var existing))
            {
                if (string.Equals(existing.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (row.TimeUtc > existing.LastSeenUtc)
                {
                    superseded = RemovalOf(
                        existing.Face,
                        existing.CorrelationId,
                        existing.ActiveSinceUtc,
                        row.TimeUtc,
                        existing.StandingDurationMs);
                    _byCorrelation.Remove(existingId);
                    _activeCorrelationByCondition.Remove(conditionKey);
                }
                else
                {
                    superseded = RemovalOf(face, correlationId, row.ActiveSinceUtc, existing.LastSeenUtc, row.DurationMs);
                    return false;
                }
            }

            var active = row.AlarmState == AlarmStateValues.Incoming;
            face.Active = active;
            face.Retain = true;
            var tracked = new TrackedOccurrence
            {
                CorrelationId = correlationId,
                ActiveSinceUtc = row.ActiveSinceUtc,
                StandingDurationMs = PositiveDuration(row.DurationMs),
                LastSignature = BuildSignature(row.AlarmState, face),
                LastSeenUtc = row.TimeUtc,
                AppliedUtc = row.TimeUtc,
                IsActive = active,
                AlarmState = row.AlarmState ?? "",
                Face = face,
                InactiveFace = active ? null : CopyNotification(face)
            };
            _byCorrelation[correlationId] = tracked;
            _activeCorrelationByCondition[conditionKey] = correlationId;
            return true;
        }

        private EventLogRecord RemovalOf(
            OpcUaAlarmNotification face,
            string correlationId,
            DateTime? activeSinceUtc,
            DateTime removedUtc,
            long? durationMs)
        {
            if (face == null || string.IsNullOrEmpty(correlationId) || removedUtc == DateTime.MinValue)
            {
                return null;
            }

            face = CopyNotification(face);
            face.Active = false;
            face.Retain = false;
            face.Acked = false;
            return BuildRecord(
                face,
                correlationId,
                AlarmStateValues.Removed,
                activeSinceUtc,
                removedUtc,
                PositiveDuration(durationMs));
        }

        private static long? PositiveDuration(long? durationMs)
        {
            return durationMs is > 0 ? durationMs : null;
        }

        private static long? DurationSince(DateTime? activeSinceUtc, DateTime nowUtc)
        {
            if (!activeSinceUtc.HasValue)
            {
                return null;
            }

            var sinceUtc = activeSinceUtc.Value.ToUniversalTime();
            if (sinceUtc >= nowUtc)
            {
                return 0;
            }

            var milliseconds = (nowUtc - sinceUtc).TotalMilliseconds;
            if (milliseconds >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)milliseconds;
        }

        private static string BuildSignature(string alarmState, OpcUaAlarmNotification notification)
        {
            return string.Join(
                "\n",
                alarmState,
                notification.Severity.ToString(CultureInfo.InvariantCulture),
                notification.LimitState ?? "",
                RetainToken(notification.Retain));
        }

        private static OpcUaAlarmNotification FaceFromProps(EventLogRecord row)
        {
            if (string.IsNullOrWhiteSpace(row.Props))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(row.Props);
                var root = document.RootElement;
                var face = new OpcUaAlarmNotification
                {
                    ConditionId = ParseNode(ReadProp(root, "ConditionId")),
                    BranchId = ParseNode(ReadProp(root, "BranchId")),
                    ConditionName = ReadProp(root, "ConditionName"),
                    SourceName = ReadProp(root, "SourceName"),
                    Message = row.Message ?? "",
                    Severity = (ushort)Math.Clamp(row.Severity ?? 0, 0, ushort.MaxValue),
                    LimitState = ReadProp(root, "LimitState"),
                    Retain = row.Retain,
                    Active = row.Active,
                    Acked = row.Acked,
                    EventType = ParseNode(row.EventType),
                    EventTypeName = row.EventTypeName ?? "",
                    AlarmClassSymbol = row.AlarmClassSymbol ?? "",
                    AlarmClassName = row.AlarmClassName ?? "",
                    TextColor = row.TextColor,
                    BackColor = row.BackColor,
                    Flashing = row.Flashing
                };
                if (face.ConditionId == null && string.IsNullOrWhiteSpace(face.ConditionName))
                {
                    return null;
                }

                foreach (var prop in root.EnumerateObject())
                {
                    if (!OwnPropKeys.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        face.ExtraProps ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        face.ExtraProps[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                return face;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ReadProp(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return "";
            }

            return value.GetString() ?? "";
        }

        private static NodeId ParseNode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return NodeId.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private bool TryDetachInactive(string conditionKey, out EventLogRecord removed)
        {
            removed = null;
            if (!_activeCorrelationByCondition.TryGetValue(conditionKey, out var correlationId)
                || !_byCorrelation.TryGetValue(correlationId, out var tracked)
                || tracked.IsActive
                || tracked.InactiveFace == null)
            {
                return false;
            }

            _byCorrelation.Remove(correlationId);
            _activeCorrelationByCondition.Remove(conditionKey);
            var face = tracked.InactiveFace;
            face.Active = false;
            face.Retain = false;
            face.Acked = false;
            removed = BuildRecord(
                face,
                correlationId,
                AlarmStateValues.Removed,
                tracked.ActiveSinceUtc,
                tracked.LastSeenUtc,
                tracked.StandingDurationMs);
            return true;
        }

        private static void ApplyPresentation(OpcUaAlarmNotification face, OpcUaAlarmNotification notification)
        {
            if (face == null || notification == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(notification.EventTypeName))
            {
                face.EventTypeName = notification.EventTypeName;
            }

            if (!string.IsNullOrEmpty(notification.AlarmClassSymbol))
            {
                face.AlarmClassSymbol = notification.AlarmClassSymbol;
            }

            if (!string.IsNullOrEmpty(notification.AlarmClassName))
            {
                face.AlarmClassName = notification.AlarmClassName;
            }

            if (notification.AlarmClassId != null)
            {
                face.AlarmClassId = notification.AlarmClassId;
            }

            if (notification.TextColor.HasValue)
            {
                face.TextColor = notification.TextColor;
            }

            if (notification.BackColor.HasValue)
            {
                face.BackColor = notification.BackColor;
            }

            if (notification.Flashing.HasValue)
            {
                face.Flashing = notification.Flashing;
            }
        }

        private static OpcUaAlarmNotification CopyNotification(OpcUaAlarmNotification notification)
        {
            return new OpcUaAlarmNotification
            {
                EventId = notification.EventId,
                EventType = notification.EventType,
                EventTypeName = notification.EventTypeName,
                AlarmClassSymbol = notification.AlarmClassSymbol,
                AlarmClassName = notification.AlarmClassName,
                AlarmClassId = notification.AlarmClassId,
                TextColor = notification.TextColor,
                BackColor = notification.BackColor,
                Flashing = notification.Flashing,
                Time = notification.Time,
                ReceiveTime = notification.ReceiveTime,
                ObservedUtc = notification.ObservedUtc,
                Message = notification.Message,
                Severity = notification.Severity,
                ConditionName = notification.ConditionName,
                ConditionId = notification.ConditionId,
                BranchId = notification.BranchId,
                Retain = notification.Retain,
                Active = notification.Active,
                Acked = notification.Acked,
                Enabled = notification.Enabled,
                Confirmed = notification.Confirmed,
                Suppressed = notification.Suppressed,
                OutOfService = notification.OutOfService,
                SuppressedOrShelved = notification.SuppressedOrShelved,
                LimitState = notification.LimitState,
                ActiveSince = notification.ActiveSince,
                SourceName = notification.SourceName,
                ExtraProps = notification.ExtraProps
            };
        }

        private static bool IsRaiseClearNotification(OpcUaAlarmNotification notification)
        {
            return !notification.Active.HasValue
                && !notification.Acked.HasValue
                && notification.Retain.HasValue;
        }

        // RaiseClear (HmiConditionType) has Retain and no ActiveState or AckedState.
        // Retain marks the raised state. Clearing it is not an acknowledgement.
        private static void ApplyRaiseClearState(OpcUaAlarmNotification notification)
        {
            if (notification.Active.HasValue || notification.Acked.HasValue || !notification.Retain.HasValue)
            {
                return;
            }

            notification.Active = notification.Retain.Value;
            notification.Acked = false;
        }

        // WinCC sets AckedState when the condition leaves the list.
        // Retain=false is removal. The journal acknowledgement time stays empty.
        private static void ClearListRemovalAck(OpcUaAlarmNotification notification)
        {
            if (notification.Retain == false && notification.Active != true && notification.Acked == true)
            {
                notification.Acked = false;
            }
        }

        private static string RetainToken(bool? retain)
        {
            if (retain == true)
            {
                return "1";
            }

            if (retain == false)
            {
                return "0";
            }

            return "";
        }

        private DateTime OccurrenceTimeUtc(OpcUaAlarmNotification notification, string conditionKey, DateTime eventTimeUtc)
        {
            if (notification.ActiveSince == DateTime.MinValue || !IsFirstSighting(conditionKey))
            {
                return eventTimeUtc;
            }

            var enteredUtc = notification.ActiveSince.ToUniversalTime();
            if (enteredUtc < eventTimeUtc - RefreshStampSlack)
            {
                return enteredUtc;
            }

            return eventTimeUtc;
        }

        private bool IsFirstSighting(string conditionKey)
        {
            if (_activeCorrelationByCondition.ContainsKey(conditionKey))
            {
                return false;
            }

            var unknownId = BuildCorrelationId(_sourceId, conditionKey, DateTime.MinValue);
            return !_byCorrelation.ContainsKey(unknownId);
        }

        private DateTime? ResolveActiveSince(
            OpcUaAlarmNotification notification,
            string conditionKey,
            DateTime eventTimeUtc,
            out string correlationId)
        {
            if (notification.Active == true)
            {
                if (_activeCorrelationByCondition.TryGetValue(conditionKey, out var existingId)
                    && _byCorrelation.TryGetValue(existingId, out var existing))
                {
                    correlationId = existingId;
                    return existing.ActiveSinceUtc ?? FirstActiveSince(notification, eventTimeUtc);
                }

                var since = FirstActiveSince(notification, eventTimeUtc);
                correlationId = BuildCorrelationId(_sourceId, conditionKey, since);
                _activeCorrelationByCondition[conditionKey] = correlationId;
                return since;
            }

            if (_activeCorrelationByCondition.TryGetValue(conditionKey, out var activeId))
            {
                if (_byCorrelation.TryGetValue(activeId, out var tracked))
                {
                    correlationId = activeId;
                    return tracked.ActiveSinceUtc;
                }

                _activeCorrelationByCondition.Remove(conditionKey);
            }

            correlationId = BuildCorrelationId(_sourceId, conditionKey, DateTime.MinValue);
            return null;
        }

        private static DateTime FirstActiveSince(OpcUaAlarmNotification notification, DateTime eventTimeUtc)
        {
            if (notification.Active == true && notification.ActiveSince != DateTime.MinValue)
            {
                return notification.ActiveSince.ToUniversalTime();
            }

            return eventTimeUtc;
        }

        private EventLogRecord BuildRecord(
            OpcUaAlarmNotification notification,
            string correlationId,
            string alarmState,
            DateTime? activeSinceUtc,
            DateTime eventTimeUtc,
            long? durationMs)
        {
            var message = BuildMessage(notification);
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConditionId"] = notification.ConditionId?.ToString() ?? "",
                ["ConditionName"] = Truncate(notification.ConditionName, 128),
                ["BranchId"] = notification.BranchId?.ToString() ?? "",
                ["SourceName"] = Truncate(notification.SourceName, 128),
                ["Severity"] = notification.Severity.ToString(CultureInfo.InvariantCulture),
                ["EventType"] = notification.EventType?.ToString() ?? "",
                ["DsName"] = Truncate(_sourceName, 128)
            };
            AddBool(props, "Active", notification.Active);
            AddBool(props, "Acked", notification.Acked);
            AddBool(props, "Retain", notification.Retain);
            AddBool(props, "Enabled", notification.Enabled);
            AddBool(props, "Confirmed", notification.Confirmed);
            AddBool(props, "Suppressed", notification.Suppressed);
            AddBool(props, "OutOfService", notification.OutOfService);
            AddBool(props, "SuppressedOrShelved", notification.SuppressedOrShelved);
            if (activeSinceUtc.HasValue)
            {
                props["ActiveSince"] = activeSinceUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(notification.LimitState))
            {
                props["LimitState"] = Truncate(notification.LimitState, 128);
            }

            if (notification.ExtraProps != null)
            {
                foreach (var extra in notification.ExtraProps)
                {
                    if (!OwnPropKeys.Contains(extra.Key))
                    {
                        props[extra.Key] = Truncate(extra.Value ?? "", 512);
                    }
                }
            }

            ResolvePresentation(notification);
            return new EventLogRecord
            {
                TimeUtc = eventTimeUtc,
                Measurement = EventLogConstants.ProcessMeasurementName,
                Level = SeverityToLevel(notification.Severity),
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.AlarmComponent,
                SourceId = _sourceId,
                SourceType = _sourceType,
                Message = message,
                Props = Truncate(JsonSerializer.Serialize(props), EventLogConstants.MaxPropsJsonChars),
                CorrelationId = correlationId,
                AlarmState = alarmState,
                Severity = notification.Severity,
                Active = notification.Active,
                Acked = notification.Acked,
                Retain = notification.Retain,
                ActiveSinceUtc = activeSinceUtc,
                EventType = notification.EventType?.ToString() ?? "",
                EventTypeName = Truncate(notification.EventTypeName, 128),
                AlarmClassSymbol = Truncate(notification.AlarmClassSymbol, 128),
                AlarmClassName = Truncate(notification.AlarmClassName, 128),
                TextColor = notification.TextColor,
                BackColor = notification.BackColor,
                Flashing = notification.Flashing,
                DurationMs = durationMs
            };
        }

        private void ResolvePresentation(OpcUaAlarmNotification notification)
        {
            if (notification == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(notification.EventTypeName))
            {
                var typeName = LookupEventTypeName?.Invoke(notification.EventType);
                if (!string.IsNullOrEmpty(typeName))
                {
                    notification.EventTypeName = typeName;
                }
            }

            if (string.IsNullOrEmpty(notification.AlarmClassName) && notification.AlarmClassId != null)
            {
                var className = LookupClassName?.Invoke(notification.AlarmClassId);
                if (!string.IsNullOrEmpty(className))
                {
                    notification.AlarmClassName = className;
                }
            }

            if (string.IsNullOrEmpty(notification.AlarmClassName))
            {
                var className = LookupConditionClassName?.Invoke(notification.ConditionName);
                if (!string.IsNullOrEmpty(className))
                {
                    notification.AlarmClassName = className;
                }
            }
        }

        private static void AddBool(Dictionary<string, string> props, string name, bool? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            props[name] = value.Value ? "true" : "false";
        }

        // Part 9: ActiveState/TransitionTime is when the current state was entered.
        // Part 5: Time is when this event occurred. One batch can share a Time
        // while each state entry has its own TransitionTime. The later one is the entry.
        // RaiseClear omits TransitionTime and repeats the raise Time. ReceiveTime,
        // then client arrival, apply only to that Retain=false transition.
        private static DateTime ClearTimeUtc(
            OpcUaAlarmNotification notification,
            DateTime eventTimeUtc,
            DateTime? raiseUtc)
        {
            if (notification.Active != false)
            {
                return eventTimeUtc;
            }

            var opcTimeUtc = notification.Time == DateTime.MinValue
                ? eventTimeUtc
                : notification.Time.ToUniversalTime();

            if (notification.ActiveSince != DateTime.MinValue)
            {
                var transitionUtc = notification.ActiveSince.ToUniversalTime();
                if (IsAfterInstant(transitionUtc, opcTimeUtc))
                {
                    return transitionUtc;
                }

                if (IsAfterInstant(opcTimeUtc, transitionUtc))
                {
                    return opcTimeUtc;
                }
            }

            if (IsAfterRaise(opcTimeUtc, raiseUtc))
            {
                return opcTimeUtc;
            }

            if (notification.Retain != false || !raiseUtc.HasValue)
            {
                return eventTimeUtc;
            }

            if (IsAfterRaise(notification.ReceiveTime, raiseUtc))
            {
                return notification.ReceiveTime.ToUniversalTime();
            }

            if (IsAfterRaise(notification.ObservedUtc, raiseUtc))
            {
                return notification.ObservedUtc;
            }

            return eventTimeUtc;
        }

        private static bool IsAfterInstant(DateTime laterUtc, DateTime earlierUtc)
        {
            if (laterUtc == DateTime.MinValue || earlierUtc == DateTime.MinValue)
            {
                return false;
            }

            var millis = (laterUtc - earlierUtc).TotalMilliseconds;
            return !double.IsNaN(millis) && millis >= 1;
        }

        private static bool IsAfterRaise(DateTime instantUtc, DateTime? raiseUtc)
        {
            if (instantUtc == DateTime.MinValue || !raiseUtc.HasValue)
            {
                return false;
            }

            var millis = (instantUtc - raiseUtc.Value).TotalMilliseconds;
            return !double.IsNaN(millis) && millis >= 1;
        }

        // WinCC Duration is clear time minus raise time, on the clear row and the later removal row.
        // ActiveState/TransitionTime is that clear instant. Event time can sit on a later tick.
        private static long? ResolveStandingDuration(
            OpcUaAlarmNotification notification,
            DateTime eventTimeUtc,
            DateTime? raiseUtc,
            TrackedOccurrence tracked)
        {
            if (notification.Active == true)
            {
                tracked.StandingDurationMs = null;
                return 0;
            }

            if (notification.Active != false)
            {
                return null;
            }

            if (tracked.StandingDurationMs.HasValue)
            {
                return tracked.StandingDurationMs;
            }

            var endUtc = eventTimeUtc;
            if (notification.ActiveSince != DateTime.MinValue)
            {
                var transitionUtc = notification.ActiveSince.ToUniversalTime();
                if (!raiseUtc.HasValue || transitionUtc > raiseUtc.Value)
                {
                    endUtc = transitionUtc;
                }
            }

            var durationMs = MillisBetween(raiseUtc, endUtc);
            if (durationMs.HasValue)
            {
                tracked.StandingDurationMs = durationMs;
            }

            return durationMs;
        }

        private static long? MillisBetween(DateTime? raiseUtc, DateTime endUtc)
        {
            if (!raiseUtc.HasValue)
            {
                return null;
            }

            var millis = (endUtc - raiseUtc.Value).TotalMilliseconds;
            if (double.IsNaN(millis) || millis < 1)
            {
                return null;
            }

            if (millis >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Round(millis, MidpointRounding.AwayFromZero);
        }

        // WinCC LoggedAlarm.EventText is the journal message. The alarm name
        // stays in ConditionName. Older points stored "Name: text"; strip that once.
        private static string BuildMessage(OpcUaAlarmNotification notification)
        {
            var name = string.IsNullOrWhiteSpace(notification.ConditionName)
                ? notification.SourceName
                : notification.ConditionName;
            var text = notification.Message?.Trim() ?? "";
            var prefix = string.IsNullOrWhiteSpace(name) ? "" : name.Trim() + ": ";
            if (prefix.Length > 0 && text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text.Substring(prefix.Length).Trim();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return Truncate(name ?? "", EventLogConstants.MaxMessageChars);
            }

            return Truncate(text, EventLogConstants.MaxMessageChars);
        }

        private static string BuildConditionKey(OpcUaAlarmNotification notification)
        {
            var identity = notification.ConditionId != null
                ? notification.ConditionId.ToString()
                : $"name:{notification.SourceName}|{notification.ConditionName}";
            return $"{identity}|{notification.BranchId}";
        }

        public static string BuildCorrelationId(int sourceId, string conditionKey, DateTime activeSinceUtc)
        {
            var key = $"{sourceId}|{conditionKey}|{activeSinceUtc:O}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string SeverityToLevel(ushort severity)
        {
            if (severity >= 700)
            {
                return "Error";
            }

            if (severity >= 400)
            {
                return "Warning";
            }

            return "Information";
        }

        private void TrimIfNeeded()
        {
            if (_byCorrelation.Count > EventLogConstants.MaxTrackedConditions)
            {
                TrimTracked();
            }

            if (_parkedRemoval.Count <= EventLogConstants.MaxTrackedConditions)
            {
                return;
            }

            var dropParked = _parkedRemoval
                .OrderBy(pair => pair.Value.AppliedUtc)
                .Take(_parkedRemoval.Count - EventLogConstants.MaxTrackedConditions)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var conditionKey in dropParked)
            {
                _parkedRemoval.Remove(conditionKey);
                _suspect.Remove(conditionKey);
                _refreshBaseline.Remove(conditionKey);
                _refreshNewKeys.Remove(conditionKey);
            }
        }

        private void TrimTracked()
        {
            var remove = _byCorrelation
                .OrderBy(pair => pair.Value.LastSeenUtc)
                .Take(_byCorrelation.Count - EventLogConstants.MaxTrackedConditions)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in remove)
            {
                _byCorrelation.Remove(key);
            }

            var removedIds = new HashSet<string>(remove, StringComparer.Ordinal);
            var staleConditionKeys = _activeCorrelationByCondition
                .Where(pair => removedIds.Contains(pair.Value))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var conditionKey in staleConditionKeys)
            {
                ForgetCondition(conditionKey);
            }
        }

        private void ForgetCondition(string conditionKey)
        {
            _activeCorrelationByCondition.Remove(conditionKey);
            _parkedRemoval.Remove(conditionKey);
            _suspect.Remove(conditionKey);
            _refreshBaseline.Remove(conditionKey);
            _refreshNewKeys.Remove(conditionKey);
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? "";
            }

            return value.Substring(0, maxChars);
        }

    }
}
