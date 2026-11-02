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
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services.EventLog;
using Opc.Ua;
using OpcUaSimpleClientApi;
using Serilog;

namespace DataLogService.DataSource.OpcUa
{
    internal class DataSourceOpcUa : DataSourceBase, ITagBrowseSource, IAlarmSource
    {
        #region public const
        /// <summary>
        /// max allowed item count to register / unregister
        /// </summary>
        public const int MAX_REGISTER_BLOCK_SIZE = 1000;
        #endregion

        #region private fields
        protected readonly OpcUaClientApi _client = new();
        private readonly Dictionary<string, OpcUaDataTag> _dataTagItems = [];
        private readonly object _browseLock = new();
        private CancellationTokenSource _browseCts;
        private OpcUaBrowseJobStatus _browseStatus = new();
        private DateTime _browseStepShownAtUtc = DateTime.MinValue;
        private DateTime? _browseActivityUtc;
        // Local clock minus server clock; the value times are moved by it while _clockCompensate is set.
        private readonly ClockOffsetEstimator _serverClock = new();
        private readonly bool _clockCompensate;
        // Without compensation: why the server was dropped for a clock too far ahead (rejoin with hysteresis).
        private volatile string _clockRefusal;
        private string _dataTagChangeMarker = $"{DateTime.Now.Ticks}";
        private bool _running = true;
        private bool _disposed;
        private readonly bool _alarmsEnabled;
        private readonly AlarmTracker _alarmTracker;
        private readonly OpcUaAlarmSeeder _alarmSeeder;
        private static readonly int[] StandardCycles = [0, 10, 50, 100, 500, 1000, 5000, 10000];
        private const int MaxMinCycle = 10000;
        private static readonly TimeSpan AlarmRefreshInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan AlarmRefreshTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan AlarmSeedTimeout =
            TimeSpan.FromMilliseconds(EventLogConstants.AlarmSeedQueryTimeoutMs);
        #endregion

        #region construction
        public DataSourceOpcUa(DataSourceModel dataSource) : base(dataSource)
        {
            _alarmsEnabled = dataSource.OpcUaAlarmsEnabled;
            _clockCompensate = dataSource.ClockCompensate;
            _alarmTracker = new AlarmTracker(Id, Name)
            {
                LookupClassName = _client.LookupAlarmClassName,
                LookupConditionClassName = _client.LookupConditionClassName,
                LookupEventTypeName = _client.LookupEventTypeName
            };
            _alarmSeeder = new OpcUaAlarmSeeder(
                Id,
                Name,
                LogPfx,
                _alarmTracker,
                () => Global.Service?.EventLog as IEventLogAccess,
                RememberAlarmClass,
                record => Global.Service?.EventLog?.Enqueue(record));

            // Add client callbacks 
            _client.OnClientMessage += OnClientMessage;
            _client.OnClientError += OnClientError;
            _client.OnMonitoredValue += OnMonitoredValue;
            _client.OnServerKeepAlive += OnServerKeepAlive;
            _client.OnStateChange += OnStateChange;
            _client.OnAlarmNotification += OnAlarmNotification;
            _client.PreparingAlarmSubscription += ArmAlarmRefresh;
            _client.AlarmRefreshRejected += OnAlarmRefreshRejected;
            _client.AlarmSubscriptionPreparing += SeedOpenAlarmsFromArchive;
            _client.SetAlarmEventsEnabled(_alarmsEnabled);
            _client.OnUnresolvedBrowseNodeKey = nodeKey =>
                Log.Warning(LogPfx + "Browse checkpoint node could not be resolved: {NodeKey}", Name, Id, nodeKey);
        }

        public override void Init()
        {
            base.Init();

            ConnectToServer();

            // Start worker threads
            new Thread(UpdateTagsWorker) { IsBackground = true }.Start();
            new Thread(CheckOutdatedValuesWorker) { IsBackground = true }.Start();
            if (_alarmsEnabled)
            {
                new Thread(AlarmRefreshWorker) { IsBackground = true, Name = "AlarmRefresh" }.Start();
            }
        }

        public override void Dispose()
        {
            _running = false;
            _disposed = true;
            CancelBrowse();
            try
            {
                Global.Service?.CancelOpcUaXmlImport(Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, LogPfx + "Could not cancel XML import", Name, Id);
            }

            _client.Disconnect();
            _client.OnClientMessage -= OnClientMessage;
            _client.OnClientError -= OnClientError;
            _client.OnMonitoredValue -= OnMonitoredValue;
            _client.OnServerKeepAlive -= OnServerKeepAlive;
            _client.OnStateChange -= OnStateChange;
            _client.OnAlarmNotification -= OnAlarmNotification;
            _client.AlarmRefreshRejected -= OnAlarmRefreshRejected;
            _client.SetAlarmEventsEnabled(false);
            base.Dispose();
        }
        #endregion

        #region private methods

        #region connect to server
        private void ConnectToServer()
        {
            var url = $"opc.tcp://{_dataSource.Host}:{_dataSource.Port}";
            var security = OpcUaSecuritySettings.FromModel(_dataSource);
            Log.Information(LogPfx + "Connect to OPC-UA server {Address}, locale {Locale}, security {Security}", Name, Id,
                url, string.IsNullOrEmpty(_dataSource.OpcUaLocale) ? "server default" : _dataSource.OpcUaLocale,
                security.ToString());

            _client.Connect(url,
                appName: "DataLogService",
                username: _dataSource.User,
                password: _dataSource.Pass,
                locale: _dataSource.OpcUaLocale,
                security: security);
        }
        #endregion

        #region worker threads
        private void UpdateTagsWorker(object obj)
        {
            while (_running)
            {
                try
                {
                    if (_client.IsConnected && DatabaseDataTagsChanged())
                    {
                        UpdateTags();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogPfx + "An exception was thrown", Name, Id);
                    ReconnectAfterTagSyncFailure();
                }
                Thread.Sleep(1000);
            }
        }

