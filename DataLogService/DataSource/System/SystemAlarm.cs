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
using System.Text.Json;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using Serilog;

namespace DataLogService.DataSource.System
{
    /// <summary>
    /// Watches the connection state of every other data source and reports a source in state
    /// Error as a ProcessEvent alarm of the SystemAlarm class. One occurrence is raised per
    /// error and cleared as soon as the source recovers, is deactivated, or disappears.
    /// A second alarm per source reports a device clock off by more than the accepted difference
    /// (<see cref="SystemClockAlarmRules"/>), as long as the source watches its clock.
    /// </summary>
    public sealed class SystemAlarm : IDisposable
    {
        #region private const
        private const string ConditionKey = "SystemConnectionError";
        private const string RaiseTemplate = "Data source connection error";
        private const string ClearTemplate = "Data source connection error cleared";
        private const string RaiseLevel = "Error";
        private const string ClearLevel = "Information";
        private const string RemovedReason = "removed or deactivated";
        private const string ClockConditionKey = "SystemClockDeviation";
        private const string ClockRaiseTemplate = "Data source clock deviation";
        private const string ClockClearTemplate = "Data source clock deviation cleared";
        private const string ClockRaiseLevel = "Warning";
        private const string NotWatchedReason = "no longer watching its clock";
        private const int TextLimit = 256;
        #endregion

        #region private fields
        private static readonly Func<DateTime> SystemUtcNow = () => DateTime.UtcNow;

        private readonly int _ownDataSourceId;
        private readonly Func<IReadOnlyList<DataSourceInfoModel>> _dataSourcesProvider;
        private readonly Action<EventLogRecord> _publish;
        private readonly Func<DateTime> _utcNow;
        private readonly Dictionary<int, OpenConnectionAlarm> _openAlarms = [];
        private readonly Dictionary<int, OpenConnectionAlarm> _openClockAlarms = [];
        // Since when the clock of a source is off without an open alarm yet.
        private readonly Dictionary<int, DateTime> _clockOffSinceUtc = [];
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _stopEvent = new(false);
        private Thread _worker;
        #endregion

        #region construction
        public SystemAlarm(
            int ownDataSourceId,
            Func<IReadOnlyList<DataSourceInfoModel>> dataSourcesProvider,
            Action<EventLogRecord> publish,
            Func<DateTime> utcNow = null)
        {
            _ownDataSourceId = ownDataSourceId;
            _dataSourcesProvider = dataSourcesProvider;
            _publish = publish;
            _utcNow = utcNow ?? SystemUtcNow;
        }

        public void Dispose()
        {
            _stopEvent.Set();
            _worker?.Join(TimeSpan.FromSeconds(5));
            _worker = null;

            // Closing open occurrences stops a recreated system data source from raising the
            // same connection error again under a new correlation id.
            lock (_lock)
            {
                var nowUtc = _utcNow();
                foreach (var sourceId in _openAlarms.Keys.ToList())
                {
                    Clear(sourceId, nowUtc, null, RemovedReason);
                }
                foreach (var sourceId in _openClockAlarms.Keys.ToList())
                {
                    ClearClock(sourceId, nowUtc, null, RemovedReason);
                }
            }
        }
        #endregion

        #region public methods
        public void Start()
        {
            if (_worker != null)
            {
                return;
            }

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SystemAlarm" };
            _worker.Start();
        }

        /// <summary>
        /// Reads the current data source states and runs one alarm cycle.
        /// </summary>
        public void RunOnce()
        {
            Evaluate(_dataSourcesProvider?.Invoke() ?? [], _utcNow());
        }

