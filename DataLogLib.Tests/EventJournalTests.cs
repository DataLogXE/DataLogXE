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

using System.Text;
using DataLogLib.Common.Serilog;
using DataLogLib.Services.EventLog;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLogLib.Tests
{
    public sealed class EventJournalTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlx-events-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Fact]
        public void Records_of_any_length_come_back_in_order()
        {
            var journal = new RecordJournal(_folder, "events", 1 << 20, segmentBytes: 64);
            Assert.True(journal.IsEmpty);
            var written = Enumerable.Range(0, 30).Select(i => new string('x', i * 3) + i).ToList();
            Assert.Equal(0, journal.Append([.. written.Select(Encoding.UTF8.GetBytes)]));
            Assert.Equal(30, journal.Count);

            List<string> read = [];
            for (var chunk = journal.Peek(7); chunk.Count > 0; chunk = journal.Peek(7))
            {
                read.AddRange(chunk.Select(Encoding.UTF8.GetString));
                journal.Commit(chunk.Count);
            }

            Assert.Equal(written, read);
            Assert.True(journal.IsEmpty);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void A_restart_goes_on_after_the_records_written_and_skips_a_cut_off_record()
        {
            var journal = new RecordJournal(_folder, "events", 1 << 20, segmentBytes: 1 << 16);
            journal.Append([.. Enumerable.Range(0, 10).Select(i => Encoding.UTF8.GetBytes("r" + i))]);
            journal.Commit(journal.Peek(4).Count);
            // A crash while a record was appended: its length says more than the file holds
            var last = Directory.GetFiles(_folder, "*.jnl").Max();
            File.AppendAllBytes(last, [50, 0, 0, 0, 1, 2]);

            var reopened = new RecordJournal(_folder, "events", 1 << 20, segmentBytes: 1 << 16);
            Assert.Equal(6, reopened.Count);
            reopened.Append([Encoding.UTF8.GetBytes("r10")]);
            List<string> read = [];
            for (var chunk = reopened.Peek(100); chunk.Count > 0; chunk = reopened.Peek(100))
            {
                read.AddRange(chunk.Select(Encoding.UTF8.GetString));
                reopened.Commit(chunk.Count);
            }

            Assert.Equal(["r4", "r5", "r6", "r7", "r8", "r9", "r10"], read);
            Assert.True(reopened.IsEmpty);
        }

        [Fact]
        public void Records_beyond_the_size_limit_are_not_written()
        {
            // 4 bytes of length and 6 of text per record
            var journal = new RecordJournal(_folder, "events", 50);
            Assert.Equal(1, journal.Append([.. Enumerable.Range(0, 6).Select(i => Encoding.UTF8.GetBytes("record"))]));
            Assert.Equal(5, journal.Count);
        }

        [Fact]
        public void Alarms_the_database_does_not_take_reach_it_later_in_order_and_complete()
        {
            var failing = true;
            List<EventLogPoint> stored = [];
            var worker = new EventLogWorker(
                new SerilogSink(),
                points =>
                {
                    if (failing)
                    {
                        throw new InvalidOperationException("database down");
                    }
                    stored.AddRange(points);
                },
                _ => { },
                new RecordJournal(_folder, "events", 1 << 20),
                new ConfigurationBuilder().Build());

            var start = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            EventLogRecord Alarm(int i) => new()
            {
                TimeUtc = start.AddSeconds(i),
                Measurement = EventLogConstants.ProcessMeasurementName,
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.AlarmComponent,
                SourceId = 3,
                CorrelationId = "c" + i,
                AlarmState = AlarmStateValues.Incoming,
                Message = "Alarm ä " + i,
                Severity = 500,
                ActiveSinceUtc = start
            };

            // The database is down: two flushes of alarms go to the journal, none is dropped
            for (var i = 0; i < 700; i++)
            {
                worker.EnqueueDirect(Alarm(i));
            }
            worker.Flush();
            for (var i = 700; i < 1200; i++)
            {
                worker.EnqueueDirect(Alarm(i));
            }
            worker.Flush();
            Assert.Equal(1200, worker.JournalCount);
            Assert.Empty(stored);

            // Back: the journal comes first, then the new alarms
            failing = false;
            worker.EnqueueDirect(Alarm(1200));
            worker.Flush();
            Assert.Equal(0, worker.JournalCount);
            Assert.Equal(Enumerable.Range(0, 1201).Select(i => "c" + i), stored.Select(p => p.CorrelationId));
            var sample = stored[42];
            Assert.Equal((start.AddSeconds(42), DateTimeKind.Utc, "Alarm ä 42", 500, start),
                (sample.TimeUtc, sample.TimeUtc.Kind, sample.Message, sample.Severity, sample.ActiveSinceUtc));
            Assert.Empty(Directory.GetFiles(_folder));
        }
    }
}
