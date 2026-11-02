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
using Xunit;

namespace DataLogLib.Tests
{
    public class SystemConnectionAlarmRulesTests
    {
        [Fact]
        public void ShouldMonitor_active_opc_ua_returns_true()
        {
            var model = new DataSourceModel { TypeId = (int)DataSourceType.OpcUa, IsActive = 1 };
            Assert.True(SystemConnectionAlarmRules.ShouldMonitor(model));
        }

        [Theory]
        [InlineData(DataSourceType.System)]
        [InlineData(DataSourceType.Test)]
        [InlineData(DataSourceType.Undefined)]
        public void ShouldMonitor_non_opc_ua_returns_false(DataSourceType type)
        {
            var model = new DataSourceModel { TypeId = (int)type, IsActive = 1 };
            Assert.False(SystemConnectionAlarmRules.ShouldMonitor(model));
        }

        [Fact]
        public void ShouldMonitor_inactive_returns_false()
        {
            var model = new DataSourceModel { TypeId = (int)DataSourceType.OpcUa, IsActive = 0 };
            Assert.False(SystemConnectionAlarmRules.ShouldMonitor(model));
        }

        [Fact]
        public void ShouldMonitor_null_returns_false()
        {
            Assert.False(SystemConnectionAlarmRules.ShouldMonitor(null));
        }

        [Fact]
        public void Decide_error_returns_raise()
        {
            Assert.Equal(
                SystemConnectionAlarmRules.AlarmCycleAction.Raise,
                SystemConnectionAlarmRules.Decide(DataSourceState.Error));
        }

        [Theory]
        [InlineData(DataSourceState.Connected)]
        [InlineData(DataSourceState.Inactive)]
        public void Decide_healthy_states_return_clear(DataSourceState state)
        {
            Assert.Equal(SystemConnectionAlarmRules.AlarmCycleAction.Clear, SystemConnectionAlarmRules.Decide(state));
        }

        [Theory]
        [InlineData(DataSourceState.Connecting)]
        [InlineData(DataSourceState.Disconnected)]
        [InlineData(DataSourceState.Disconnecting)]
        [InlineData(DataSourceState.Loading)]
        [InlineData(DataSourceState.Undefined)]
        public void Decide_transitional_states_return_none(DataSourceState state)
        {
            Assert.Equal(SystemConnectionAlarmRules.AlarmCycleAction.None, SystemConnectionAlarmRules.Decide(state));
        }
    }
}