        /// <summary>
        /// One alarm cycle over the given data source states. Used by the worker and by tests.
        /// </summary>
        public void Evaluate(IReadOnlyList<DataSourceInfoModel> dataSources, DateTime nowUtc)
        {
            var monitored = BuildMonitoredSources(dataSources);

            lock (_lock)
            {
                foreach (var source in monitored.Sources)
                {
                    switch (SystemConnectionAlarmRules.Decide(source.State))
                    {
                        case SystemConnectionAlarmRules.AlarmCycleAction.Raise:
                            Raise(source, nowUtc);
                            break;
                        case SystemConnectionAlarmRules.AlarmCycleAction.Clear:
                            Clear(source.DataSource.Id, nowUtc, source, null);
                            break;
                    }
                    EvaluateClock(source, nowUtc);
                }

                ClearVanishedSources(monitored.SourceIds, nowUtc);
            }
        }

        public List<EventLogRecord> CopyActiveAlarms()
        {
            var nowUtc = _utcNow();
            lock (_lock)
            {
                var records = new List<EventLogRecord>(_openAlarms.Count + _openClockAlarms.Count);
                foreach (var alarm in _openAlarms.Values)
                {
                    records.Add(BuildRaiseRecord(alarm.Source, alarm.CorrelationId, alarm.ActiveSinceUtc, nowUtc));
                }
                foreach (var alarm in _openClockAlarms.Values)
                {
                    records.Add(BuildClockRaiseRecord(alarm.Source, alarm.CorrelationId, alarm.ActiveSinceUtc, nowUtc));
                }

                return records;
            }
        }
        #endregion

        #region private methods
        private void WorkerLoop(object obj)
        {
            Log.Information("Startup SystemAlarm data source connection watch");

            while (!_stopEvent.IsSet)
            {
                try
                {
                    RunOnce();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SystemAlarm: data source connection check failed");
                }

                _stopEvent.Wait(EventLogConstants.SystemAlarmCheckIntervalMs);
            }

            Log.Information("SystemAlarm data source connection watch stopped");
        }

        private void Raise(DataSourceInfoModel source, DateTime nowUtc)
        {
            var sourceId = source.DataSource.Id;
            if (_openAlarms.ContainsKey(sourceId))
            {
                return;
            }

            var alarm = new OpenConnectionAlarm
            {
                Source = source,
                ActiveSinceUtc = nowUtc,
                // Same occurrence id scheme as the OPC UA alarms.
                CorrelationId = AlarmTracker.BuildCorrelationId(sourceId, ConditionKey, nowUtc)
            };
            _openAlarms[sourceId] = alarm;

            Log.Warning("SystemAlarm: {DsName} Id: {DsId} raised a connection alarm", source.DataSource.Name, sourceId);
            Publish(BuildRaiseRecord(source, alarm.CorrelationId, nowUtc, nowUtc));
        }

        private EventLogRecord BuildRaiseRecord(
            DataSourceInfoModel source,
            string correlationId,
            DateTime activeSinceUtc,
            DateTime nowUtc)
        {
            var record = NewAlarmRecord(source, correlationId);
            record.TimeUtc = activeSinceUtc;
            record.Level = RaiseLevel;
            record.Severity = EventLogConstants.SystemAlarmSeverity;
            record.AlarmState = AlarmStateValues.Incoming;
            record.Active = true;
            record.Retain = true;
            record.ActiveSinceUtc = activeSinceUtc;
            record.DurationMs = StandingDurationMs(activeSinceUtc, nowUtc);
            record.Template = RaiseTemplate;
            record.Message = RaiseMessage(source);
            return record;
        }

        private void Clear(
            int sourceId,
            DateTime nowUtc,
            DataSourceInfoModel recoveredSource,
            string reason)
        {
            if (!_openAlarms.Remove(sourceId, out var alarm))
            {
                return;
            }

            var source = recoveredSource ?? alarm.Source;
            var record = NewAlarmRecord(source, alarm.CorrelationId);
            record.TimeUtc = nowUtc;
            record.Level = ClearLevel;
            record.Severity = EventLogConstants.SystemAlarmClearSeverity;
            record.AlarmState = AlarmStateValues.Normal;
            record.Active = false;
            record.Retain = false;
            record.ActiveSinceUtc = alarm.ActiveSinceUtc;
            record.DurationMs = StandingDurationMs(alarm.ActiveSinceUtc, nowUtc);
            record.Template = ClearTemplate;
            record.Message = ClearMessage(source, reason);

            Log.Information("SystemAlarm: {DsName} Id: {DsId} cleared the connection alarm", source.DataSource.Name, sourceId);
            Publish(record);
        }

