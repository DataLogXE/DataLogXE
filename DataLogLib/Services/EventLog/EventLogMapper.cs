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

using Serilog.Events;
using System.Text.Json;

namespace DataLogLib.Services.EventLog
{
    internal static class EventLogMapper
    {
        private static readonly HashSet<string> PropWhitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            "DsName",
            "Address",
            "ClientMessage",
            "ClientState",
            "NodeKey",
            "Count",
            "Active",
            "ElapsedMs",
            "TelegrafPort",
            "ArchiveInsertMillis",
            "ArchiveInsertTime",
            "TestParameter",
            "ServerTimeOffset"
        };

        public static EventLogRecord FromLogEvent(LogEvent logEvent)
        {
            var record = new EventLogRecord
            {
                TimeUtc = logEvent.Timestamp.UtcDateTime,
                Level = logEvent.Level.ToString(),
                Message = Truncate(logEvent.RenderMessage(), EventLogConstants.MaxMessageChars),
                Template = Truncate(logEvent.MessageTemplate.Text, EventLogConstants.MaxTemplateChars)
            };

            if (logEvent.Exception != null)
            {
                record.Exception = Truncate(logEvent.Exception.ToString(), EventLogConstants.MaxExceptionChars);
            }

            var sourceId = 0;
            if (TryGetScalarInt(logEvent, "DsId", out var dsId))
            {
                sourceId = dsId;
                record.Kind = "datasource";
                record.Component = "service";
            }
            else
            {
                record.Kind = "application";
                record.Component = MapComponent(logEvent);
            }

            record.SourceId = sourceId;
            record.SourceType = TryGetScalarString(logEvent, "SourceType") ?? "";

            record.Props = BuildPropsJson(logEvent, sourceId);
            return record;
        }

        /// <summary>Start of the log message of a failed application event write.</summary>
        public const string AppEventWriteFailed = "App event write failed";

        /// <summary>
        /// Application events that are not written: the failed writes of the application log itself,
        /// which would loop while the database is unavailable, and the queue overflow.
        /// </summary>
        public static bool ShouldSkipAppEvent(EventLogRecord record)
        {
            if (record.Message.Contains(AppEventWriteFailed, StringComparison.Ordinal))
            {
                return true;
            }

            if (record.Message.Contains("Event log queue overflow", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string MapComponent(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue("SourceContext", out var raw))
            {
                return "service";
            }

            var text = raw.ToString().Trim('"');
            if (string.IsNullOrEmpty(text))
            {
                return "service";
            }

            if (text.Contains("ArchiveAccess", StringComparison.OrdinalIgnoreCase))
            {
                return "archive";
            }

            if (text.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                || text.Contains("DataAccess", StringComparison.OrdinalIgnoreCase))
            {
                return "postgres";
            }

            if (text.Contains("OpcUa", StringComparison.OrdinalIgnoreCase))
            {
                return "opcua";
            }

            if (text.Contains("DataLogApi", StringComparison.OrdinalIgnoreCase))
            {
                return "api";
            }

            return "service";
        }

        private static string BuildPropsJson(LogEvent logEvent, int sourceId)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in logEvent.Properties)
            {
                if (pair.Key is "DsId" or "SourceContext" or "SourceType")
                {
                    continue;
                }

                if (!PropWhitelist.Contains(pair.Key))
                {
                    continue;
                }

                dict[pair.Key] = Truncate(pair.Value.ToString().Trim('"'), 256);
            }

            if (sourceId > 0)
            {
                var dsName = TryGetScalarString(logEvent, "DsName");
                if (!string.IsNullOrEmpty(dsName))
                {
                    dict["DsName"] = Truncate(dsName, 256);
                }
            }

            if (dict.Count == 0)
            {
                return "";
            }

            var json = JsonSerializer.Serialize(dict);
            return Truncate(json, EventLogConstants.MaxPropsJsonChars);
        }

        private static bool TryGetScalarInt(LogEvent logEvent, string name, out int value)
        {
            value = 0;
            if (!logEvent.Properties.TryGetValue(name, out var prop))
            {
                return false;
            }

            var text = prop.ToString().Trim('"');
            return int.TryParse(text, out value);
        }

        private static string TryGetScalarString(LogEvent logEvent, string name)
        {
            if (!logEvent.Properties.TryGetValue(name, out var prop))
            {
                return null;
            }

            return prop.ToString().Trim('"');
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? "";
            }

            return value.Substring(0, maxChars);
        }
    }
}
