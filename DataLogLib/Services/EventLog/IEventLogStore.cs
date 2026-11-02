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

namespace DataLogLib.Services.EventLog
{
    internal interface IEventLogStore
    {
        void InsertEventPoints(IReadOnlyList<EventLogPoint> points);
        void FlushWrites();
        EventLogQueryResult QueryEvents(EventLogQuery query, CancellationToken cancellationToken = default);
        EventLogSummaryResult QueryEventSummary(EventLogQuery query, CancellationToken cancellationToken = default);
        long QueryEventCount(EventLogQuery query, CancellationToken cancellationToken = default);
        IReadOnlyList<string> QueryAlarmStates(EventLogQuery query, CancellationToken cancellationToken = default);
        EventLogQueryResult QueryAlarmHistory(AlarmHistoryQuery query, CancellationToken cancellationToken = default);
        EventLogQueryResult QueryOpenAlarms(int sourceId, CancellationToken cancellationToken = default);
        EventLogQueryResult QueryOpenAlarmLookback(
            int sourceId,
            IReadOnlyList<string> correlationIds,
            CancellationToken cancellationToken = default);
    }
}