        // A source that is not connected keeps its clock alarm: its clock is unknown, the connection alarm reports it.
        private void EvaluateClock(DataSourceInfoModel source, DateTime nowUtc)
        {
            var ds = source.DataSource;
            if (!SystemClockAlarmRules.ShouldWatch(ds))
            {
                _clockOffSinceUtc.Remove(ds.Id);
                ClearClock(ds.Id, nowUtc, source, NotWatchedReason);
                return;
            }

            if (source.State != DataSourceState.Connected || source.ClockOffsetMs is not long offsetMs)
            {
                _clockOffSinceUtc.Remove(ds.Id);
                return;
            }

            if (!SystemClockAlarmRules.IsOff(offsetMs, ds.ClockWarnMs))
            {
                _clockOffSinceUtc.Remove(ds.Id);
                if (SystemClockAlarmRules.IsBack(offsetMs, ds.ClockWarnMs))
                {
                    ClearClock(ds.Id, nowUtc, source, null);
                }
                return;
            }

            if (_openClockAlarms.ContainsKey(ds.Id))
            {
                return;
            }
            if (!_clockOffSinceUtc.TryGetValue(ds.Id, out var sinceUtc))
            {
                _clockOffSinceUtc[ds.Id] = nowUtc;
                return;
            }
            if ((nowUtc - sinceUtc).TotalMilliseconds >= SystemClockAlarmRules.RaiseDelayMs)
            {
                _clockOffSinceUtc.Remove(ds.Id);
                RaiseClock(source, nowUtc);
            }
        }

        private void RaiseClock(DataSourceInfoModel source, DateTime nowUtc)
        {
            var sourceId = source.DataSource.Id;
            var alarm = new OpenConnectionAlarm
            {
                Source = source,
                ActiveSinceUtc = nowUtc,
                CorrelationId = AlarmTracker.BuildCorrelationId(sourceId, ClockConditionKey, nowUtc)
            };
            _openClockAlarms[sourceId] = alarm;

            Log.Warning("SystemAlarm: {DsName} Id: {DsId} raised a clock alarm ({ClockOffsetMs} ms)",
                source.DataSource.Name, sourceId, source.ClockOffsetMs);
            Publish(BuildClockRaiseRecord(source, alarm.CorrelationId, nowUtc, nowUtc));
        }

        private EventLogRecord BuildClockRaiseRecord(
            DataSourceInfoModel source,
            string correlationId,
            DateTime activeSinceUtc,
            DateTime nowUtc)
        {
            var record = NewClockAlarmRecord(source, correlationId);
            record.TimeUtc = activeSinceUtc;
            record.Level = ClockRaiseLevel;
            record.Severity = EventLogConstants.SystemClockAlarmSeverity;
            record.AlarmState = AlarmStateValues.Incoming;
            record.Active = true;
            record.Retain = true;
            record.ActiveSinceUtc = activeSinceUtc;
            record.DurationMs = StandingDurationMs(activeSinceUtc, nowUtc);
            record.Template = ClockRaiseTemplate;
            record.Message = ClockRaiseMessage(source);
            return record;
        }

        private void ClearClock(
            int sourceId,
            DateTime nowUtc,
            DataSourceInfoModel currentSource,
            string reason)
        {
            if (!_openClockAlarms.Remove(sourceId, out var alarm))
            {
                return;
            }

            var source = currentSource ?? alarm.Source;
            var record = NewClockAlarmRecord(source, alarm.CorrelationId);
            record.TimeUtc = nowUtc;
            record.Level = ClearLevel;
            record.Severity = EventLogConstants.SystemAlarmClearSeverity;
            record.AlarmState = AlarmStateValues.Normal;
            record.Active = false;
            record.Retain = false;
            record.ActiveSinceUtc = alarm.ActiveSinceUtc;
            record.DurationMs = StandingDurationMs(alarm.ActiveSinceUtc, nowUtc);
            record.Template = ClockClearTemplate;
            record.Message = ClockClearMessage(source, reason);

            Log.Information("SystemAlarm: {DsName} Id: {DsId} cleared the clock alarm", source.DataSource.Name, sourceId);
            Publish(record);
        }

