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

namespace DataLogApi.Hosting
{
    /// <summary>
    /// Where the Serilog file sink writes: {Folder}/{BaseName}.log, rolled to {BaseName}_001.log and so on at
    /// <see cref="FileSizeLimitBytes"/>, the newest <see cref="RetainedFileCount"/> files kept.
    /// </summary>
    public sealed class LogFiles
    {
        public const long FileSizeLimitBytes = 10L * 1024 * 1024;
        public const int RetainedFileCount = 10;

        public LogFiles(string folder, string baseName)
        {
            Folder = folder;
            BaseName = baseName;
        }

        public string Folder { get; }
        public string BaseName { get; }

        /// <summary>The log files, newest first; empty when the folder does not exist.</summary>
        public IReadOnlyList<FileInfo> List()
        {
            var folder = new DirectoryInfo(Folder);
            if (!folder.Exists)
            {
                return [];
            }

            return folder.EnumerateFiles(BaseName + "*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
    }
}
