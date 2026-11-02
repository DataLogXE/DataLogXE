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

using Opc.Ua;

namespace DataLogService.DataSource.OpcUa
{
    public enum OpcUaAlarmNoticeKind
    {
        Alarm = 0,
        RefreshStart = 1,
        RefreshEnd = 2,
        RefreshRequired = 3,
        QueueOverflow = 4
    }

    public sealed class OpcUaAlarmNotification
    {
        public OpcUaAlarmNoticeKind Kind { get; set; } = OpcUaAlarmNoticeKind.Alarm;
        public byte[] EventId { get; set; }
        public NodeId EventType { get; set; }
        public string EventTypeName { get; set; } = "";
        public string AlarmClassSymbol { get; set; } = "";
        public string AlarmClassName { get; set; } = "";
        public NodeId AlarmClassId { get; set; }
        public uint? TextColor { get; set; }
        public uint? BackColor { get; set; }
        public bool? Flashing { get; set; }
        public DateTime Time { get; set; }
        public DateTime ReceiveTime { get; set; }
        public DateTime ObservedUtc { get; set; }
        public string Message { get; set; } = "";
        public ushort Severity { get; set; }
        public string ConditionName { get; set; } = "";
        public NodeId ConditionId { get; set; }
        public NodeId BranchId { get; set; }
        public bool? Retain { get; set; }
        public bool? Active { get; set; }
        public bool? Acked { get; set; }
        public bool? Enabled { get; set; }
        public bool? Confirmed { get; set; }
        public bool? Suppressed { get; set; }
        public bool? OutOfService { get; set; }
        public bool? SuppressedOrShelved { get; set; }
        public string LimitState { get; set; } = "";
        public DateTime ActiveSince { get; set; }
        public string SourceName { get; set; } = "";

        // Further props of the source (e.g. the S7 alarm id), written with every row of the alarm.
        public Dictionary<string, string> ExtraProps { get; set; }
    }

    public sealed class OpcUaAlarmNotificationEventArgs : EventArgs
    {
        public OpcUaAlarmNotification Notification { get; }

        public OpcUaAlarmNotificationEventArgs(OpcUaAlarmNotification notification)
        {
            Notification = notification;
        }
    }
}
