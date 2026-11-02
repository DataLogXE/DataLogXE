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
    public class EventLogRecord
    {
        public DateTime TimeUtc { get; set; }
        public string Measurement { get; set; } = EventLogConstants.MeasurementName;
        public string Level { get; set; } = "Information";
        public string Kind { get; set; } = "application";
        public string Component { get; set; } = "service";
        public int SourceId { get; set; }
        public int AffectedSourceId { get; set; }
        public string SourceType { get; set; } = "";
        public string Message { get; set; } = "";
        public string Template { get; set; } = "";
        public string Exception { get; set; } = "";
        public string Props { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public string AlarmState { get; set; } = "";
        public int? Severity { get; set; }
        public bool? Active { get; set; }
        public bool? Acked { get; set; }
        public bool? Retain { get; set; }
        public DateTime? ActiveSinceUtc { get; set; }
        public string EventType { get; set; }
        public string EventTypeName { get; set; }
        public string AlarmClassSymbol { get; set; }
        public string AlarmClassName { get; set; }
        public uint? TextColor { get; set; }
        public uint? BackColor { get; set; }
        public bool? Flashing { get; set; }
        public long? DurationMs { get; set; }

        public EventLogPoint ToPoint()
        {
            return new EventLogPoint
            {
                TimeUtc = TimeUtc,
                Measurement = Measurement,
                Level = Level,
                Kind = Kind,
                Component = Component,
                SourceId = SourceId,
                AffectedSourceId = AffectedSourceId,
                SourceType = SourceType,
                Message = Message,
                Template = Template,
                Exception = Exception,
                Props = Props,
                CorrelationId = CorrelationId,
                AlarmState = AlarmState,
                Severity = Severity,
                Active = Active,
                Acked = Acked,
                Retain = Retain,
                ActiveSinceUtc = ActiveSinceUtc,
                EventType = EventType,
                EventTypeName = EventTypeName,
                AlarmClassSymbol = AlarmClassSymbol,
                AlarmClassName = AlarmClassName,
                TextColor = TextColor,
                BackColor = BackColor,
                Flashing = Flashing,
                DurationMs = DurationMs
            };
        }

        public static EventLogRecord FromPoint(EventLogPoint point)
        {
            return new EventLogRecord
            {
                TimeUtc = point.TimeUtc,
                Measurement = point.Measurement,
                Level = point.Level,
                Kind = point.Kind,
                Component = point.Component,
                SourceId = point.SourceId,
                AffectedSourceId = point.AffectedSourceId,
                SourceType = point.SourceType,
                Message = point.Message,
                Template = point.Template,
                Exception = point.Exception,
                Props = point.Props,
                CorrelationId = point.CorrelationId,
                AlarmState = point.AlarmState,
                Severity = point.Severity,
                Active = point.Active,
                Acked = point.Acked,
                Retain = point.Retain,
                ActiveSinceUtc = point.ActiveSinceUtc,
                EventType = point.EventType,
                EventTypeName = point.EventTypeName,
                AlarmClassSymbol = point.AlarmClassSymbol,
                AlarmClassName = point.AlarmClassName,
                TextColor = point.TextColor,
                BackColor = point.BackColor,
                Flashing = point.Flashing,
                DurationMs = point.DurationMs
            };
        }
    }
}