        private void ReconnectAfterTagSyncFailure()
        {
            _client.Disconnect();
            _dataTagChangeMarker = $"{DateTime.Now.Ticks}";

            Thread.Sleep(2000);
            Log.Information(LogPfx + "Wait for the server to release resources, " +
                "try to reconnect in 30 s", Name, Id);

            var waitUntil = DateTime.UtcNow.AddSeconds(30);
            while (_running && DateTime.UtcNow < waitUntil)
            {
                if (_client.State == ConnectionState.Disconnected)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            while (_running)
            {
                if (_client.State != ConnectionState.Disconnected)
                {
                    if (!WaitForClientState(ConnectionState.Disconnected, 30000))
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                ConnectToServer();
                if (_client.State == ConnectionState.Connecting || _client.IsConnected)
                {
                    return;
                }

                Thread.Sleep(5000);
            }
        }

        private bool WaitForClientState(ConnectionState expected, int timeoutMs)
        {
            var start = DateTime.UtcNow;
            while (_running && _client.State != expected)
            {
                if ((DateTime.UtcNow - start).TotalMilliseconds >= timeoutMs)
                {
                    return _client.State == expected;
                }

                Thread.Sleep(100);
            }

            return _client.State == expected;
        }

        private void CheckOutdatedValuesWorker(object obj)
        {
            while (_running)
            {
                try
                {
                    // Without compensation the values carry server times, their age counts on the server clock.
                    long valueBaseShiftMs = _clockCompensate ? 0 : _serverClock.ShiftMs ?? 0;
                    lock (_dataTagItems)
                    {
                        foreach (var dti in _dataTagItems)
                        {
                            dti.Value.CheckValueOutOfDate(_client.IsConnected, valueBaseShiftMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogPfx + "An exception was thrown", Name, Id);
                }

                Thread.Sleep(1000);
            }
        }
        #endregion

        #region client events
        private void OnClientError(object sender, ClientMessageEventArgs e)
        {
            Log.Error(LogPfx + "OpcUaClient: {ClientMessage}", Name, Id, e.Message);
        }

        private void OnClientMessage(object sender, ClientMessageEventArgs e)
        {
            Log.Information(LogPfx + "OpcUaClient: {ClientMessage}", Name, Id, e.Message);
        }

        private void OnStateChange(object sender, StateChangeEventArgs e)
        {
            Log.Information(LogPfx + "OpcUaClient: State {ClientState}", Name, Id, e.State);

            // Reset data tags if not connected
            if (e.State == ConnectionState.Connected)
            {
                ApplyServerMinCycle(_client.MinSupportedSampleRate);
                _client.SetAlarmEventsEnabled(_alarmsEnabled);
                TryResumeBrowse();
            }

            if (e.State != ConnectionState.Connected)
            {
                _serverClock.Reset();
                InitDataTags();
                // A lost connection is no refresh failure. The next subscription arms a new one.
                _alarmTracker.ResetForNewSubscription();
            }

            if (e.State == ConnectionState.Disconnected)
            {
                CleanupAllDataTags();
            }

            UpdateServiceState();
        }

        /// <summary>
        /// Takes the MinSupportedSampleRate the server reported on connect as min cycle of the source and stores it;
        /// a server that reports none keeps the stored value. The min cycle is not part of the sync key, so the
        /// stored value does not reconnect the source.
        /// </summary>
        private void ApplyServerMinCycle(int? serverRate)
        {
            if (serverRate is not int rate)
            {
                return;
            }

            rate = Math.Min(rate, MaxMinCycle);
            if (rate == _dataSource.MinCycle)
            {
                return;
            }

            Log.Information(LogPfx + "Server min. sample rate {Rate} ms, min cycle was {MinCycle} ms",
                Name, Id, rate, _dataSource.MinCycle);
            _dataSource.MinCycle = rate;
            InitializeCycleList();
            try
            {
                Global.Service.DataAccess.DataSourceMinCycleSet(Id, rate);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, LogPfx + "Could not store the min cycle", Name, Id);
            }
        }

        private void OnServerKeepAlive(object sender, ServerKeepAliveEventArgs e)
        {
            if (_client.IsConnected && e.ServerTime != DateTime.MinValue)
            {
                AddServerClockSample(e.ServerTime);
            }
        }

        private void OnMonitoredValue(object sender, MonitoredValueNotificationEventArgs e)
        {
            lock (_dataTagItems)
            {
                if (_dataTagItems.TryGetValue(e.ExpNodeIdString, out var opcUaDataTag))
                {
                    opcUaDataTag.ProcessMonitoredValue(ValueClockShiftMs(), e);
                }
            }

        }

        public List<EventLogRecord> CopyActiveAlarms()
        {
            if (!_alarmsEnabled)
            {
                return [];
            }

            return _alarmTracker.CopyActiveRecords();
        }

        private void ArmAlarmRefresh()
        {
            _alarmTracker.ResetForNewSubscription();
            if (!_alarmTracker.TryArmRefresh())
            {
                Log.Warning(LogPfx + "OpcUaClient: Alarm refresh arm failed", Name, Id);
            }
        }

        // Runs on the client's subscription start thread, before the alarm subscription exists,
        // so no live notification can reach the tracker ahead of the archive state.
        private void SeedOpenAlarmsFromArchive()
        {
            if (_disposed)
            {
                return;
            }

            _alarmSeeder.TrySeed(AlarmSeedTimeout);
        }

        private void AlarmRefreshWorker()
        {
            var nextUtc = DateTime.UtcNow + AlarmRefreshInterval;
            while (_running)
            {
                if (_client.IsConnected)
                {
                    PublishRefreshFailure(_alarmTracker.ExpireRefresh(AlarmRefreshTimeout));
                }
                if (DateTime.UtcNow >= nextUtc)
                {
                    nextUtc = DateTime.UtcNow + AlarmRefreshInterval;
                    RequestHourlyRefresh();
                }

                Thread.Sleep(1000);
            }
        }

        private void RequestHourlyRefresh()
        {
            if (!_alarmsEnabled || !_client.IsConnected)
            {
                return;
            }

            _client.TryRefreshAlarms(
                () =>
                {
                    if (_alarmTracker.TryArmRefresh())
                    {
                        return true;
                    }

                    Log.Information(LogPfx + "OpcUaClient: Alarm refresh skipped, one is already running", Name, Id);
                    return false;
                },
                _alarmTracker.DisarmRefresh);
        }

        private void OnAlarmRefreshRejected()
        {
            if (!_alarmsEnabled)
            {
                return;
            }

            try
            {
                PublishRefreshFailure(_alarmTracker.RejectRefresh());
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogPfx + "OpcUaClient: Alarm refresh failure handling failed", Name, Id);
            }
        }

        // A failed refresh leaves the alarm list unconfirmed. The closed alarms get a
        // Removed point, so the archive seed does not restore them on the next start.
        private void PublishRefreshFailure(AlarmTracker.AlarmRefreshReport report)
        {
            if (report == null || !report.Applied)
            {
                return;
            }

            Log.Warning(
                LogPfx + "OpcUaClient: Alarm refresh failed ({Failure}), closed {Closed} of {Open} alarms",
                Name, Id, report.Failure, report.Closed, report.Open);
            foreach (var record in report.Closing)
            {
                RememberAlarmClass(record);
                Global.Service?.EventLog?.Enqueue(record);
                Log.Information(
                    LogPfx + "OpcUaClient: Alarm refresh failure closed state={AlarmState} message={Message} correlation={Correlation} time={Time}",
                    Name, Id, record.AlarmState, record.Message, record.CorrelationId,
                    record.TimeUtc.ToString("o", CultureInfo.InvariantCulture));
            }
        }

        private void OnAlarmNotification(object sender, OpcUaAlarmNotificationEventArgs e)
        {
            if (!_alarmsEnabled || e?.Notification == null)
            {
                return;
            }

            var notice = e.Notification;
            if (notice.Kind == OpcUaAlarmNoticeKind.RefreshStart)
            {
                if (!_alarmTracker.BeginRefresh())
                {
                    Log.Information(LogPfx + "OpcUaClient: Alarm refresh start ignored", Name, Id);
                }

                return;
            }

            if (notice.Kind == OpcUaAlarmNoticeKind.RefreshEnd)
            {
                var refreshUtc = notice.Time == DateTime.MinValue
                    ? DateTime.UtcNow
                    : ToAlarmTime(notice.Time).ToUniversalTime();
                var report = _alarmTracker.EndRefresh(refreshUtc);
                if (!report.Applied)
                {
                    return;
                }

                foreach (var record in report.Closing)
                {
                    RememberAlarmClass(record);
                    Global.Service?.EventLog?.Enqueue(record);
                }

                if (report.Closed > 0 || report.New > 0)
                {
                    Log.Warning(
                        LogPfx + "OpcUaClient: Alarm refresh open={Open} confirmed={Confirmed} closed={Closed} new={New}",
                        Name, Id, report.Open, report.Confirmed, report.Closed, report.New);
                }
                else
                {
                    Log.Information(
                        LogPfx + "OpcUaClient: Alarm refresh open={Open} confirmed={Confirmed} closed={Closed} new={New}",
                        Name, Id, report.Open, report.Confirmed, report.Closed, report.New);
                }

                return;
            }

            if (notice.Kind == OpcUaAlarmNoticeKind.RefreshRequired
                || notice.Kind == OpcUaAlarmNoticeKind.QueueOverflow)
            {
                Log.Information(LogPfx + "OpcUaClient: Alarm refresh requested ({NoticeKind})", Name, Id, notice.Kind);
                RequestHourlyRefresh();
                return;
            }

            CompensateAlarmTimes(notice);
            if (!ClockGuard.Admit(notice.Time, "alarms"))
            {
                return;
            }
            var emit = _alarmTracker.Process(notice);
            RememberAlarmClass(emit.Earlier);
            RememberAlarmClass(emit.Later);
            if (emit.Earlier != null)
            {
                Global.Service?.EventLog?.Enqueue(emit.Earlier);
            }

            if (emit.Later != null)
            {
                Global.Service?.EventLog?.Enqueue(emit.Later);
            }
        }

        // The server times move onto the local clock as the values do; the props keep the server time.
        private void CompensateAlarmTimes(OpcUaAlarmNotification notice)
        {
            var time = ToAlarmTime(notice.Time);
            if (time == notice.Time)
            {
                return;
            }
            (notice.ExtraProps ??= [])["ServerTime"] = notice.Time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            notice.Time = time;
            notice.ActiveSince = ToAlarmTime(notice.ActiveSince);
        }

        private static void RememberAlarmClass(EventLogRecord record)
        {
            if (record == null)
            {
                return;
            }

            Global.Service?.RememberAlarmClass(record.AlarmClassName, record.AlarmClassSymbol);
        }
        #endregion

        #region data tag events
        private void OpcTag_OnOpcUaValue(object sender, OpcUaValue opcUaValue)
        {
            if (TryGetArchiveValue(opcUaValue, out ArchiveValue archiveValue))
            {
                InsertArchiveValue(archiveValue);
            }
        }

        private bool TryGetArchiveValue(OpcUaValue opcUaValue, out ArchiveValue archiveValue)
        {
            archiveValue = new ArchiveValue()
            {
                Id = opcUaValue.Id,
                Time = opcUaValue.ValueTime,
                State = opcUaValue.State,
            };

            var value = opcUaValue.Value;

            if (value is bool b)
            {
                archiveValue.Value = b ? 1.0 : 0.0;
                return true;
            }

            var v = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            {
                archiveValue.Value = result;
                return true;
            }

            Log.Error(LogPfx + "Unable to parse data tag value Id:{Id}, Value:{Value}", Name, Id, opcUaValue.Id, opcUaValue.Value);

            return false;
        }
        #endregion

        #region helper
        private void UpdateTags()
        {
            var dbTags = Global.Service.DataAccess.DataTagsGetByDataSourceId(Id, deleted: 0, active: 1)
                .Where(tag => tag.IsSys == 0)
                .ToList();
            var dbSyncKeyMap = dbTags.ToDictionary(d => OpcUaDataTag.GetDataTagSyncKey(d), d => d);

            List<string> toRemoveAddresses = null;
            List<MonitoredItemInfo> toAddInfos = null;
            List<int> startupTagIds = null;
            var deletedCount = 0;
            var createdCount = 0;
            var activeCount = 0;

            lock (_dataTagItems)
            {
                var srvSyncKeyMap = _dataTagItems.Values.ToDictionary(d => d.SyncKey, d => d);
                var toCreateSyncKeys = dbSyncKeyMap.Keys.Except(srvSyncKeyMap.Keys);
                var toDeleteSyncKeys = srvSyncKeyMap.Keys.Except(dbSyncKeyMap.Keys);

                var toDeleteAddrMap = srvSyncKeyMap
                    .Where(x => toDeleteSyncKeys.Contains(x.Key))
                    .ToDictionary(d => d.Value.Address, d => d.Value);

                if (toDeleteAddrMap.Count > 0)
                {
                    toRemoveAddresses = DetachDataTags(toDeleteAddrMap);
                    deletedCount = toDeleteAddrMap.Count;
                    activeCount = _dataTagItems.Count;
                }

                var toCreateDataTags = dbSyncKeyMap
                    .Where(p => toCreateSyncKeys.Contains(p.Key))
                    .Select(d => d.Value)
                    .ToList();

                if (toCreateDataTags.Count > 0)
                {
                    toAddInfos = AttachDataTags(toCreateDataTags, out startupTagIds);
                    createdCount = toCreateDataTags.Count;
                    activeCount = _dataTagItems.Count;
                }
            }

            if (toRemoveAddresses is { Count: > 0 })
            {
                _client.RemoveMonitoredItems(toRemoveAddresses);
                Log.Information(LogPfx + "Deleted {Count} datatags. Active: {Active}",
                    Name, Id, deletedCount, activeCount);
            }

            if (startupTagIds != null)
            {
                foreach (var tagId in startupTagIds)
                {
                    InsertStartupArchiveValue(tagId);
                }
            }

            if (toAddInfos is { Count: > 0 })
            {
                _client.AddMonitoredItems(toAddInfos);
                Log.Information(LogPfx + "Created {Count} datatags. Active: {Active}",
                    Name, Id, createdCount, activeCount);
            }
        }

        private List<string> DetachDataTags(Dictionary<string, OpcUaDataTag> toDelete)
        {
            var addresses = new List<string>(toDelete.Count);
            foreach (var pair in toDelete)
            {
                pair.Value.OnOpcUaValue -= OpcTag_OnOpcUaValue;
                if (_dataTagItems.Remove(pair.Key))
                {
                    addresses.Add(pair.Key);
                }
            }

            return addresses;
        }

        private List<MonitoredItemInfo> AttachDataTags(List<DataTagModel> dataTags, out List<int> startupTagIds)
        {
            var createdInfos = new List<MonitoredItemInfo>(dataTags.Count);
            startupTagIds = new List<int>(dataTags.Count);
            foreach (var dataTag in dataTags)
            {
                var opcUaDataTag = new OpcUaDataTag(dataTag);
                opcUaDataTag.OnOpcUaValue += OpcTag_OnOpcUaValue;
                createdInfos.Add(opcUaDataTag.MonitoredItemInfo);
                _dataTagItems.Add(opcUaDataTag.Address, opcUaDataTag);
                startupTagIds.Add(dataTag.Id);
            }

            return createdInfos;
        }

        private void CleanupAllDataTags()
        {
            lock (_dataTagItems)
            {
                _dataTagItems.Clear();
            }
        }

        private bool DatabaseDataTagsChanged()
        {
            var marker = Global.Service.DataAccess.ParameterGet(Consts.DATA_TAG_CHANGE_MARKER_KEY);
            var markerChanged = !marker.Equals(_dataTagChangeMarker);
            _dataTagChangeMarker = marker;

            return markerChanged;
        }

        private void InitDataTags()
        {
            lock (_dataTagItems)
            {
                foreach (var dti in _dataTagItems.Values)
                {
                    dti.Init();
                }
            }
        }

        protected override long? DeviceClockOffsetMs => -_serverClock.ShiftMs;

        // In UTC, so a change of the daylight saving time is no clock step.
        private void AddServerClockSample(DateTime serverTime)
        {
            bool first = _serverClock.ShiftMs == null;
            long sample = (long)(DateTime.UtcNow - serverTime.ToUniversalTime()).TotalMilliseconds;
            if (_serverClock.AddSample(sample, DateTime.UtcNow, out long shiftMs))
            {
                Log.Information(LogPfx + "OPC-UA server time offset: {ServerTimeOffset} ms", Name, Id, shiftMs);
            }
            if (!_clockCompensate && CheckServerClock(-shiftMs))
            {
                return;
            }
            if (first)
            {
                Log.Information(LogPfx + "Data acquisition running ...", Name, Id);
            }
        }

        /// <summary>
        /// Without compensation: drops the session when the server clock is too far ahead (see <see cref="ClockGuard"/>);
        /// the first sample after the reconnect decides again, with the hysteresis. True when dropped.
        /// </summary>
        private bool CheckServerClock(long aheadMs)
        {
            bool refusedBefore = _clockRefusal != null;
            if (!ClockGuard.RefuseConnection(aheadMs, refusedBefore))
            {
                if (refusedBefore)
                {
                    Log.Information(LogPfx + "Server clock back within the limit ({Offset} ms)", Name, Id, aheadMs);
                    _clockRefusal = null;
                    UpdateServiceState();
                }
                return false;
            }

            var error = ClockGuard.RefusalText(aheadMs);
            if (error != _clockRefusal)
            {
                Log.Warning(LogPfx + "{Error}", Name, Id, error);
            }
            _clockRefusal = error;
            _client.DropConnection(error);
            return true;
        }

        /// <summary>Why the server was dropped, while the source is not connected.</summary>
        protected override string ConnectionErrorText => _state == DataSourceState.Connected
            ? null
            : _clockRefusal ?? _client.LastSecurityError?.TrimEnd('.');

        protected override string RejectedCertificateThumbprint => _client.LastRejectedThumbprint;

        /// <summary>
        /// The shift the value times get: the estimate while compensating (null until the first sample, the values
        /// wait for it), 0 when the server times are taken as they are; after a refused clock the values wait for the
        /// first sample of the new session as well.
        /// </summary>
        private long? ValueClockShiftMs()
        {
            if (_clockCompensate)
            {
                return _serverClock.ShiftMs;
            }
            return _clockRefusal != null && _serverClock.ShiftMs == null ? null : 0;
        }

        /// <summary>Moves a server time of an alarm onto the local clock while compensating; unchanged without an estimate.</summary>
        private DateTime ToAlarmTime(DateTime serverTime)
        {
            if (!_clockCompensate || serverTime == DateTime.MinValue || _serverClock.ShiftMs is not long shiftMs)
            {
                return serverTime;
            }
            return DateTime.SpecifyKind(serverTime.ToUniversalTime().AddMilliseconds(shiftMs), DateTimeKind.Utc);
        }

        private void UpdateServiceState()
        {
            // Map OPC-UA client state to service state
            switch (_client.State)
            {
                case ConnectionState.Disconnected:
                    _state = DataSourceState.Disconnected;
                    break;
                case ConnectionState.Connecting:
                    _state = DataSourceState.Connecting;
                    break;
                case ConnectionState.Connected:
                    // After a refused clock the session counts once its first clock sample is accepted, so the
                    // connection alarm does not go and come with every reconnect.
                    _state = _clockRefusal == null ? DataSourceState.Connected : DataSourceState.Connecting;
                    break;
                case ConnectionState.Disconnecting:
                    _state = DataSourceState.Disconnecting;
                    break;
                case ConnectionState.Error:
                    _state = DataSourceState.Error;
                    break;
            }
        }
        #endregion

        #endregion

        #region protected methods
        protected override void InitializeCycleList()
        {
            Cycles = GetCycles(_dataSource.MinCycle);
        }

        /// <summary>
        /// The tag cycles offered for a source whose server samples no faster than <paramref name="minCycle"/> ms:
        /// the standard steps from that rate on (0 means the server's fastest); 10000 ms is always offered.
        /// </summary>
        internal static List<int> GetCycles(int minCycle)
        {
            var min = Math.Min(minCycle, MaxMinCycle);
            return StandardCycles.Where(c => c >= min).ToList();
        }

        /// <summary>
        /// The cycle new tags of a source get from a browse or an XML import: the fastest offered cycle.
        /// </summary>
        internal static int GetDefaultTagCycle(int minCycle)
        {
            return GetCycles(minCycle)[0];
        }

        #endregion

        #region browse import
        internal void StartBrowse(bool freshStart = true)
        {
            if (_disposed || !_running)
            {
                throw new InvalidOperationException("The data source is not connected.");
            }

            if (!_client.IsConnected)
            {
                throw new InvalidOperationException("The data source is not connected.");
            }

            lock (_browseLock)
            {
                if (_disposed || !_running)
                {
                    throw new InvalidOperationException("The data source is not connected.");
                }

                if (_browseStatus.Running)
                {
                    throw new InvalidOperationException("A browse is already running.");
                }

                if (Global.Service != null && Global.Service.IsXmlImportRunning(Id))
                {
                    throw new InvalidOperationException("An XML import is already running.");
                }

                if (freshStart)
                {
                    Global.Service?.DataAccess.BrowseCheckpointDelete(Id);
                }

                _browseCts?.Dispose();
                _browseCts = new CancellationTokenSource();
                var cancellation = _browseCts.Token;
                List<OpcUaBrowseStatusLine> preservedLines = [];
                if (!freshStart)
                {
                    preservedLines = _browseStatus.Lines
                        .Select(line => new OpcUaBrowseStatusLine
                        {
                            Text = line.Text,
                            ReplaceLast = line.ReplaceLast
                        })
                        .ToList();
                }

                _browseStatus = new OpcUaBrowseJobStatus
                {
                    Running = true,
                    Lines = preservedLines
                };
                new Thread(() => RunBrowse(cancellation))
                {
                    IsBackground = true,
                    Name = $"OpcUaBrowse-{Id}"
                }.Start();
            }
        }

        internal OpcUaBrowseJobStatus GetBrowseStatus()
        {
            lock (_browseLock)
            {
                var lines = _browseStatus.Lines
                    .Select(line => new OpcUaBrowseStatusLine
                    {
                        Text = line.Text,
                        ReplaceLast = line.ReplaceLast
                    })
                    .ToList();

                return new OpcUaBrowseJobStatus
                {
                    Running = _browseStatus.Running,
                    Paused = _browseStatus.Paused,
                    Completed = _browseStatus.Completed,
                    Error = _browseStatus.Error,
                    Lines = lines
                };
            }
        }

        internal void CancelBrowse()
        {
            CancellationTokenSource cts;
            var running = false;
            lock (_browseLock)
            {
                cts = _browseCts;
                running = _browseStatus.Running;
            }

            cts?.Cancel();
            if (!running)
            {
                Global.Service?.DataAccess.BrowseCheckpointDelete(Id);
            }
        }

        private void TryResumeBrowse()
        {
            if (_disposed || !_running || !_client.IsConnected)
            {
                return;
            }

            if (Global.Service == null
                || !Global.Service.DataAccess.BrowseCheckpointExists(Id)
                || !Global.Service.DataAccess.BrowseCheckpointResumeOnConnect(Id))
            {
                return;
            }

            try
            {
                StartBrowse(freshStart: false);
            }
            catch (InvalidOperationException)
            {
                // A browse or XML import is already active.
            }
        }

        void ITagBrowseSource.StartBrowse() => StartBrowse();
        OpcUaBrowseJobStatus ITagBrowseSource.GetBrowseStatus() => GetBrowseStatus();
        void ITagBrowseSource.CancelBrowse() => CancelBrowse();
        bool ITagBrowseSource.IsBrowseRunning => IsBrowseRunning;

        internal bool IsBrowseRunning
        {
            get
            {
                lock (_browseLock)
                {
                    return _browseStatus.Running;
                }
            }
        }

        private void RunBrowse(CancellationToken cancellationToken)
        {
            var store = new OpcUaBrowseCheckpointStore(Id, renamed => Log.Information(
                LogPfx + "Browse: {Count} array element tags take the index address", Name, Id, renamed));
            List<OpcUaAuxSession> sessions = null;
            var accumulatedBrowseFailures = 0;
            try
            {
                if (!Global.Service.DataAccess.BrowseCheckpointExists(Id))
                {
                    Global.Service.DataAccess.BrowseCheckpointCreate(Id);
                }

                var resumeState = Global.Service.DataAccess.BrowseCheckpointLoad(Id);
                var resumingWalk = HasBrowseWalkProgress(resumeState);
                if (!resumingWalk)
                {
                    AddBrowseStep("Browse address space …", cancellationToken);
                }
                else if (_browseStatus.Lines.Count == 0)
                {
                    AddBrowseInfo("Resuming browse from saved progress …", false);
                }

                var scalarsFound = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sessions = null;
                    try
                    {
                        sessions = _client.OpenBrowseSessions(1, cancellationToken);
                        var session = sessions[0];
                        var resume = Global.Service.DataAccess.BrowseCheckpointLoad(Id);
                        if (string.Equals(resume.Phase, "sync", StringComparison.Ordinal))
                        {
                            break;
                        }

                        SetBrowsePaused(false);
                        _client.BrowseVariableNodes(
                            session,
                            chunk =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (chunk != null)
                                {
                                    scalarsFound += chunk.Count;
                                }
                            },
                            p => UpdateBrowseStep(FormatBrowseWalkStep(p)),
                            cancellationToken,
                            store,
                            resume);
                        accumulatedBrowseFailures += _client.LastBrowseFailures;
                        break;
                    }
                    catch (Exception ex) when (ShouldPauseBrowse(ex))
                    {
                        Log.Warning(ex, LogPfx + "OPC UA browse paused", Name, Id);
                        AddBrowseInfo("Connection lost. Browse resumes when the server is connected.", false);
                        SetBrowsePaused(true);
                        WaitUntilBrowseConnected(cancellationToken);
                        SetBrowsePaused(false);
                        DismissConnectionLostLine();
                    }
                    finally
                    {
                        _client.ClearBrowseCaches(sessions);
                        _client.CloseBrowseSessions(sessions);
                        sessions = null;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                Global.Service.DataAccess.BrowseCheckpointSetPhase(Id, "sync");
                HoldBrowseStep(cancellationToken);
                if (_client.LastStructureExpansionLimited)
                {
                    AddBrowseStep("Structure expansion stopped at the limit", cancellationToken);
                }

                if (_client.LastArraysTruncated > 0)
                {
                    AddBrowseStep(
                        $"{_client.LastArraysTruncated} arrays cut at {OpcUaBrowser.MaxArrayElements} elements",
                        cancellationToken);
                }

                if (accumulatedBrowseFailures > 0)
                {
                    AddBrowseStep($"{accumulatedBrowseFailures} nodes could not be browsed", cancellationToken);
                }

                // Tags under nodes the browse did not reach would look deleted. Only a complete browse with
                // at least one variable may remove tags; otherwise existing tags are kept as they are.
                var browseComplete = accumulatedBrowseFailures == 0 && !_client.LastStructureExpansionLimited;
                AddBrowseStep("Update data tags …", cancellationToken);
                var found = Global.Service.DataAccess.BrowseCheckpointLoad(Id).FoundCount;
                scalarsFound = Math.Max(scalarsFound, found);
                if (!browseComplete || found == 0)
                {
                    AddBrowseStep("Browse incomplete or empty, existing data tags are not deleted", cancellationToken);
                }

                var syncResult = Global.Service.DataAccess.DataTagsSyncFromBrowseCheckpoint(
                    Id,
                    GetDefaultTagCycle(_dataSource.MinCycle),
                    progress =>
                    {
                        if (progress?.Phase == "prepare")
                        {
                            return;
                        }

                        UpdateBrowseStep(FormatBrowseTagSyncStep(progress, scalarsFound));
                    },
                    cancellationToken,
                    allowDelete: browseComplete);

                UpdateBrowseStep(FormatBrowseTagUpdate(syncResult));
                HoldBrowseStep(cancellationToken);
                AddBrowseStep("Browse completed successfully", cancellationToken);
                Global.Service.DataAccess.BrowseCheckpointDelete(Id);

                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Paused = false;
                    _browseStatus.Completed = true;
                    _browseStatus.Error = null;
                }
            }
            catch (OperationCanceledException)
            {
                Global.Service?.DataAccess.BrowseCheckpointDelete(Id);
                AddBrowseInfo("The browse was cancelled.", false);
                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Paused = false;
                    _browseStatus.Completed = true;
                    _browseStatus.Error = "The browse was cancelled.";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogPfx + "OPC UA browse failed", Name, Id);
                var message = FormatBrowseFailure(ex);
                AddBrowseInfo(
                    message + " Reconnect to resume, or Cancel to discard progress.",
                    false);
                Global.Service?.DataAccess.BrowseCheckpointSetResumeOnConnect(Id, true);
                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Paused = false;
                    _browseStatus.Completed = false;
                    _browseStatus.Error = message;
                }
            }
        }