        private void ClearVanishedSources(HashSet<int> monitoredSourceIds, DateTime nowUtc)
        {
            foreach (var sourceId in _openAlarms.Keys.Where(id => !monitoredSourceIds.Contains(id)).ToList())
            {
                Clear(sourceId, nowUtc, null, RemovedReason);
            }
            foreach (var sourceId in _openClockAlarms.Keys.Where(id => !monitoredSourceIds.Contains(id)).ToList())
            {
                ClearClock(sourceId, nowUtc, null, RemovedReason);
            }
            foreach (var sourceId in _clockOffSinceUtc.Keys.Where(id => !monitoredSourceIds.Contains(id)).ToList())
            {
                _clockOffSinceUtc.Remove(sourceId);
            }
        }

        private MonitoredSources BuildMonitoredSources(IReadOnlyList<DataSourceInfoModel> dataSources)
        {
            var result = new MonitoredSources();
            if (dataSources == null)
            {
                return result;
            }

            foreach (var source in dataSources)
            {
                if (source?.DataSource == null || source.DataSource.Id == _ownDataSourceId)
                {
                    continue;
                }

                if (!SystemConnectionAlarmRules.ShouldMonitor(source.DataSource))
                {
                    continue;
                }

                result.Sources.Add(source);
                result.SourceIds.Add(source.DataSource.Id);
            }

            return result;
        }

        private EventLogRecord NewAlarmRecord(DataSourceInfoModel affectedSource, string correlationId)
        {
            var affectedId = affectedSource.DataSource.Id;
            return new EventLogRecord
            {
                Measurement = EventLogConstants.ProcessMeasurementName,
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.SystemAlarmComponent,
                SourceId = _ownDataSourceId,
                AffectedSourceId = affectedId,
                SourceType = DataSourceType.System.ToString(),
                CorrelationId = correlationId,
                EventType = EventLogConstants.SystemAlarmEventType,
                EventTypeName = EventLogConstants.SystemAlarmEventTypeName,
                AlarmClassSymbol = EventLogConstants.SystemAlarmClass,
                AlarmClassName = EventLogConstants.SystemAlarmClass,
                Acked = false,
                Props = BuildProps(affectedSource)
            };
        }

        private EventLogRecord NewClockAlarmRecord(DataSourceInfoModel affectedSource, string correlationId)
        {
            var record = NewAlarmRecord(affectedSource, correlationId);
            record.EventType = EventLogConstants.SystemClockAlarmEventType;
            record.EventTypeName = EventLogConstants.SystemClockAlarmEventTypeName;
            record.Props = BuildProps(affectedSource, withClock: true);
            return record;
        }

        private static string ClockRaiseMessage(DataSourceInfoModel source)
        {
            var ds = source.DataSource;
            long offsetMs = source.ClockOffsetMs ?? 0;
            return Limit($"The clock of data source {ds.Name} ({SourceTypeName(ds)}) is {Seconds(offsetMs)} "
                + $"{(offsetMs > 0 ? "ahead of" : "behind")} DataLogXE. Accepted: {Seconds(ds.ClockWarnMs)}.{AddressPart(ds)}",
                EventLogConstants.MaxMessageChars);
        }

        private static string ClockClearMessage(DataSourceInfoModel source, string reason)
        {
            var ds = source.DataSource;
            if (!string.IsNullOrEmpty(reason))
            {
                return Limit($"Data source {ds.Name} ({SourceTypeName(ds)}) is {reason}. "
                    + "Clock deviation alarm closed.", EventLogConstants.MaxMessageChars);
            }

            var clock = source.ClockOffsetMs is long offsetMs ? $" Difference: {Seconds(offsetMs)}." : "";
            return Limit($"The clock of data source {ds.Name} ({SourceTypeName(ds)}) is back within "
                + $"{Seconds(ds.ClockWarnMs)}.{clock}{AddressPart(ds)}", EventLogConstants.MaxMessageChars);
        }

