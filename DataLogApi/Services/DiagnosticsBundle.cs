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

using DataLogApi.Hosting;
using DataLogLib.Common.Serilog;
using DataLogLib.Services;
using DataLogLib.Services.DataAccess;
using DataLogLib.Services.EventLog;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DataLogApi.Services
{
    /// <summary>
    /// The support bundle: one ZIP file with what a bug report needs, written while it is read. Each part is written on
    /// its own; one that fails is named in errors.txt and the others stay, so the bundle also helps when the database
    /// is down (the log files are written all the same). Secrets stay out: the configuration is a whitelist, the data
    /// sources go without their addresses, and the log files are masked like the log itself.
    /// </summary>
    public sealed class DiagnosticsBundle
    {
        public const int DefaultHours = 24;
        public const int MaxHours = 14 * 24;
        public const long MaxLogFileBytes = 50L * 1024 * 1024;

        // The configuration keys of the bundle; every other key (passwords, the master key, DATABASE_URL) stays out
        private static readonly string[] ConfigKeys =
        [
            "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT",
            "DATA_PATH", "LOG_PATH", "OPCUA_CERT_PATH", "DATABASE_PATH",
            "POSTGRES_HOST", "POSTGRES_PORT", "POSTGRES_DB", "POSTGRES_USER",
            "EVENT_LOG_ENABLED", "EVENT_LOG_MIN_LEVEL", "TELEGRAF_PORT",
            "ARCHIVE_COUNT_BACKFILL_DAYS", "ARCHIVE_REPAIR_DAYS", "ARCHIVE_REPAIR_MAX_LOAD",
            LoadPacer.MaxLoadKey, DatabaseStartupWait.TimeoutKey,
            "Serilog:MinimumLevel:Default"
        ];

        // Keys whose value is a secret: only whether they are set
        private static readonly string[] SecretKeys = ["POSTGRES_PASSWORD", "DATALOG_MASTER_KEY", "DATABASE_URL"];

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly IConfiguration _configuration;
        private readonly LogFiles _logFiles;
        private readonly EventLogAppService _events;
        private readonly DataSourceAppService _sources;
        private readonly List<string> _errors = [];

        public DiagnosticsBundle(
            IConfiguration configuration, LogFiles logFiles, EventLogAppService events, DataSourceAppService sources)
        {
            _configuration = configuration;
            _logFiles = logFiles;
            _events = events;
            _sources = sources;
        }

        public static int NormalizeHours(int? hours)
        {
            var value = hours ?? DefaultHours;
            return value is >= 1 and <= MaxHours ? value : throw new ArgumentException("The range spans 1 hour to 14 days.");
        }

        public static string FileName(DateTime nowUtc) =>
            $"datalogxe-support_{nowUtc:yyyy-MM-dd'T'HH-mm'Z'}.zip";

        public async Task WriteAsync(Stream body, int hours, string userName, CancellationToken cancellationToken)
        {
            var nowUtc = DateTime.UtcNow;
            var fromUtc = nowUtc.AddHours(-hours);
            using var zip = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true);

            await Part(zip, "manifest.json", stream => WriteJson(stream, Manifest(nowUtc, fromUtc, userName)));
            await Part(zip, "datasources.json", stream => WriteJson(stream, DataSources()));
            await Part(zip, "runtime.json", stream => WriteJson(stream, Runtime()));
            await Part(zip, "config.json", stream => WriteJson(stream, Config()));
            await Part(zip, "database.json",
                stream => WriteJson(stream, DatabaseDiagnostics.Read(_configuration, cancellationToken)));
            await Part(zip, "appevent.ndjson", stream => SystemLogExport.WriteAsync(
                stream,
                _events.ExportSystemLog(new AppLogQuery(), fromUtc, nowUtc.AddMinutes(1), cancellationToken),
                SystemLogExport.FormatNdjson,
                decimalComma: false,
                SourceNames(),
                cancellationToken));
            await WriteLogFilesAsync(zip, cancellationToken);

            if (_errors.Count > 0)
            {
                await Part(zip, "errors.txt", stream => WriteText(stream, string.Join(Environment.NewLine, _errors)));
            }
        }

        private IReadOnlyDictionary<int, string> SourceNames()
        {
            return _sources.GetList().ToDictionary(s => s.Id, s => s.Name ?? "");
        }

        /// <summary>Writes one entry; an error is noted in errors.txt and leaves the entry as far as it got.</summary>
        private async Task Part(ZipArchive zip, string name, Func<Stream, Task> write)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            try
            {
                await write(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _errors.Add($"{name}: {LogRedaction.Redact(ex.Message)}");
            }
        }

        private async Task WriteLogFilesAsync(ZipArchive zip, CancellationToken cancellationToken)
        {
            IReadOnlyList<FileInfo> files;
            try
            {
                files = _logFiles.List();
            }
            catch (Exception ex)
            {
                _errors.Add($"logs: {ex.Message}");
                return;
            }

            if (files.Count == 0)
            {
                _errors.Add($"logs: no log files in {_logFiles.Folder}");
                return;
            }

            long total = 0;
            var skipped = new List<string>();
            foreach (var file in files)
            {
                if (total + file.Length > MaxLogFileBytes)
                {
                    skipped.Add(file.Name);
                    continue;
                }

                total += file.Length;
                await Part(zip, "logs/" + file.Name, async stream =>
                {
                    // The newest file is still being written: share it with the logger
                    await using var input = new FileStream(
                        file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(input, Encoding.UTF8);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        await writer.WriteLineAsync(LogRedaction.Redact(line));
                    }
                });
            }

            if (skipped.Count > 0)
            {
                _errors.Add($"logs: left out above {MaxLogFileBytes / (1024 * 1024)} MB: {string.Join(", ", skipped)}");
            }
        }

        private static object Manifest(DateTime nowUtc, DateTime fromUtc, string userName)
        {
            var process = Process.GetCurrentProcess();
            return new
            {
                product = "DataLogXE",
                version = Assembly.GetEntryAssembly()?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                createdUtc = nowUtc,
                createdBy = userName,
                logFromUtc = fromUtc,
                logToUtc = nowUtc,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
                processStartedUtc = process.StartTime.ToUniversalTime(),
                uptime = (nowUtc - process.StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm\:ss"),
                timeZone = TimeZoneInfo.Local.Id
            };
        }

        private object DataSources()
        {
            // Without host and port: the addresses of the plant stay out of the bundle
            return _sources.GetList().Select(s => new
            {
                s.Id,
                s.Name,
                s.Type,
                s.State,
                s.IsActive,
                s.IsSys,
                s.OpcUaSecurityMode,
                s.OpcUaCertificateRejected,
                s.OpcUaAlarmsEnabled,
                s.S7AlarmsEnabled,
                s.ClockOffsetMs,
                s.ValuesPerSec,
                s.TagBrowseActive,
                s.TagImportActive,
                s.CreatedAt
            }).ToList();
        }

        private object Runtime()
        {
            var process = Process.GetCurrentProcess();
            var guard = LoadGuard.Shared;
            var dataPath = Environment.GetEnvironmentVariable("DATA_PATH");
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                dataPath = "/app/data";
            }

            return new
            {
                load = new
                {
                    level = guard.Level.ToString(),
                    reason = guard.Reason,
                    since = guard.LevelFor.ToString(@"d\.hh\:mm\:ss"),
                    recordingAtRisk = guard.RecordingAtRisk
                },
                process = new
                {
                    workingSetBytes = process.WorkingSet64,
                    gcHeapBytes = GC.GetTotalMemory(false),
                    gcTotalAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    threads = process.Threads.Count,
                    threadPoolThreads = ThreadPool.ThreadCount,
                    pendingWorkItems = ThreadPool.PendingWorkItemCount
                },
                disk = Disk(dataPath),
                journals = Folder(Path.Combine(dataPath, "journal")),
                logFiles = _logFiles.List().Select(f => new { name = f.Name, bytes = f.Length, lastWriteUtc = f.LastWriteTimeUtc })
            };
        }

        private static object Disk(string path)
        {
            try
            {
                var drive = new DriveInfo(Path.GetFullPath(path));
                return new { path, totalBytes = drive.TotalSize, freeBytes = drive.AvailableFreeSpace };
            }
            catch (Exception ex)
            {
                return new { path, error = ex.Message };
            }
        }

        private static object Folder(string path)
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists)
            {
                return Array.Empty<object>();
            }

            return folder.EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => new { name = Path.GetRelativePath(path, f.FullName), bytes = f.Length, lastWriteUtc = f.LastWriteTimeUtc })
                .ToList();
        }

        private object Config()
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in ConfigKeys)
            {
                var value = _configuration[key];
                if (!string.IsNullOrEmpty(value))
                {
                    values[key] = LogRedaction.Redact(value);
                }
            }

            foreach (var key in SecretKeys)
            {
                values[key] = string.IsNullOrEmpty(_configuration[key]) ? "(not set)" : "(set)";
            }

            return values;
        }

        private static async Task WriteJson(Stream stream, object value)
        {
            await JsonSerializer.SerializeAsync(stream, value, Json);
        }

        private static async Task WriteText(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
            await stream.WriteAsync(bytes);
        }
    }
}
