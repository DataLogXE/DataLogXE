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
    public class LoadGuardTests
    {
        private static readonly LoadReadings Calm = new(0.3, 340L << 20, 10, 0, TimeSpan.Zero);

        [Fact]
        public void Each_reading_has_its_limits()
        {
            Assert.Equal(LoadLevel.Normal, LoadGuard.Classify(Calm).Level);
            Assert.Equal(LoadLevel.Normal, LoadGuard.Classify(new LoadReadings(null, null, null, 0, TimeSpan.Zero)).Level);

            Assert.Equal(LoadLevel.Tense, LoadGuard.Classify(Calm with { LoadPerCore = 1.2 }).Level);
            Assert.Equal(LoadLevel.Critical, LoadGuard.Classify(Calm with { LoadPerCore = 2.5 }).Level);
            Assert.Equal(LoadLevel.Tense, LoadGuard.Classify(Calm with { SwapInPagesPerSec = 300 }).Level);
            Assert.Equal(LoadLevel.Critical, LoadGuard.Classify(Calm with { SwapInPagesPerSec = 1500 }).Level);
            Assert.Equal(LoadLevel.Tense, LoadGuard.Classify(Calm with { InsertBacklog = 0.3 }).Level);
            Assert.Equal(LoadLevel.Critical, LoadGuard.Classify(Calm with { InsertBacklog = 1 }).Level);
            Assert.Equal(LoadLevel.Critical, LoadGuard.Classify(Calm with { MemAvailableBytes = 80L << 20 }).Level);
            Assert.Equal(LoadLevel.Tense, LoadGuard.Classify(Calm with { PoolWait = TimeSpan.FromSeconds(2) }).Level);

            var (level, reason) = LoadGuard.Classify(Calm with { LoadPerCore = 2.4, PoolWait = TimeSpan.FromSeconds(1.5) });
            Assert.Equal(LoadLevel.Critical, level);
            Assert.Equal("load 2.4 per core, 1.5 s wait for a database connection", reason);
        }

        [Fact]
        public void A_high_load_alone_does_not_put_the_recording_at_risk()
        {
            Assert.False(LoadGuard.PutsRecordingAtRisk(Calm with { LoadPerCore = 3, PoolWait = TimeSpan.FromSeconds(5) }));
            Assert.False(LoadGuard.PutsRecordingAtRisk(Calm with { InsertBacklog = 0.3, SwapInPagesPerSec = 300 }));
            Assert.True(LoadGuard.PutsRecordingAtRisk(Calm with { InsertBacklog = 0.7 }));
            Assert.True(LoadGuard.PutsRecordingAtRisk(Calm with { MemAvailableBytes = 80L << 20 }));
            Assert.True(LoadGuard.PutsRecordingAtRisk(Calm with { SwapInPagesPerSec = 1500 }));

            var guard = new LoadGuard(() => null, () => null, () => null);
            guard.Apply(Calm with { LoadPerCore = 3 });
            Assert.False(guard.RecordingAtRisk);
            guard.Apply(Calm with { InsertBacklog = 0.9 });
            Assert.True(guard.RecordingAtRisk);
        }

        [Fact]
        public void A_level_is_entered_after_two_samples_and_left_one_step_at_a_time()
        {
            var guard = new LoadGuard(() => null, () => null, () => null);
            var critical = Calm with { LoadPerCore = 3 };

            // A single peak does not count
            guard.Apply(critical);
            guard.Apply(Calm);
            Assert.Equal(LoadLevel.Normal, guard.Level);

            guard.Apply(critical);
            guard.Apply(critical);
            Assert.Equal(LoadLevel.Critical, guard.Level);
            Assert.Equal("load 3.0 per core", guard.Reason);

            for (var i = 0; i < LoadGuard.LeaveSamples - 1; i++)
            {
                guard.Apply(Calm);
            }
            Assert.Equal(LoadLevel.Critical, guard.Level);
            guard.Apply(Calm);
            Assert.Equal(LoadLevel.Tense, guard.Level);
            Assert.Equal("calming down after critical", guard.Reason);
            // A peak while it calms down starts the count again
            guard.Apply(critical);
            for (var i = 0; i < LoadGuard.LeaveSamples; i++)
            {
                guard.Apply(Calm);
            }
            Assert.Equal(LoadLevel.Normal, guard.Level);
            Assert.Equal("", guard.Reason);
        }

        [Fact]
        public async Task Waiting_work_goes_on_once_the_guard_is_below_its_level()
        {
            var guard = new LoadGuard(() => null, () => null, () => null);
            var tense = Calm with { InsertBacklog = 0.5 };
            guard.Apply(tense);
            guard.Apply(tense);

            List<string> phases = [];
            var large = guard.WaitBelowAsync(LoadLevel.Tense, phases.Add, CancellationToken.None);
            var medium = guard.WaitBelowAsync(LoadLevel.Critical, phases.Add, CancellationToken.None);
            Assert.True(medium.IsCompleted);
            Assert.False(large.IsCompleted);
            Assert.Equal(["Waiting: the system is busy (insert buffer 50% full)"], phases);

            var pacer = new LoadPacer(0.8, readLoad: () => 0.1, guard: guard);
            var step = Task.Run(() => pacer.Step());
            await Task.Delay(200);
            Assert.False(step.IsCompleted);

            for (var i = 0; i < LoadGuard.LeaveSamples; i++)
            {
                guard.Apply(Calm);
            }
            await large.WaitAsync(TimeSpan.FromSeconds(5));
            await step.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task A_wait_ends_when_the_work_is_cancelled()
        {
            var guard = new LoadGuard(() => null, () => null, () => null);
            var critical = Calm with { MemAvailableBytes = 50L << 20 };
            guard.Apply(critical);
            guard.Apply(critical);
            Assert.Equal("50 MB memory available", guard.Reason);

            using var cancel = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.WaitBelowAsync(LoadLevel.Critical, null, cancel.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => guard.WaitBelow(LoadLevel.Tense, "Test", new CancellationTokenSource(100).Token));
        }
    }
}
