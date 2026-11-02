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
using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public class CompressionRepairWorkerTests
    {
        private static readonly DateTime Day = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Local);
        private static readonly string Uid = FakeArchive.Uid(1);

        // One value every stepSeconds between the hours
        private static List<ArchiveValue> Raw(double fromHour, double toHour, double stepSeconds = 10) =>
            Enumerable.Range(0, (int)((toHour - fromHour) * 3600 / stepSeconds))
                .Select(i => new ArchiveValue
                {
                    Id = 1,
                    Value = Math.Sin(i / 5.0) * (1 + i % 7),
                    Time = Day.AddHours(fromHour).AddSeconds(i * stepSeconds),
                    State = ValueState.Valid
                })
                .ToList();

        // Without pauses, the tests do not wait for the duty cycle or the load of the test machine
        private static CompressionRepairWorker Worker(FakeArchive archive, int checkDays = CompressionRepairWorker.DefaultCheckDays) =>
            new(archive, checkDays, dutyCycle: 1, maxLoad: 0);

        // Returns the loads in turn, the last one from then on, and counts the reads
        private static CompressionRepairWorker LoadWorker(FakeArchive archive, List<double?> reads, params double?[] loads) =>
            new(archive, dutyCycle: 1, readLoad: () =>
            {
                var load = loads[Math.Min(reads.Count, loads.Length - 1)];
                reads.Add(load);
                return load;
            }, loadCheckMs: 1);

        private static void AssertLevelsComplete(FakeArchive archive, List<ArchiveValue> raw)
        {
            foreach (var hour in raw.GroupBy(v => CompressionLevels.WindowStart(v.Time, CompressionLevels.Coarsest)))
            {
                var expected = CompressionWorkerTests.M4Times(hour.ToList()).ToHashSet();
                foreach (var level in CompressionLevels.CascadeLevels)
                {
                    var times = archive.Compressed(Uid, level.Type)
                        .Where(v => v.Time >= hour.Key && v.Time < hour.Key.AddHours(1))
                        .Select(v => v.Time)
                        .ToHashSet();
                    Assert.Superset(expected, times);
                }
            }
        }

        [Fact]
        public void Startup_rebuilds_the_hours_before_the_startup()
        {
            // Crash at 10:40: the open windows of hour 9 and 10 never reached the levels
            var archive = new FakeArchive();
            var raw = Raw(9, 10 + 40 / 60.0);
            archive.AddRaw(Uid, raw);
            archive.SetCompressionVerifiedUntil(Day.AddHours(9));

            Worker(archive).RunStartup(Day.AddHours(10).AddMinutes(41));

            AssertLevelsComplete(archive, raw);
        }

        [Fact]
        public void Startup_rebuilds_incomplete_hours_since_the_last_check()
        {
            var archive = new FakeArchive();
            var complete = Raw(5, 6);
            var missing = Raw(6, 7);
            var partial = Raw(7, 8);
            archive.AddRaw(Uid, complete.Concat(missing).Concat(partial));

            // Hour 5 is complete, hour 6 has no level points, hour 7 lost its first half (crash)
            archive.InsertValues(CompressionCascade.CompressAll(complete, []));
            archive.InsertValues(CompressionCascade.CompressAll(partial.Where(v => v.Time.Minute >= 30).ToList(), []));
            var writtenBefore = archive.Compressed(Uid).Count(v => v.Time.Hour == 5);

            Worker(archive).RunStartup(Day.AddHours(12));

            AssertLevelsComplete(archive, complete.Concat(missing).Concat(partial).ToList());
            // The complete hour is not rebuilt
            Assert.Equal(writtenBefore, archive.Compressed(Uid).Count(v => v.Time.Hour == 5));
            Assert.DoesNotContain(archive.RawReads, r => r.Start <= Day.AddHours(5) && r.End > Day.AddHours(5));
            // Checked up to the hours rebuilt on every startup
            Assert.Equal(Day.AddHours(11), archive.GetCompressionVerifiedUntil());
        }

        [Fact]
        public void Startup_checks_only_the_hours_after_the_last_check()
        {
            var archive = new FakeArchive();
            archive.AddRaw(Uid, Raw(5, 8));
            archive.SetCompressionVerifiedUntil(Day.AddHours(7));

            Worker(archive).RunStartup(Day.AddHours(12));

            Assert.All(archive.CoverageReads, r => Assert.True(r.Start >= Day.AddHours(7)));
            // Hours before the last check stay as they are
            Assert.DoesNotContain(archive.Compressed(Uid), v => v.Time < Day.AddHours(7));
        }

        [Fact]
        public void Lost_values_rebuild_their_hours()
        {
            var archive = new FakeArchive();
            var raw = Raw(5, 6);
            archive.AddRaw(Uid, raw);
            var worker = Worker(archive);

            worker.Enqueue([raw[100], raw[200]]);
            worker.ProcessQueue();

            AssertLevelsComplete(archive, raw);
            Assert.Single(archive.RawReads);
        }

        [Fact]
        public void Consecutive_hours_are_read_together_up_to_the_value_limit()
        {
            var archive = new FakeArchive();
            archive.AddRaw(Uid, Raw(0, 6));
            archive.SetCompressionVerifiedUntil(Day);

            Worker(archive).RunStartup(Day.AddHours(12));

            // 6 hours with 360 values each fit into one read
            Assert.Single(archive.RawReads, r => r.End <= Day.AddHours(11));
            Assert.Equal((Day, Day.AddHours(6)), archive.RawReads.Select(r => (r.Start, r.End)).First());
        }

        [Fact]
        public void Startup_after_a_shutdown_reads_nothing()
        {
            // The shutdown wrote the open windows: every level reaches the last raw value
            var archive = new FakeArchive();
            var raw = Raw(9, 10 + 40 / 60.0);
            archive.AddRaw(Uid, raw);
            archive.InsertValues(CompressionCascade.CompressAll(raw, []));
            archive.SetCompressionVerifiedUntil(Day.AddHours(9));

            Worker(archive).RunStartup(Day.AddHours(10).AddMinutes(45));

            Assert.Empty(archive.RawReads);
        }

        [Fact]
        public void Startup_after_a_crash_rebuilds_from_the_end_of_the_last_closed_windows()
        {
            // The levels were written up to 10:00 (hour 9 closed), the crash lost 10:00 to 10:40
            var archive = new FakeArchive();
            var closed = Raw(9, 10);
            var lost = Raw(10, 10 + 40 / 60.0);
            archive.AddRaw(Uid, closed.Concat(lost));
            archive.InsertValues(CompressionCascade.CompressAll(closed, []));
            archive.SetCompressionVerifiedUntil(Day.AddHours(9));

            Worker(archive).RunStartup(Day.AddHours(10).AddMinutes(45));

            // From the end of the earliest last window: the C001 window of 9:59:50 ends at 9:59:51
            var read = Assert.Single(archive.RawReads);
            Assert.Equal(Day.AddHours(10).AddSeconds(-9), read.Start);
            AssertLevelsComplete(archive, closed.Concat(lost).ToList());
        }

        [Fact]
        public void Without_a_check_only_the_last_days_are_checked()
        {
            var archive = new FakeArchive();
            archive.AddRaw(Uid, Raw(-5 * 24, -5 * 24 + 1));
            archive.AddRaw(Uid, Raw(-24, -23));

            Worker(archive, checkDays: 2).RunStartup(Day.AddHours(12));

            Assert.All(archive.CoverageReads, r => Assert.True(r.Start >= Day.AddHours(11).AddDays(-2)));
            Assert.DoesNotContain(archive.Compressed(Uid), v => v.Time < Day.AddDays(-2));
            Assert.Contains(archive.Compressed(Uid), v => v.Time.Date == Day.AddDays(-1));
        }

        [Fact]
        public void The_check_keeps_its_progress_after_each_day()
        {
            var archive = new FakeArchive();
            var raw = Raw(-48, -47).Concat(Raw(-24, -23)).Concat(Raw(5, 6)).ToList();
            archive.AddRaw(Uid, raw);

            Worker(archive, checkDays: 3).RunStartup(Day.AddHours(12));

            // From 3 days before the hours rebuilt on every startup, one day per step
            var checkStart = Day.AddHours(11).AddDays(-3);
            Assert.Equal([checkStart.AddDays(1), checkStart.AddDays(2), Day.AddHours(11)], archive.VerifiedHistory);
            AssertLevelsComplete(archive, raw);
        }

        [Fact]
        public void A_restart_continues_after_the_checked_days()
        {
            // The first run checked the oldest day before the restart
            var archive = new FakeArchive();
            archive.AddRaw(Uid, Raw(-48, -47).Concat(Raw(-24, -23)));
            var checkedUntil = Day.AddHours(11).AddDays(-2);
            archive.SetCompressionVerifiedUntil(checkedUntil);

            Worker(archive, checkDays: 3).RunStartup(Day.AddHours(12));

            Assert.All(archive.CoverageReads, r => Assert.True(r.Start >= checkedUntil));
            Assert.DoesNotContain(archive.Compressed(Uid), v => v.Time < checkedUntil);
            Assert.Contains(archive.Compressed(Uid), v => v.Time.Date == Day.AddDays(-1));
        }

        [Fact]
        public void A_dense_range_is_compressed_in_batches_with_the_same_result()
        {
            // 72000 values (20 Hz) are more than one batch: the cascade gets them in parts
            var archive = new FakeArchive();
            var raw = Raw(5, 6, 0.05);
            Assert.True(raw.Count > CompressionRepairWorker.BatchValues);
            archive.AddRaw(Uid, raw);
            var worker = Worker(archive);

            worker.Enqueue([raw[0]]);
            worker.ProcessQueue();

            var expected = CompressionCascade.CompressAll(raw, [])
                .Select(v => (v.Compression, v.Time))
                .ToHashSet();
            var written = archive.Compressed(Uid).Select(v => (v.Compression, v.Time)).ToHashSet();
            Assert.Equal(expected, written);
            Assert.True(archive.CompressedWrites > 1);
        }

        [Fact]
        public void A_high_load_pauses_until_it_falls_below_the_resume_share()
        {
            var archive = new FakeArchive();
            var raw = Raw(5, 6);
            archive.AddRaw(Uid, raw);
            List<double?> reads = [];
            // Above the limit, between resume share and limit (still paused), then below
            var worker = LoadWorker(archive, reads, 1.2, 0.7, 0.5);

            worker.Enqueue([raw[0]]);
            worker.ProcessQueue();

            Assert.Equal([1.2, 0.7, 0.5], reads);
            AssertLevelsComplete(archive, raw);
        }

        [Fact]
        public void A_load_below_the_limit_does_not_pause()
        {
            var archive = new FakeArchive();
            var raw = Raw(5, 6);
            archive.AddRaw(Uid, raw);
            List<double?> reads = [];
            var worker = LoadWorker(archive, reads, 0.7);

            worker.Enqueue([raw[0]]);
            worker.ProcessQueue();

            // One read for the single step: running at 0.7 is not paused
            Assert.Single(reads);
            AssertLevelsComplete(archive, raw);
        }

        [Fact]
        public void An_unknown_load_does_not_pause()
        {
            var archive = new FakeArchive();
            var raw = Raw(5, 6);
            archive.AddRaw(Uid, raw);
            List<double?> reads = [];
            var worker = LoadWorker(archive, reads, [null]);

            worker.Enqueue([raw[0]]);
            worker.ProcessQueue();

            Assert.Single(reads);
            AssertLevelsComplete(archive, raw);
        }

        [Theory]
        [InlineData("1.52 0.98 0.73 2/345 1234\n", 4, 0.38)]
        [InlineData("6.08 5.24 3.86 5/400 999", 4, 1.52)]
        public void Load_average_is_parsed_per_core(string loadAvg, int cores, double expected)
        {
            Assert.Equal(expected, SystemLoad.ParseLoadPerCore(loadAvg, cores)!.Value, 6);
        }

        [Theory]
        [InlineData("", 4)]
        [InlineData("x 1 2", 4)]
        [InlineData("1.0 1 2", 0)]
        public void Invalid_load_average_is_null(string loadAvg, int cores)
        {
            Assert.Null(SystemLoad.ParseLoadPerCore(loadAvg, cores));
        }
    }
}
