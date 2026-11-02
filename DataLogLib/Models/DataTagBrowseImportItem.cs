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
    public class DataTagBrowseImportItem
    {
        public string Address { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public int TypeId { get; set; }
        /// <summary>Description as JSON culture -> text (<see cref="DataTagDescription"/>), null for none.</summary>
        public string Description { get; set; }
    }

    public class DataTagBrowseSyncProgress
    {
        public string Phase { get; set; }
        public int Count { get; set; }
        public int Scanned { get; set; }
        public int Total { get; set; }
        public bool Finished { get; set; }
        public int Deleted { get; set; }
        public int Restored { get; set; }
        public int Updated { get; set; }
        public int Created { get; set; }
    }

    public class DataTagBrowseSyncResult
    {
        public int Prepared { get; set; }
        public int Deleted { get; set; }
        public int Undeleted { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
    }
}
