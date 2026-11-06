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
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DataLogApi.Services
{
    /// <summary>
    /// The system log as a download, written while it is read (one row held at a time). CSV for a spreadsheet: a BOM,
    /// ";" where the decimal mark of the browser is a comma (as Excel there expects), the time in UTC (ISO 8601).
    /// NDJSON for tools: one JSON object per line with every column, also the template and the properties.
    /// </summary>
    public static class SystemLogExport
    {
        public const string FormatCsv = "csv";
        public const string FormatNdjson = "ndjson";
        public static readonly TimeSpan MaxRange = TimeSpan.FromDays(14);
        private const int FlushRows = 2000;

        // One export or support bundle at a time: each reads up to two weeks of compressed log on a Pi
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static bool TryEnter() => Gate.Wait(0);

        public static void Exit() => Gate.Release();

        public static string NormalizeFormat(string format)
        {
            var value = string.IsNullOrWhiteSpace(format) ? FormatCsv : format.Trim().ToLowerInvariant();
            return value is FormatCsv or FormatNdjson ? value : throw new ArgumentException($"The format '{format}' is invalid.");
        }

        public static string ContentType(string format) =>
            format == FormatNdjson ? "application/x-ndjson; charset=utf-8" : "text/csv; charset=utf-8";

        public static string FileName(DateTime fromUtc, DateTime toUtc, string format) =>
            $"datalogxe-log_{Stamp(fromUtc)}_{Stamp(toUtc)}.{format}";

        public static async Task WriteAsync(
            Stream body,
            IAsyncEnumerable<EventLogRecord> records,
            string format,
            bool decimalComma,
            IReadOnlyDictionary<int, string> sourceNames,
            CancellationToken cancellationToken)
        {
            var csv = format != FormatNdjson;
            await using var writer = new StreamWriter(body, new UTF8Encoding(csv), 64 * 1024, leaveOpen: true);
            var separator = decimalComma ? ';' : ',';
            if (csv)
            {
                await writer.WriteLineAsync(string.Join(separator,
                    "Time (UTC)", "Level", "Component", "Source ID", "Source", "Message", "Exception", "Correlation ID"));
            }

            var rows = 0;
            await foreach (var record in records.WithCancellation(cancellationToken))
            {
                var sourceName = record.SourceId > 0 && sourceNames.TryGetValue(record.SourceId, out var name) ? name : "";
                if (csv)
                {
                    await writer.WriteLineAsync(string.Join(separator,
                        Time(record.TimeUtc),
                        Csv(record.Level, separator),
                        Csv(record.Component, separator),
                        record.SourceId > 0 ? record.SourceId.ToString(CultureInfo.InvariantCulture) : "",
                        Csv(sourceName, separator),
                        Csv(record.Message, separator),
                        Csv(record.Exception, separator),
                        Csv(record.CorrelationId, separator)));
                }
                else
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        time = Time(record.TimeUtc),
                        level = record.Level,
                        kind = record.Kind,
                        component = record.Component,
                        sourceId = record.SourceId,
                        source = sourceName,
                        sourceType = record.SourceType,
                        affectedSourceId = record.AffectedSourceId,
                        message = record.Message,
                        template = record.Template,
                        exception = record.Exception,
                        props = ParseProps(record.Props),
                        correlationId = record.CorrelationId
                    }));
                }

                if (++rows % FlushRows == 0)
                {
                    await writer.FlushAsync(cancellationToken);
                }
            }

            await writer.FlushAsync(cancellationToken);
        }

        internal static string Csv(string value, char separator)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            // A leading =, +, - or @ would make a formula in a spreadsheet
            if (value[0] is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            if (value.IndexOfAny([separator, '"', '\r', '\n']) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static string Time(DateTime timeUtc) =>
            DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

        private static string Stamp(DateTime timeUtc) =>
            DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH-mm'Z'", CultureInfo.InvariantCulture);

        private static JsonElement? ParseProps(string props)
        {
            if (string.IsNullOrEmpty(props))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(props);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(props);
            }
        }
    }
}
