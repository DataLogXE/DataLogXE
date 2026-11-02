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

using System.Text.Json;
using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using DataLogService.DataSource.S7CommPlus;
using S7CommPlusDriver;
using S7CommPlusDriver.Alarming;
using Xunit;

namespace DataLogLib.Tests.S7CommPlus
{
    public class S7AlarmMapperTests
    {
        private static readonly DateTime Received = new DateTime(2026, 10, 17, 19, 40, 0, DateTimeKind.Utc);

        private static AlarmDai Captured(string hex)
        {
            var n = Notification.DeserializeFromPdu(new MemoryStream(Convert.FromHexString(hex)));
            return Assert.Single(AlarmDai.FromNotification(n));
        }

        private static AlarmTracker Tracker() => new AlarmTracker(10, "s7-1500sp", "S7CommPlus");

        private static List<EventLogRecord> Rows(AlarmTracker.AlarmEmit emit)
        {
            return new[] { emit.Earlier, emit.Later }.Where(r => r != null).ToList();
        }

        private static AlarmDai ProgramAlarm(byte states, DateTime coming, DateTime? going = null, DateTime? ack = null, uint seq = 5)
        {
            return new AlarmDai
            {
                CpuAlarmId = 0x8A0E000C00010000,
                AllStatesInfo = states,
                Domain = 256,
                MessageTypeRaw = (int)AlarmMessageType.Alarm,
                SequenceCounter = seq,
                HmiInfo = new AlarmHmiInfo { Priority = 8, AlarmClass = 1 },
                Coming = new AlarmTransition { TimestampUtc = coming, AckTimestampUtc = ack },
                Going = going.HasValue ? new AlarmTransition { TimestampUtc = going } : null,
            };
        }

        [Fact]
        public void Notify_alarm_stands_while_active_and_leaves_with_the_next_refresh()
        {
            var tracker = Tracker();
            var coming = S7AlarmMapper.Map(Captured(S7CommPlusAlarmFixtures.ProgramAlarmComing), 0x0409, Received);
            Assert.True(coming.Active);
            Assert.True(coming.Acked);
            Assert.Equal("Program alarms 0", coming.AlarmClassName);
            Assert.Equal("S7 notification", coming.EventTypeName);
            Assert.Equal((ushort)1, coming.Severity);
            Assert.Equal("S7 alarm 0x8A0E000B00010000", coming.Message);

            var raised = Assert.Single(Rows(tracker.Process(coming)));
            Assert.Equal(AlarmStateValues.Incoming, raised.AlarmState);
            Assert.Equal("S7CommPlus", raised.SourceType);
            Assert.Single(tracker.CopyActiveRecords());

            var going = S7AlarmMapper.Map(Captured(S7CommPlusAlarmFixtures.ProgramAlarmGoing), 0x0409, Received.AddSeconds(5));
            Assert.False(going.Active);
            Assert.False(going.Retain);
            Assert.Empty(Rows(tracker.Process(going)));
            Assert.Empty(tracker.CopyActiveRecords());

            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            var report = tracker.EndRefresh(Received.AddSeconds(8));
            var removed = Assert.Single(report.Closing);
            Assert.Equal(AlarmStateValues.Removed, removed.AlarmState);
            Assert.Equal(raised.CorrelationId, removed.CorrelationId);
        }

        [Fact]
        public void Info_reports_are_single_history_rows()
        {
            var tracker = Tracker();
            var first = S7AlarmMapper.Map(Captured(S7CommPlusAlarmFixtures.OpcUaSessionCreated), 0x0407, Received);
            var second = S7AlarmMapper.Map(Captured(S7CommPlusAlarmFixtures.SecurityLoginDenied), 0x0407, Received);

            var rowA = Assert.Single(Rows(tracker.Process(first)));
            var rowB = Assert.Single(Rows(tracker.Process(second)));
            Assert.Equal(AlarmStateValues.Normal, rowA.AlarmState);
            Assert.Equal(AlarmStateValues.Normal, rowB.AlarmState);
            Assert.NotEqual(rowA.CorrelationId, rowB.CorrelationId);
            Assert.Empty(tracker.CopyActiveRecords());
            // System diagnostic placeholders stay: their values are in the props.
            Assert.StartsWith("CPU-Info: OPC UA-Server: Session-Status geändert zu Created - Session-ID: @5X%u@.", rowA.Message);
            Assert.Equal("System diagnostics", rowA.AlarmClassName);
            Assert.Equal("Security", rowB.AlarmClassName);

            var props = JsonSerializer.Deserialize<Dictionary<string, string>>(rowA.Props);
            Assert.Equal("0x8A7E0000000F8009", props["CpuAlarmId"]);
            Assert.Equal("InfoReport", props["MessageType"]);
            Assert.Equal("MyPlc1512SP", props["SourceName"]);
            Assert.StartsWith("Kurzbezeichnung: OPC UA", props["InfoText"]);
            Assert.True(rowA.Props.Length < EventLogConstants.MaxPropsJsonChars);
        }

