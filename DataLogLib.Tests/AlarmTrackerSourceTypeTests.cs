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
using Xunit;

namespace DataLogLib.Tests
{
    public class AlarmTrackerSourceTypeTests
    {
        private static Dictionary<string, string> Props(EventLogRecord row)
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(row.Props);
        }

        [Fact]
        public void Rows_carry_the_source_type_and_the_extra_props()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName, "S7CommPlus");
            var notification = AlarmSeedTestData.ActiveNotification("S7Alarm");
            notification.ExtraProps = new Dictionary<string, string>
            {
                ["CpuAlarmId"] = "0x8A0E000B00010000",
                ["ConditionName"] = "must not replace the own prop"
            };

            var row = tracker.Process(notification).Earlier;

            Assert.NotNull(row);
            Assert.Equal("S7CommPlus", row.SourceType);
            Assert.Equal(EventLogConstants.AlarmComponent, row.Component);
            var props = Props(row);
            Assert.Equal("0x8A0E000B00010000", props["CpuAlarmId"]);
            Assert.Equal("S7Alarm", props["ConditionName"]);
        }

        [Fact]
        public void Default_source_type_stays_opc_ua()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var row = tracker.Process(AlarmSeedTestData.ActiveNotification("UaAlarm")).Earlier;
            Assert.Equal("OpcUa", row.SourceType);
        }

        [Fact]
        public void Extra_props_of_a_seeded_alarm_reach_its_closing_row()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName, "S7CommPlus");
            var row = AlarmSeedTestData.OpenRow("corr-s7", "S7Alarm", AlarmSeedTestData.MinutesAgo(10), AlarmSeedTestData.MinutesAgo(10));
            row.Props = "{\"ConditionName\":\"S7Alarm\",\"CpuAlarmId\":\"0x1\"}";
            tracker.SeedOpenAlarms([row], new List<EventLogRecord>());

            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            var report = tracker.EndRefresh(DateTime.UtcNow);

            var closing = Assert.Single(report.Closing);
            Assert.Equal("0x1", Props(closing)["CpuAlarmId"]);
            Assert.Equal("S7CommPlus", closing.SourceType);
        }
    }
}
