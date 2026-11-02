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
using DataLogLib.Services;
using DataLogLib.Services.EventLog;
using DataLogLib.Services.ArchiveAccess;
using DataLogService.DataSource.System.Telegraf;
using Serilog;

namespace DataLogService.DataSource.System
{
    internal class DataSourceSystem : DataSourceBase, IAlarmSource
    {
        #region public const
        public const string ARCHIVE_VALUE_COUNT_MIO = "ArchiveValueCountSumMio";
        public const string ARCHIVE_VALUE_COUNT_PER_SECOND = "ArchiveValueCountPerSec";
        public const string DATABASE_DISK_SIZE_GB = "DatabaseDiskSizeGb";
        public const string ARCHIVE_COMP_TIME_MS = "ArchiveCompTimeMs";
        public const string ARCHIVE_COMP_COUNT_RAW = "ArchiveCompCountRaw";
        public const string ARCHIVE_COMP_COUNT_1S = "ArchiveCompCount1Sec";
        public const string SYSTEM_LOAD_1_SEC = "SystemLoad1Sec";
        public const string SYSTEM_CPU_COUNT = "SystemCpuCount";
        public const string SYSTEM_LOAD_PERCENT = "SystemLoadPercent";
        #endregion

        #region private fields
        private SystemAlarm _systemAlarm;
        private TelegrafParser _telegrafParser;
        private DataTagModel _archiveValueCountSumMio;
        private DataTagModel _archiveValueCountPerSec;
        private DataTagModel _databaseDiskSizeGb;
        private DataTagModel _archiveCompTimeMs;
        private DataTagModel _archiveCompCountRaw;
        private DataTagModel _archiveCompCount1Sec;
        private DataTagModel _systemLoadPercent;
        private double? _systemCpuCount;
        private double? _systemLoadPercentValue;
        private readonly Dictionary<string, DataTagModel> _telegrafDataTagModels = [];
        #endregion

        #region construction
        public DataSourceSystem(DataSourceModel dataSource) : base(dataSource) { }
        #endregion

        #region public methods
        public override void Init()
        {
            base.Init();

            // Report the connection errors of the other data sources as SystemAlarm alarms
            _systemAlarm = new SystemAlarm(
                _dataSource.Id,
                () => Global.Service.GetDataSourceInfos(),
                record => Global.Service?.EventLog?.Enqueue(record));
            _systemAlarm.Start();

            _state = DataSourceState.Connected;

            // Collect archive information
            InitArchiveDataTags();
            RegisterArchiveEvents();

            // Collect system information using telegraf
            var port = int.Parse(Global.Service.Configuration["TELEGRAF_PORT"] ?? "8092");
            _telegrafParser = new TelegrafParser(port, OnTelegrafValue);

            _state = DataSourceState.Connected;
        }

        // Latest SystemLoadPercent value and the data tags of the database size, the value count and the values per second for the dashboard.
        public SystemLoadState GetSystemLoadState()
        {
            return new SystemLoadState
            {
                DataSourceId = Id,
                DataSourceName = Name,
                DataTagId = _systemLoadPercent?.Id,
                Percent = _systemLoadPercentValue,
                DatabaseDiskSizeTagId = _databaseDiskSizeGb?.Id,
                ValueCountSumTagId = _archiveValueCountSumMio?.Id,
                ValueCountPerSecTagId = _archiveValueCountPerSec?.Id
            };
        }

        // Open connection alarms for the live alarm list of the API.
        public List<EventLogRecord> CopyActiveAlarms()
        {
            return _systemAlarm?.CopyActiveAlarms() ?? [];
        }

        public override void Dispose()
        {
            UnregisterArchiveEvents();
            _systemAlarm?.Dispose();
            _telegrafParser?.Dispose();
            base.Dispose();
        }
        #endregion

