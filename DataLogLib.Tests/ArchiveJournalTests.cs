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

using System.Diagnostics;
using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public sealed class ArchiveJournalTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlx-journal-" + Guid.NewGuid().ToString("N"));
        private readonly DateTime _base = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Local);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Fact]
        public void Values_come_back_in_order_with_every_field()
        {
            var journal = new ArchiveJournal(_folder, 1 << 20, segmentBytes: 10 * ArchiveJournal.RecordSize);
            Assert.True(journal.IsEmpty);
            Assert.Equal(0, journal.Append(Values(0, 25)));
            journal.Append([new ArchiveValue { Id = 7, Value = double.NaN, Time = _base, State = ValueState.Invalid, Compression = CompressionType.C010 }]);
            Assert.Equal(26, journal.Count);

            List<ArchiveValue> read = [];
            for (var chunk = journal.Peek(8); chunk.Count > 0; chunk = journal.Peek(8))
            {
                read.AddRange(chunk);
                journal.Commit(chunk.Count);
            }

            Assert.Equal(Enumerable.Range(0, 25).Select(i => (double)i), read.Take(25).Select(v => v.Value));
            Assert.Equal(_base.AddSeconds(3), read[3].Time);
            Assert.Equal(DateTimeKind.Local, read[3].Time.Kind);
            var last = read[^1];
            Assert.Equal((7, ValueState.Invalid, CompressionType.C010), (last.Id, last.State, last.Compression));
            Assert.True(double.IsNaN(last.Value));
            Assert.True(journal.IsEmpty);
            // A written journal leaves no files
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void A_restart_goes_on_after_the_values_written_and_ignores_a_cut_off_record()
        {
            var journal = new ArchiveJournal(_folder, 1 << 20, segmentBytes: 10 * ArchiveJournal.RecordSize);
            journal.Append(Values(0, 15));
            journal.Commit(journal.Peek(4).Count);
            // A crash while a record was appended: half a record at the end of the last segment
            var last = Directory.GetFiles(_folder, "*.jnl").Max();
            File.AppendAllBytes(last, new byte[ArchiveJournal.RecordSize / 2]);

            var reopened = new ArchiveJournal(_folder, 1 << 20, segmentBytes: 10 * ArchiveJournal.RecordSize);
            Assert.Equal(11, reopened.Count);
            reopened.Append(Values(15, 3));
            List<ArchiveValue> read = [];
            for (var chunk = reopened.Peek(100); chunk.Count > 0; chunk = reopened.Peek(100))
            {
                read.AddRange(chunk);
                reopened.Commit(chunk.Count);
            }

            Assert.Equal(Enumerable.Range(4, 14).Select(i => (double)i), read.Select(v => v.Value));
            Assert.True(reopened.IsEmpty);
        }

        [Fact]
        public void Values_beyond_the_size_limit_are_not_written()
        {
            var journal = new ArchiveJournal(_folder, 10 * ArchiveJournal.RecordSize);
            Assert.Equal(0, journal.Append(Values(0, 6)));
            Assert.Equal(2, journal.Append(Values(6, 6)));
            Assert.Equal(10, journal.Count);
            journal.Commit(journal.Peek(5).Count);
            Assert.Equal(0, journal.Append(Values(12, 5)));
        }

        [Fact]
        public void A_full_buffer_goes_to_the_journal_and_reaches_the_archive_in_order()
        {
            var archive = new FakeArchive { FailInserts = true };
            var worker = new ArchiveInsertWorker();
            worker.Init(archive, new ArchiveJournal(_folder, 1L << 30));
            List<double> handed = [];
            worker.OnArchiveInsert += (_, values) =>
            {
                lock (handed)
                {
                    handed.AddRange(values.Select(v => v.Value));
                }
            };

            // The archive is down: more values than the buffer holds, in batches as a data source sends them
            const int Total = ArchiveInsertWorker.BUFFER_MAX_SIZE + 25_000;
            for (var start = 0; start < Total; start += 1000)
            {
                worker.InsertValues(Values(start, 1000));
            }
            Assert.Equal(Total - ArchiveInsertWorker.BUFFER_MAX_SIZE, worker.JournalCount);

            archive.FailInserts = false;
            WaitUntil(() => worker.JournalCount == 0 && Count(handed) == Total);
            Assert.Equal(Enumerable.Range(0, Total).Select(i => (double)i), handed);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void The_journal_of_the_last_run_is_written_once_the_archive_resolves_the_tags()
        {
            var journal = new ArchiveJournal(_folder, 1L << 30);
            journal.Append(Values(0, 100));

            var archive = new FakeArchive();
            var worker = new ArchiveInsertWorker();
            worker.Init(archive, new ArchiveJournal(_folder, 1L << 30));
            List<double> handed = [];
            worker.OnArchiveInsert += (_, values) =>
            {
                lock (handed)
                {
                    handed.AddRange(values.Select(v => v.Value));
                }
            };
            // New values queue behind the journal
            worker.InsertValues(Values(100, 10));
            Thread.Sleep(1500);
            Assert.Equal(0, Count(handed));
            Assert.Equal(110, worker.JournalCount);

            worker.StartReplay();
            WaitUntil(() => Count(handed) == 110);
            Assert.Equal(Enumerable.Range(0, 110).Select(i => (double)i), handed);
        }

        private List<ArchiveValue> Values(int start, int count)
            => Enumerable.Range(start, count)
                .Select(i => new ArchiveValue { Id = 1, Value = i, Time = _base.AddSeconds(i), State = ValueState.Valid })
                .ToList();

        private static int Count(List<double> values)
        {
            lock (values)
            {
                return values.Count;
            }
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "timed out");
                Thread.Sleep(50);
            }
        }
    }
}