        private static bool HasBrowseWalkProgress(BrowseCheckpointState resume)
        {
            if (resume == null || !resume.Exists)
            {
                return false;
            }

            return (resume.Queue?.Count ?? 0) > 0
                || (resume.Inflight?.Count ?? 0) > 0
                || (resume.Pending?.Count ?? 0) > 0
                || resume.FoundCount > 0
                || (resume.Visited?.Count ?? 0) > 0;
        }

        private static bool ShouldPauseBrowse(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is OpcUaBrowseInterruptedException || current is TimeoutException)
                {
                    return true;
                }

                if (current is InvalidOperationException invalid
                    && (invalid.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase)
                        || invalid.Message.Contains("could not be opened", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (current is ServiceResultException serviceResult
                    && IsBrowseTransportStatus((uint)serviceResult.StatusCode))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBrowseTransportStatus(uint code)
        {
            return code == StatusCodes.BadTimeout
                || code == StatusCodes.BadRequestTimeout
                || code == StatusCodes.BadSecureChannelClosed
                || code == StatusCodes.BadConnectionClosed
                || code == StatusCodes.BadSessionClosed
                || code == StatusCodes.BadDisconnect
                || code == StatusCodes.BadNotConnected
                || code == StatusCodes.BadServerNotConnected
                || code == StatusCodes.BadNoCommunication
                || code == StatusCodes.BadRequestInterrupted
                || code == StatusCodes.BadSecureChannelIdInvalid;
        }

        private void WaitUntilBrowseConnected(CancellationToken cancellationToken)
        {
            var lastTouch = DateTime.UtcNow;
            if (cancellationToken.WaitHandle.WaitOne(2000))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            while (!_client.IsConnected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((DateTime.UtcNow - lastTouch).TotalSeconds >= 60)
                {
                    Global.Service?.DataAccess.BrowseCheckpointTouch(Id);
                    lastTouch = DateTime.UtcNow;
                }

                if (cancellationToken.WaitHandle.WaitOne(1000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void SetBrowsePaused(bool paused)
        {
            if (paused)
            {
                Global.Service?.DataAccess.BrowseCheckpointSetResumeOnConnect(Id, true);
            }

            lock (_browseLock)
            {
                _browseStatus.Paused = paused;
                _browseStatus.Running = true;
            }
        }

        private void DismissConnectionLostLine()
        {
            lock (_browseLock)
            {
                if (_browseStatus.Lines.Count == 0)
                {
                    return;
                }

                var last = _browseStatus.Lines[^1].Text ?? "";
                if (last.StartsWith("Connection lost.", StringComparison.Ordinal))
                {
                    _browseStatus.Lines.RemoveAt(_browseStatus.Lines.Count - 1);
                }
            }
        }

        private static string FormatBrowseWalkStep(OpcUaBrowseProgress progress)
        {
            // Short enough for one line in the dialog, also with the detail and the UI's "waiting" suffix.
            progress ??= new OpcUaBrowseProgress();
            var text = $"Browse … {progress.NodesBrowsed} nodes";
            if (progress.ScalarTags > 0)
            {
                text += $" · {progress.ScalarTags} tags";
            }

            if (!string.IsNullOrEmpty(progress.Detail))
            {
                text += " · " + progress.Detail;
            }

            return text;
        }

        private static string FormatBrowseTagSyncStep(DataTagBrowseSyncProgress progress, int total)
        {
            progress ??= new DataTagBrowseSyncProgress();
            return progress.Phase switch
            {
                "prepare" when total > 0 => $"Update data tags … {progress.Count} / {total}",
                "prepare" => "Update data tags … preparing",
                "delete" => $"Update data tags … deleted {progress.Count}",
                "undelete" => $"Update data tags … restored {progress.Count}",
                "create" => $"Update data tags … created {progress.Count}",
                "update" => $"Update data tags … updated {progress.Count}",
                _ => "Update data tags …"
            };
        }

        private static string FormatBrowseTagUpdate(DataTagBrowseSyncResult result)
        {
            var parts = new List<string>();
            if (result.Created > 0)
            {
                parts.Add($"Created {result.Created}");
            }
            if (result.Updated > 0)
            {
                parts.Add($"Updated {result.Updated}");
            }
            if (result.Undeleted > 0)
            {
                parts.Add($"Restored {result.Undeleted}");
            }
            if (result.Deleted > 0)
            {
                parts.Add($"Deleted {result.Deleted}");
            }

            if (parts.Count == 0)
            {
                return "Update data tags … no changes";
            }

            return "Update data tags … " + string.Join(" · ", parts);
        }

        private void AddBrowseStep(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddBrowseInfo(text, false);
            MarkStepShown();
            TouchBrowseActivity();
        }

        private void UpdateBrowseStep(string text)
        {
            AddBrowseInfo(text, true);
        }

        private void TouchBrowseActivity()
        {
            lock (_browseLock)
            {
                _browseActivityUtc = DateTime.UtcNow;
            }
        }

        private void MarkStepShown()
        {
            _browseStepShownAtUtc = DateTime.UtcNow;
        }

        private void HoldBrowseStep(CancellationToken cancellationToken)
        {
            var remaining = 1000 - (int)(DateTime.UtcNow - _browseStepShownAtUtc).TotalMilliseconds;
            if (remaining <= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (cancellationToken.WaitHandle.WaitOne(remaining))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private void AddBrowseInfo(string text, bool replaceLast)
        {
            lock (_browseLock)
            {
                if (_browseStatus.Lines.Count > 0
                    && text.StartsWith("Connection lost.", StringComparison.Ordinal)
                    && _browseStatus.Lines[^1].Text.StartsWith("Connection lost.", StringComparison.Ordinal))
                {
                    _browseStatus.Lines[^1] = new OpcUaBrowseStatusLine
                    {
                        Text = text,
                        ReplaceLast = true
                    };
                    return;
                }

                _browseStatus.Lines.Add(new OpcUaBrowseStatusLine
                {
                    Text = text,
                    ReplaceLast = replaceLast
                });
            }
        }

        private static string FormatBrowseFailure(Exception ex)
        {
            if (ex is OutOfMemoryException)
            {
                return "An error occurred during browse (out of memory).";
            }

            var symbolic = TryGetOpcStatusName(ex);
            if (string.IsNullOrWhiteSpace(symbolic))
            {
                return "An error occurred during browse.";
            }

            return $"An error occurred during browse ({symbolic}).";
        }

        private static string TryGetOpcStatusName(Exception ex)
        {
            while (ex != null)
            {
                if (ex is ServiceResultException sre)
                {
                    var name = StatusCodes.GetBrowseName((uint)sre.StatusCode);
                    if (!string.IsNullOrWhiteSpace(name)
                        && !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }

                ex = ex.InnerException;
            }

            return null;
        }
        #endregion

        #region helper classes
        private class OpcUaDataTag
        {
            #region public enums
            public enum StateType { INIT, ACTIVE };
            #endregion

            #region public events
            public event EventHandler<OpcUaValue> OnOpcUaValue;
            #endregion

            #region public properties
            public string Address { get => _dataTag.Address; }
            public string SyncKey { get => GetDataTagSyncKey(_dataTag); }
            public StateType State { get; private set; } = StateType.INIT;
            public MonitoredItemInfo MonitoredItemInfo { get; private set; }
            #endregion

            #region private fields
            private readonly DataTagModel _dataTag;
            private readonly object _lock = new();
            private OpcUaValue _lastOpcUaValue = null;
            #endregion

            #region construction
            public OpcUaDataTag(DataTagModel dataTag)
            {
                _dataTag = dataTag;

                // create monitored item info
                MonitoredItemInfo = new MonitoredItemInfo()
                {
                    ExpNodeIdString = _dataTag.Address,
                    SamplingInterval = _dataTag.Cycle,
                    QueueSize = GetQueueSize()
                };
            }
            #endregion

            #region internal methods
            internal void Init()
            {
                State = StateType.INIT;
            }

            internal void ProcessMonitoredValue(long? serverTimeOffset, MonitoredValueNotificationEventArgs e)
            {
                // As long as the time offset has not yet been determined, exit here. 
                if (serverTimeOffset == null) return;

                lock (_lock)
                {
                    long offset = serverTimeOffset.Value;
                    if (State == StateType.INIT)
                    {
                        // Insert inital opc ua value
                        State = StateType.ACTIVE;
                        _lastOpcUaValue = new OpcUaValue(_dataTag.Id, e, offset - 1, ValueState.Invalid);
                        DoOnOpcUaValue(_lastOpcUaValue);
                    }

                    // Insert valid opc ua value
                    var opcUaValue = new OpcUaValue(_dataTag.Id, e, offset, ValueState.Valid);
                    DoOnOpcUaValue(opcUaValue);

                    // Save last value to re-insert if value is out of date
                    if (opcUaValue.ValueTime > _lastOpcUaValue.ValueTime)
                    {
                        _lastOpcUaValue = opcUaValue;
                    }
                }
            }

            /// <param name="valueShiftMs">local clock minus the clock of the value times (0 while compensating)</param>
            internal void CheckValueOutOfDate(bool connected, long valueShiftMs)
            {
                lock (_lock)
                {
                    // if not connected or value not valid -> return
                    if (!connected || State == StateType.INIT || _lastOpcUaValue == null)
                    {
                        _lastOpcUaValue = null;
                        return;
                    }

                    // check value age -> insert again if value is out of date
                    var maxAge = Consts.ARCHIVE_VALUE_MIN_MS;
                    var now = DateTime.Now.AddMilliseconds(-valueShiftMs);
                    var ageMillis = (now - _lastOpcUaValue.ValueTime).TotalMilliseconds;
                    if (ageMillis > maxAge * 2)
                    {
                        _lastOpcUaValue.ValueTime = now.AddMilliseconds(-maxAge);
                        _lastOpcUaValue.State = ValueState.Resend;
                        DoOnOpcUaValue(_lastOpcUaValue);
                    }
                }
            }

            internal static string GetDataTagSyncKey(DataTagModel dataTag)
            {
                return $"{dataTag.Id};{dataTag.Address};{dataTag.Cycle}";
            }
            #endregion

            #region private methods
            private void DoOnOpcUaValue(OpcUaValue value)
            {
                OnOpcUaValue?.Invoke(this, value);
            }

            private uint GetQueueSize()
            {
                var cycle = _dataTag.Cycle;
                if (cycle >= 1000)
                {
                    return 1;
                }
                else if (cycle < 1000 && cycle >= 100)
                {
                    return 10;
                }
                else
                {
                    return 100;
                }
            }
            #endregion
        }

        private class OpcUaValue
        {
            #region properties
            public int Id { get; private set; }
            public object Value { get; private set; }
            public DateTime ValueTime { get; set; }
            //public DateTime InsertTime { get; private set; }
            public ValueState State { get; set; }
            #endregion

            #region construction
            public OpcUaValue(int id, MonitoredValueNotificationEventArgs e, long offset, ValueState state = ValueState.Invalid)
            {
                Id = id;
                Value = e.Value;
                ValueTime = e.SourceTimestamp.ToLocalTime().AddMilliseconds(offset);
                State = state;
            }
            #endregion
        }

        private sealed class OpcUaBrowseCheckpointStore : IOpcUaBrowseStore
        {
            private readonly int _dataSourceId;
            private readonly Action<int> _onRenamed;

            public OpcUaBrowseCheckpointStore(int dataSourceId, Action<int> onRenamed)
            {
                _dataSourceId = dataSourceId;
                _onRenamed = onRenamed;
            }

            public void SetInflight(string phase, IReadOnlyList<string> nodeKeys)
            {
                Global.Service.DataAccess.BrowseCheckpointSetInflight(_dataSourceId, phase, nodeKeys);
            }

            public void CommitBrowse(
                IReadOnlyList<string> finishedParents,
                IReadOnlyList<string> newObjects,
                IReadOnlyList<BrowseCheckpointNode> newVariables)
            {
                Global.Service.DataAccess.BrowseCheckpointCommitBrowse(
                    _dataSourceId, finishedParents, newObjects, newVariables);
            }

            public void CommitClassify(
                IReadOnlyList<string> finishedVariables,
                IReadOnlyList<string> newObjects,
                IReadOnlyList<DataTagBrowseImportItem> scalars)
            {
                Global.Service.DataAccess.BrowseCheckpointCommitClassify(
                    _dataSourceId, finishedVariables, newObjects, scalars);
            }

            public void RenameTags(IReadOnlyList<KeyValuePair<string, string>> renames)
            {
                var renamed = Global.Service.DataAccess.DataTagsRenameAddresses(_dataSourceId, renames);
                if (renamed > 0)
                {
                    _onRenamed?.Invoke(renamed);
                }
            }
        }
        #endregion
    }
}
