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

using System.Diagnostics;
﻿using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.OpcUaNodeset;
using DataLogLib.Services;
using DataLogLib.Services.DataAccess;
using DataLogLib.Services.EventLog;
using DataLogService.DataSource;
using DataLogService.DataSource.OpcUa;
using DataLogService.DataSource.S7CommPlus;
using DataLogService.DataSource.System;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace DataLogService
{
    public class ServiceCtrl
    {
        #region public properties
        public IConfiguration Configuration { get; private set; }
        public IArchiveAccess ArchiveAccess { get => _archiveCtrl; }
        public IDataAccess DataAccess { get => _dataCtrl; }
        public IEventLogWriter EventLog { get; set; }
        #endregion

        #region private fields
        private readonly DataAccessCtrl _dataCtrl = new();
        private readonly ArchiveAccessCtrl _archiveCtrl = new();
        private readonly Dictionary<int, DataSourceBase> _dataSources = [];
        private readonly XmlImportSupervisor _xmlImport;
        private readonly object _activeAlarmLock = new();
        private readonly ManualResetEventSlim _activeAlarmStop = new(false);
        private readonly HashSet<string> _seenAlarmClasses = new(StringComparer.Ordinal);
        private List<EventLogRecord> _activeAlarms = [];
        private volatile bool _serviceRunning = true;
        #endregion

        #region construction
        public ServiceCtrl(IConfiguration configuration)
        {
            Log.Information("Startup DataLogXE Service");
            Global.Service = this;

            Configuration = configuration;
            // The database may still recover when the host has just restarted. Wait before the workers start.
            DatabaseStartupWait.Run(configuration);
            // The data connection applies the schema; the archive workers read its tables right after they start,
            // which logged "relation ... does not exist" on the first start of a slow device.
            _dataCtrl.Init(configuration);
            _archiveCtrl.Init(configuration);
            // The archive names its measurements after the data tag uid
            _archiveCtrl.SetUidLookup(_dataCtrl);
            LoadSeenAlarmClasses();
            _xmlImport = new XmlImportSupervisor(_dataCtrl);

            new Thread(DataSourceSyncWorker) { IsBackground = true }.Start();
            new Thread(BrowseCheckpointPurgeWorker) { IsBackground = true, Name = "BrowseCheckpointPurge" }.Start();
            new Thread(SuperviseActiveAlarms) { IsBackground = true, Name = "ActiveAlarmsSupervisor" }.Start();
            new Thread(DataSourceDeleteWorker) { IsBackground = true, Name = "DataSourceDelete" }.Start();
        }
        #endregion

        #region public methods
        public List<DataSourceInfoModel> GetDataSourceInfos()
        {
            lock (_dataSources)
            {
                // A deleted data source runs until the sync worker stops it, but is no longer listed.
                var result = _dataSources.Values
                    .Where(d => !_deleting.ContainsKey(d.Id))
                    .Select(d => d.DataSourceInfo)
                    .ToList();
                return result;
            }
        }

        public DataSourceListUiState GetDataSourceListUiState(int dataSourceId)
        {
            ThroughputSnapshot throughput;
            var tagBrowseActive = false;
            lock (_dataSources)
            {
                if (!_dataSources.TryGetValue(dataSourceId, out var dataSource))
                {
                    return new DataSourceListUiState();
                }

                if (dataSource is ITagBrowseSource browseSource)
                {
                    var status = browseSource.GetBrowseStatus();
                    tagBrowseActive = status.Running || status.Paused;
                }

                throughput = dataSource.GetThroughputSnapshot();
            }

            return new DataSourceListUiState
            {
                TagBrowseActive = tagBrowseActive,
                TagImportActive = _xmlImport.IsRunning(dataSourceId),
                ValuesPerSec = throughput.LastValue,
                ValuesPerSecSeries = throughput.Series ?? []
            };
        }

        public SystemLoadState GetSystemLoadState()
        {
            lock (_dataSources)
            {
                var system = _dataSources.Values.OfType<DataSourceSystem>().FirstOrDefault();
                return system?.GetSystemLoadState() ?? new SystemLoadState();
            }
        }

        public List<EventLogRecord> GetActiveAlarms(IReadOnlyList<int> sourceIds)
        {
            List<EventLogRecord> snapshot;
            lock (_activeAlarmLock)
            {
                snapshot = _activeAlarms;
            }

            if (sourceIds == null || sourceIds.Count == 0)
            {
                return snapshot;
            }

            var items = new List<EventLogRecord>();
            foreach (var item in snapshot)
            {
                if (EventLogSourceFilter.MatchesSourceFilter(item, sourceIds))
                {
                    items.Add(item);
                }
            }

            return items;
        }

        public DataSourceInfoModel GetDataSourceInfo(int id)
        {
            lock (_dataSources)
            {
                var result = _dataSources.Values.Select(d => d.DataSourceInfo)
                    .Where(d => d.DataSource.Id == id).FirstOrDefault();
                return result;
            }
        }

        /// <summary>Problems of single data tags of a running source (S7CommPlus: not resolved, PLC limit ...).</summary>
        public Dictionary<int, string> GetTagIssues(int dataSourceId, IEnumerable<int> dataTagIds)
        {
            var result = new Dictionary<int, string>();
            lock (_dataSources)
            {
                if (!_dataSources.TryGetValue(dataSourceId, out var dataSource))
                {
                    return result;
                }

                foreach (var id in dataTagIds)
                {
                    var issue = dataSource.GetTagIssue(id);
                    if (!string.IsNullOrEmpty(issue))
                    {
                        result[id] = issue;
                    }
                }
            }

            return result;
        }

        public S7ConnectionTestResult TestS7CommPlusConnection(string host, int port, int timeoutMs)
        {
            var result = DataSourceS7CommPlus.TestConnection(host, port, timeoutMs);
            lock (_dataSources)
            {
                foreach (var s7 in _dataSources.Values.OfType<DataSourceS7CommPlus>()
                    .Where(s => string.Equals(s.Host.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    var (subscriptions, values) = s7.GetSubscriptionUsage();
                    result.DataLogXeSources++;
                    result.DataLogXeSubscriptions += subscriptions;
                    result.DataLogXeValues += values;
                }
            }

            return result;
        }

        public void StartOpcUaBrowse(int dataSourceId)
        {
            lock (_dataSources)
            {
                GetBrowseSourceUnlocked(dataSourceId, "Browse").StartBrowse();
            }
        }

        public OpcUaBrowseJobStatus GetOpcUaBrowseStatus(int dataSourceId)
        {
            lock (_dataSources)
            {
                if (!_dataSources.TryGetValue(dataSourceId, out var dataSource))
                {
                    throw new KeyNotFoundException($"Data source {dataSourceId} was not found.");
                }

                return dataSource is ITagBrowseSource browseSource
                    ? browseSource.GetBrowseStatus()
                    : new OpcUaBrowseJobStatus();
            }
        }

        public void CancelOpcUaBrowse(int dataSourceId)
        {
            lock (_dataSources)
            {
                GetBrowseSourceUnlocked(dataSourceId, "Browse").CancelBrowse();
            }
        }

        /// <summary>
        /// Throws when an XML import cannot start now. Checked before the upload, so a large file is not sent for
        /// nothing, and again when the import starts.
        /// </summary>
        public void EnsureOpcUaXmlImportCanStart(int dataSourceId)
        {
            var model = DataAccess.DataSourceGetById(dataSourceId);
            if ((DataSourceType)model.TypeId != DataSourceType.OpcUa)
            {
                throw new InvalidOperationException("XML import is only available for OPC UA data sources.");
            }

            var running = _xmlImport.RunningDataSourceId();
            if (running == dataSourceId)
            {
                throw new InvalidOperationException(XmlImportLimits.AlreadyRunningMessage);
            }

            if (running.HasValue)
            {
                string name;
                try
                {
                    name = DataAccess.DataSourceGetById(running.Value).Name;
                }
                catch (InvalidOperationException)
                {
                    name = running.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                throw new InvalidOperationException(
                    "An XML import is running for the data source " + name + ". Only one import runs at a time.");
            }

            lock (_dataSources)
            {
                if (_dataSources.TryGetValue(dataSourceId, out var dataSource) &&
                    dataSource is DataSourceOpcUa opcUa &&
                    opcUa.IsBrowseRunning)
                {
                    throw new InvalidOperationException(XmlImportLimits.BrowseRunningMessage);
                }
            }

            if (DataAccess.BrowseCheckpointExists(dataSourceId))
            {
                throw new InvalidOperationException(XmlImportLimits.BrowseUnfinishedMessage);
            }
        }

        /// <summary>
        /// Deletes a data source: it disappears at once (and from the charts), its data tags are removed in the
        /// background, which takes minutes for hundreds of thousands of tags on a Raspberry Pi.
        /// </summary>
        public void DeleteDataSource(int dataSourceId)
        {
            if (!DataAccess.DataSourceMarkDeleting(dataSourceId))
            {
                throw new KeyNotFoundException($"Data source {dataSourceId} was not found.");
            }

            _deleting[dataSourceId] = true;
            _deleteSignal.Set();
        }

        public void StartOpcUaXmlImport(int dataSourceId, string filePath, XmlImportUpload upload)
        {
            EnsureOpcUaXmlImportCanStart(dataSourceId);
            var model = DataAccess.DataSourceGetById(dataSourceId);
            _xmlImport.Start(dataSourceId, DataSourceOpcUa.GetDefaultTagCycle(model.MinCycle), filePath, upload);
        }

        public OpcUaBrowseJobStatus GetOpcUaXmlImportStatus(int dataSourceId)
        {
            return _xmlImport.Get(dataSourceId);
        }

        public void CancelOpcUaXmlImport(int dataSourceId)
        {
            _xmlImport.Cancel(dataSourceId);
        }

        public bool IsXmlImportRunning(int dataSourceId)
        {
            return _xmlImport.IsRunning(dataSourceId);
        }

        public void Shutdown()
        {
            Log.Information("Shutdown DataLogXE Service");
            _serviceRunning = false;
            _activeAlarmStop.Set();
            _xmlImport.StopAll();
            lock (_dataSources)
            {
                foreach (var dataSource in _dataSources.Values.ToList())
                {
                    dataSource.Dispose();
                }

                _dataSources.Clear();
            }

            try
            {
                _archiveCtrl.Flush();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Archive flush on shutdown failed");
            }

            _dataCtrl.Close();
        }
        #endregion

        #region private methods

        private void SuperviseActiveAlarms()
        {
            while (_serviceRunning)
            {
                try
                {
                    var worker = new Thread(PublishActiveAlarmsLoop)
                    {
                        IsBackground = true,
                        Name = "ActiveAlarms"
                    };
                    Log.Information("Active alarms task started");
                    worker.Start();
                    worker.Join();
                    if (_serviceRunning)
                    {
                        Log.Warning("Active alarms task ended, restarting");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Active alarms supervisor failed");
                }

                if (_serviceRunning)
                {
                    _activeAlarmStop.Wait(TimeSpan.FromSeconds(1));
                }
            }

            Log.Information("Active alarms task stopped");
        }

        private void PublishActiveAlarmsLoop()
        {
            while (_serviceRunning)
            {
                try
                {
                    var items = CollectActiveAlarms();
                    RememberAlarmClasses(items);
                    lock (_activeAlarmLock)
                    {
                        _activeAlarms = items;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Active alarms publish failed");
                }

                if (!_serviceRunning)
                {
                    return;
                }

                _activeAlarmStop.Wait(TimeSpan.FromMilliseconds(EventLogConstants.ActiveAlarmPublishIntervalMs));
            }
        }

        private List<EventLogRecord> CollectActiveAlarms()
        {
            var items = new List<EventLogRecord>();
            lock (_dataSources)
            {
                foreach (var dataSource in _dataSources.Values)
                {
                    if (dataSource is IAlarmSource alarmSource)
                    {
                        items.AddRange(alarmSource.CopyActiveAlarms());
                    }
                }
            }

            items.Sort(static (left, right) => right.TimeUtc.CompareTo(left.TimeUtc));
            if (items.Count > EventLogConstants.MaxTrackedConditions)
            {
                items.RemoveRange(
                    EventLogConstants.MaxTrackedConditions,
                    items.Count - EventLogConstants.MaxTrackedConditions);
            }

            return items;
        }

        private const int MaxSeenAlarmClasses = 256;
        private const int AlarmClassTextMaxLength = 128;

        public List<AlarmClassModel> GetAlarmClasses()
        {
            return _dataCtrl.AlarmClassGetAll();
        }

        public void RememberAlarmClass(string name, string symbol)
        {
            var className = TrimAlarmClass(name);
            var classSymbol = TrimAlarmClass(symbol);
            var key = className.Length > 0 ? className : classSymbol;
            if (key.Length == 0)
            {
                return;
            }

            lock (_seenAlarmClasses)
            {
                if (!_seenAlarmClasses.Add(key))
                {
                    return;
                }

                if (_seenAlarmClasses.Count > MaxSeenAlarmClasses)
                {
                    _seenAlarmClasses.Remove(key);
                    return;
                }
            }

            try
            {
                _dataCtrl.AlarmClassInsert(key, classSymbol);
            }
            catch (Exception ex)
            {
                lock (_seenAlarmClasses)
                {
                    _seenAlarmClasses.Remove(key);
                }

                Log.Error(ex, "Failed to store alarm class {AlarmClass}", key);
            }
        }

        private void RememberAlarmClasses(IEnumerable<EventLogRecord> records)
        {
            if (records == null)
            {
                return;
            }

            foreach (var record in records)
            {
                if (record == null || !string.Equals(record.Kind, EventLogConstants.AlarmKind, StringComparison.Ordinal))
                {
                    continue;
                }

                RememberAlarmClass(record.AlarmClassName, record.AlarmClassSymbol);
            }
        }

        private void LoadSeenAlarmClasses()
        {
            foreach (var item in _dataCtrl.AlarmClassGetAll())
            {
                if (!string.IsNullOrEmpty(item?.Name))
                {
                    _seenAlarmClasses.Add(item.Name);
                }
            }
        }

        private static string TrimAlarmClass(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= AlarmClassTextMaxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, AlarmClassTextMaxLength);
        }

        #region data source sync
        private void BrowseCheckpointPurgeWorker(object obj)
        {
            while (true)
            {
                try
                {
                    var removed = DataAccess.BrowseCheckpointPurgeStale();
                    if (removed > 0)
                    {
                        Log.Information("Removed {Count} stale OPC UA browse checkpoints", removed);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "OPC UA browse checkpoint purge failed");
                }

                Thread.Sleep(TimeSpan.FromHours(1));
            }
        }

        private readonly AutoResetEvent _deleteSignal = new(false);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _deleting = new();

        private void DataSourceDeleteWorker(object obj)
        {
            while (true)
            {
                try
                {
                    foreach (var id in DataAccess.DataSourcesDeleting())
                    {
                        RemoveDeletedDataSource(id);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Removing a deleted data source failed");
                }

                _deleteSignal.WaitOne(TimeSpan.FromSeconds(30));
            }
        }

        private void RemoveDeletedDataSource(int id)
        {
            // The sync worker stops the data source within 5 s (and with it its XML import); until then it may
            // still write to its data tags.
            var clock = Stopwatch.StartNew();
            while (IsDataSourceLoaded(id) || _xmlImport.RunningDataSourceId() == id)
            {
                if (clock.Elapsed > TimeSpan.FromMinutes(2))
                {
                    return;
                }

                Thread.Sleep(1000);
            }

            var pacer = new LoadPacer(LoadPacer.MaxLoadFromEnvironment(), work: "Removing the data tags of a data source");
            var removed = 0;
            int chunk;
            while ((chunk = DataAccess.DataSourceDeleteTagChunk(id, DataAccessCtrl.DataSourceDeleteChunkRows)) > 0)
            {
                removed += chunk;
                pacer.Step();
            }

            DataAccess.DataSourceDeleteRemoved(id);
            _deleting.TryRemove(id, out _);
            Log.Information(
                "Deleted data source {DataSourceId} with {Count} data tags in {Seconds} s ({Paused} s paused for the load)",
                id,
                removed,
                (int)clock.Elapsed.TotalSeconds,
                (int)pacer.Paused.TotalSeconds);
        }

        private bool IsDataSourceLoaded(int id)
        {
            lock (_dataSources)
            {
                return _dataSources.ContainsKey(id);
            }
        }

        private void DataSourceSyncWorker(object obj)
        {
            while (true)
            {
                try
                {
                    lock (_dataSources)
                    {
                        DataSourceSync();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An exception was thrown!");
                }
                Thread.Sleep(5000);
            }
        }

        private void DataSourceSync()
        {
            // Sync data sources
            var dbSources = DataAccess.DataSourceGetAll().ToDictionary(d => GetSyncKey(d), d => d);
            var dsSources = _dataSources.Values.ToDictionary(d => GetSyncKey(d), d => d);

            var dbKeys = dbSources.Keys;
            var dsKeys = dsSources.Keys;

            var toCreateKeys = dbKeys.Except(dsKeys).ToList();
            var toRemoveKeys = dsKeys.Except(dbKeys).ToList();

            // Remove data sources
            foreach (var toRemoveKey in toRemoveKeys)
            {
                var id = dsSources[toRemoveKey].Id;
                var ds = _dataSources[id];
                ds.Dispose();
                _dataSources.Remove(id);
            }

            // Create data sources
            foreach (var toCreateKey in toCreateKeys)
            {
                var dataSourceModel = dbSources[toCreateKey];
                var ds = DataSourceBase.Create(dataSourceModel);
                if (ds == null)
                {
                    continue;
                }

                try
                {
                    ds.Init();
                    _dataSources.Add(ds.Id, ds);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Data source init failed: {DsName} Id: {DsId}", dataSourceModel.Name, dataSourceModel.Id);
                    ds.Dispose();
                }
            }
        }

        private static string GetSyncKey(DataSourceModel dataSourceModel)
        {
            return DataSourceBase.GetDataSourceSyncKey(dataSourceModel);
        }

        private static string GetSyncKey(DataSourceBase dataSourceBase)
        {
            return dataSourceBase.SyncKey;
        }

        private ITagBrowseSource GetBrowseSourceUnlocked(int dataSourceId, string actionName)
        {
            if (!_dataSources.TryGetValue(dataSourceId, out var dataSource))
            {
                throw new KeyNotFoundException($"Data source {dataSourceId} was not found.");
            }

            if (dataSource is ITagBrowseSource browseSource)
            {
                return browseSource;
            }

            var type = (DataSourceType)dataSource.DataSourceInfo.DataSource.TypeId;
            if (type == DataSourceType.OpcUa || type == DataSourceType.S7CommPlus)
            {
                throw new InvalidOperationException("The data source is not active.");
            }

            throw new InvalidOperationException($"{actionName} is only available for OPC UA and S7CommPlus data sources.");
        }

        #endregion
      
        #endregion
    }
}
