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
    public static class EventLogConstants
    {
        public const string MeasurementName = "DataLogEvent";
        public const string ProcessMeasurementName = "ProcessEvent";

        public const int MaxMessageChars = 4096;
        public const int MaxExceptionChars = 4096;
        public const int MaxTemplateChars = 512;
        public const int MaxPropsJsonChars = 2048;

        public const int SinkMaxQueue = 10000;
        public const int WorkerBatchSize = 500;
        public const int WorkerFlushIntervalMs = 1000;

        public const int MaxSourceFilterCount = 32;
        public const int DefaultQueryLimit = 100;
        public const int MaxQueryLimit = 200;

        // Sorting a page reads the ordered ids of the whole range once and pages over them. A range
        // with more alarms keeps the first ones of the order (8 bytes each in memory, 4 MB); the table
        // says so and the rest is reached by the reverse order or a shorter range.
        public const int MaxSortableEvents = 500000;

        // Component of every process alarm (OPC UA and S7CommPlus); the archive seed selects by it.
        public const string AlarmComponent = "opcua_alarm";
        public const string SystemAlarmComponent = "system_alarm";
        public const string SystemAlarmClass = "SystemAlarm";
        public const string SystemAlarmEventType = "SystemConnectionError";
        public const string SystemAlarmEventTypeName = "System connection error";
        public const int SystemAlarmSeverity = 700;
        public const int SystemAlarmClearSeverity = 1;
        public const string SystemClockAlarmEventType = "SystemClockDeviation";
        public const string SystemClockAlarmEventTypeName = "System clock deviation";
        public const int SystemClockAlarmSeverity = 500;
        public const int SystemAlarmCheckIntervalMs = 2000;
        public const string AlarmKind = "process";
        public const int AlarmHistoryLimit = 50;
        public const int MaxTrackedConditions = 5000;
        public const int AlarmEventQueueSize = 100;
        public const int ActiveAlarmPublishIntervalMs = 5000;
        // Only open occurrences that lack raise time or duration. Not every past cycle.
        public const int AlarmSeedLookback = 8;
        // Open occurrences older than this are not restored from the archive.
        public const int AlarmSeedRangeDays = 15;
        // The alarm subscription waits for the seed, so a slow archive delays it at most this long.
        public const int AlarmSeedQueryTimeoutMs = 15000;
    }
}
