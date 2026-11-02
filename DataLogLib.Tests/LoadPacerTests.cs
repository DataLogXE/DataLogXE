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

using DataLogLib.Services;
using Xunit;

namespace DataLogLib.Tests
{
    public class LoadPacerTests
    {
        [Fact]
        public void Pauses_three_times_the_work_while_the_load_is_above_the_limit()
        {
            var pauses = new List<TimeSpan>();
            var pacer = new LoadPacer(0.8, 0.25, () => 2.0, (pause, _) => pauses.Add(pause));
            Thread.Sleep(40);
            pacer.Step();

            Assert.True(pacer.Slowed);
            Assert.Single(pauses);
            Assert.InRange(pauses[0].TotalMilliseconds, 110, 1000);
            Assert.Equal(pauses[0], pacer.Paused);
        }

        [Fact]
        public void A_pause_lasts_at_most_five_seconds()
        {
            var pauses = new List<TimeSpan>();
            var pacer = new LoadPacer(0.8, 0.01, () => 2.0, (pause, _) => pauses.Add(pause));
            Thread.Sleep(100);
            pacer.Step();

            Assert.Equal(TimeSpan.FromSeconds(5), pauses.Single());
        }

        [Theory]
        [InlineData(0.8, 0.5)]
        [InlineData(0.8, null)]
        [InlineData(0, 9.0)]
        public void Runs_at_full_speed_below_the_limit_without_a_load_or_without_a_limit(double maxLoad, double? load)
        {
            var pauses = new List<TimeSpan>();
            var pacer = new LoadPacer(maxLoad, 0.25, () => load, (pause, _) => pauses.Add(pause));
            pacer.Step();
            pacer.Step();

            Assert.False(pacer.Slowed);
            Assert.Empty(pauses);
        }

        [Fact]
        public void A_cancel_during_the_pause_ends_the_step()
        {
            using var cancel = new CancellationTokenSource();
            var pacer = new LoadPacer(0.8, 0.25, () => 2.0, (_, _) => cancel.Cancel());
            Assert.Throws<OperationCanceledException>(() => pacer.Step(cancel.Token));
        }
    }
}
