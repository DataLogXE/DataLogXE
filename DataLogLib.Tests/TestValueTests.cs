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

using DataLogLib.Models;
using DataLogService.DataSource.Test;
using Xunit;

namespace DataLogLib.Tests
{
    public class TestValueTests
    {
        private static TestTypeDef Type(TestSignalKind kind, int periodSec) =>
            TestTypes.All.Single(t => t.Kind == kind && t.PeriodSec == periodSec);

        [Fact]
        public void Every_type_has_its_own_name_that_is_not_the_prefix_of_another()
        {
            var names = TestTypes.All.Select(t => $"{t.Name} ").ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var name in names)
            {
                Assert.Single(names, n => n.StartsWith(name, StringComparison.Ordinal));
            }
        }

        [Fact]
        public void Bool_types_blink_with_several_periods_and_spike_with_several_intervals()
        {
            var blink = TestTypes.All.Where(t => t.Kind == TestSignalKind.BoolBlink).ToList();
            var spike = TestTypes.All.Where(t => t.Kind == TestSignalKind.BoolSpike).ToList();

            Assert.True(blink.Count >= 3);
            Assert.True(spike.Count >= 3);
            Assert.All(blink.Concat(spike), t => Assert.True(t.IsBool));
            Assert.DoesNotContain(TestTypes.All.Except(blink).Except(spike), t => t.IsBool);
        }

        [Fact]
        public void A_blinking_tag_is_true_in_the_first_half_of_the_period_and_false_in_the_second()
        {
            var value = new TestValue(new DataTagModel { Id = 1 }, Type(TestSignalKind.BoolBlink, 1));

            var seen = new HashSet<double>();
            var end = DateTime.UtcNow.AddMilliseconds(2500);
            while (DateTime.UtcNow < end)
            {
                seen.Add(value.Value);
                Thread.Sleep(20);
            }

            Assert.Equal([0.0, 1.0], seen.OrderBy(v => v));
        }

        [Fact]
        public void A_spiking_tag_stays_false_until_the_first_interval_has_passed()
        {
            var value = new TestValue(new DataTagModel { Id = 1 }, Type(TestSignalKind.BoolSpike, 36000));

            Assert.Equal(0, value.Value);
            Assert.Equal(0, value.Value);
        }
    }
}
