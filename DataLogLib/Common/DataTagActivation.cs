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

namespace DataLogLib.Common
{
    /// <summary>
    /// Which data tags may be activated. An S7 browse imports strings and system types as type Unknown: they never get
    /// a value, and activated, the source wrote a startup value for them on every connect, each 5 s after the previous
    /// one, which on the Raspberry Pi (2026-10-29) sent a value of days ago into the compression. An OPC UA tag of type
    /// Unknown only has a type the import did not resolve and may still deliver numbers.
    /// </summary>
    public static class DataTagActivation
    {
        public const string NotLoggableText = "Strings and system types cannot be logged, so they cannot be activated.";

        public static bool CanActivate(DataSourceType sourceType, DataTagType tagType)
            => sourceType != DataSourceType.S7CommPlus || tagType != DataTagType.Unknown;
    }
}
