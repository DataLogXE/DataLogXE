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
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// The alarm table of the UI renders the state label in TypeScript. The archive sorts on the
    /// same text, so this mapping must stay in sync with datalog-ui/src/format.ts.
    /// </summary>
    public class EventLogLabelsTests
    {
        [Theory]
        [InlineData("Incoming", "Incoming")]
        [InlineData("Incoming/Outgoing", "Incoming/Outgoing")]
        [InlineData("Normal", "Normal")]
        [InlineData("ActiveUnacked", "Incoming")]
        [InlineData("ActiveAcked", "Incoming")]
        [InlineData("InactiveUnacked", "Inactive, unacknowledged")]
        [InlineData("Inactive", "Removed")]
        [InlineData("Removed", "Removed")]
        [InlineData("Disabled", "Disabled")]
        [InlineData("OutOfService", "Out of service")]
        [InlineData("Suppressed", "Suppressed")]
        [InlineData("Shelved", "Shelved")]
        [InlineData("SuppressedOrShelved", "Suppressed or shelved")]
        [InlineData("SomethingElse", "SomethingElse")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void AlarmState_maps_the_state_like_the_alarm_table(string state, string expected)
        {
            Assert.Equal(expected, EventLogLabels.AlarmState(state, null));
        }

        [Fact]
        public void AlarmState_marks_an_unconfirmed_state_and_appends_the_limit_state()
        {
            Assert.Equal("Incoming, unconfirmed", EventLogLabels.AlarmState("ActiveUnackedUnconfirmed", null));
            Assert.Equal("Incoming · Limited", EventLogLabels.AlarmState("ActiveUnacked", "Limited"));
            Assert.Equal("", EventLogLabels.AlarmState(null, "Limited"));
        }

        [Fact]
        public void LimitStateFromProps_reads_the_props_json()
        {
            Assert.Equal("Limited", EventLogLabels.LimitStateFromProps("{\"LimitState\":\"Limited\"}"));
            Assert.Equal("Limited", EventLogLabels.LimitStateFromProps("{\"LimitState\":\"  Limited \"}"));
            Assert.Equal("", EventLogLabels.LimitStateFromProps("{\"DsName\":\"Line A\"}"));
            Assert.Equal("", EventLogLabels.LimitStateFromProps("{ not json"));
            Assert.Equal("", EventLogLabels.LimitStateFromProps(""));
            Assert.Equal("", EventLogLabels.LimitStateFromProps(null));
        }

        [Fact]
        public void ClassLabel_prefers_the_symbol_and_formats_a_namespaced_name()
        {
            Assert.Equal("ProcessAlarm", EventLogLabels.ClassLabel(" ProcessAlarm ", "Namespace::Other"));
            Assert.Equal("Boiler", EventLogLabels.ClassLabel(null, "Namespace::Boiler"));
            Assert.Equal("Boiler", EventLogLabels.ClassLabel("  ", "Boiler"));
            Assert.Equal("", EventLogLabels.ClassLabel(null, null));
        }
    }
}
