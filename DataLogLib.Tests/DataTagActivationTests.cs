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
using Xunit;

namespace DataLogLib.Tests
{
    public class DataTagActivationTests
    {
        [Theory]
        [InlineData(DataSourceType.S7CommPlus, DataTagType.Unknown, false)]
        [InlineData(DataSourceType.S7CommPlus, DataTagType.Number, true)]
        [InlineData(DataSourceType.S7CommPlus, DataTagType.Bool, true)]
        [InlineData(DataSourceType.OpcUa, DataTagType.Unknown, true)]
        [InlineData(DataSourceType.Test, DataTagType.Unknown, true)]
        public void Only_S7_strings_and_system_types_cannot_be_activated(DataSourceType source, DataTagType tag, bool expected)
        {
            Assert.Equal(expected, DataTagActivation.CanActivate(source, tag));
        }
    }
}
