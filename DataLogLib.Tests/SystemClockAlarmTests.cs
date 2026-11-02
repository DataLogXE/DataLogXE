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
using DataLogService.DataSource.System;
using Xunit;

namespace DataLogLib.Tests
{
    public class SystemClockAlarmTests
    {
        private static readonly DateTime T0 = new(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);

        private readonly List<EventLogRecord> _published = [];
        private readonly SystemAlarm _alarm;

        public SystemClockAlarmTests()
        {
            _alarm = new SystemAlarm(1, () => [], _published.Add, () => T0);
        }

        private static DataSourceInfoModel Source(long? offsetMs, int warnMs = 10_000, bool monitor = true,
            DataSourceState state = DataSourceState.Connected, int id = 3, int typeId = (int)DataSourceType.OpcUa)
        {
            return new DataSourceInfoModel
            {
                DataSource = new DataSourceModel
                {
                    Id = id,
                    Name = "MyPlc1512SP",
                    TypeId = typeId,
                    IsActive = 1,
                    Host = "192.0.2.241",
                    Port = 4840,
                    ClockWarnMs = warnMs,
                    ClockMonitor = monitor
                },
                State = state,
                ClockOffsetMs = offsetMs
            };
        }

        private List<EventLogRecord> Clock() =>
            _published.Where(r => r.EventType == EventLogConstants.SystemClockAlarmEventType).ToList();

        private void Run(DataSourceInfoModel source, int seconds) => _alarm.Evaluate([source], T0.AddSeconds(seconds));

        [Fact]
        public void A_clock_off_raises_only_after_the_raise_delay()
        {
            Run(Source(-12_300), 0);
            Run(Source(-12_300), 28);
            Assert.Empty(Clock());

            Run(Source(-12_300), 30);

            var raise = Assert.Single(Clock());
            Assert.Equal(AlarmStateValues.Incoming, raise.AlarmState);
            Assert.True(raise.Active);
            Assert.Equal(EventLogConstants.SystemClockAlarmSeverity, raise.Severity);
            Assert.Equal(3, raise.AffectedSourceId);
            Assert.Contains("12.3 s behind", raise.Message);
            Assert.Contains("\"ClockOffsetMs\":\"-12300\"", raise.Props);
            Assert.Single(_alarm.CopyActiveAlarms());
        }

        [Fact]
        public void A_short_excursion_does_not_raise()
        {
            Run(Source(-12_000), 0);
            Run(Source(-6_000), 20);
            Run(Source(-12_000), 40);
            Run(Source(-12_000), 60);
            Assert.Empty(Clock());
        }

        [Fact]
        public void The_alarm_goes_only_clearly_below_the_limit()
        {
            Run(Source(12_000), 0);
            Run(Source(12_000), 30);
            Run(Source(9_500), 40);
            Assert.Single(Clock());

            Run(Source(8_900), 50);

            Assert.Equal(2, Clock().Count);
            Assert.Equal(AlarmStateValues.Normal, Clock()[1].AlarmState);
            Assert.Equal(Clock()[0].CorrelationId, Clock()[1].CorrelationId);
            Assert.Empty(_alarm.CopyActiveAlarms());
        }

        [Fact]
        public void Turning_the_watch_off_closes_the_alarm()
        {
            Run(Source(-12_000), 0);
            Run(Source(-12_000), 30);
            Run(Source(-12_000, monitor: false), 40);

            Assert.Equal(2, Clock().Count);
            Assert.Contains("no longer watching its clock", Clock()[1].Message);
        }

        [Fact]
        public void A_lost_connection_keeps_the_clock_alarm_and_raises_the_connection_alarm()
        {
            Run(Source(-12_000), 0);
            Run(Source(-12_000), 30);
            Run(Source(null, state: DataSourceState.Error), 40);

            Assert.Single(Clock());
            Assert.Single(_published, r => r.EventType == EventLogConstants.SystemAlarmEventType);
            Assert.Equal(2, _alarm.CopyActiveAlarms().Count);
        }

        [Fact]
        public void A_removed_source_closes_the_alarm()
        {
            Run(Source(-12_000), 0);
            Run(Source(-12_000), 30);
            _alarm.Evaluate([], T0.AddSeconds(40));

            Assert.Equal(2, Clock().Count);
            Assert.Contains("removed or deactivated", Clock()[1].Message);
        }

        [Fact]
        public void The_connection_alarm_names_the_reason()
        {
            var source = Source(null, state: DataSourceState.Error);
            source.ConnectionError = "Device clock is 4.2 h ahead (at most 3 h while the device times are taken as they are), connection refused";
            Run(source, 0);

            var raise = Assert.Single(_published, r => r.EventType == EventLogConstants.SystemAlarmEventType);
            Assert.Contains("Reason: Device clock is 4.2 h ahead", raise.Message);
            Assert.Contains("\"Reason\":", raise.Props);
        }

        [Fact]
        public void A_source_without_a_clock_is_not_watched()
        {
            Run(Source(-12_000, typeId: (int)DataSourceType.Test), 0);
            Run(Source(-12_000, typeId: (int)DataSourceType.Test), 30);
            Assert.Empty(Clock());
        }
    }
}
