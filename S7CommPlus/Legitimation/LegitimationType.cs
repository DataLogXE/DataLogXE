#region License
/******************************************************************************
 * S7CommPlusDriver
 *
 * From S7CommPlusDriver (https://github.com/thomas-v2/S7CommPlusDriver),
 * file src/S7CommPlusDriver/Legitimation/LegitimationType.cs,
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
    public static class LegitimationType 
    {
        public const UInt32 Legacy = 1;
        public const UInt32 New = 2;
    }
}