        #region private methods
        private void InitArchiveDataTags()
        {
            IDataAccess da = Global.Service.DataAccess;
            DataTagModel dtm;

            dtm = GetSysDataTagModel(ARCHIVE_VALUE_COUNT_MIO, ArchiveStatsWorker.VALUE_COUNT_SUM_CYCLE_MS);
            _archiveValueCountSumMio = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_archiveValueCountSumMio.Id);

            dtm = GetSysDataTagModel(ARCHIVE_VALUE_COUNT_PER_SECOND, Consts.ARCHIVE_STATS_CYLE_MS);
            _archiveValueCountPerSec = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_archiveValueCountPerSec.Id);

            dtm = GetSysDataTagModel(DATABASE_DISK_SIZE_GB, ArchiveStatsWorker.VALUE_COUNT_SUM_CYCLE_MS);
            _databaseDiskSizeGb = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_databaseDiskSizeGb.Id);

            dtm = GetSysDataTagModel(ARCHIVE_COMP_TIME_MS, Consts.ARCHIVE_COMPRESSION_CYLE_MS);
            _archiveCompTimeMs = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_archiveCompTimeMs.Id);

            dtm = GetSysDataTagModel(ARCHIVE_COMP_COUNT_RAW, Consts.ARCHIVE_COMPRESSION_CYLE_MS);
            _archiveCompCountRaw = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_archiveCompCountRaw.Id);

            dtm = GetSysDataTagModel(ARCHIVE_COMP_COUNT_1S, Consts.ARCHIVE_COMPRESSION_CYLE_MS);
            _archiveCompCount1Sec = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_archiveCompCount1Sec.Id);
        }

        private DataTagModel GetSysDataTagModel(string address, int cycle = 60000)
        {
            return new()
            {
                Id = 0,
                DataSourceId = _dataSource.Id,
                Address = address,
                Name = address,
                IsActive = 1,
                IsSys = 1,
                TypeId = (int)DataTagType.Number,
                Cycle = cycle,
            };
        }

        private void RegisterArchiveEvents()
        {
            Global.Service.ArchiveAccess.OnArchiveCompCountRaw += Archive_OnArchiveCompCountRaw;
            Global.Service.ArchiveAccess.OnArchiveCompCount1Sec += Archive_OnArchiveCompCount1Sec;
            Global.Service.ArchiveAccess.OnArchiveCompTimeMs += Archive_OnArchiveCompTimeMs;
            Global.Service.ArchiveAccess.OnValueCountSumMio += Archive_OnValueCountSumMio;
            Global.Service.ArchiveAccess.OnValueCountPerSec += Archive_OnValueCountPerSec;
            Global.Service.ArchiveAccess.OnDatabaseDiskSizeGb += Archive_OnDatabaseDiskSizeGb;
        }

        // A re-created System data source must not leave the old instance subscribed (duplicate values).
        private void UnregisterArchiveEvents()
        {
            var archive = Global.Service?.ArchiveAccess;
            if (archive == null)
            {
                return;
            }

            archive.OnArchiveCompCountRaw -= Archive_OnArchiveCompCountRaw;
            archive.OnArchiveCompCount1Sec -= Archive_OnArchiveCompCount1Sec;
            archive.OnArchiveCompTimeMs -= Archive_OnArchiveCompTimeMs;
            archive.OnValueCountSumMio -= Archive_OnValueCountSumMio;
            archive.OnValueCountPerSec -= Archive_OnValueCountPerSec;
            archive.OnDatabaseDiskSizeGb -= Archive_OnDatabaseDiskSizeGb;
        }

        private void Archive_OnValueCountPerSec(object sender, (DateTime, long) e)
        {
            DoOnArchiveValue(_archiveValueCountPerSec, e.Item1, e.Item2);
        }

        private void Archive_OnValueCountSumMio(object sender, (DateTime, double) e)
        {
            DoOnArchiveValue(_archiveValueCountSumMio, e.Item1, e.Item2);
        }

        private void Archive_OnDatabaseDiskSizeGb(object sender, (DateTime, double) e)
        {
            DoOnArchiveValue(_databaseDiskSizeGb, e.Item1, e.Item2);
        }

        private void Archive_OnArchiveCompTimeMs(object sender, long e)
        {
            DoOnArchiveValue(_archiveCompTimeMs, DateTime.Now, e);
        }

        private void Archive_OnArchiveCompCount1Sec(object sender, int e)
        {
            DoOnArchiveValue(_archiveCompCount1Sec, DateTime.Now, e);
        }

        private void Archive_OnArchiveCompCountRaw(object sender, int e)
        {
            DoOnArchiveValue(_archiveCompCountRaw, DateTime.Now, e);
        }

        private void OnTelegrafValue(TelegrafValue telegrafValue)
        {
            // Every Telegraf connection runs on its own thread; the tag cache and the CPU count are shared.
            lock (_telegrafDataTagModels)
            {
                var dtm = GetTelegrafDataTagModel(telegrafValue.Name);
                DoOnArchiveValue(dtm, telegrafValue.Time, telegrafValue.Value);

                if (telegrafValue.Name == SYSTEM_CPU_COUNT)
                {
                    _systemCpuCount = telegrafValue.Value;
                }
                else if (telegrafValue.Name == SYSTEM_LOAD_1_SEC)
                {
                    DoOnSystemLoadPercent(telegrafValue);
                }
            }
        }

        // SystemLoadPercent is a computed data tag: it is created once both the SystemLoad1Sec and
        // the SystemCpuCount data tags exist and is written whenever SystemLoad1Sec gets a value.
        private void DoOnSystemLoadPercent(TelegrafValue telegrafValue)
        {
            if (!_telegrafDataTagModels.ContainsKey(SYSTEM_CPU_COUNT))
            {
                return;
            }

            var dataTag = GetSystemLoadPercentDataTagModel();
            var cpuCount = _systemCpuCount;
            var percent = cpuCount is > 0
                ? telegrafValue.Value * 100.0 / cpuCount.Value
                : 0.0;
            _systemLoadPercentValue = percent;
            DoOnArchiveValue(dataTag, telegrafValue.Time, percent);
        }

        private DataTagModel GetSystemLoadPercentDataTagModel()
        {
            if (_systemLoadPercent != null)
            {
                return _systemLoadPercent;
            }

            var dtm = GetSysDataTagModel(SYSTEM_LOAD_PERCENT, Consts.TELEGRAF_INTERVAL_MS * 2);
            IDataAccess da = Global.Service.DataAccess;
            _systemLoadPercent = da.DataTagGetOrCreateByAddress(dtm);
            InsertStartupArchiveValue(_systemLoadPercent.Id);
            return _systemLoadPercent;
        }

        private DataTagModel GetTelegrafDataTagModel(string name)
        {
            if (_telegrafDataTagModels.ContainsKey(name))
            {
                return _telegrafDataTagModels[name];
            }
            else
            {
                var dtm = GetSysDataTagModel(name, Consts.TELEGRAF_INTERVAL_MS * 2);
                IDataAccess da = Global.Service.DataAccess;
                dtm = da.DataTagGetOrCreateByAddress(dtm);
                InsertStartupArchiveValue(dtm.Id);
                _telegrafDataTagModels.Add(name, dtm);
                return dtm;
            }
        }

        private void DoOnArchiveValue(DataTagModel dataTag, DateTime dateTime, double value, ValueState valueState = ValueState.Valid)
        {
            Log.Debug(LogPfx + "{TagName}:{TagValu}", Name, Id, dataTag.Name, value);
            var av = new ArchiveValue()
            {
                Id = dataTag.Id,
                Time = dateTime,
                Value = value,
                State = valueState,
                Compression = CompressionType.RAW,
            };
            InsertArchiveValue(av);
        }
        #endregion
    }
}
