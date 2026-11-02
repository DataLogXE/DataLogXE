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

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Reads the load of the host. In a container without a CPU limit /proc/loadavg and the
    /// processor count are those of the host.
    /// </summary>
    internal static class SystemLoad
    {
        /// <summary>
        /// Returns the load average of the last minute per core, which also counts the processes
        /// waiting for the disk or swap. Null when it cannot be read (not Linux).
        /// </summary>
        internal static double? ReadLoadPerCore()
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                return ParseLoadPerCore(File.ReadAllText("/proc/loadavg"), Environment.ProcessorCount);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses the first field of /proc/loadavg (e.g. "1.52 0.98 0.73 2/345 1234").
        /// </summary>
        internal static double? ParseLoadPerCore(string loadAvg, int cores)
        {
            var parts = loadAvg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || cores <= 0 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var load))
            {
                return null;
            }
            return load / cores;
        }
    }
}
