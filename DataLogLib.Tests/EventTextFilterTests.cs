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
    /// The alarm search matches like the data tag filter and datalog-ui/src/textFilter.ts.
    /// </summary>
    public class EventTextFilterTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\"\"")]
        public void Parse_without_text_is_empty(string q)
        {
            var filter = EventTextFilter.Parse(q);
            Assert.True(filter.IsEmpty);
            Assert.Null(filter.LikePattern);
            Assert.True(filter.Matches("anything"));
        }

        [Theory]
        [InlineData("motor", "%motor%")]
        [InlineData(" Motor ", "%Motor%")]
        [InlineData("Temp*High", "%Temp%High%")]
        [InlineData("?otor", "%_otor%")]
        [InlineData("50%_a\\b", "%50\\%\\_a\\\\b%")]
        [InlineData("\"Motor 1\"", "%Motor 1%")]
        public void Parse_builds_the_like_pattern(string q, string expected)
        {
            Assert.Equal(expected, EventTextFilter.Parse(q).LikePattern);
        }

        [Theory]
        [InlineData("motor", "Motor M1 hot", true)]
        [InlineData("m1 HOT", "Motor M1 hot", true)]
        [InlineData("Motor*hot", "Motor M1 hot", true)]
        [InlineData("M?", "Motor M1 hot", true)]
        [InlineData("valve", "Motor M1 hot", false)]
        [InlineData("50%", "Level 50% reached", true)]
        [InlineData("50%", "Level 500 reached", false)]
        [InlineData("a_b", "axb", false)]
        public void Matches_like_the_tag_filter(string q, string text, bool expected)
        {
            Assert.Equal(expected, EventTextFilter.Parse(q).Matches(text));
        }

        [Fact]
        public void MatchingAlarmStates_uses_raw_values_and_labels()
        {
            var unacknowledged = EventTextFilter.Parse("unacknowledged").MatchingAlarmStates();
            Assert.Contains(AlarmStateValues.InactiveUnacked, unacknowledged);
            Assert.Contains(AlarmStateValues.InactiveUnacked + AlarmStateValues.UnconfirmedSuffix, unacknowledged);
            Assert.DoesNotContain(AlarmStateValues.Incoming, unacknowledged);

            // ActiveUnacked is shown as "Incoming".
            var incoming = EventTextFilter.Parse("incom*").MatchingAlarmStates();
            Assert.Contains(AlarmStateValues.Incoming, incoming);
            Assert.Contains(AlarmStateValues.ActiveUnacked, incoming);

            var unconfirmed = EventTextFilter.Parse("unconfirmed").MatchingAlarmStates();
            Assert.All(unconfirmed, state => Assert.EndsWith(AlarmStateValues.UnconfirmedSuffix, state));

            Assert.Empty(EventTextFilter.Parse("Motor").MatchingAlarmStates());
        }
    }
}
