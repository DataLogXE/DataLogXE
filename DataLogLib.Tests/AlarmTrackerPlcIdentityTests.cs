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
using OpcUaSimpleClientApi;
using Xunit;

namespace DataLogLib.Tests
{
    public class AlarmTrackerPlcIdentityTests
    {
        [Fact]
        public void ConditionIdForAlarm_KeepsWinccNameWhenThePropertyIsMissing()
        {
            var instanceId = new NodeId("1.1.1.385875970.0.0", 1);

            var identity = OpcUaClientApi.ConditionIdForAlarm(null, "Bitmeldung_1", instanceId);

            Assert.Null(identity);
        }

        [Fact]
        public void ConditionIdForAlarm_UsesTheInstanceWhenTheSiemensAlarmHasNoName()
        {
            var instanceId = new NodeId(new byte[] { 0x08, 0x80, 0x00, 0x01, 0x00, 0x00, 0x7e, 0x8a }, 3);

            var identity = OpcUaClientApi.ConditionIdForAlarm(NodeId.Null, "", instanceId);

            Assert.Equal(instanceId, identity);
        }

        [Fact]
        public void RepeatedEventId_WithTheSameState_StaysOneOpenAlarm()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var raise = SiemensAlarm(active: true, acked: true, retain: true);

            var first = tracker.Process(raise);
            var replay = Copy(raise);
            replay.Time = raise.Time.AddMinutes(1);
            var second = tracker.Process(replay);

            Assert.Equal(AlarmStateValues.Incoming, first.Earlier.AlarmState);
            Assert.Null(second.Earlier);
            Assert.Single(tracker.CopyActiveRecords());
        }

        [Fact]
        public void RepeatedEventId_WhenTheAlarmClears_LeavesOnTheNextRefresh()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var raise = SiemensAlarm(active: true, acked: true, retain: true);
            tracker.Process(raise);

            var clear = Copy(raise);
            clear.Time = raise.Time.AddMinutes(5);
            clear.Active = false;
            clear.Retain = false;
            var live = tracker.Process(clear);

            Assert.Null(live.Earlier);
            Assert.Empty(tracker.CopyActiveRecords());
            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            var report = tracker.EndRefresh(clear.Time.AddMinutes(1));

            Assert.Equal(AlarmStateValues.Removed, Assert.Single(report.Closing).AlarmState);
            Assert.Empty(tracker.CopyActiveRecords());
        }

        [Fact]
        public void RepeatedEventId_WhenTheAlarmIsAcknowledged_WritesNoSecondRow()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var raise = SiemensAlarm(active: true, acked: false, retain: true);
            tracker.Process(raise);

            var ack = Copy(raise);
            ack.Time = raise.Time.AddMinutes(2);
            ack.Acked = true;
            var second = tracker.Process(ack);

            Assert.Null(second.Earlier);
            Assert.Null(second.Later);
            Assert.Single(tracker.CopyActiveRecords());
        }

        [Fact]
        public void WinccAcknowledge_WritesNoSecondRow()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var raise = SiemensAlarm(active: true, acked: false, retain: true);
            raise.ConditionId = null;
            raise.ConditionName = "Bitmeldung_1";
            raise.SourceName = "HMI_RT_1";
            tracker.Process(raise);

            var ack = Copy(raise);
            ack.EventId = [0x01, 0x02, 0x03];
            ack.SourceName = raise.SourceName;
            ack.Time = raise.Time.AddSeconds(10);
            ack.Acked = true;
            var second = tracker.Process(ack);

            Assert.Null(second.Earlier);
            Assert.Null(second.Later);
            Assert.Single(tracker.CopyActiveRecords());
        }

        private static OpcUaAlarmNotification SiemensAlarm(bool active, bool acked, bool retain)
        {
            var time = new DateTime(2026, 9, 20, 20, 22, 35, DateTimeKind.Utc);
            return new OpcUaAlarmNotification
            {
                EventId = [0xe2, 0x74, 0xfe, 0x95, 0xb0, 0x9b, 0x4a, 0x49, 0xbb, 0xe3, 0x0f, 0x08, 0x5e, 0x97, 0x41, 0x29],
                Time = time,
                ReceiveTime = time,
                ObservedUtc = time,
                ActiveSince = time,
                Message = "Error: Hardware component not available due to type mismatch",
                Severity = 1,
                ConditionId = new NodeId(new byte[] { 0x08, 0x80, 0x00, 0x01, 0x00, 0x00, 0x7e, 0x8a }, 3),
                Retain = retain,
                Active = active,
                Acked = acked,
                Enabled = true
            };
        }

        private static OpcUaAlarmNotification Copy(OpcUaAlarmNotification source)
        {
            return new OpcUaAlarmNotification
            {
                EventId = source.EventId,
                Time = source.Time,
                ReceiveTime = source.ReceiveTime,
                ObservedUtc = source.ObservedUtc,
                ActiveSince = source.ActiveSince,
                Message = source.Message,
                Severity = source.Severity,
                ConditionId = source.ConditionId,
                ConditionName = source.ConditionName,
                Retain = source.Retain,
                Active = source.Active,
                Acked = source.Acked,
                Enabled = source.Enabled
            };
        }
    }
}
