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

using DataLogApi.Dtos;
using DataLogLib.Common;
using DataLogLib.OpcUaNodeset;
using DataLogService;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DataLogApi.Services
{
    public class OpcUaXmlImportAppService
    {
        public const long MaxFileBytes = XmlImportLimits.MaxFileBytes;
        private const int StreamBufferBytes = 64 * 1024;

        private static readonly string UploadFolderPath = XmlImportLimits.WorkFolderPath;

        private readonly ServiceCtrl _service;

        public OpcUaXmlImportAppService(ServiceCtrl service)
        {
            _service = service;
        }

        public async Task<OpcUaBrowseStatusDto> StartAsync(
            int dataSourceId,
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            EnsureOpcUaSource(dataSourceId);
            // Before the upload: a running import or browse rejects the request without reading the file.
            _service.EnsureOpcUaXmlImportCanStart(dataSourceId);
            CleanupOldUploads();
            EnsureFreeSpace(UploadFolderPath, XmlImportLimits.SidecarHeadroomFloorBytes);
            if (request.ContentLength is > 0)
            {
                if (request.ContentLength.Value > MaxFileBytes)
                {
                    throw new ArgumentException("The XML file is larger than 2 GB.");
                }

                EnsureFreeSpace(UploadFolderPath, XmlImportLimits.RequiredDiskBytes(request.ContentLength.Value));
            }

            var uploadTimer = System.Diagnostics.Stopwatch.StartNew();
            var (filePath, fileName) = await SaveXmlUploadAsync(request, cancellationToken);
            try
            {
                _service.StartOpcUaXmlImport(dataSourceId, filePath, new XmlImportUpload
                {
                    FileName = Path.GetFileName(fileName),
                    Bytes = new FileInfo(filePath).Length,
                    Seconds = uploadTimer.Elapsed.TotalSeconds
                });
            }
            catch
            {
                TryDeleteFile(filePath);
                throw;
            }

            return ToDto(_service.GetOpcUaXmlImportStatus(dataSourceId));
        }

        public OpcUaBrowseStatusDto Get(int dataSourceId)
        {
            return ToDto(_service.GetOpcUaXmlImportStatus(dataSourceId));
        }

        public void Cancel(int dataSourceId)
        {
            _service.CancelOpcUaXmlImport(dataSourceId);
        }

        private void EnsureOpcUaSource(int dataSourceId)
        {
            var source = EnsureSourceExists(dataSourceId);
            if ((DataSourceType)source.TypeId != DataSourceType.OpcUa)
            {
                throw new InvalidOperationException("XML import is only available for OPC UA data sources.");
            }
        }

        private DataLogLib.Models.DataSourceModel EnsureSourceExists(int dataSourceId)
        {
            try
            {
                return _service.DataAccess.DataSourceGetById(dataSourceId);
            }
            catch (InvalidOperationException)
            {
                throw new KeyNotFoundException($"Data source {dataSourceId} was not found.");
            }
        }

        private static async Task<(string FilePath, string FileName)> SaveXmlUploadAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
                !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The request must be multipart/form-data.");
            }

            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary))
            {
                throw new ArgumentException("The multipart boundary is missing.");
            }

            var reader = new MultipartReader(boundary, request.Body)
            {
                HeadersCountLimit = 16,
                HeadersLengthLimit = 16 * 1024,
                BodyLengthLimit = MaxFileBytes
            };

            Directory.CreateDirectory(UploadFolderPath);

            MultipartSection section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !disposition.DispositionType.Equals("form-data") ||
                    string.IsNullOrEmpty(disposition.FileName.Value))
                {
                    continue;
                }

                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "";
                if (!fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Only XML files can be imported.");
                }

                var filePath = Path.Combine(UploadFolderPath, Path.GetRandomFileName());
                try
                {
                    await WriteSectionToFileAsync(section, filePath, cancellationToken);
                    return (filePath, fileName);
                }
                catch
                {
                    TryDeleteFile(filePath);
                    throw;
                }
            }

            throw new ArgumentException("An XML file is required.");
        }

        private static async Task WriteSectionToFileAsync(
            MultipartSection section,
            string filePath,
            CancellationToken cancellationToken)
        {
            await using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                StreamBufferBytes,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            var buffer = new byte[StreamBufferBytes];
            long total = 0;
            int read;
            while ((read = await section.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxFileBytes)
                {
                    throw new ArgumentException("The XML file is larger than 2 GB.");
                }

                if (total % (1024 * 1024) < read)
                {
                    EnsureFreeSpace(Path.GetDirectoryName(filePath), XmlImportLimits.RequiredDiskBytes(total));
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total == 0)
            {
                throw new ArgumentException("The XML file is empty.");
            }

            await output.FlushAsync(cancellationToken);
            EnsureFreeSpace(Path.GetDirectoryName(filePath), XmlImportLimits.RequiredDiskBytes(total));
        }

        private static void EnsureFreeSpace(string folder, long requiredBytes)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? "." : folder));
            if (string.IsNullOrEmpty(root))
            {
                root = "/";
            }

            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < requiredBytes)
            {
                throw new ArgumentException(XmlImportLimits.NotEnoughDiskMessage);
            }
        }

        private static void CleanupOldUploads()
        {
            try
            {
                if (!Directory.Exists(UploadFolderPath))
                {
                    return;
                }

                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var file in Directory.EnumerateFiles(UploadFolderPath))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Leave files that are still locked by a running import.
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(UploadFolderPath))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            Directory.Delete(directory, true);
                        }
                    }
                    catch
                    {
                        // Leave folders that are still locked by a running import.
                    }
                }
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // The import job deletes the file when it finishes.
            }
        }

        private static OpcUaBrowseStatusDto ToDto(OpcUaBrowseJobStatus status)
        {
            status ??= new OpcUaBrowseJobStatus();
            return new OpcUaBrowseStatusDto
            {
                Running = status.Running,
                Completed = status.Completed,
                Error = status.Error,
                Lines = (status.Lines ?? []).Select(line => new OpcUaBrowseStatusLineDto
                {
                    Text = line.Text ?? "",
                    ReplaceLast = line.ReplaceLast,
                    Percent = line.Percent
                }).ToList()
            };
        }
    }
}
