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
    /// Receives the progress of a long read, so a caller can tell the user what the query is doing.
    /// Percent is 0..100 when the share of the work is known; Count is the number of rows seen so far.
    /// </summary>
    public interface IQueryProgress
    {
        void Report(string phase, double? percent = null, long? count = null);
    }
}
