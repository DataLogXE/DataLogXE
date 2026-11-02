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
    public class CompressionWorkerTests
    {
        private static readonly DateTime WindowStart = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Local);
        private static readonly string Uid = FakeArchive.Uid(1);

        // Sine values every 100 ms from the given second, all within the hour of WindowStart
        internal static List<ArchiveValue> Values(int fromSecond, int seconds, int stepMs = 100) =>
            Enumerable.Range(0, seconds * 1000 / stepMs)
                .Select(i => fromSecond * 1000 / stepMs + i)
                .Select(i => new ArchiveValue
                {
                    Id = 1,
                    Value = Math.Sin(i / 7.0) * (1 + i % 13),
                    Time = WindowStart.AddMilliseconds((double)i * stepMs),
                    State = ValueState.Valid
                })
                .ToList();

        // Inserts the values like the service and flushes on shutdown. Every call is a new run
        // of the service, so the open windows are only kept by the flush.
        private static void RunAndShutdown(FakeArchive archive, List<ArchiveValue> values, Action<List<ArchiveValue>> onLost = null)
        {
            var insertWorker = new ArchiveInsertWorker();
            var compressionWorker = new CompressionWorker();
            insertWorker.Init(archive);
            compressionWorker.Init(archive, insertWorker, onLost);

            insertWorker.InsertValues(values);
            insertWorker.Flush();
            try
            {
                compressionWorker.Flush();
            }
            catch (IOException)
            {
                // Archive outage, the values are kept for a retry
            }
        }

        internal static List<DateTime> M4Times(List<ArchiveValue> values)
        {
            var sorted = values.OrderBy(v => v.Time).ToList();
            return DataCompression.M4Indices(sorted.Select(v => (v.Time.ToOADate(), v.Value)).ToList())
                .Select(i => sorted[i].Time)
                .ToList();
        }

        [Fact]
        public void Flush_writes_the_open_windows_of_all_levels()
        {
            var archive = new FakeArchive();
            var values = Values(0, 25);
            RunAndShutdown(archive, values);

            foreach (var level in CompressionLevels.CascadeLevels)
            {
                var times = archive.Compressed(Uid, level.Type).Select(v => v.Time).ToHashSet();
                Assert.Superset(M4Times(values).ToHashSet(), times);
            }

            // The open hour window is written as one part with the M4 points of all values
            var hour = archive.Compressed(Uid, CompressionType.C3600).Select(v => v.Time);
            Assert.Equal(M4Times(values), hour.OrderBy(t => t));
        }

        [Fact]
        public void Window_parts_before_and_after_a_restart_keep_the_m4_points_of_the_whole_window()
        {
            var archive = new FakeArchive();
            // The restart at 23 s splits the open window of every level but C001
            var before = Values(0, 23);
            var after = Values(23, 32);
            RunAndShutdown(archive, before);
            RunAndShutdown(archive, after);

            foreach (var level in CompressionLevels.CascadeLevels)
            {
                var times = archive.Compressed(Uid, level.Type).Select(v => v.Time).ToHashSet();
                Assert.Superset(M4Times(before.Concat(after).ToList()).ToHashSet(), times);
            }
        }

        [Fact]
        public void Values_dropped_after_a_long_outage_are_reported_as_lost()
        {
            // One value per second for 30 h: more C001 points than the pending limit
            var archive = new FakeArchive { FailWrites = true };
            var values = Values(0, 30 * 3600, 1000);
            List<ArchiveValue> lost = [];
            RunAndShutdown(archive, values, lost.AddRange);

            Assert.NotEmpty(lost);
            Assert.Empty(archive.Compressed(Uid));
            // The oldest values are dropped
            Assert.Equal(values[0].Time, lost.Min(v => v.Time));
        }
    }
}
