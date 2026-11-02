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

namespace DataLogService.DataSource.Test
{
    /// <summary>Lets a value through when it changed, or repeats it after <see cref="RepeatMs"/> without a write.</summary>
    internal class TestWriteFilter
    {
        public const long RepeatMs = 20_000;

        private readonly Dictionary<int, (double Value, long Ms)> _lastWritten = [];

        public bool ShouldWrite(int tagId, double value, long nowMs)
        {
            if (_lastWritten.TryGetValue(tagId, out var last)
                && last.Value == value
                && nowMs - last.Ms < RepeatMs)
            {
                return false;
            }

            _lastWritten[tagId] = (value, nowMs);
            return true;
        }
    }
}
