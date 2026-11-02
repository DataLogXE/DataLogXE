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

using DataLogLib.Common;
using DataLogLib.Models;
using Serilog;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Reduces raw values to the M4 points (first, min, max, last) of the windows of all levels:
    /// C001 from the raw values, every coarser level from the points of the previous one (lossless,
    /// see CompressionLevels). The values of the open windows are kept per level until a watermark
    /// closes them. Used by the CompressionWorker for the live values and by the
    /// CompressionRepairWorker to rebuild the levels from the raw values in the archive.
    /// </summary>
    internal sealed class CompressionCascade
    {
        private readonly List<CascadeLevel> _levels = CompressionLevels.CascadeLevels
            .Select(l => new CascadeLevel(l)).ToList();

        private sealed class CascadeLevel(CompressionLevel level)
        {
            public CompressionLevel Level { get; } = level;
            public Dictionary<int, List<ArchiveValue>> Buffer { get; } = [];
        }

        /// <summary>
        /// Adds the raw values and returns the M4 points of all levels of the windows that end before
        /// the watermark (long.MaxValue closes all windows). Values that could not be compressed are
        /// added to lost, so their time ranges can be repaired from the raw values.
        /// </summary>
        internal List<ArchiveValue> Compress(List<ArchiveValue> raw, long watermark, List<ArchiveValue> lost)
        {
            List<ArchiveValue> compressed = [];
            var input = raw;
            foreach (var level in _levels)
            {
                var output = CompressLevel(level, input, watermark, lost);
                compressed.AddRange(output);
                input = output;
            }
            return compressed;
        }

        /// <summary>
        /// Returns the M4 points of all levels of the given raw values, all windows closed.
        /// </summary>
        internal static List<ArchiveValue> CompressAll(List<ArchiveValue> raw, List<ArchiveValue> lost)
            => new CompressionCascade().Compress(raw, long.MaxValue, lost);

        /// <summary>
        /// Adds the values to the level and returns the M4 points of all windows that end before
        /// the watermark. Values arriving after their window was closed are emitted as an extra
        /// window part; the union of M4 points still contains the min and max of the window.
        /// The returned values are copies typed with the compression of the level.
        /// </summary>
        private static List<ArchiveValue> CompressLevel(CascadeLevel cascadeLevel, List<ArchiveValue> input, long watermark, List<ArchiveValue> lost)
        {
            var windowTicks = cascadeLevel.Level.Seconds * TimeSpan.TicksPerSecond;
            var buffer = cascadeLevel.Buffer;

            foreach (var value in input)
            {
                if (!buffer.TryGetValue(value.Id, out var l))
                {
                    l = [];
                    buffer.Add(value.Id, l);
                }
                l.Add(value);
            }

            // Windows ending at or before the watermark are complete
            var openWindow = watermark == long.MinValue ? long.MinValue : watermark / windowTicks;
            List<ArchiveValue> output = [];
            foreach (var tagId in buffer.Keys.ToList())
            {
                var values = buffer[tagId];
                List<ArchiveValue> complete = null;
                try
                {
                    complete = values.Where(v => v.Time.Ticks / windowTicks < openWindow).ToList();
                    if (complete.Count == 0)
                    {
                        continue;
                    }

                    values.RemoveAll(v => v.Time.Ticks / windowTicks < openWindow);
                    foreach (var window in complete.GroupBy(v => v.Time.Ticks / windowTicks).OrderBy(g => g.Key))
                    {
                        output.AddRange(CompressTag(window.ToList()));
                    }
                }
                catch (Exception ex)
                {
                    // A single faulty tag must not affect the others. Its values are repaired later.
                    Log.Error(ex, "Compression of tag id {TagId} to {Level} failed!", tagId, cascadeLevel.Level.Type);
                    lost.AddRange(values);
                    if (complete != null)
                    {
                        lost.AddRange(complete);
                    }
                    values.Clear();
                }

                if (values.Count == 0)
                {
                    buffer.Remove(tagId);
                }
            }

            // Copies, because the values are also the input of the next level
            return output
                .OrderBy(v => v.Time)
                .Select(v => new ArchiveValue
                {
                    Id = v.Id,
                    Value = v.Value,
                    Time = v.Time,
                    State = v.State,
                    Compression = cascadeLevel.Level.Type
                })
                .ToList();
        }

        private static List<ArchiveValue> CompressTag(List<ArchiveValue> values)
        {
            // Only valid and finite values can be reduced by M4, sorted by time.
            // Of all others (invalid, NaN, infinity) the first and the last are kept: they mark
            // the gap in the chart, and a long invalid period must not fill the coarse levels.
            List<ArchiveValue> validValues = values
                .Where(value => value.State > ValueState.Invalid && double.IsFinite(value.Value))
                .OrderBy(value => value.Time)
                .ToList();

            List<(double, double)> validTuples = GetValidValueTuples(validValues);
            var indices = DataCompression.M4Indices(validTuples);

            List<ArchiveValue> compressed = new(indices.Count);
            indices.ForEach(i => compressed.Add(validValues[i]));

            var invalidValues = values
                .Where(value => value.State <= ValueState.Invalid || !double.IsFinite(value.Value))
                .OrderBy(value => value.Time)
                .ToList();
            if (invalidValues.Count > 0)
            {
                compressed.Add(invalidValues[0]);
            }
            if (invalidValues.Count > 1)
            {
                compressed.Add(invalidValues[^1]);
            }

            return compressed;
        }

        private static List<(double, double)> GetValidValueTuples(List<ArchiveValue> values)
        {
            List<(double, double)> result = new(values.Count);
            values.ForEach(value =>
            {
                result.Add((value.Time.ToOADate(), value.Value));
            });
            return result;
        }
    }
}
