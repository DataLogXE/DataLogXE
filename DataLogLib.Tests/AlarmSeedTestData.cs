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

using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using Opc.Ua;

namespace DataLogLib.Tests
{
    /// <summary>
    /// Rows in the shape the archive returns them for open OPC UA alarms.
    /// </summary>
    internal static class AlarmSeedTestData
    {
        public const int SourceId = 7;
        public const string SourceName = "Robb-PC1";
        public const string LogPrefix = "Service: {DsName} Id: {DsId} ";

        public static EventLogRecord OpenRow(
            string correlationId,
            string conditionName,
            DateTime timeUtc,
            DateTime? activeSinceUtc = null,
            long? durationMs = null,
            string alarmState = AlarmStateValues.Incoming)
        {
            return new EventLogRecord
            {
                TimeUtc = timeUtc,
                CorrelationId = correlationId,
                AlarmState = alarmState,
                ActiveSinceUtc = activeSinceUtc,
                DurationMs = durationMs,
                Severity = 500,
                Message = conditionName + " message",
                Props = $"{{\"ConditionName\":\"{conditionName}\"}}"
            };
        }

        public static OpcUaAlarmNotification ActiveNotification(string conditionName)
        {
            var now = DateTime.UtcNow;
            return new OpcUaAlarmNotification
            {
                EventId = Guid.NewGuid().ToByteArray(),
                Time = now,
                ReceiveTime = now,
                ObservedUtc = now,
                ActiveSince = now,
                Message = conditionName + " message",
                Severity = 500,
                ConditionName = conditionName,
                ConditionId = new NodeId(conditionName, 2),
                Retain = true,
                Active = true,
                Acked = false,
                Enabled = true
            };
        }

        public static DateTime MinutesAgo(int minutes)
        {
            return DateTime.UtcNow.AddMinutes(-minutes);
        }
    }
}
