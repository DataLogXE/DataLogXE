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
using Microsoft.Extensions.Configuration;

namespace DataLogLib.Services.ArchiveAccess
{
    internal interface IArchive
    {
        DateTime? GetArchiveEndTime(int dataTagId);
        DateTime? GetArchiveStartTime();
        List<PlotPoint> GetPlotPoints(int dataTagId, CompressionType compression, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);

        /// <summary>The M4 points (first, min, max, last raw value) per bucket of bucketSeconds counted from origin (unix seconds), chosen by the archive from the raw values of the range; gaps keep their first and last point per bucket. For ranges with more raw values than a read may take and no compression level.</summary>
        List<PlotPoint> GetRawM4PlotPoints(int dataTagId, DateTime startTime, DateTime endTime, double origin, double bucketSeconds, CancellationToken cancellationToken = default);

        /// <summary>The raw values of the range in time order, at most limit rows; invalid states are points without value. For the export of the raw data; onRead gets the rows read so far every few thousand rows.</summary>
        List<PlotPoint> GetRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken = default, Action<long> onRead = null);

        /// <summary>The raw values of the range in time order as they are read, like GetRawPlotPoints without a limit; only a few of them are held at a time. For the streamed export of the raw data.</summary>
        IAsyncEnumerable<PlotPoint> StreamRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
        long? GetRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
        /// <summary>The number of valid values in the range and min, max, mean and standard deviation of the signal in it (mean and deviation over time, the value before the range counts as long as it holds), null for a tag without uid. The last value of the tag holds for the hold time at most: a tag without a newer row has no signal after that.</summary>
        RawStatistics GetRawStatistics(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        /// <summary>How long a bool tag was high, low and without signal in the range (the time before an invalid value counts as no signal), null for a tag without uid. The value that holds at the start is the last one within the lookback before it. The last value of the tag holds for the hold time at most.</summary>
        BoolDurations GetBoolDurations(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        /// <summary>How long a tag had no signal in the range: NaN or invalid, and the time before an invalid value, and the time after the last value of the tag once the hold time has passed. Null for a tag without uid. The value that holds at the start is the last one within the lookback before it.</summary>
        double? GetInvalidSeconds(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        /// <summary>Sum of the archived values per minute, the archive value count.</summary>
        long? GetValueCountSum();
        long? GetDatabaseDiskSizeBytes();
        void Init(IConfiguration configuration);
        /// <summary>Adds the number of values archived in the minute ending at time.</summary>
        void InsertValueCount(DateTime time, long count);

        /// <summary>Writes the values and throws when the archive does not accept them.</summary>
        void InsertValues(List<ArchiveValue> values);

        /// <summary>Writes the values and throws when the archive does not accept them.</summary>
        void InsertValuesAndWait(List<ArchiveValue> values);

        #region compression repair
        bool TryGetUid(int dataTagId, out string uid);
        /// <summary>The uids of all tags with raw values in the range and the time of their first one.</summary>
        List<(string Uid, DateTime First)> GetRawUids(DateTime startTime, DateTime endTime);
        /// <summary>Streams the raw values of the tag with the uid in time order.</summary>
        void ReadRawValues(string uid, DateTime startTime, DateTime endTime, Action<ArchiveValue> onValue);
        /// <summary>The time of the last value of every raw and compressed measurement in the range.</summary>
        List<(string Uid, CompressionType Compression, DateTime Last)> GetLastTimes(DateTime startTime, DateTime endTime);
        /// <summary>Writes compressed values of the tag with the uid, throws when not accepted.</summary>
        void InsertCompressedValuesAndWait(string uid, List<ArchiveValue> values);
        List<HourCoverage> GetHourCoverage(string uid, DateTime startTime, DateTime endTime);
        DateTime? GetCompressionVerifiedUntil();
        void SetCompressionVerifiedUntil(DateTime time);
        #endregion
    }

    /// <summary>
    /// First and last raw value time of a local hour, the first and last time of the coarsest
    /// compression level in that hour (null when the level has no point in the hour) and the
    /// number of raw values.
    /// </summary>
    public sealed record HourCoverage(DateTime Hour, DateTime RawFirst, DateTime RawLast, DateTime? LevelFirst, DateTime? LevelLast, long RawCount)
    {
        /// <summary>
        /// A complete hour of the level keeps the first and the last raw value of the hour (M4),
        /// so a different first or last time shows values that never reached the level.
        /// </summary>
        public bool IsComplete => LevelFirst == RawFirst && LevelLast == RawLast;
    }
}
