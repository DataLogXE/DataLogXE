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
    public class DataTagListItemModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Address { get; set; }
        /// <summary>Comment in the language of the source, null when it was not read (rows outside the list index).</summary>
        public string Comment { get; set; }
        public int Cycle { get; set; }
        public int IsActive { get; set; }
        public int IsSys { get; set; }
    }

    public enum DataTagSortColumn
    {
        Id,
        Name,
        DisplayName,
        Address,
        Cycle,
        Active,
        Value,
        Status,
        Comment
    }

    public interface ITagActualLookup
    {
        bool TryGetActual(int id, out double value, out int state, out long timeUtcTicks);
    }

    public readonly record struct TagLiveSort(bool SourceOffline, DateTime NowUtc, ITagActualLookup Actual);

    public readonly record struct DataTagSortLevel(DataTagSortColumn Column, bool Ascending);

    public class DataTagListPageModel
    {
        public int Total { get; set; }
        public List<DataTagListItemModel> Items { get; set; } = [];
        public bool Preparing { get; set; }
        public int PreparePercent { get; set; }
    }
}
