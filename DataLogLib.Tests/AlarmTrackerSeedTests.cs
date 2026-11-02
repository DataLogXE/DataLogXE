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

using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using Xunit;

namespace DataLogLib.Tests
{
    public class AlarmTrackerSeedTests
    {
        [Fact]
        public void SeedOpenAlarms_WithOpenRows_ActivatesThem()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var rows = new List<EventLogRecord>
            {
                AlarmSeedTestData.OpenRow("corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(10)),
                AlarmSeedTestData.OpenRow("corr-b", "AlarmB", AlarmSeedTestData.MinutesAgo(5))
            };
            var superseded = new List<EventLogRecord>();

            var seeded = tracker.SeedOpenAlarms(rows, superseded);

            Assert.Equal(2, seeded);
            Assert.True(tracker.ArchiveSeeded);
            Assert.Empty(superseded);
            Assert.Equal(2, tracker.CopyActiveRecords().Count);
        }

        [Fact]
        public void SeedOpenAlarms_SupersedesOlderOccurrenceOfTheSameCondition()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var rows = new List<EventLogRecord>
            {
                AlarmSeedTestData.OpenRow("corr-old", "AlarmA", AlarmSeedTestData.MinutesAgo(10)),
                AlarmSeedTestData.OpenRow("corr-new", "AlarmA", AlarmSeedTestData.MinutesAgo(2))
            };
            var superseded = new List<EventLogRecord>();

            tracker.SeedOpenAlarms(rows, superseded);

            var closed = Assert.Single(superseded);
            Assert.Equal("corr-old", closed.CorrelationId);
            Assert.Equal(AlarmStateValues.Removed, closed.AlarmState);
            var active = Assert.Single(tracker.CopyActiveRecords());
            Assert.Equal("corr-new", active.CorrelationId);
        }

        [Fact]
        public void SeedOpenAlarms_WithEmptyRows_StillMarksTheSeedAsDone()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);

            tracker.SeedOpenAlarms([], new List<EventLogRecord>());

            Assert.True(tracker.ArchiveSeeded);
        }

        [Fact]
        public void NeedsOccurrenceBackfill_FlagsMissingRaiseTimeOrDuration()
        {
            Assert.False(AlarmTracker.NeedsOccurrenceBackfill(null));
            Assert.False(AlarmTracker.NeedsOccurrenceBackfill(
                AlarmSeedTestData.OpenRow("", "AlarmA", AlarmSeedTestData.MinutesAgo(1))));
            Assert.False(AlarmTracker.NeedsOccurrenceBackfill(
                AlarmSeedTestData.OpenRow("corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(1), AlarmSeedTestData.MinutesAgo(9))));
            Assert.True(AlarmTracker.NeedsOccurrenceBackfill(
                AlarmSeedTestData.OpenRow("corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(1))));
            Assert.True(AlarmTracker.NeedsOccurrenceBackfill(
                AlarmSeedTestData.OpenRow(
                    "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(1),
                    AlarmSeedTestData.MinutesAgo(9), null, AlarmStateValues.IncomingOutgoing)));
            Assert.False(AlarmTracker.NeedsOccurrenceBackfill(
                AlarmSeedTestData.OpenRow(
                    "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(1),
                    AlarmSeedTestData.MinutesAgo(9), 1000, AlarmStateValues.IncomingOutgoing)));
        }

        [Fact]
        public void CompleteOpenRows_FillsRaiseTimeAndDurationFromHistory()
        {
            var since = AlarmSeedTestData.MinutesAgo(60);
            var open = AlarmSeedTestData.OpenRow(
                "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(5),
                null, null, AlarmStateValues.IncomingOutgoing);
            var history = AlarmSeedTestData.OpenRow(
                "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(55), since, 3600000);

            var notes = AlarmTracker.CompleteOpenRows([open], [history]);

            var note = Assert.Single(notes);
            Assert.Equal(since, open.ActiveSinceUtc);
            Assert.Equal(3600000, open.DurationMs);
            Assert.True(note.FilledActiveSince);
            Assert.True(note.FilledDuration);
            Assert.False(note.StillMissing);
        }

        [Fact]
        public void CompleteOpenRows_ComputesDurationFromTheRaiseTime()
        {
            var since = AlarmSeedTestData.MinutesAgo(60);
            var open = AlarmSeedTestData.OpenRow(
                "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(5),
                since, null, AlarmStateValues.IncomingOutgoing);
            var history = AlarmSeedTestData.OpenRow(
                "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(30), since, null);

            var notes = AlarmTracker.CompleteOpenRows([open], [history]);

            var note = Assert.Single(notes);
            Assert.True(note.FilledDuration);
            Assert.NotNull(open.DurationMs);
            Assert.InRange(open.DurationMs.Value, 54L * 60 * 1000, 56L * 60 * 1000);
        }

        [Fact]
        public void CompleteOpenRows_WithoutHistory_ReportsStillMissing()
        {
            var open = AlarmSeedTestData.OpenRow(
                "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(5),
                null, null, AlarmStateValues.IncomingOutgoing);

            var notes = AlarmTracker.CompleteOpenRows([open], []);

            var note = Assert.Single(notes);
            Assert.True(note.StillMissing);
            Assert.False(note.FilledActiveSince);
            Assert.False(note.FilledDuration);
        }

        [Fact]
        public void CompleteOpenRows_WithoutOpenRows_ReturnsNothing()
        {
            Assert.Empty(AlarmTracker.CompleteOpenRows([], []));
            Assert.Empty(AlarmTracker.CompleteOpenRows(null, null));
        }
    }
}
