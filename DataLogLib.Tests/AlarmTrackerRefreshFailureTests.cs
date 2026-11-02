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
    public class AlarmTrackerRefreshFailureTests
    {
        [Fact]
        public void RejectRefresh_ClosesEveryOpenAlarm()
        {
            var tracker = TrackerWith("AlarmA", "AlarmB");

            var report = tracker.RejectRefresh();

            Assert.True(report.Applied);
            Assert.Equal(AlarmTracker.AlarmRefreshFailure.Rejected, report.Failure);
            Assert.Equal(2, report.Closed);
            Assert.All(report.Closing, record => Assert.Equal(AlarmStateValues.Removed, record.AlarmState));
            Assert.Empty(tracker.CopyActiveRecords());
            Assert.False(tracker.RefreshBusy);
        }

        [Fact]
        public void ExpireRefresh_WithoutRefreshStart_ClosesEveryOpenAlarm()
        {
            var tracker = TrackerWith("AlarmA", "AlarmB");
            Assert.True(tracker.TryArmRefresh());
            Thread.Sleep(20);

            var report = tracker.ExpireRefresh(TimeSpan.FromMilliseconds(1));

            Assert.True(report.Applied);
            Assert.Equal(AlarmTracker.AlarmRefreshFailure.NoStart, report.Failure);
            Assert.Equal(2, report.Closed);
            Assert.Empty(tracker.CopyActiveRecords());
        }

        [Fact]
        public void ExpireRefresh_WithoutRefreshEnd_KeepsTheReportedAlarms()
        {
            var tracker = TrackerWith("AlarmA", "AlarmB");
            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            tracker.Process(AlarmSeedTestData.ActiveNotification("AlarmA"));
            Thread.Sleep(20);

            var report = tracker.ExpireRefresh(TimeSpan.FromMilliseconds(1));

            Assert.Equal(AlarmTracker.AlarmRefreshFailure.NoEnd, report.Failure);
            var closed = Assert.Single(report.Closing);
            Assert.Equal("AlarmB message", closed.Message);
            var active = Assert.Single(tracker.CopyActiveRecords());
            Assert.Equal("AlarmA message", active.Message);
        }

        [Fact]
        public void ExpireRefresh_BeforeTheTimeout_ChangesNothing()
        {
            var tracker = TrackerWith("AlarmA", "AlarmB");
            Assert.True(tracker.TryArmRefresh());

            var report = tracker.ExpireRefresh(TimeSpan.FromHours(1));

            Assert.False(report.Applied);
            Assert.True(tracker.RefreshBusy);
            Assert.Equal(2, tracker.CopyActiveRecords().Count);
        }

        [Fact]
        public void ResetForNewSubscription_DropsTheRefreshWithoutClosing()
        {
            var tracker = TrackerWith("AlarmA");
            Assert.True(tracker.TryArmRefresh());
            tracker.ResetForNewSubscription();
            Thread.Sleep(20);

            var report = tracker.ExpireRefresh(TimeSpan.FromMilliseconds(1));

            Assert.False(report.Applied);
            Assert.Single(tracker.CopyActiveRecords());
        }

        private static AlarmTracker TrackerWith(params string[] conditionNames)
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            foreach (var conditionName in conditionNames)
            {
                tracker.Process(AlarmSeedTestData.ActiveNotification(conditionName));
            }

            Assert.Equal(conditionNames.Length, tracker.CopyActiveRecords().Count);
            return tracker;
        }
    }
}
