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
    public class IngestRateTests
    {
        [Fact]
        public void A_steady_rate_stays_steady_although_the_chunks_beat_against_the_reads()
        {
            // 400 values/s written in chunks every 1.3 s (1 s sleep plus the write), read every 1 s
            var rate = new IngestRate(10, 10);
            var takeS = 0.0;
            rate.Add(takeS, 0);
            List<double> read = [];
            for (var nowS = 1.0; nowS <= 120; nowS += 1.0)
            {
                while (takeS + 1.3 <= nowS)
                {
                    takeS += 1.3;
                    rate.Add(takeS, 520);
                }
                read.Add(rate.Rate(nowS).GetValueOrDefault());
            }

            Assert.All(read.Skip(2), r => Assert.InRange(r, 399.9, 400.1));
        }

        [Fact]
        public void The_rate_follows_a_change_within_the_window_and_drops_to_0_without_writes()
        {
            var rate = new IngestRate(10, 10);
            Assert.Null(rate.Rate(0));
            for (var s = 0; s <= 30; s++)
            {
                rate.Add(s, s <= 15 ? 400 : 800);
            }

            Assert.Equal(800, rate.Rate(30).GetValueOrDefault(), 6);
            Assert.Equal(800, rate.Rate(40).GetValueOrDefault(), 6);
            Assert.Equal(0, rate.Rate(40.5));
        }
    }
}
