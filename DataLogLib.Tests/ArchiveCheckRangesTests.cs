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

using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public class ArchiveCheckRangesTests
    {
        private static readonly DateTime Now = new(2026, 10, 29, 19, 25, 40, DateTimeKind.Utc);

        [Fact]
        public void A_value_of_days_ago_is_checked_apart_from_the_current_windows()
        {
            // The C001 batch on the Pi on 2026-10-29: 107 tags with the windows of one cycle and a startup
            // value 4 days back, which made one check read all 107 tags over the 4 days
            List<(int TagId, DateTime Time)> keys = [];
            for (var tag = 1; tag <= 107; tag++)
            {
                for (var second = 0; second < 10; second++)
                {
                    keys.Add((tag, Now.AddSeconds(second)));
                }
            }
            keys.Add((200, Now.AddDays(-4)));

            var ranges = TimescaleArchive.CheckRanges(keys, 1);

            Assert.Equal(2, ranges.Count);
            Assert.Equal([200], ranges[0].TagIds);
            Assert.Equal(Now.AddDays(-4), ranges[0].First);
            Assert.Equal(Now.AddDays(-4), ranges[0].Last);
            Assert.Equal(Enumerable.Range(1, 107), ranges[1].TagIds.Order());
            Assert.Equal(Now, ranges[1].First);
            Assert.Equal(Now.AddSeconds(9), ranges[1].Last);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(3600)]
        public void Every_key_lies_in_a_range_of_its_tag_and_no_range_exceeds_the_budget(int windowSeconds)
        {
            // A repair of one slow tag over 3 days, a catch-up of 119 tags over an hour and two single old values
            List<(int TagId, DateTime Time)> keys = [];
            for (var second = 0; second < 3 * 86400; second += 60)
            {
                keys.Add((1, Now.AddDays(-5).AddSeconds(second)));
            }
            for (var tag = 2; tag <= 120; tag++)
            {
                for (var second = 0; second < 3600; second += 4)
                {
                    keys.Add((tag, Now.AddSeconds(second)));
                }
            }
            keys.Add((300, Now.AddDays(-30)));
            keys.Add((301, Now.AddHours(-12)));

            var ranges = TimescaleArchive.CheckRanges(keys, windowSeconds);

            foreach (var (tagIds, first, last) in ranges)
            {
                var windows = (last - first).Ticks / (windowSeconds * TimeSpan.TicksPerSecond) + 1;
                Assert.True(windows * tagIds.Length <= TimescaleArchive.CheckWindowBudget,
                    $"{tagIds.Length} tags over {windows} windows from {first:O}");
            }
            var rangeTags = ranges.Select(r => (Tags: r.TagIds.ToHashSet(), r.First, r.Last)).ToList();
            Assert.All(keys, key => Assert.Contains(rangeTags,
                r => r.First <= key.Time && key.Time <= r.Last && r.Tags.Contains(key.TagId)));
            // Not one check per key: the slow tag needs 11 ranges of C001 and the catch-up 18
            Assert.InRange(ranges.Count, 1, 40);
        }

        [Fact]
        public void No_keys_need_no_check()
        {
            Assert.Empty(TimescaleArchive.CheckRanges([], 1));
        }
    }
}
