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
using Xunit;

namespace DataLogLib.Tests
{
    public class ClockGuardTests
    {
        private static readonly DateTime Now = new(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Exactly_3_h_ahead_is_taken_more_is_not()
        {
            Assert.False(ClockGuard.IsTooFarAhead(Now.AddHours(3), Now));
            Assert.True(ClockGuard.IsTooFarAhead(Now.AddHours(3).AddMilliseconds(1), Now));
            Assert.False(ClockGuard.IsTooFarAhead(Now.AddYears(-30), Now));
        }

        [Fact]
        public void Local_times_are_compared_on_the_utc_clock()
        {
            var local = Now.AddHours(3).AddMinutes(1).ToLocalTime();
            Assert.Equal(DateTimeKind.Local, local.Kind);
            Assert.True(ClockGuard.IsTooFarAhead(local, Now));
            Assert.False(ClockGuard.IsTooFarAhead(Now.AddHours(2).ToLocalTime(), Now));
        }

        [Fact]
        public void A_device_more_than_3_h_ahead_is_refused()
        {
            Assert.False(ClockGuard.RefuseConnection(ClockGuard.MaxAheadMs, refusedBefore: false));
            Assert.True(ClockGuard.RefuseConnection(ClockGuard.MaxAheadMs + 1, refusedBefore: false));
            Assert.False(ClockGuard.RefuseConnection(-ClockGuard.MaxAheadMs * 10, refusedBefore: false));
        }

        [Fact]
        public void After_a_refusal_the_clock_must_be_back_below_the_hysteresis()
        {
            long justBelowLimit = ClockGuard.MaxAheadMs - 60_000;
            Assert.True(ClockGuard.RefuseConnection(justBelowLimit, refusedBefore: true));
            Assert.False(ClockGuard.RefuseConnection(ClockGuard.MaxAheadMs - ClockGuard.RejoinHysteresisMs, refusedBefore: true));
        }

        [Fact]
        public void Admit_drops_only_times_too_far_ahead()
        {
            Assert.True(ClockGuard.Admit(DateTime.UtcNow.AddHours(2), "test values"));
            Assert.False(ClockGuard.Admit(DateTime.UtcNow.AddHours(4), "test values"));
            Assert.False(ClockGuard.Admit(DateTime.Now.AddYears(70), "test values"));
        }

        [Fact]
        public void The_refusal_text_names_the_hours()
        {
            Assert.Contains("4.2 h ahead", ClockGuard.RefusalText(15_120_000));
        }
    }
}
