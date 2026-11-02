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
    /// <summary>
    /// A plot of a chart with the data tag and data source it shows
    /// </summary>
    public class ChartPlotDetailModel
    {
        /// <summary>
        /// The chart plot database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The sorting position
        /// </summary>
        public int Sort { get; set; }

        public int DataTagId { get; set; }
        public string TagName { get; set; }
        public string DisplayName { get; set; }

        /// <summary>
        /// 0=undefined; 1=boolean; 2=number
        /// </summary>
        public int TypeId { get; set; }

        public int IsActive { get; set; }
        public int IsSys { get; set; }
        public int IsDeleted { get; set; }
        public int DataSourceId { get; set; }
        public string DataSourceName { get; set; }
    }
}
