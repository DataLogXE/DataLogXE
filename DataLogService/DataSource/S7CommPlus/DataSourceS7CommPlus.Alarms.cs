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
using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using S7CommPlusDriver;
using S7CommPlusDriver.Alarming;
using S7CommPlusDriver.Symbols;
using Serilog;

namespace DataLogService.DataSource.S7CommPlus
{
    /// <summary>
    /// PLC alarms: one alarm subscription per session, the active alarm list of the PLC as refresh after every
    /// connect, shortly after an alarm went or was acknowledged and once a minute. Rows take the path of the OPC UA
    /// alarms (alarm tracker, archive seed, live list).
    /// </summary>
    internal sealed partial class DataSourceS7CommPlus : IAlarmSource
    {
        // The PLC sends no notification for an acknowledgement (seen on a 1512SP): an alarm acknowledged after it
        // went only leaves the PLC's alarm list, so the list is read once a minute (one small request).
        private static readonly TimeSpan AlarmRefreshInterval = TimeSpan.FromMinutes(1);
        // A going alarm leaves the live list with the next refresh: wait a little for further changes,
        // and keep a distance between two refreshes for a PLC with many alarms.
        private static readonly TimeSpan AlarmRefreshDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan AlarmRefreshMinDistance = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AlarmSeedTimeout = TimeSpan.FromMilliseconds(EventLogConstants.AlarmSeedQueryTimeoutMs);
        private const int MaxLoggedDecodeErrors = 64;

        private bool _alarmsEnabled;
        private int _alarmLanguage = S7AlarmMapper.DefaultLanguage;
        private AlarmTracker _alarmTracker;
        private OpcUaAlarmSeeder _alarmSeeder;
        private uint _alarmSubscriptionId;
        private DateTime _nextAlarmRefreshUtc = DateTime.MaxValue;
        private DateTime _lastAlarmRefreshUtc = DateTime.MinValue;
        private long _alarmRefreshDueTicks;               // UTC ticks, 0 = none; set by the notification thread
        // The first refresh after a connect waits for the PLC clock offset (from the first data notification).
        private static readonly TimeSpan AlarmFirstRefreshDelay = TimeSpan.FromSeconds(2);
        private readonly HashSet<ulong> _loggedDecodeErrors = [];

        private void InitAlarms(DataSourceModel dataSource)
        {
            _alarmsEnabled = dataSource.S7AlarmsEnabled;
            _alarmLanguage = S7AlarmLanguages.LanguageId(dataSource.S7Locale);
            _alarmTracker = new AlarmTracker(Id, Name, "S7CommPlus");
            _alarmSeeder = new OpcUaAlarmSeeder(
                Id,
                Name,
                LogPfx,
                _alarmTracker,
                () => Global.Service?.EventLog as IEventLogAccess,
                RememberAlarmClass,
                record => Global.Service?.EventLog?.Enqueue(record));
        }

        public List<EventLogRecord> CopyActiveAlarms()
        {
            return _alarmsEnabled ? _alarmTracker.CopyActiveRecords() : [];
        }

        /// <summary>After a connect, on the worker thread: archive seed, alarm subscription, first refresh.</summary>
        private void StartAlarms()
        {
            if (!_alarmsEnabled || _conn == null)
            {
                return;
            }

            // Before the subscription exists, so no live alarm reaches the tracker ahead of the archive state.
            _alarmSeeder.TrySeed(AlarmSeedTimeout);
            _alarmTracker.ResetForNewSubscription();

            int res = _conn.AlarmSubscriptionCreate(
                new uint[] { (uint)_alarmLanguage, (uint)S7AlarmMapper.DefaultLanguage }, out uint objectId, out ulong rv);
            if (res != 0)
            {
                Log.Warning(LogPfx + "No alarm subscription on the PLC ({Error}{Rv}), PLC alarms are not received in this session",
                    Name, Id, S7Consts.ErrorText(res), rv != 0 ? $", 0x{rv:X16}" : "");
                return;
            }
            _alarmSubscriptionId = objectId;
            _dispatcher.Register(objectId, OnAlarmNotification);
            Log.Information(LogPfx + "Alarm subscription created", Name, Id);
            Interlocked.Exchange(ref _alarmRefreshDueTicks, (DateTime.UtcNow + AlarmFirstRefreshDelay).Ticks);
            _nextAlarmRefreshUtc = DateTime.UtcNow + AlarmRefreshInterval;
        }

        // PLC clock minus local clock: the negated shift, when one was measured.
        protected override long? DeviceClockOffsetMs => -_plcClock.ShiftMs;

        /// <summary>
        /// Server clock minus PLC clock as the tag values use it (from data notifications and live alarms); zero
        /// without a sample and when the source takes the PLC times as they are.
        /// </summary>
        private TimeSpan AlarmClockShift()
        {
            return _clockCompensate && _plcClock.ShiftMs is long shiftMs ? TimeSpan.FromMilliseconds(shiftMs) : TimeSpan.Zero;
        }

        /// <summary>Worker loop: the delayed refresh after a going or acknowledged alarm, and the periodic one.</summary>
        private void AlarmWork(DateTime nowUtc)
        {
            if (_alarmSubscriptionId == 0)
            {
                return;
            }
            long due = Interlocked.Read(ref _alarmRefreshDueTicks);
            if (due != 0 && nowUtc.Ticks >= due)
            {
                Interlocked.CompareExchange(ref _alarmRefreshDueTicks, 0, due);
                RefreshAlarms(_lastAlarmRefreshUtc == DateTime.MinValue ? "connect" : "change");
            }
            else if (nowUtc >= _nextAlarmRefreshUtc)
            {
                RefreshAlarms("periodic");
            }
        }

