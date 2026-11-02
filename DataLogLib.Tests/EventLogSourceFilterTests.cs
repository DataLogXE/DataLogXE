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
    public class EventLogSourceFilterTests
    {
        [Fact]
        public void MatchesSourceFilter_empty_filter_matches_all()
        {
            var record = new EventLogRecord { SourceId = 1 };
            Assert.True(EventLogSourceFilter.MatchesSourceFilter(record, null));
            Assert.True(EventLogSourceFilter.MatchesSourceFilter(record, []));
        }

        [Fact]
        public void MatchesSourceFilter_direct_source_id()
        {
            var record = new EventLogRecord { SourceId = 2, Component = EventLogConstants.AlarmComponent };
            Assert.True(EventLogSourceFilter.MatchesSourceFilter(record, [2]));
            Assert.False(EventLogSourceFilter.MatchesSourceFilter(record, [3]));
        }

        [Fact]
        public void MatchesSourceFilter_system_alarm_by_affected_source_id()
        {
            var record = new EventLogRecord
            {
                SourceId = 1,
                Component = EventLogConstants.SystemAlarmComponent,
                AffectedSourceId = 2
            };
            Assert.True(EventLogSourceFilter.MatchesSourceFilter(record, [2]));
            Assert.False(EventLogSourceFilter.MatchesSourceFilter(record, [3]));
        }

        [Fact]
        public void MatchesSourceFilter_system_alarm_from_props_fallback()
        {
            var record = new EventLogRecord
            {
                SourceId = 1,
                Component = EventLogConstants.SystemAlarmComponent,
                Props = "{\"AffectedSourceId\":\"5\",\"DsName\":\"Opc\"}"
            };
            Assert.True(EventLogSourceFilter.MatchesSourceFilter(record, [5]));
        }

        [Fact]
        public void TryParseAffectedSourceIdFromProps_invalid_json_returns_zero()
        {
            Assert.Equal(0, EventLogSourceFilter.TryParseAffectedSourceIdFromProps("{not json"));
        }
    }
}
