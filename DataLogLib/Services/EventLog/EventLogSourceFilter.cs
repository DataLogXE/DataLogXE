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
    /// <summary>
    /// Matches event log records against UI/API source id filters, including system connection
    /// alarms that report under the system data source but affect an OPC UA source.
    /// </summary>
    public static class EventLogSourceFilter
    {
        public static bool MatchesSourceFilter(EventLogRecord record, IReadOnlyList<int> filterIds)
        {
            if (record == null)
            {
                return false;
            }

            if (filterIds == null || filterIds.Count == 0)
            {
                return true;
            }

            if (filterIds.Contains(record.SourceId))
            {
                return true;
            }

            var affectedId = ResolveAffectedSourceId(record);
            if (affectedId <= 0)
            {
                return false;
            }

            return string.Equals(
                       record.Component,
                       EventLogConstants.SystemAlarmComponent,
                       StringComparison.Ordinal)
                   && filterIds.Contains(affectedId);
        }

        public static int ResolveAffectedSourceId(EventLogRecord record)
        {
            if (record == null)
            {
                return 0;
            }

            if (record.AffectedSourceId > 0)
            {
                return record.AffectedSourceId;
            }

            return TryParseAffectedSourceIdFromProps(record.Props);
        }

        public static int TryParseAffectedSourceIdFromProps(string propsJson)
        {
            if (string.IsNullOrWhiteSpace(propsJson))
            {
                return 0;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(propsJson);
                if (doc.RootElement.TryGetProperty("AffectedSourceId", out var element))
                {
                    if (element.ValueKind == System.Text.Json.JsonValueKind.Number
                        && element.TryGetInt32(out var id))
                    {
                        return id;
                    }

                    if (element.ValueKind == System.Text.Json.JsonValueKind.String
                        && int.TryParse(element.GetString(), out id))
                    {
                        return id;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return 0;
        }
    }
}
