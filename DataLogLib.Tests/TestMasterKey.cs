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

using DataLogLib.Common.Crypt;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DataLogLib.Tests
{
    internal static class TestMasterKey
    {
        /// <summary>
        /// The tests never read or create a master.key file: every data source write uses this key.
        /// </summary>
        [ModuleInitializer]
        internal static void Init()
        {
            MasterKey.SetForTests(RandomNumberGenerator.GetBytes(MasterKey.KeyBytes));
        }
    }
}
