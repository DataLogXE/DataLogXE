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
    public class ArchiveValue
    {
        /// <summary>
        /// The Tag ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The archive data value
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// The archive value time
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// The value state
        /// </summary>
        public ValueState State { get; set; }

        /// <summary>
        /// The Compression level of the value
        /// </summary>
        public CompressionType Compression { get; set; } = CompressionType.RAW;
    }
}
