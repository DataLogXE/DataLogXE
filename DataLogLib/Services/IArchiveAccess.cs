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

namespace DataLogLib.Services
{
    public interface IArchiveAccess : ITagActualLookup
    {
        #region events
        event EventHandler<(DateTime, long)> OnValueCountPerSec;
        event EventHandler<(DateTime, double)> OnValueCountSumMio;
        event EventHandler<(DateTime, double)> OnDatabaseDiskSizeGb;
        event EventHandler<long> OnArchiveCompTimeMs;
        event EventHandler<int> OnArchiveCompCountRaw;
        event EventHandler<int> OnArchiveCompCount1Sec;
        #endregion

        void InsertValue(ArchiveValue value);
        void InsertValues(List<ArchiveValue> values);
        List<ArchiveValue> GetActualValues(List<int> ids);
        DateTime? GetArchiveStartTime();
        DateTime? GetArchiveEndTime(int dataTagId);
        long? GetRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
        /// <summary>About how many raw values the range holds, from the counters only (the counted part scaled to the range), for the cost of a read; null when the counters hold nothing of the range or the tag has no uid.</summary>
        long? EstimateRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
        /// <summary>The number of valid values in the range and min, max, mean and standard deviation of the signal in it (mean and deviation over time, the value before the range counts as long as it holds), null for a tag without uid. The last value of the tag holds for the hold time at most: a tag without a newer row has no signal after that.</summary>
        RawStatistics GetRawStatistics(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        /// <summary>How long a bool tag was high, low and without signal in the range (the time before an invalid value counts as no signal), null for a tag without uid. The value that holds at the start is the last one within the lookback before it. The last value of the tag holds for the hold time at most.</summary>
        BoolDurations GetBoolDurations(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        /// <summary>How long a tag had no signal in the range: NaN or invalid, and the time before an invalid value, and the time after the last value of the tag once the hold time has passed. Null for a tag without uid. The value that holds at the start is the last one within the lookback before it.</summary>
        double? GetInvalidSeconds(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
        List<PlotPoint> GetPlotPoints(int dataTagId, CompressionType compression, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);

        /// <summary>The M4 points (first, min, max, last raw value) per bucket of bucketSeconds counted from origin (unix seconds), chosen by the archive from the raw values of the range; gaps keep their first and last point per bucket. For ranges with more raw values than a read may take and no compression level.</summary>
        List<PlotPoint> GetRawM4PlotPoints(int dataTagId, DateTime startTime, DateTime endTime, double origin, double bucketSeconds, CancellationToken cancellationToken = default);

        /// <summary>The raw values of the range in time order, at most limit rows; invalid states are points without value. For the export of the raw data; onRead gets the rows read so far every few thousand rows.</summary>
        List<PlotPoint> GetRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken = default, Action<long> onRead = null);

        /// <summary>The raw values of the range in time order as they are read, like GetRawPlotPoints without a limit; only a few of them are held at a time. For the streamed export of the raw data.</summary>
        IAsyncEnumerable<PlotPoint> StreamRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default);
        PlotPoint GetPlotPointAt(int dataTagId, DateTime dateTime);
        /// <summary>The raw value at the time: the last one stored at or before it within the lookback, if the next one stored is valid; the newest one of the tag only for the hold time. A point without value when the signal was lost, null when there is no row, and for a time in the future.</summary>
        PlotPoint GetRawValueAt(int dataTagId, DateTime dateTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default);
    }
}
