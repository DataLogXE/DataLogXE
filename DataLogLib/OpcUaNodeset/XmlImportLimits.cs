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

namespace DataLogLib.OpcUaNodeset
{
    public static class XmlImportLimits
    {
        public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
        public const long SidecarHeadroomFloorBytes = 256L * 1024 * 1024;

        // The runtime reserves several GB of virtual address space. A resident cap
        // aborts the process before any XML is read, so no address-space limit is set.
        public const long AddressSpaceLimitBytes = 0;

        public const int BatchSize = 256;
        public const int QueueBatches = 2;
        public const int InsertChunkRows = 20000;
        public const int CommitRows = 20000;
        public const int CommitMs = 2000;
        public const int FileBufferBytes = 64 * 1024;
        public const int MaxTagBytes = 256 * 1024;
        public const int MaxTextChars = 2048;
        public const int MaxDisplayNameChars = 512;
        public const int StatusLineCap = 12;
        public const int StallSeconds = 30;
        public const int AliveTimeoutSeconds = 60;
        public const int StallAbortSeconds = 15 * 60;
        public const int CancelGraceMs = 2000;
        public const int PendingTypeCap = 16384;
        public static readonly TimeSpan ConnectWait = TimeSpan.FromMinutes(2);
        public const long ChildHeapFloorBytes = 96L * 1024 * 1024;
        public const long ChildHeapCeilingBytes = 512L * 1024 * 1024;
        public const int ChildHeapPercent = 25;

        public const string ArgName = "--xml-import";
        public const string WorkFolderName = "xml-import";

        public const string RanOutOfMemoryMessage = "The XML import ran out of memory.";
        public const string SystemKilledMessage = "The XML import was stopped by the system because memory was exhausted.";
        public const string NotEnoughDiskMessage = "Not enough free disk space to import this XML file.";
        public const string CouldNotStartMessage = "The XML import could not be started.";
        public const string SaveOutOfMemoryMessage = "The XML import ran out of memory while saving tags.";
        public const string CancelledMessage = "The XML import was cancelled.";
        public const string StalledMessage = "The XML import stopped making progress.";
        public const string NotRespondingMessage = "The import process is not responding.";
        public const string UnknownMessage = "An error occurred during XML import.";
        public const string MalformedXmlMessage = "The XML file is not well formed.";
        public const string DatabaseBusyMessage =
            "The database did not answer in time. Try the import again when the device is less busy.";
        public const string AlreadyRunningMessage = "An XML import is already running.";
        public const string NoImportableTagsMessage =
            "The file contains no importable variables (scalar variables with string NodeIds). Nothing was changed.";
        public const string BrowseRunningMessage =
            "A browse is running for this data source. Wait for it to finish or cancel it before importing XML.";
        public const string BrowseUnfinishedMessage =
            "An unfinished browse exists for this data source. Cancel it in Browse OPC UA before importing XML.";

        public static string WorkFolderPath
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("DATA_PATH");
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Environment.GetEnvironmentVariable("DATABASE_PATH");
                }

                if (string.IsNullOrWhiteSpace(root))
                {
                    root = "/app/data";
                }

                return Path.Combine(root, WorkFolderName);
            }
        }

        public static long RequiredDiskBytes(long fileBytes)
        {
            if (fileBytes < 0)
            {
                fileBytes = 0;
            }

            return (fileBytes / 2) + SidecarHeadroomFloorBytes;
        }
    }
}
