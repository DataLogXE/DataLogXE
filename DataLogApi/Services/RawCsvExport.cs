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

using DataLogLib.Models;
using System.Globalization;
using System.Text;

namespace DataLogApi.Services
{
    /// <summary>A data tag of a raw data CSV export with the names its rows carry.</summary>
    public sealed record RawCsvTag(int TagId, string SourceName, string TagName);

    /// <summary>
    /// The raw data export as CSV, written while the archive is read, so its size has no limit: only a few rows are held
    /// at a time. The same file as the CSV of the chart in the browser: a BOM, one row per value and data tag (the gaps
    /// of invalid values left out), ";" and "," where the decimal mark of the browser is a comma, and the local time of
    /// the browser, given as the offsets from UTC that hold from a time on (the server may not know its time zone).
    /// </summary>
    public sealed class RawCsvExport
    {
        private const int FlushRows = 2000;

        private readonly bool _decimalComma;
        private readonly char _separator;
        // From which unix second on which offset in minutes holds, sorted by time; the first one also holds before
        private readonly (double From, int Minutes)[] _offsets;

        public RawCsvExport(bool decimalComma, IEnumerable<(double From, int Minutes)> offsets)
        {
            _decimalComma = decimalComma;
            _separator = decimalComma ? ';' : ',';
            _offsets = offsets.OrderBy(o => o.From).ToArray();
            if (_offsets.Length == 0)
            {
                _offsets = [(0, 0)];
            }
        }

        /// <summary>The offsets of a query, each "unix seconds:minutes east of UTC".</summary>
        public static List<(double From, int Minutes)> ParseOffsets(IEnumerable<string> values)
        {
            List<(double From, int Minutes)> result = [];
            foreach (var value in values ?? [])
            {
                var parts = value?.Split(':');
                if (parts is not { Length: 2 }
                    || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var from)
                    || !double.IsFinite(from)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    || Math.Abs(minutes) > 18 * 60)
                {
                    throw new ArgumentException($"The time zone offset '{value}' is invalid.");
                }
                result.Add((from, minutes));
            }
            return result;
        }

        public async Task WriteAsync(
            Stream body,
            IReadOnlyList<RawCsvTag> tags,
            Func<RawCsvTag, IAsyncEnumerable<PlotPoint>> read,
            CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(body, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
            var lines = new StringBuilder();
            AppendLine(lines, ["Data source", "Data tag", "Time", "Time (UTC)", "Value"]);
            var rows = 0;
            foreach (var tag in tags)
            {
                var source = Field(tag.SourceName);
                var name = Field(tag.TagName);
                await foreach (var point in read(tag).WithCancellation(cancellationToken))
                {
                    if (point.Raw is not double value)
                    {
                        continue;
                    }
                    var time = DateTime.UnixEpoch.AddMilliseconds(Math.Round(point.X * 1000));
                    lines.Append(source).Append(_separator)
                        .Append(name).Append(_separator)
                        .Append(time.AddMinutes(OffsetAt(point.X)).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(_separator)
                        .Append(time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(_separator)
                        .Append(Number(value)).Append("\r\n");
                    if (++rows % FlushRows == 0)
                    {
                        await writer.WriteAsync(lines, cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                        lines.Clear();
                    }
                }
            }
            await writer.WriteAsync(lines, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        private int OffsetAt(double unixSeconds)
        {
            var minutes = _offsets[0].Minutes;
            foreach (var (from, offset) in _offsets)
            {
                if (from > unixSeconds)
                {
                    break;
                }
                minutes = offset;
            }
            return minutes;
        }

        private void AppendLine(StringBuilder lines, string[] fields)
        {
            lines.AppendJoin(_separator, fields.Select(Field)).Append("\r\n");
        }

        private string Field(string text)
        {
            text ??= "";
            return text.IndexOfAny(['"', '\n', '\r', _separator]) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
        }

        private string Number(double value)
        {
            if (!double.IsFinite(value))
            {
                return "";
            }
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            return _decimalComma ? text.Replace('.', ',') : text;
        }
    }
}
