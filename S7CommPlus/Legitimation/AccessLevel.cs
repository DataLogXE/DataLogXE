#region License
/******************************************************************************
 * S7CommPlusDriver
 *
 * From S7CommPlusDriver (https://github.com/thomas-v2/S7CommPlusDriver),
 * file src/S7CommPlusDriver/Legitimation/AccessLevel.cs,
 * contributed by Ich-Eben.
 * Licensed under the GNU Lesser General Public License, version 3 or (at your
 * option) any later version, like all sources of S7CommPlusDriver.
 /****************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace S7CommPlusDriver.Legitimation
{
    public static class AccessLevel 
    {
        public const UInt32 FullAccess = 1;
        public const UInt32 ReadAccess = 2;
        public const UInt32 HMIAccess = 3;
        public const UInt32 NoAccess = 4;
    }
}
