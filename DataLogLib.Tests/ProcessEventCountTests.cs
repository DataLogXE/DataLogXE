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
using Npgsql;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class ProcessEventCountTests
    {
        private const string PumpProps = "{\"ConditionId\":\"ns=3;s=\\\"DB1\\\".Pump\",\"BranchId\":\"\",\"DsName\":\"plc\"}";
        private const string ValveProps = "{\"ConditionId\":\"ns=3;s=\\\"DB1\\\".Pump2\",\"BranchId\":\"\"}";
        private readonly ProcessEventStore _store;
        // Three hours ago at the start of the hour, so every point falls into a closed hour
        private readonly DateTime _hour = FloorHour(DateTime.UtcNow).AddHours(-3);

        public ProcessEventCountTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            PostgresTestConfiguration.EnsureBaselineApplied();
            Execute("TRUNCATE processevent, processeventopen, processeventcount1h");
            Execute("UPDATE processeventcountstate SET live_from_id = NULL, backfill_before = NULL");
            _store = new ProcessEventStore(PostgresTestConfiguration.Create());
            _store.InvalidateListCache();
        }

        [Fact]
        public void Writes_count_each_alarm_per_state_and_hour()
        {
            if (_store == null)
            {
                return;
            }

            // In time order: the store moves a point that is older than the one before behind it
            _store.InsertEventPoints([
                Point(_hour.AddMinutes(1), "o1", AlarmStateValues.Incoming, PumpProps, "Pump 80 °C"),
                Point(_hour.AddMinutes(2), "o1", AlarmStateValues.IncomingOutgoing, PumpProps, "Pump 80 °C"),
                Point(_hour.AddMinutes(3), "o2", AlarmStateValues.Incoming, PumpProps, "Pump 95 °C"),
                // Another condition whose id starts like the first one
                Point(_hour.AddMinutes(4), "o4", AlarmStateValues.Incoming, ValveProps, "Pump 80 °C"),
                // Another source
                Point(_hour.AddMinutes(5), "o5", AlarmStateValues.Incoming, PumpProps, "Pump 80 °C", sourceId: 3),
                Point(_hour.AddMinutes(61), "o3", AlarmStateValues.Incoming, PumpProps, "Pump 90 °C")
            ]);

            var counts = _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming));
            // Every message text of the condition counts, so do the occurrences
            Assert.Equal([2L, 1L, 0L], counts.Counts);
            // The statistics stop going back at the oldest stored event
            Assert.Equal(_hour.AddMinutes(1), counts.OldestUtc);

            Assert.Equal([1L, 0L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.IncomingOutgoing)).Counts);
            Assert.Equal([1L, 0L, 0L], _store.QueryAlarmCounts(Query(ValveProps, AlarmStateValues.Incoming)).Counts);
            Assert.Equal([0L, 0L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Removed)).Counts);
        }

        [Fact]
        public void Backfill_counts_the_rows_from_before_once()
        {
            if (_store == null)
            {
                return;
            }

            // Stored before schema 17: nothing counted
            _store.CountOnWrite = false;
            _store.InsertEventPoints([
                Point(_hour.AddHours(-30), "old", AlarmStateValues.Incoming, PumpProps),
                Point(_hour.AddMinutes(-50), "o0", AlarmStateValues.Incoming, PumpProps),
                Point(_hour.AddMinutes(1), "o1", AlarmStateValues.Incoming, PumpProps)
            ]);
            _store.CountOnWrite = true;

            // The first write that counts takes the next id and counts its points at once
            _store.InsertEventPoints([Point(_hour.AddMinutes(2), "o2", AlarmStateValues.Incoming, PumpProps)]);
            var before = _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming, hours: 3, from: _hour.AddHours(-1)));
            Assert.Equal([0L, 1L, 0L], before.Counts);
            Assert.NotNull(before.CompleteFromUtc);

            var oldest = _store.ReadOldestEventTime();
            var steps = 0;
            AlarmCountBackfillStep step;
            do
            {
                step = _store.BackfillCountsStep(4, oldest);
                steps += 1;
            }
            while (!step.Done && steps < 50);

            Assert.True(step.Done);
            Assert.Equal(FloorHour(_hour.AddHours(-30)), step.Before);
            var after = _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming, hours: 3, from: _hour.AddHours(-1)));
            Assert.Equal([1L, 2L, 0L], after.Counts);
            Assert.Null(after.CompleteFromUtc);
            var old = _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming, hours: 1, from: FloorHour(_hour.AddHours(-30))));
            Assert.Equal([1L], old.Counts);

            // A finished backfill counts nothing more
            Assert.True(_store.BackfillCountsStep(4, oldest).Done);
            Assert.Equal([1L, 2L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming, hours: 3, from: _hour.AddHours(-1))).Counts);
        }

        [Fact]
        public void Backfill_starts_without_any_counting_write()
        {
            if (_store == null)
            {
                return;
            }

            _store.CountOnWrite = false;
            _store.InsertEventPoints([Point(_hour.AddMinutes(1), "o1", AlarmStateValues.Incoming, PumpProps)]);
            _store.CountOnWrite = true;

            var oldest = _store.ReadOldestEventTime();
            Assert.Null(_store.BackfillCountsStep(4, oldest).Before);
            Assert.True(_store.CountsAvailable());
            _store.PrepareCountBackfill();
            Assert.True(_store.BackfillCountsStep(4, oldest).Done);
            Assert.Equal([1L, 0L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming)).Counts);

            // A write afterwards counts its points on top
            _store.InsertEventPoints([Point(_hour.AddMinutes(2), "o2", AlarmStateValues.Incoming, PumpProps)]);
            Assert.Equal([2L, 0L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming)).Counts);
        }

        [Fact]
        public void System_alarms_count_per_reported_source_and_type()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints([
                SystemPoint(_hour.AddMinutes(1), "s1", 6, "SystemConnectionError", "Source 6 failed"),
                SystemPoint(_hour.AddMinutes(2), "s2", 6, "SystemConnectionError", "Source 6 failed again"),
                SystemPoint(_hour.AddMinutes(3), "s3", 6, "SystemClockDeviation", "Clock of source 6"),
                SystemPoint(_hour.AddMinutes(4), "s4", 7, "SystemConnectionError", "Source 7 failed")
            ]);

            // The pending alarm in memory has no affected source of its own: it comes from the props as on the write
            var reference = new EventLogRecord
            {
                Component = EventLogConstants.SystemAlarmComponent,
                SourceId = 1,
                EventType = "SystemConnectionError",
                Props = "{\"AffectedSourceId\":\"6\"}",
                Message = "Source 6 failed"
            };
            var counts = _store.QueryAlarmCounts(new AlarmCountQuery
            {
                Reference = reference,
                AlarmState = AlarmStateValues.Incoming,
                Edges = Edges(_hour, 3)
            });
            Assert.Equal([2L, 0L, 0L], counts.Counts);
        }

        [Fact]
        public void Process_alarms_without_condition_id_count_by_name_else_by_message()
        {
            if (_store == null)
            {
                return;
            }

            const string named = "{\"ConditionId\":\"\",\"SourceName\":\"Tank\",\"ConditionName\":\"Level\"}";
            _store.InsertEventPoints([
                Point(_hour.AddMinutes(1), "n1", AlarmStateValues.Incoming, named, "Level 1"),
                Point(_hour.AddMinutes(2), "n2", AlarmStateValues.Incoming, named, "Level 2"),
                Point(_hour.AddMinutes(3), "m1", AlarmStateValues.Incoming, "", "Plain"),
                Point(_hour.AddMinutes(4), "m2", AlarmStateValues.Incoming, "", "Plain"),
                Point(_hour.AddMinutes(5), "m3", AlarmStateValues.Incoming, "", "Other")
            ]);

            Assert.Equal([2L, 0L, 0L], _store.QueryAlarmCounts(Query(named, AlarmStateValues.Incoming)).Counts);
            Assert.Equal([2L, 0L, 0L], _store.QueryAlarmCounts(Query("", AlarmStateValues.Incoming, message: "Plain")).Counts);
        }

        [Fact]
        public void Retention_drops_the_counts_of_expired_hours()
        {
            if (_store == null)
            {
                return;
            }

            _store.InsertEventPoints([
                Point(_hour.AddDays(-3), "o0", AlarmStateValues.Incoming, PumpProps),
                Point(_hour.AddMinutes(1), "o1", AlarmStateValues.Incoming, PumpProps)
            ]);
            var original = _store.ReadRetentionSeconds();
            try
            {
                _store.SetRetentionSeconds(2L * 24 * 3600);
                _store.ApplyRetention();
                Assert.Equal([0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming, hours: 1, from: _hour.AddDays(-3))).Counts);
                Assert.Equal([1L, 0L, 0L], _store.QueryAlarmCounts(Query(PumpProps, AlarmStateValues.Incoming)).Counts);
            }
            finally
            {
                _store.SetRetentionSeconds(original);
            }
        }

        private AlarmCountQuery Query(string props, string state, int hours = 3, DateTime? from = null, string message = "Pump")
        {
            return new AlarmCountQuery
            {
                Reference = new EventLogRecord
                {
                    Component = EventLogConstants.AlarmComponent,
                    SourceId = 2,
                    Props = props,
                    Message = message
                },
                AlarmState = state,
                Edges = Edges(from ?? _hour, hours)
            };
        }

        private static List<DateTime> Edges(DateTime from, int hours)
        {
            return Enumerable.Range(0, hours + 1).Select(index => from.AddHours(index)).ToList();
        }

        private static EventLogPoint Point(DateTime time, string correlationId, string state, string props, string message = "Pump", int sourceId = 2)
        {
            return new EventLogPoint
            {
                TimeUtc = time,
                Measurement = EventLogConstants.ProcessMeasurementName,
                Level = "Warning",
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.AlarmComponent,
                SourceId = sourceId,
                SourceType = "OpcUa",
                CorrelationId = correlationId,
                AlarmState = state,
                Props = props,
                Message = message
            };
        }

        private static EventLogPoint SystemPoint(DateTime time, string correlationId, int affectedSourceId, string eventType, string message)
        {
            return new EventLogPoint
            {
                TimeUtc = time,
                Measurement = EventLogConstants.ProcessMeasurementName,
                Level = "Error",
                Kind = EventLogConstants.AlarmKind,
                Component = EventLogConstants.SystemAlarmComponent,
                SourceId = 1,
                SourceType = "System",
                CorrelationId = correlationId,
                AlarmState = AlarmStateValues.Incoming,
                EventType = eventType,
                Props = $"{{\"AffectedSourceId\":\"{affectedSourceId}\"}}",
                Message = message
            };
        }

        private static DateTime FloorHour(DateTime time)
        {
            return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        }

        private static void Execute(string sql)
        {
            using var dataSource = NpgsqlDataSource.Create(TestPostgres.ConnectionString);
            using var command = dataSource.CreateCommand(sql);
            command.ExecuteNonQuery();
        }
    }
}
