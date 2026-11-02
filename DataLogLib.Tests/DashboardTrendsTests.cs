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

using DataLogService;
using Xunit;

namespace DataLogLib.Tests
{
    public class DashboardTrendsTests
    {
        private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Series_keeps_the_last_ten_minutes_and_leaves_out_unknown_values()
        {
            var rate = 0.0;
            double? load = null;
            var trends = new DashboardTrends(() => rate, () => load, () => 3, _ => null, (_, _) => 0);

            for (var i = 0; i < DashboardTrends.SampleCapacity + 5; i++)
            {
                rate = i;
                load = i % 2 == 0 ? i : null;
                trends.Sample(T0 + i * DashboardTrends.SampleCycle);
            }

            var snapshot = trends.GetSnapshot();
            Assert.Equal(DashboardTrends.SampleCapacity, snapshot.ValuesPerSecSeries.Length);
            Assert.Equal(5, snapshot.ValuesPerSecSeries[0]);
            Assert.Equal(DashboardTrends.SampleCapacity + 4, snapshot.ValuesPerSecSeries[^1]);
            Assert.All(snapshot.LoadPercentSeries, value => Assert.Equal(0, value % 2));
            Assert.Equal(3, snapshot.ActiveTags);
        }

        [Fact]
        public void Database_and_alarms_are_read_only_on_their_cycle()
        {
            var sizeReads = new List<DateTime>();
            var alarmReads = new List<(DateTime From, DateTime To)>();
            var trends = new DashboardTrends(
                () => 1,
                () => 1,
                () => 1,
                at => { sizeReads.Add(at); return 4.5; },
                (from, to) => { alarmReads.Add((from, to)); return 7; });

            // Eleven minutes of samples
            for (var i = 0; i <= 66; i++)
            {
                trends.Sample(T0 + i * DashboardTrends.SampleCycle);
            }

            Assert.Equal(2, sizeReads.Count);
            Assert.Equal(T0 - DashboardTrends.GrowthSpan, sizeReads[0]);
            Assert.Equal(12, alarmReads.Count);
            Assert.Equal((T0 - DashboardTrends.AlarmSpan, T0), alarmReads[0]);

            var snapshot = trends.GetSnapshot();
            Assert.Equal(4.5, snapshot.DatabaseSizeGbWeekAgo);
            Assert.Equal(7, snapshot.IncomingAlarms24h);
        }

        [Fact]
        public void A_failing_read_gives_unknown_and_the_next_cycle_tries_again()
        {
            var fail = true;
            var trends = new DashboardTrends(
                () => throw new InvalidOperationException("archive down"),
                () => 3,
                () => fail ? throw new InvalidOperationException("db down") : 135,
                _ => fail ? throw new InvalidOperationException("db down") : 2.0,
                (_, _) => fail ? throw new InvalidOperationException("db down") : 1);

            trends.Sample(T0);
            var failed = trends.GetSnapshot();
            Assert.Empty(failed.ValuesPerSecSeries);
            Assert.Equal([3.0], failed.LoadPercentSeries);
            Assert.Null(failed.ActiveTags);
            Assert.Null(failed.DatabaseSizeGbWeekAgo);
            Assert.Null(failed.IncomingAlarms24h);

            fail = false;
            trends.Sample(T0 + DashboardTrends.GrowthCycle);
            var recovered = trends.GetSnapshot();
            Assert.Equal(135, recovered.ActiveTags);
            Assert.Equal(2.0, recovered.DatabaseSizeGbWeekAgo);
            Assert.Equal(1, recovered.IncomingAlarms24h);
        }
    }
}
