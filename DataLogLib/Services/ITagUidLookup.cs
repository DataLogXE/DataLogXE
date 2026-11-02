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

namespace DataLogLib.Services
{
    /// <summary>
    /// Resolves the globally unique data tag uid (datatag.guid) for data tag ids.
    /// The archive names its measurements after the uid, because ids are only unique per configuration database.
    /// </summary>
    public interface ITagUidLookup
    {
        /// <summary>
        /// Returns the uid of every known data tag id. Unknown ids are missing from the result.
        /// </summary>
        IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds);
    }
}
