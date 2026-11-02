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

using Npgsql;

namespace DataLogLib.OpcUaNodeset
{
    public static class XmlImportMemory
    {
        public static string Describe(Exception exception)
        {
            if (exception is OutOfMemoryException)
            {
                return XmlImportLimits.RanOutOfMemoryMessage;
            }

            if (IsDiskFull(exception))
            {
                return XmlImportLimits.NotEnoughDiskMessage;
            }

            if (exception is NpgsqlException { IsTransient: true })
            {
                return XmlImportLimits.DatabaseBusyMessage;
            }

            if (exception is InvalidDataException invalid)
            {
                return string.IsNullOrWhiteSpace(invalid.Message)
                    ? XmlImportLimits.MalformedXmlMessage
                    : invalid.Message;
            }

            return XmlImportLimits.UnknownMessage;
        }

        public static bool IsDiskFull(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is PostgresException postgres && postgres.SqlState == "53100")
                {
                    return true;
                }

                if (current is IOException io && IsDiskFullIo(io))
                {
                    return true;
                }

                var message = current.Message ?? "";
                if (message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("database or disk is full", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Disk full", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDiskFullIo(IOException exception)
        {
            const int errorDiskFull = 112;
            const int errorHandleDiskFull = 39;
            const int enospc = 28;
            var code = exception.HResult & 0xFFFF;
            return code is errorDiskFull or errorHandleDiskFull or enospc;
        }
    }
}
