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

namespace DataLogLib.Models
{
    public class DataTagDetailModel
    {
        /// <summary>
        /// The unique DataTag identifier
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The unique Guid
        /// </summary>
        public string Guid { get; set; }

        /// <summary>
        /// Data Type
        /// 0=undefined; 1=boolean; 2=number
        /// </summary>
        public int TypeId { get; set; }

        /// <summary>
        /// The DataTag name
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// OPC UA display name captured by browse or nodeset import
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// The unique address string
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// The data tag multilingual description
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// The sampling interval in milliseconds
        /// </summary>
        public int Cycle { get; set; }

        /// <summary>
        /// Data acquisition is activated
        /// 0=not active; 1=active
        /// </summary>
        public int IsActive { get; set; }

        /// <summary>
        /// The DataTag is a SYSTEM DataTag
        /// 0=no system DataTag; 1=system DataTag
        /// </summary>
        public int IsSys { get; set; }

        /// <summary>
        /// The associated data source identifier
        /// </summary>
        public int DataSourceId { get; set; }

        /// <summary>
        /// The name of the associated data source
        /// </summary>
        public string DataSourceName { get; set; }

        /// <summary>
        /// Data Source Type
        /// 0=undefined; 1=boolean; 2=number
        /// </summary>
        public int DataSourceTypeId { get; set; }

        /// <summary>
        /// The data source is a SYSTEM DataSource
        /// 0=no system data source; 1=system data source
        /// </summary>
        public int DataSourceIsSys { get; set; }

        /// <summary>
        /// Full name of the DataTag, composed of DataSourceName and Name
        /// </summary>
        public string DataTagFullName
        {
            get
            {
                return $"{DataSourceName}.{Name}";
            }
        }
    }
}
