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
using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using Xunit;

namespace DataLogLib.Tests
{
    public class OpcUaAlarmSeederTests
    {
        [Fact]
        public void TrySeed_WhenQueryFails_KeepsTrackerUnseededAndDoesNotThrow()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var enqueued = new List<EventLogRecord>();
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => throw new HttpRequestException("Operation timed out (127.0.0.1:18087)")
            };
            var seeder = CreateSeeder(tracker, access, enqueued);

            var seeded = seeder.TrySeed(TimeSpan.FromSeconds(5));

            Assert.False(seeded);
            Assert.False(tracker.ArchiveSeeded);
            Assert.Empty(enqueued);
            Assert.Equal(1, access.FlushCalls);
        }

        [Fact]
        public void TrySeed_WhenQueryHangs_StopsAtTheTimeout()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, cancellationToken) =>
                {
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                    return new EventLogQueryResult();
                }
            };
            var seeder = CreateSeeder(tracker, access, []);

            var stopwatch = Stopwatch.StartNew();
            var seeded = seeder.TrySeed(TimeSpan.FromMilliseconds(200));
            stopwatch.Stop();

            Assert.False(seeded);
            Assert.False(tracker.ArchiveSeeded);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
            // A seed that timed out is not done later, so the read was tried twice
            Assert.Equal(2, access.QueryOpenAlarmsCalls);
        }

        [Fact]
        public void TrySeed_WhenWritingTheQueuedEventsIsSlow_TheReadKeepsItsWholeTimeout()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var access = new FakeEventLogAccess
            {
                // The writes of all sources at a start took most of the 15 s on a Raspberry Pi
                OnFlush = () => Thread.Sleep(400),
                OnQueryOpenAlarms = (_, cancellationToken) =>
                {
                    Assert.False(cancellationToken.WaitHandle.WaitOne(150));
                    return new EventLogQueryResult();
                }
            };
            var seeder = CreateSeeder(tracker, access, []);

            Assert.True(seeder.TrySeed(TimeSpan.FromMilliseconds(300)));
            Assert.True(tracker.ArchiveSeeded);
            Assert.Equal(1, access.QueryOpenAlarmsCalls);
        }

        [Fact]
        public void TrySeed_WhenTheFirstReadTimesOut_SeedsWithTheSecond()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var calls = 0;
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        cancellationToken.WaitHandle.WaitOne();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return new EventLogQueryResult();
                }
            };
            var seeder = CreateSeeder(tracker, access, []);

            Assert.True(seeder.TrySeed(TimeSpan.FromMilliseconds(200)));
            Assert.True(tracker.ArchiveSeeded);
            Assert.Equal(2, access.QueryOpenAlarmsCalls);
        }

        [Fact]
        public void TrySeed_WhenEventLogIsNotReady_SkipsWithoutThrowing()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var seeder = new OpcUaAlarmSeeder(
                AlarmSeedTestData.SourceId,
                AlarmSeedTestData.SourceName,
                AlarmSeedTestData.LogPrefix,
                tracker,
                () => null,
                _ => { },
                _ => { });

            var seeded = seeder.TrySeed(TimeSpan.FromSeconds(1));

            Assert.False(seeded);
            Assert.False(tracker.ArchiveSeeded);
        }

        private static OpcUaAlarmSeeder CreateSeeder(
            AlarmTracker tracker,
            IEventLogAccess access,
            List<EventLogRecord> enqueued)
        {
            return new OpcUaAlarmSeeder(
                AlarmSeedTestData.SourceId,
                AlarmSeedTestData.SourceName,
                AlarmSeedTestData.LogPrefix,
                tracker,
                () => access,
                _ => { },
                enqueued.Add);
        }

        [Fact]
        public void TrySeed_WithOpenRows_RestoresThemIntoTheTracker()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var enqueued = new List<EventLogRecord>();
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => new EventLogQueryResult
                {
                    Items =
                    [
                        AlarmSeedTestData.OpenRow("corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(10)),
                        AlarmSeedTestData.OpenRow("corr-b", "AlarmB", AlarmSeedTestData.MinutesAgo(5))
                    ]
                }
            };
            var seeder = CreateSeeder(tracker, access, enqueued);

            var seeded = seeder.TrySeed(TimeSpan.FromSeconds(5));

            Assert.True(seeded);
            Assert.True(tracker.ArchiveSeeded);
            Assert.Empty(enqueued);
            Assert.Equal(2, tracker.CopyActiveRecords().Count);
        }

        [Fact]
        public void TrySeed_WhenAnOlderOccurrenceIsSuperseded_ReportsItAsClosed()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var enqueued = new List<EventLogRecord>();
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => new EventLogQueryResult
                {
                    Items =
                    [
                        AlarmSeedTestData.OpenRow("corr-old", "AlarmA", AlarmSeedTestData.MinutesAgo(10)),
                        AlarmSeedTestData.OpenRow("corr-new", "AlarmA", AlarmSeedTestData.MinutesAgo(2))
                    ]
                }
            };
            var seeder = CreateSeeder(tracker, access, enqueued);

            Assert.True(seeder.TrySeed(TimeSpan.FromSeconds(5)));

            var closed = Assert.Single(enqueued);
            Assert.Equal("corr-old", closed.CorrelationId);
            Assert.Equal(AlarmStateValues.Removed, closed.AlarmState);
            Assert.Equal("corr-new", Assert.Single(tracker.CopyActiveRecords()).CorrelationId);
        }

        [Fact]
        public void TrySeed_WhenBackfillFails_StillSeedsTheOpenRows()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => new EventLogQueryResult
                {
                    Items =
                    [
                        AlarmSeedTestData.OpenRow(
                            "corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(5),
                            null, null, AlarmStateValues.IncomingOutgoing)
                    ]
                },
                OnQueryOpenAlarmLookback = (_, _, _) => throw new HttpRequestException("Operation timed out")
            };
            var seeder = CreateSeeder(tracker, access, []);

            var seeded = seeder.TrySeed(TimeSpan.FromSeconds(5));

            Assert.True(seeded);
            Assert.True(tracker.ArchiveSeeded);
        }

        [Fact]
        public void TrySeed_WhenLiveStateIsPresent_KeepsItAndSkipsTheArchive()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            tracker.Process(AlarmSeedTestData.ActiveNotification("AlarmLive"));
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => new EventLogQueryResult
                {
                    Items = [AlarmSeedTestData.OpenRow("corr-archive", "AlarmArchive", AlarmSeedTestData.MinutesAgo(10))]
                }
            };
            var seeder = CreateSeeder(tracker, access, []);

            Assert.True(seeder.TrySeed(TimeSpan.FromSeconds(5)));

            Assert.True(tracker.ArchiveSeeded);
            Assert.Equal(0, access.QueryOpenAlarmsCalls);
            var active = Assert.Single(tracker.CopyActiveRecords());
            Assert.Contains("AlarmLive", active.Props);
        }

        [Fact]
        public async Task TrySeed_FromTwoThreads_QueriesTheArchiveOnce()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            using var release = new ManualResetEventSlim();
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) =>
                {
                    release.Wait(TimeSpan.FromSeconds(5));
                    return new EventLogQueryResult
                    {
                        Items = [AlarmSeedTestData.OpenRow("corr-a", "AlarmA", AlarmSeedTestData.MinutesAgo(10))]
                    };
                }
            };
            var seeder = CreateSeeder(tracker, access, []);

            var first = Task.Run(() => seeder.TrySeed(TimeSpan.FromSeconds(5)));
            var second = Task.Run(() => seeder.TrySeed(TimeSpan.FromSeconds(5)));
            release.Set();

            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, Assert.True);
            Assert.Equal(1, access.QueryOpenAlarmsCalls);
            Assert.Single(tracker.CopyActiveRecords());
        }

        [Fact]
        public void TrySeed_WhenHttpClientTimesOut_ReportsFailureAndStaysRetryable()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            var access = new FakeEventLogAccess
            {
                OnQueryOpenAlarms = (_, _) => throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.",
                    new TimeoutException("The operation was canceled."))
            };
            var seeder = CreateSeeder(tracker, access, []);

            Assert.False(seeder.TrySeed(TimeSpan.FromSeconds(5)));
            Assert.False(tracker.ArchiveSeeded);
        }

        private sealed class FakeEventLogAccess : IEventLogAccess
        {
            public Func<int, CancellationToken, EventLogQueryResult> OnQueryOpenAlarms { get; set; }
            public Func<int, IReadOnlyList<string>, CancellationToken, EventLogQueryResult> OnQueryOpenAlarmLookback { get; set; }
            public int FlushCalls { get; private set; }
            private int _queryOpenAlarmsCalls;
            public int QueryOpenAlarmsCalls => Volatile.Read(ref _queryOpenAlarmsCalls);

            public Action OnFlush { get; set; }

            public void Flush()
            {
                FlushCalls++;
                OnFlush?.Invoke();
            }

            public EventLogQueryResult QueryOpenAlarms(int sourceId, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _queryOpenAlarmsCalls);
                return OnQueryOpenAlarms?.Invoke(sourceId, cancellationToken) ?? new EventLogQueryResult();
            }

            public EventLogQueryResult QueryOpenAlarmLookback(
                int sourceId,
                IReadOnlyList<string> correlationIds,
                CancellationToken cancellationToken = default)
            {
                return OnQueryOpenAlarmLookback?.Invoke(sourceId, correlationIds, cancellationToken)
                    ?? new EventLogQueryResult();
            }

            public EventLogQueryResult QueryEvents(EventLogQuery query, CancellationToken cancellationToken = default)
            {
                return new EventLogQueryResult();
            }

            public EventLogSummaryResult QueryEventSummary(EventLogQuery query, CancellationToken cancellationToken = default)
            {
                return new EventLogSummaryResult();
            }

            public long QueryEventCount(EventLogQuery query, CancellationToken cancellationToken = default)
            {
                return 0;
            }

            public EventLogQueryResult QueryAlarmHistory(AlarmHistoryQuery query, CancellationToken cancellationToken = default)
            {
                return new EventLogQueryResult();
            }
        }
    }
}
