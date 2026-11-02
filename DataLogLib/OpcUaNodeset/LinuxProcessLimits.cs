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

using System.Globalization;
using System.Runtime.InteropServices;

namespace DataLogLib.OpcUaNodeset
{
    public static class LinuxProcessLimits
    {
        private const int RlimitAddressSpace = 9;

        public static void TryApplyAddressSpaceLimit(long bytes)
        {
            if (!OperatingSystem.IsLinux() || bytes <= 0)
            {
                return;
            }

            var limit = new RLimit
            {
                Current = (ulong)bytes,
                Maximum = (ulong)bytes
            };
            setrlimit(RlimitAddressSpace, ref limit);
        }

        public static long ResolveChildHeapHardLimitBytes()
        {
            var available = TryReadMemAvailableBytes();
            if (available <= 0)
            {
                return XmlImportLimits.ChildHeapFloorBytes;
            }

            var limit = available * XmlImportLimits.ChildHeapPercent / 100;
            if (limit < XmlImportLimits.ChildHeapFloorBytes)
            {
                return XmlImportLimits.ChildHeapFloorBytes;
            }

            if (limit > XmlImportLimits.ChildHeapCeilingBytes)
            {
                return XmlImportLimits.ChildHeapCeilingBytes;
            }

            return limit;
        }

        public static string TryReadProcessStatusSummary(int pid)
        {
            if (!OperatingSystem.IsLinux() || pid <= 0)
            {
                return "";
            }

            try
            {
                var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/status";
                if (!File.Exists(path))
                {
                    return "";
                }

                string state = "";
                string rss = "";
                foreach (var line in File.ReadLines(path))
                {
                    if (line.StartsWith("State:", StringComparison.Ordinal))
                    {
                        state = line[6..].Trim();
                    }
                    else if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    {
                        rss = line[6..].Trim();
                    }

                    if (state.Length > 0 && rss.Length > 0)
                    {
                        break;
                    }
                }

                if (state.Length == 0 && rss.Length == 0)
                {
                    return "";
                }

                return "State=" + state + " VmRSS=" + rss;
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static long TryReadMemAvailableBytes()
        {
            if (!OperatingSystem.IsLinux())
            {
                return 0;
            }

            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    {
                        return kb * 1024L;
                    }
                }
            }
            catch (Exception)
            {
                return 0;
            }

            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit
        {
            public ulong Current;
            public ulong Maximum;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int setrlimit(int resource, ref RLimit limit);
    }
}
