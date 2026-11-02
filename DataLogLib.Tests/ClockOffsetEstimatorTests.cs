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

using DataLogService.DataSource;
using Xunit;

namespace DataLogLib.Tests
{
    public class ClockOffsetEstimatorTests
    {
        private static readonly DateTime Start = new(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Unknown_before_the_first_sample()
        {
            Assert.Null(new ClockOffsetEstimator().ShiftMs);
        }

        [Fact]
        public void Slow_samples_after_a_start_give_way_to_the_first_fast_one()
        {
            // The case of 2026-10-18: 10.3 s measured under load, the true shift is 6.0 s.
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(10_329, Start, out _);
            estimator.AddSample(8_100, Start.AddSeconds(1), out _);
            bool reported = estimator.AddSample(6_044, Start.AddSeconds(2), out long estimate);

            Assert.True(reported);
            Assert.Equal(6_044, estimate);
            Assert.Equal(6_044, estimator.ShiftMs);
        }

        [Fact]
        public void Single_slow_samples_do_not_move_the_estimate()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            bool reported = estimator.AddSample(9_500, Start.AddSeconds(5), out long estimate);

            Assert.False(reported);
            Assert.Equal(6_000, estimate);
        }

        [Fact]
        public void Small_changes_are_taken_but_not_reported()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            bool reported = estimator.AddSample(5_990, Start.AddSeconds(1), out long estimate);

            Assert.False(reported);
            Assert.Equal(5_990, estimate);
        }

        [Fact]
        public void Device_clock_set_forward_is_taken_at_once()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            bool reported = estimator.AddSample(-54_000, Start.AddSeconds(1), out long estimate);

            Assert.True(reported);
            Assert.Equal(-54_000, estimate);
        }

        [Fact]
        public void Device_clock_set_back_is_taken_after_the_confirm_time()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);

            for (int s = 10; s < 70; s += 10)
            {
                estimator.AddSample(66_000 + s, Start.AddSeconds(s), out long during);
                Assert.Equal(6_000, during);
            }
            bool reported = estimator.AddSample(66_005, Start.AddSeconds(70), out long estimate);

            Assert.True(reported);
            Assert.Equal(66_005, estimate);
        }

        [Fact]
        public void A_load_peak_shorter_than_the_confirm_time_does_not_move_the_estimate()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            for (int s = 1; s <= 50; s++)
            {
                estimator.AddSample(9_000, Start.AddSeconds(s), out _);
            }
            // One fast sample ends the peak, later slow samples start a new one.
            estimator.AddSample(6_010, Start.AddSeconds(51), out _);
            for (int s = 52; s <= 100; s++)
            {
                estimator.AddSample(9_000, Start.AddSeconds(s), out _);
            }

            Assert.Equal(6_000, estimator.ShiftMs);
        }

        [Fact]
        public void Old_samples_leave_the_window_so_drift_is_followed()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            estimator.AddSample(6_400, Start.AddMinutes(5), out _);
            Assert.Equal(6_000, estimator.ShiftMs);

            estimator.AddSample(6_400, Start.AddMinutes(11), out long estimate);

            Assert.Equal(6_400, estimate);
        }

        [Fact]
        public void Reset_forgets_the_estimate()
        {
            var estimator = new ClockOffsetEstimator();
            estimator.AddSample(6_000, Start, out _);
            estimator.Reset();

            Assert.Null(estimator.ShiftMs);
            Assert.True(estimator.AddSample(7_000, Start.AddSeconds(1), out long estimate));
            Assert.Equal(7_000, estimate);
        }
    }
}
