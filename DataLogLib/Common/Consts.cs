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

namespace DataLogLib.Common
{
    public class Consts
    {
        /// <summary>
        /// Minimum time in which process values are written to the archive by default. 
        /// If no new value is read from the data acquisition, then the last valid value 
        /// is inserted into the archive with the status "Resend".
        /// </summary>
        public const int ARCHIVE_VALUE_MIN_MS = 10000;

        /// <summary>
        /// How long the last value of a tag holds when nothing was stored after it, at least. A connected data
        /// source stores a row at least every <see cref="ARCHIVE_VALUE_MIN_MS"/> (the resend is sent after twice that
        /// time and stamped one interval back), so a tag without a row for longer has lost its signal. A tag with a
        /// long cycle holds for twice its cycle, see <see cref="ArchiveHold"/>.
        /// </summary>
        public const int ARCHIVE_VALUE_HOLD_MS = ARCHIVE_VALUE_MIN_MS * 3;

        /// <summary>
        /// Cycle time in which the archive statistics are calculated
        /// </summary>
        public const int ARCHIVE_STATS_CYLE_MS = 1000;

        /// <summary>
        /// Cycle time of the per-data-source insert rate tag ValueCountPerSec.
        /// </summary>
        public const int VALUES_PER_SECOND_CYCLE_MS = 10000;

        /// <summary>
        /// History window for per-data-source throughput sparklines in the UI.
        /// </summary>
        public const int DATASOURCE_THROUGHPUT_SPARKLINE_MINUTES = 10;

        /// <summary>
        /// Cycle time of raw data compression. 
        /// The time indicates how long the raw data is collected until the 
        /// LTTB algorithm compresses the data and clear the buffer.
        /// </summary>
        public const int ARCHIVE_COMPRESSION_CYLE_MS = 10000;

        /// <summary>
        /// Parameter key for update marker
        /// </summary>
        public const string DATA_TAG_CHANGE_MARKER_KEY = "DataTagUpdate";

        /// <summary>
        /// Represents the interval time for Telegraf in milliseconds.
        /// This value is typically used to define how often Telegraf collects
        /// and sends metrics data.
        /// </summary>
        public const int TELEGRAF_INTERVAL_MS = 5000;

    }
}
