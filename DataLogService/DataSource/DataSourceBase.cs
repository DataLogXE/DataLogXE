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
using DataLogLib.Common.Crypt;
using DataLogLib.Models;
using DataLogService.DataSource.OpcUa;
using DataLogService.DataSource.S7CommPlus;
using DataLogService.DataSource.System;
using DataLogService.DataSource.Test;
using Serilog;

namespace DataLogService.DataSource
{
    internal class DataSourceBase : IDisposable
    {
        #region public properties
        public string LogPfx { get; set; } = "Service: {DsName} Id: {DsId} ";
        public int Id { get => _dataSource.Id; }
        public string Name { get => _dataSource.Name; }
        public string SyncKey { get => GetDataSourceSyncKey(_dataSource); }
        public DataSourceInfoModel DataSourceInfo { get => GetDataSourceInfo(); }
        public List<int> Cycles { get; protected set; }
        /// <summary>Logger that tags each event with DsId and DsName, so the event log files it under this data source.</summary>
        public ILogger SourceLog { get; }
        #endregion

        #region protected fields
        protected readonly DataSourceModel _dataSource;
        protected DataSourceState _state = DataSourceState.Undefined;
        #endregion

        #region private fields
        private readonly DataSourceThroughput _throughput = new();
        #endregion

        #region construction
        public DataSourceBase(DataSourceModel dataSource)
        {
            _dataSource = dataSource;
            SourceLog = Log.ForContext("DsId", dataSource.Id).ForContext("DsName", dataSource.Name);
            InitializeCycleList();
            Log.Information(LogPfx + "Create data source", Name, Id);
        }

        public virtual void Dispose()
        {
            _throughput.Dispose();
            Log.Information(LogPfx + "Remove data source", Name, Id);
        }
        #endregion

        #region public methods
        internal static DataSourceBase Create(DataSourceModel dataSourceModel)
        {
            if (dataSourceModel.IsActive == 0)
            {
                return new InactiveDataSource(dataSourceModel);
            }

            switch ((DataSourceType)dataSourceModel.TypeId)
            {
                case DataSourceType.System:
                    return new DataSourceSystem(dataSourceModel);
                case DataSourceType.OpcUa:
                    return new DataSourceOpcUa(dataSourceModel);
                case DataSourceType.S7CommPlus:
                    return new DataSourceS7CommPlus(dataSourceModel);
                case DataSourceType.Test:
                    return new DataSourceTest(dataSourceModel);
            }
            Log.Error($"Data source {dataSourceModel.Name} type {dataSourceModel.TypeId} is not supported!");
            return null;
        }

        public virtual void Init()
        {
            if (_dataSource.IsActive != 0)
            {
                _throughput.Start(_dataSource, InsertArchiveValue);
            }
        }

        public static string GetDataSourceSyncKey(DataSourceModel dataSourceModel)
        {
            var d = dataSourceModel;
            return
                $"{d.Id};{d.Name};{d.TypeId};{d.IsActive};{d.IsSys};" +
                $"{d.Host};{d.Port};{d.User};{SecretProtector.Fingerprint(d.Pass)};{d.Guid};" +
                $"{d.TestCycleMs};{d.TestTagCount};{d.OpcUaAlarmsEnabled};{d.OpcUaLocale};{d.S7AlarmsEnabled};{d.S7Locale};{d.ClockWarnMs};{d.ClockMonitor};{d.ClockCompensate};" +
                $"{d.OpcUaSecurityMode};{d.OpcUaSecurityPolicy};{d.OpcUaCertTrust};{d.OpcUaServerCertThumbprint}";
        }
        #endregion

        #region protected methods
        protected void InsertArchiveValue(ArchiveValue value)
        {
            if (value == null || value.Id <= 0)
            {
                Log.Error(LogPfx + "Refusing archive value without a valid data tag id", Name, Id);
                return;
            }
            if (!ClockGuard.Admit(value.Time, "values"))
            {
                return;
            }

            Global.Service.ArchiveAccess.InsertValue(value);
            _throughput.NoteInsert(value.Id);
        }

        protected void InsertStartupArchiveValue(int dataTagId)
        {
            InsertStartupArchiveValue(dataTagId, InsertArchiveValue);
        }

        internal static void InsertStartupArchiveValue(int dataTagId, Action<ArchiveValue> insert)
        {
            if (dataTagId <= 0 || insert == null)
            {
                return;
            }

            var lastValue = Global.Service.ArchiveAccess.GetArchiveEndTime(dataTagId);
            if (lastValue.HasValue && lastValue.Value > DateTime.Now.AddYears(-10))
            {
                insert(new ArchiveValue()
                {
                    Id = dataTagId,
                    Value = 0,
                    Time = lastValue.Value.AddSeconds(5),
                    State = ValueState.Invalid
                });
            }
            else
            {
                insert(new ArchiveValue()
                {
                    Id = dataTagId,
                    Value = 0,
                    Time = DateTime.Now,
                    State = ValueState.Invalid
                });
            }
        }

        /// <summary>A problem of a single data tag of a running source (not resolved, not subscribed ...), null when fine.</summary>
        internal virtual string GetTagIssue(int dataTagId)
        {
            return null;
        }

        protected virtual void InitializeCycleList()
        {
            Cycles = [1000];
        }

        internal ThroughputSnapshot GetThroughputSnapshot()
        {
            return _throughput.GetSnapshot();
        }
        #endregion

        /// <summary>Device clock minus local clock in ms (positive: the device is ahead), null when not measured.</summary>
        protected virtual long? DeviceClockOffsetMs => null;

        /// <summary>Why the source is not connected (e.g. a refused device clock), null when unknown.</summary>
        protected virtual string ConnectionErrorText => null;

        /// <summary>The server certificate the last connect refused as not pinned, null when none.</summary>
        protected virtual string RejectedCertificateThumbprint => null;

        #region private methods
        private DataSourceInfoModel GetDataSourceInfo()
        {
            var result = new DataSourceInfoModel()
            {
                DataSource = _dataSource,
                State = _state,
                Cycles = Cycles,
                ClockOffsetMs = _state == DataSourceState.Connected ? DeviceClockOffsetMs : null,
                ConnectionError = ConnectionErrorText,
                RejectedCertificateThumbprint = _state == DataSourceState.Connected ? null : RejectedCertificateThumbprint,
            };
            return result;
        }
        #endregion
    }
}
