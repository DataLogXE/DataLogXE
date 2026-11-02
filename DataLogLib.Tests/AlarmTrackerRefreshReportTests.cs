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

using DataLogService.DataSource.OpcUa;
using Xunit;

namespace DataLogLib.Tests
{
    public class AlarmTrackerRefreshReportTests
    {
        [Fact]
        public void EndRefresh_CountsOnlyClosedActiveAlarmsAsMissed()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            tracker.Process(AlarmSeedTestData.ActiveNotification("Went"));
            tracker.Process(AlarmSeedTestData.ActiveNotification("Standing"));
            tracker.Process(AlarmSeedTestData.ActiveNotification("Kept"));
            var going = AlarmSeedTestData.ActiveNotification("Went");
            going.Active = false;
            tracker.Process(going);

            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            tracker.Process(AlarmSeedTestData.ActiveNotification("Kept"));
            var report = tracker.EndRefresh(DateTime.UtcNow);

            Assert.Equal(3, report.Open);
            Assert.Equal(1, report.Confirmed);
            Assert.Equal(2, report.Closed);
            Assert.Equal(1, report.Missed);
            Assert.Equal(0, report.New);
        }

        [Fact]
        public void EndRefresh_ClosingOnlyGoneAlarms_HasNoMissed()
        {
            var tracker = new AlarmTracker(AlarmSeedTestData.SourceId, AlarmSeedTestData.SourceName);
            tracker.Process(AlarmSeedTestData.ActiveNotification("Went"));
            var going = AlarmSeedTestData.ActiveNotification("Went");
            going.Active = false;
            tracker.Process(going);

            Assert.True(tracker.TryArmRefresh());
            Assert.True(tracker.BeginRefresh());
            var report = tracker.EndRefresh(DateTime.UtcNow);

            Assert.Equal(1, report.Closed);
            Assert.Equal(0, report.Missed);
        }
    }
}