        private static string Seconds(long ms)
        {
            return (Math.Abs(ms) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private static string RaiseMessage(DataSourceInfoModel source)
        {
            var ds = source.DataSource;
            var reason = string.IsNullOrEmpty(source.ConnectionError) ? "" : $" Reason: {source.ConnectionError}.";
            return Limit($"Data source {ds.Name} ({SourceTypeName(ds)}) has a connection error. "
                + $"State: {source.State}.{reason}{AddressPart(ds)}", EventLogConstants.MaxMessageChars);
        }

        private static string ClearMessage(DataSourceInfoModel source, string reason)
        {
            var ds = source.DataSource;
            if (string.IsNullOrEmpty(reason))
            {
                return Limit($"Data source {ds.Name} ({SourceTypeName(ds)}) left the connection error. "
                    + $"State: {source.State}.{AddressPart(ds)}", EventLogConstants.MaxMessageChars);
            }

            return Limit($"Data source {ds.Name} ({SourceTypeName(ds)}) is {reason}. "
                + "Connection error alarm closed.", EventLogConstants.MaxMessageChars);
        }

        private static string AddressPart(DataSourceModel dataSource)
        {
            var address = BuildAddress(dataSource);
            if (address.Length == 0)
            {
                return "";
            }

            return $" Address: {address}";
        }

        private static string BuildAddress(DataSourceModel dataSource)
        {
            if (string.IsNullOrWhiteSpace(dataSource.Host))
            {
                return "";
            }

            if (dataSource.Port <= 0)
            {
                return Limit(dataSource.Host, TextLimit);
            }

            return Limit($"{dataSource.Host}:{dataSource.Port}", TextLimit);
        }

        private static string SourceTypeName(DataSourceModel dataSource)
        {
            return ((DataSourceType)dataSource.TypeId).ToString();
        }

        private static string BuildProps(DataSourceInfoModel source, bool withClock = false)
        {
            var ds = source.DataSource;
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AffectedSourceId"] = ds.Id.ToString(),
                ["DsName"] = Limit(ds.Name, TextLimit),
                ["SourceType"] = SourceTypeName(ds),
                ["ClientState"] = source.State.ToString()
            };
            if (!string.IsNullOrEmpty(source.ConnectionError))
            {
                props["Reason"] = Limit(source.ConnectionError, TextLimit);
            }

            var address = BuildAddress(ds);
            if (address.Length > 0)
            {
                props["Address"] = address;
            }

            if (withClock)
            {
                if (source.ClockOffsetMs is long offsetMs)
                {
                    props["ClockOffsetMs"] = offsetMs.ToString(CultureInfo.InvariantCulture);
                }
                props["ClockWarnMs"] = ds.ClockWarnMs.ToString(CultureInfo.InvariantCulture);
            }

            return Limit(JsonSerializer.Serialize(props), EventLogConstants.MaxPropsJsonChars);
        }

        private static long StandingDurationMs(DateTime activeSinceUtc, DateTime nowUtc)
        {
            var durationMs = (long)(nowUtc - activeSinceUtc).TotalMilliseconds;
            return durationMs < 0 ? 0 : durationMs;
        }

        private static string Limit(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, maxLength);
        }

        private void Publish(EventLogRecord record)
        {
            _publish?.Invoke(record);
        }
        #endregion

        #region private types
        private sealed class OpenConnectionAlarm
        {
            public DataSourceInfoModel Source { get; init; }
            public string CorrelationId { get; init; } = "";
            public DateTime ActiveSinceUtc { get; init; }
        }

        private sealed class MonitoredSources
        {
            public List<DataSourceInfoModel> Sources { get; } = [];
            public HashSet<int> SourceIds { get; } = [];
        }
        #endregion
    }
}