        [Fact]
        public void Alarm_with_acknowledgement_goes_through_incoming_outgoing()
        {
            var tracker = Tracker();
            var t0 = Received;

            var coming = S7AlarmMapper.Map(ProgramAlarm(0x85, t0), 0x0409, t0);
            Assert.False(coming.Acked);
            var raised = Assert.Single(Rows(tracker.Process(coming)));
            Assert.Equal(AlarmStateValues.Incoming, raised.AlarmState);

            var going = S7AlarmMapper.Map(ProgramAlarm(0x84, t0, going: t0.AddSeconds(5)), 0x0409, t0.AddSeconds(5));
            Assert.True(going.Retain);
            var outgoing = Assert.Single(Rows(tracker.Process(going)));
            Assert.Equal(AlarmStateValues.IncomingOutgoing, outgoing.AlarmState);
            Assert.Equal(raised.CorrelationId, outgoing.CorrelationId);

            var acked = S7AlarmMapper.Map(ProgramAlarm(0x86, t0, going: t0.AddSeconds(5), ack: t0.AddSeconds(9)), 0x0409, t0.AddSeconds(9));
            Assert.True(acked.Acked);
            Assert.False(acked.Retain);
            tracker.Process(acked);
            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            var removed = Assert.Single(tracker.EndRefresh(t0.AddSeconds(12)).Closing);
            Assert.Equal(AlarmStateValues.Removed, removed.AlarmState);
            Assert.Equal(raised.CorrelationId, removed.CorrelationId);
        }

        [Fact]
        public void Standing_alarm_in_the_refresh_is_kept()
        {
            var tracker = Tracker();
            var t0 = Received;
            tracker.Process(S7AlarmMapper.Map(ProgramAlarm(0x85, t0), 0x0409, t0));

            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            Assert.Empty(Rows(tracker.Process(S7AlarmMapper.Map(ProgramAlarm(0x85, t0), 0x0409, t0.AddMinutes(1)))));
            Assert.Empty(tracker.EndRefresh(t0.AddMinutes(1)).Closing);
            Assert.Single(tracker.CopyActiveRecords());
        }

        [Fact]
        public void Plc_times_are_moved_by_the_clock_shift_and_kept_in_the_props()
        {
            var t0 = Received;
            var shift = TimeSpan.FromMinutes(-5.7);
            var n = S7AlarmMapper.Map(ProgramAlarm(0x85, t0), 0x0409, t0.Add(shift), shift);
            Assert.Equal(t0 + shift, n.Time);
            Assert.Equal(t0 + shift, n.ActiveSince);
            Assert.Equal(t0.ToString("O"), n.ExtraProps["PlcTime"]);
        }

        [Fact]
        public void A_plc_time_ahead_of_the_receive_time_is_not_used()
        {
            // PLC clock 5.7 min ahead and its offset not known yet (no shift).
            var plc = Received.AddMinutes(5.7);
            var n = S7AlarmMapper.Map(ProgramAlarm(0x85, plc), 0x0409, Received);
            Assert.Equal(Received, n.Time);
            Assert.Equal(Received, n.ActiveSince);
            Assert.Equal(plc.ToString("O"), n.ExtraProps["PlcTime"]);
        }

        [Fact]
        public void Without_compensation_the_plc_time_is_taken_as_it_is_also_ahead()
        {
            var plc = Received.AddMinutes(5.7);
            var n = S7AlarmMapper.Map(ProgramAlarm(0x85, plc), 0x0409, Received, TimeSpan.FromMinutes(-5.7), hardPlcTime: true);
            Assert.Equal(plc, n.Time);
            Assert.Equal(plc, n.ActiveSince);
            Assert.Equal(Received, n.ReceiveTime);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(8, 501)]
        [InlineData(16, 1000)]
        [InlineData(200, 1000)]
        public void Priority_maps_onto_the_severity_range(byte priority, int severity)
        {
            Assert.Equal(severity, S7AlarmMapper.SeverityOf(priority));
        }
    }
}
