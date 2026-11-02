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

namespace DataLogLib.Models
{
    public class DataSourceInfoModel
    {
        /// <summary>
        /// The data source object that contains information about the data source.
        /// </summary>
        public DataSourceModel DataSource { get; set; }

        /// <summary>
        /// The current state of the data source, represented by an enumeration.
        /// </summary>
        public DataSourceState State { get; set; }

        /// <summary>
        /// A list of sampling intervals (cycles) associated with this data source in milliseconds.
        /// </summary>
        public List<int> Cycles { get; set; }

        /// <summary>
        /// The clock of the device minus the clock of DataLogXE in milliseconds, as measured from the time stamps
        /// the device sends (positive: the device clock is ahead); null while connected without a measurement or not connected.
        /// </summary>
        public long? ClockOffsetMs { get; set; }

        /// <summary>Why the source is not connected (e.g. a device clock too far ahead), null when unknown.</summary>
        public string ConnectionError { get; set; }

        /// <summary>
        /// The thumbprint of the server certificate the last connect refused because it is not the pinned one,
        /// null while none was refused.
        /// </summary>
        public string RejectedCertificateThumbprint { get; set; }
    }
}
