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
    public enum CompressionType
    {
        RAW, C001, C005, C010, C030, C060, C300, C600, C1800, C3600
    }

    public enum ValueState
    {
        Invalid = 0, Valid = 1, Resend = 2
    }

    public enum DataSourceType
    {
        Undefined = 0, System = 1, OpcUa = 2, S7CommPlus = 3, Test = 99
    }

    public enum DataSourceState
    {
        Undefined = 0, Inactive = 1, Loading = 2,
        Disconnected = 10, Connecting = 11, Connected = 12, Disconnecting = 13, 
        Error = 20
    }

    public enum DataTagType
    {
        Unknown = 0, Bool = 1, Number = 2
    }

    public enum ChartItemType
    {
        Root = 0, Folder = 1, Chart = 2
    }

}
