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
    public class ChartModel
    {
        /// <summary>
        /// The chart ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The chart (or folder) name
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The chart or folder type id
        /// </summary>
        public int TypeId { get; set; }

        /// <summary>
        /// The tree view parent id
        /// </summary>
        public int ParentId { get; set; }

        /// <summary>
        /// The place among the children of the parent, from 0
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Get or Set TypeId
        /// </summary>
        public ChartItemType Type
        {
            get
            {
                return (ChartItemType)TypeId;
            }
            set
            {
                TypeId = (int)value;
            }
        }

        public override string ToString()
        {
            return $"Id:{Id}; Name:{Name}; TypeId:{TypeId}; Type:{Type}; ParentId:{ParentId}";
        }
    }
}