        private void StopAlarms()
        {
            _alarmSubscriptionId = 0;
            _lastAlarmRefreshUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _alarmRefreshDueTicks, 0);
            _nextAlarmRefreshUtc = DateTime.MaxValue;
            // Open alarms stay (the live list shows the source as disconnected); the refresh after the
            // reconnect closes the ones that went in between.
            _alarmTracker?.ResetForNewSubscription();
        }

        /// <summary>Reads the active alarm list of the PLC as a refresh: alarms not in it are closed.</summary>
        private void RefreshAlarms(string reason)
        {
            var conn = _conn;
            if (conn == null || _alarmSubscriptionId == 0)
            {
                return;
            }
            _lastAlarmRefreshUtc = DateTime.UtcNow;
            _nextAlarmRefreshUtc = _lastAlarmRefreshUtc + AlarmRefreshInterval;
            if (!_alarmTracker.TryArmRefresh() || !_alarmTracker.BeginRefresh())
            {
                _alarmTracker.DisarmRefresh();
                return;
            }

            int res = conn.ReadActiveAlarms(out var alarms);
            if (res != 0)
            {
                // Nothing is closed on a failed read: the next refresh decides.
                _alarmTracker.ResetForNewSubscription();
                Log.Warning(LogPfx + "Reading the active alarms failed ({Error})", Name, Id, S7Consts.ErrorText(res));
                if (!conn.Connected)
                {
                    throw new S7CommunicationException(res, "Reading the active alarms");
                }
                return;
            }

            var receivedUtc = DateTime.UtcNow;
            var shift = AlarmClockShift();
            // Oldest first: rows of a source get rising times, a newer alarm first would push the older ones up.
            alarms.Sort((a, b) => Nullable.Compare(a.LatestTimeUtc, b.LatestTimeUtc));
            foreach (var dai in alarms)
            {
                if (dai.CpuAlarmId == 0 || S7AlarmMapper.IsOneShot(dai))
                {
                    continue;
                }
                var mapped = S7AlarmMapper.Map(dai, _alarmLanguage, receivedUtc, shift, !_clockCompensate);
                if (ClockGuard.Admit(mapped.Time, "alarms"))
                {
                    Emit(_alarmTracker.Process(mapped));
                }
            }

            var report = _alarmTracker.EndRefresh(receivedUtc);
            foreach (var record in report.Closing)
            {
                RememberAlarmClass(record);
                Global.Service?.EventLog?.Enqueue(record);
            }
            if (report.Closed > 0 || report.New > 0)
            {
                // Closing alarms that went or were acknowledged is what the refresh after a change is for (a PLC
                // that toggles alarms gives one every 30 s); a refresh after a connect, an alarm closed while it
                // was active (going missed) or a new one is worth an Info line.
                var level = reason == "connect" || report.Missed > 0 || report.New > 0
                    ? Serilog.Events.LogEventLevel.Information
                    : Serilog.Events.LogEventLevel.Debug;
                Log.Write(level, LogPfx + "Alarm refresh ({Reason}): {Count} in the PLC, open={Open} confirmed={Confirmed} closed={Closed} missed={Missed} new={New}",
                    Name, Id, reason, alarms.Count, report.Open, report.Confirmed, report.Closed, report.Missed, report.New);
            }
        }

        // Notification thread: decode, map and write; never sends a request.
        private void OnAlarmNotification(Notification notification)
        {
            var receivedUtc = DateTime.UtcNow;
            foreach (var dai in AlarmDai.FromNotification(notification))
            {
                if (dai.DecodeError != null)
                {
                    LogDecodeError(dai);
                }
                if (dai.CpuAlarmId == 0)
                {
                    continue;
                }
                var plcTime = dai.LatestTimeUtc;
                if (plcTime.HasValue)
                {
                    AddPlcClockSample(plcTime.Value, receivedUtc);
                }

                var n = S7AlarmMapper.Map(dai, _alarmLanguage, receivedUtc, AlarmClockShift(), !_clockCompensate);
                if (!ClockGuard.Admit(n.Time, "alarms"))
                {
                    continue;
                }
                Emit(_alarmTracker.Process(n));
                if (!S7AlarmMapper.IsOneShot(dai) && (n.Active != true || n.Acked == true))
                {
                    ScheduleAlarmRefresh(receivedUtc);
                }
            }
        }

        private void ScheduleAlarmRefresh(DateTime nowUtc)
        {
            var due = nowUtc + AlarmRefreshDelay;
            var earliest = _lastAlarmRefreshUtc + AlarmRefreshMinDistance;
            if (due < earliest)
            {
                due = earliest;
            }
            // Keep an earlier due time; the worker checks once a second.
            Interlocked.CompareExchange(ref _alarmRefreshDueTicks, due.Ticks, 0);
        }

        private void Emit(AlarmTracker.AlarmEmit emit)
        {
            foreach (var record in new[] { emit.Earlier, emit.Later })
            {
                if (record != null)
                {
                    RememberAlarmClass(record);
                    Global.Service?.EventLog?.Enqueue(record);
                }
            }
        }

        private void LogDecodeError(AlarmDai dai)
        {
            lock (_loggedDecodeErrors)
            {
                if (_loggedDecodeErrors.Count >= MaxLoggedDecodeErrors || !_loggedDecodeErrors.Add(dai.CpuAlarmId))
                {
                    return;
                }
            }
            Log.Warning(LogPfx + "Alarm 0x{AlarmId:X16} only partly decoded: {Error}", Name, Id, dai.CpuAlarmId, dai.DecodeError);
        }

        private static void RememberAlarmClass(EventLogRecord record)
        {
            if (record == null)
            {
                return;
            }
            Global.Service?.RememberAlarmClass(record.AlarmClassName, record.AlarmClassSymbol);
        }
    }
}
