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

using System.Diagnostics;
using System.Globalization;
using DataLogLib.Models;
using DataLogLib.OpcUaNodeset;
using DataLogLib.Services;
using Serilog;

namespace DataLogService
{
    /// <summary>The uploaded file of an XML import, shown as the first line of its status.</summary>
    public sealed class XmlImportUpload
    {
        public string FileName { get; set; }
        public long Bytes { get; set; }
        public double Seconds { get; set; }
    }

    internal sealed class XmlImportSupervisor
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, XmlImportRun> _runs = [];
        private readonly IDataAccess _dataAccess;

        public XmlImportSupervisor(IDataAccess dataAccess)
        {
            _dataAccess = dataAccess;
        }

        public bool IsRunning(int dataSourceId)
        {
            lock (_gate)
            {
                return _runs.TryGetValue(dataSourceId, out var run) && run.Status.Running;
            }
        }

        /// <summary>The data source whose import is running, null when none runs.</summary>
        public int? RunningDataSourceId()
        {
            lock (_gate)
            {
                foreach (var run in _runs.Values)
                {
                    if (run.Status.Running)
                    {
                        return run.DataSourceId;
                    }
                }

                return null;
            }
        }

        public void Start(int dataSourceId, int minCycle, string filePath, XmlImportUpload upload)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The XML file path is missing.");
            }

            XmlImportRun run;
            lock (_gate)
            {
                // One import at a time: each child may take a quarter of the memory of the host (1 GB on a Pi).
                if (_runs.Values.Any(existing => existing.Status.Running))
                {
                    throw new InvalidOperationException(XmlImportLimits.AlreadyRunningMessage);
                }

                run = CreateRun(dataSourceId, minCycle, filePath, upload);
                _runs[dataSourceId] = run;
            }

            try
            {
                run.Process = StartChild(run.JobPath);
                TryLowerPriority(run.Process);
                TryIonice(run.Process.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "XML import process did not start for data source {DataSourceId}", dataSourceId);
                lock (_gate)
                {
                    run.Status.Running = false;
                    run.Status.Completed = true;
                    run.Status.Error = XmlImportLimits.CouldNotStartMessage;
                }

                Cleanup(run);
                throw new InvalidOperationException(XmlImportLimits.CouldNotStartMessage);
            }

            var thread = new Thread(() => Monitor(run))
            {
                IsBackground = true,
                Name = "XmlImportWatch-" + dataSourceId.ToString(CultureInfo.InvariantCulture)
            };
            thread.Start();
        }

        public OpcUaBrowseJobStatus Get(int dataSourceId)
        {
            lock (_gate)
            {
                if (!_runs.TryGetValue(dataSourceId, out var run))
                {
                    return new OpcUaBrowseJobStatus();
                }

                return run.CopyStatus();
            }
        }

        public void Cancel(int dataSourceId)
        {
            XmlImportRun run;
            lock (_gate)
            {
                if (!_runs.TryGetValue(dataSourceId, out run) || !run.Status.Running)
                {
                    return;
                }

                run.CancelRequested = true;
            }

            try
            {
                File.WriteAllText(run.CancelPath, "cancel");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not signal XML import cancel");
            }

            try
            {
                if (run.Process != null && !run.Process.WaitForExit(XmlImportLimits.CancelGraceMs))
                {
                    Kill(run.Process);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not stop XML import process");
            }

            try
            {
                run.ApplyCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Apply already finished.
            }
        }

        public void StopAll()
        {
            List<int> ids;
            lock (_gate)
            {
                ids = _runs.Keys.ToList();
            }

            foreach (var id in ids)
            {
                Cancel(id);
            }
        }

        private void Monitor(XmlImportRun run)
        {
            try
            {
                var lastWork = -1L;
                var lastPhase = "";
                var lastMoveUtc = DateTime.UtcNow;
                var phases = new PhaseClock();
                while (run.Process != null && !run.Process.HasExited)
                {
                    if (run.CancelRequested)
                    {
                        break;
                    }

                    var snapshot = XmlImportSnapshot.Read(run.StatusPath);
                    EmitPhaseMilestones(run, snapshot, phases);

                    var moved = snapshot.WorkTicks != lastWork ||
                        !string.Equals(snapshot.Phase, lastPhase, StringComparison.Ordinal);
                    if (moved)
                    {
                        lastWork = snapshot.WorkTicks;
                        lastPhase = snapshot.Phase ?? "";
                        lastMoveUtc = DateTime.UtcNow;
                    }

                    var idleSeconds = (DateTime.UtcNow - lastMoveUtc).TotalSeconds;
                    var aliveAge = snapshot.AliveUtc.HasValue
                        ? (DateTime.UtcNow - snapshot.AliveUtc.Value).TotalSeconds
                        : idleSeconds;

                    if (IsActivePhase(snapshot.Phase) &&
                        idleSeconds >= XmlImportLimits.StallAbortSeconds)
                    {
                        var summary = LinuxProcessLimits.TryReadProcessStatusSummary(run.Process.Id);
                        Log.Warning(
                            "XML import stalled. phase={Phase} work={Work} items={Items} done={Done}/{Total} idle={Idle}s {Summary}",
                            snapshot.Phase,
                            snapshot.WorkTicks,
                            snapshot.ItemsAccepted,
                            snapshot.PhaseDone,
                            snapshot.PhaseTotal,
                            (int)idleSeconds,
                            summary);
                        Kill(run.Process);
                        try
                        {
                            run.Process?.WaitForExit(XmlImportLimits.CancelGraceMs);
                        }
                        catch (Exception)
                        {
                            // Process is already gone.
                        }

                        run.Complete(XmlImportLimits.StalledMessage, XmlImportLimits.StalledMessage);
                        return;
                    }

                    if (IsActivePhase(snapshot.Phase) &&
                        aliveAge >= XmlImportLimits.AliveTimeoutSeconds)
                    {
                        run.SetCurrent(XmlImportLimits.NotRespondingMessage);
                    }
                    else
                    {
                        var line = FormatProgress(snapshot, phases, out var percent);
                        if (IsActivePhase(snapshot.Phase) &&
                            idleSeconds >= XmlImportLimits.StallSeconds)
                        {
                            line += " · no progress for " +
                                ((int)idleSeconds).ToString(CultureInfo.InvariantCulture) + " s";
                        }

                        run.SetCurrent(line, percent);
                    }

                    Thread.Sleep(500);
                }

                try
                {
                    run.Process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "XML import process wait failed");
                }

                if (run.CancelRequested)
                {
                    run.Complete(XmlImportLimits.CancelledMessage, XmlImportLimits.CancelledMessage);
                    return;
                }

                if (IsMemoryExit(run.Process))
                {
                    Log.Warning(
                        "XML import process was killed by the system. exit={ExitCode}",
                        SafeExitCode(run.Process));
                    run.Complete(XmlImportLimits.SystemKilledMessage, XmlImportLimits.SystemKilledMessage);
                    return;
                }

                var exitCode = run.Process?.ExitCode ?? 1;
                var finalSnapshot = XmlImportSnapshot.Read(run.StatusPath);
                if (exitCode == 2)
                {
                    run.Complete(XmlImportLimits.CancelledMessage, XmlImportLimits.CancelledMessage);
                    return;
                }

                if (exitCode == 3)
                {
                    var message = string.IsNullOrWhiteSpace(finalSnapshot.Error)
                        ? XmlImportLimits.RanOutOfMemoryMessage
                        : finalSnapshot.Error;
                    Log.Warning("XML import child ran out of memory. exit={ExitCode} error={Error}", exitCode, message);
                    run.Complete(message, message);
                    return;
                }

                if (exitCode != 0 || !string.Equals(finalSnapshot.Phase, "ready", StringComparison.Ordinal))
                {
                    var message = string.IsNullOrWhiteSpace(finalSnapshot.Error)
                        ? XmlImportLimits.UnknownMessage
                        : finalSnapshot.Error;
                    Log.Warning(
                        "XML import child failed. exit={ExitCode} phase={Phase} error={Error}",
                        exitCode,
                        finalSnapshot.Phase,
                        message);
                    run.Complete(message, message);
                    return;
                }

                EmitPhaseMilestones(run, finalSnapshot, phases);
                if (finalSnapshot.ItemsAccepted <= 0)
                {
                    // Nothing to apply; the tag list stays as it is.
                    run.Complete(XmlImportLimits.NoImportableTagsMessage, XmlImportLimits.NoImportableTagsMessage);
                    return;
                }

                var renames = XmlImportJob.LoadRenames(run.StatusPath);
                if (renames.Count > 0)
                {
                    var renamed = _dataAccess.DataTagsRenameAddresses(run.DataSourceId, renames);
                    if (renamed > 0)
                    {
                        Log.Information(
                            "OPC UA XML import: {Count} array element tags of data source {Id} take the index address",
                            renamed, run.DataSourceId);
                    }
                }

                run.SetCurrent("Check existing tags … starting", 0);
                var syncTimer = new SyncTimer();
                var pacer = new LoadPacer(LoadPacer.MaxLoadFromEnvironment(), work: "OPC UA XML import");
                var result = _dataAccess.DataTagsSyncFromXmlStage(
                    run.DataSourceId,
                    run.MinCycle,
                    run.JobId,
                    progress =>
                    {
                        // Reported after each chunk: the place to slow down while the device is busy.
                        if (progress != null && !progress.Finished)
                        {
                            pacer.Step(run.ApplyCancel.Token);
                        }

                        ReportSync(run, progress, syncTimer, pacer.Slowed);
                    },
                    run.ApplyCancel.Token);
                run.AddMilestone(FormatCompleted(result, run.StartedUtc));
                run.Complete(null, null);
            }
            catch (OperationCanceledException)
            {
                run.Complete(XmlImportLimits.CancelledMessage, XmlImportLimits.CancelledMessage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OPC UA XML import failed for data source {DataSourceId}", run.DataSourceId);
                var message = ex is OutOfMemoryException
                    ? XmlImportLimits.SaveOutOfMemoryMessage
                    : XmlImportMemory.IsDiskFull(ex)
                        ? XmlImportLimits.NotEnoughDiskMessage
                        : XmlImportLimits.UnknownMessage;
                run.Complete(message, message);
            }
            finally
            {
                ClearStage();
                Cleanup(run);
                // Only now another import may start: the stage is emptied as a whole.
                run.Finish();
            }
        }

        // One import runs at a time and this one is over, so the stage holds only its rows. TRUNCATE takes no time
        // and leaves nothing for autovacuum; a DELETE of 570 000 rows ran past its timeout on a busy Pi.
        private void ClearStage()
        {
            try
            {
                _dataAccess.XmlImportStageClear();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not clear the XML import stage");
            }
        }

        private static XmlImportRun CreateRun(int dataSourceId, int minCycle, string filePath, XmlImportUpload upload)
        {
            var directory = Path.Combine(XmlImportLimits.WorkFolderPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var run = new XmlImportRun
            {
                DataSourceId = dataSourceId,
                MinCycle = minCycle,
                Directory = directory,
                SourcePath = filePath,
                JobId = Guid.NewGuid(),
                StatusPath = Path.Combine(directory, "status.txt"),
                CancelPath = Path.Combine(directory, "cancel"),
                JobPath = Path.Combine(directory, "job.txt"),
                StartedUtc = DateTime.UtcNow.AddSeconds(-Math.Max(0, upload?.Seconds ?? 0))
            };
            new XmlImportJob
            {
                SourcePath = run.SourcePath,
                JobId = run.JobId,
                StatusPath = run.StatusPath,
                CancelPath = run.CancelPath
            }.Save(run.JobPath);
            run.Status.Running = true;
            if (upload != null)
            {
                run.FinishCurrent(
                    "Upload " + ShortFileName(upload.FileName) + " … " +
                    FormatBytes(upload.Bytes) + " · " + FormatElapsed(upload.Seconds));
            }

            run.SetCurrent("Parse nodeset file …", 0);
            return run;
        }

        // TIA names its exports after project, PLC and server; the middle goes, so the sizes and times stay visible.
        internal static string ShortFileName(string fileName)
        {
            const int Max = 28;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "file";
            }

            return fileName.Length <= Max ? fileName : fileName[..12] + "…" + fileName[^(Max - 13)..];
        }

        private static Process StartChild(string jobPath)
        {
            var host = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("The application host path is missing.");
            }

            var entry = Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
            var start = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            // "dotnet DataLogApi.dll" needs the dll as the first argument. The apphost (DataLogApi) reports the dll as
            // its first argument too but runs it by itself; passing it again hides --xml-import and starts a second API.
            if (!string.IsNullOrWhiteSpace(entry) &&
                !string.Equals(Path.GetFullPath(host), Path.GetFullPath(entry), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    Path.GetFileNameWithoutExtension(host),
                    Path.GetFileNameWithoutExtension(entry),
                    StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(entry);
            }

            start.ArgumentList.Add(XmlImportLimits.ArgName);
            start.ArgumentList.Add(jobPath);
            ApplyChildMemoryEnvironment(start);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("The XML import process did not start.");
            }

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    Log.Warning("XML import child: {Line}", args.Data);
                }
            };
            process.BeginErrorReadLine();
            return process;
        }

        private static void ApplyChildMemoryEnvironment(ProcessStartInfo start)
        {
            var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (!string.IsNullOrWhiteSpace(databaseUrl))
            {
                start.Environment["DATABASE_URL"] = databaseUrl;
            }

            start.Environment["DOTNET_gcServer"] = "0";
            start.Environment["DOTNET_GCConserveMemory"] = "7";
            var heapBytes = LinuxProcessLimits.ResolveChildHeapHardLimitBytes();
            start.Environment["DOTNET_GCHeapHardLimit"] =
                heapBytes.ToString("X", CultureInfo.InvariantCulture);
            Log.Information(
                "XML import child heap hard limit set to {HeapBytes} bytes",
                heapBytes);
        }

        // Nice 19: the parser yields the CPU to the logging of values and the OPC UA sessions.
        private static void TryLowerPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.Idle;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "The priority of the XML import was not lowered");
            }
        }

        private static void TryIonice(int pid)
        {
            if (!File.Exists("/usr/bin/ionice"))
            {
                return;
            }

            try
            {
                using var ionice = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/ionice",
                    ArgumentList = { "-c", "2", "-n", "7", "-p", pid.ToString(CultureInfo.InvariantCulture) },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                ionice?.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ionice was not applied to the XML import");
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not kill XML import process");
            }
        }

        private static bool IsMemoryExit(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                var code = process.ExitCode;
                return code is 137 or 9 or -9;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int SafeExitCode(Process process)
        {
            try
            {
                return process?.ExitCode ?? -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // The parse line ends when the child resolves the types; resolve and finalize are one step for the user.
        private static void EmitPhaseMilestones(XmlImportRun run, XmlImportSnapshot snapshot, PhaseClock phases)
        {
            var order = OrderOf(snapshot?.Phase ?? "");
            if (order >= OrderOf("resolve") && phases.Emitted < OrderOf("parse"))
            {
                run.FinishCurrent(FormatParseDone(snapshot, phases.Elapsed()));
                phases.Next(OrderOf("parse"));
            }

            if (order >= OrderOf("ready") && phases.Emitted < OrderOf("ready"))
            {
                run.FinishCurrent(
                    "Prepare tag list … " + FormatCount(snapshot.ItemsAccepted) + " importable tags · " +
                    FormatElapsed(phases.Elapsed()));
                phases.Next(OrderOf("ready"));
            }
        }

        private static int OrderOf(string phase)
        {
            return phase switch
            {
                "parse" or "write" => 1,
                "resolve" => 2,
                "finalize" => 3,
                "ready" => 4,
                _ => 0
            };
        }

        private static bool IsActivePhase(string phase)
        {
            return phase == "parse" || phase == "write" || phase == "resolve" || phase == "finalize";
        }

        private static string FormatProgress(XmlImportSnapshot snapshot, PhaseClock phases, out double? percent)
        {
            percent = null;
            if (string.Equals(snapshot.Phase, "resolve", StringComparison.Ordinal) ||
                string.Equals(snapshot.Phase, "finalize", StringComparison.Ordinal))
            {
                return "Prepare tag list … " + FormatCount(snapshot.ItemsAccepted) + " tags";
            }

            if (snapshot.FileBytes <= 0)
            {
                return "Parse nodeset file … starting";
            }

            percent = Math.Clamp(100.0 * snapshot.BytesRead / snapshot.FileBytes, 0, 100);

            var parts = new List<string>
            {
                FormatBytes(snapshot.BytesRead) + " / " + FormatBytes(snapshot.FileBytes),
                FormatCount(snapshot.VariablesRead) + " variables"
            };

            var elapsed = phases.Elapsed();
            if (elapsed >= 2 && snapshot.BytesRead > 0 && snapshot.FileBytes > snapshot.BytesRead)
            {
                var bytesPerSecond = snapshot.BytesRead / elapsed;
                parts.Add(FormatRate(bytesPerSecond));
                var remainingSeconds = (snapshot.FileBytes - snapshot.BytesRead) / bytesPerSecond;
                if (remainingSeconds > 0 && !double.IsInfinity(remainingSeconds))
                {
                    parts.Add("~" + FormatDuration(remainingSeconds) + " left");
                }
            }

            if (snapshot.Slowed)
            {
                parts.Add(SlowedText);
            }

            return "Parse nodeset file … " + string.Join(" · ", parts);
        }

        private const string SlowedText = "slowed down, the device is busy";

        private static string FormatParseDone(XmlImportSnapshot snapshot, double seconds)
        {
            return "Parse nodeset file … " + FormatBytes(snapshot.FileBytes) + " · " +
                FormatCount(snapshot.VariablesRead) + " variables · " + FormatElapsed(seconds);
        }

        // Thousands separated by a narrow no-break space (491 000), readable in every language of the UI.
        internal static string FormatCount(long value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", "\u202F", StringComparison.Ordinal);
        }

        // The time a step took: tenths below 10 s, then seconds, minutes and hours.
        internal static string FormatElapsed(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0)
            {
                seconds = 0;
            }

            if (seconds < 10)
            {
                return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            }

            var whole = (long)Math.Round(seconds);
            if (whole < 60)
            {
                return whole.ToString(CultureInfo.InvariantCulture) + " s";
            }

            if (whole < 3600)
            {
                return (whole / 60).ToString(CultureInfo.InvariantCulture) + " min " +
                    (whole % 60).ToString(CultureInfo.InvariantCulture) + " s";
            }

            return (whole / 3600).ToString(CultureInfo.InvariantCulture) + " h " +
                (whole % 3600 / 60).ToString(CultureInfo.InvariantCulture) + " min";
        }

        private static string FormatBytes(long bytes)
        {
            var mb = bytes / (1024.0 * 1024.0);
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private static string FormatRate(double bytesPerSecond)
        {
            var mb = bytesPerSecond / (1024.0 * 1024.0);
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 60)
            {
                return Math.Max(1, (int)Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture) + " s";
            }

            if (seconds < 3600)
            {
                return Math.Max(1, (int)Math.Ceiling(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";
            }

            var hours = seconds / 3600.0;
            return hours.ToString("0.0", CultureInfo.InvariantCulture) + " h";
        }

        // Counts from the first report of a phase, so the wait for the first chunk is not part of the rate.
        private sealed class SyncTimer
        {
            private string _phase;
            private int _startScanned;
            private DateTime _startUtc;

            public string Rate(DataTagBrowseSyncProgress progress)
            {
                var now = DateTime.UtcNow;
                if (!string.Equals(_phase, progress.Phase, StringComparison.Ordinal))
                {
                    _phase = progress.Phase;
                    _startScanned = progress.Scanned;
                    _startUtc = now;
                    return progress.Finished ? " · " + FormatElapsed(0) : "";
                }

                var elapsed = (now - _startUtc).TotalSeconds;
                if (progress.Finished)
                {
                    return " · " + FormatElapsed(elapsed);
                }

                var done = progress.Scanned - _startScanned;
                if (elapsed < 2 || done <= 0)
                {
                    return "";
                }

                // Only the time left: the line has to fit the dialog next to the counts.
                var remaining = (progress.Total - progress.Scanned) / (done / elapsed);
                return remaining > 0 && !double.IsInfinity(remaining) ? " · ~" + FormatDuration(remaining) + " left" : "";
            }
        }

        private static void ReportSync(XmlImportRun run, DataTagBrowseSyncProgress progress, SyncTimer timer, bool slowed)
        {
            if (progress == null)
            {
                return;
            }

            var text = FormatSync(progress) + timer.Rate(progress);
            if (slowed && !progress.Finished)
            {
                text += " · " + SlowedText;
            }

            if (progress.Finished)
            {
                run.FinishCurrent(text);
            }
            else
            {
                run.SetCurrent(
                    text,
                    progress.Total > 0 ? Math.Clamp(100.0 * progress.Scanned / progress.Total, 0, 100) : null);
            }
        }

        internal static string FormatSync(DataTagBrowseSyncProgress progress)
        {
            var scanned = FormatCount(progress.Scanned);
            var total = FormatCount(progress.Total);
            // Only the changes that happened, so the line fits the dialog without being cut.
            var isNew = string.Equals(progress.Phase, "new", StringComparison.Ordinal);
            var head = isNew ? "Add new tags … " : "Check existing tags … ";
            var changes = new List<string>();
            AddChange(changes, isNew ? progress.Created : 0, "created");
            AddChange(changes, isNew ? 0 : progress.Deleted, "deleted");
            AddChange(changes, isNew ? 0 : progress.Restored, "restored");
            AddChange(changes, isNew ? 0 : progress.Updated, "updated");
            if (progress.Finished)
            {
                return head + (changes.Count > 0 ? string.Join(" · ", changes) : isNew ? "none" : "no changes");
            }

            if (!isNew && progress.Total == 0)
            {
                return "Check existing tags … no existing tags";
            }

            changes.Insert(0, scanned + " / " + total);
            return head + string.Join(" · ", changes);
        }

        private static void AddChange(List<string> changes, long count, string what)
        {
            if (count > 0)
            {
                changes.Add(FormatCount(count) + " " + what);
            }
        }

        private static string FormatCompleted(DataTagBrowseSyncResult result, DateTime startedUtc)
        {
            result ??= new DataTagBrowseSyncResult();
            var parts = new List<string>();
            if (result.Created > 0)
            {
                parts.Add(FormatCount(result.Created) + " created");
            }

            if (result.Updated > 0)
            {
                parts.Add(FormatCount(result.Updated) + " updated");
            }

            if (result.Undeleted > 0)
            {
                parts.Add(FormatCount(result.Undeleted) + " restored");
            }

            if (result.Deleted > 0)
            {
                parts.Add(FormatCount(result.Deleted) + " deleted");
            }

            if (parts.Count == 0)
            {
                parts.Add("no changes");
            }

            return "Import completed successfully in " + FormatElapsed((DateTime.UtcNow - startedUtc).TotalSeconds) +
                " · " + string.Join(" · ", parts);
        }

        private static void Cleanup(XmlImportRun run)
        {
            TryDelete(run.SourcePath);
            try
            {
                if (!string.IsNullOrWhiteSpace(run.Directory) && Directory.Exists(run.Directory))
                {
                    Directory.Delete(run.Directory, true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not delete XML import job directory");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // The next cleanup pass removes leftovers.
            }
        }

        // The step of the child whose line was finished last and the time its successor started.
        private sealed class PhaseClock
        {
            private DateTime _startUtc = DateTime.UtcNow;

            public int Emitted { get; private set; }

            public double Elapsed()
            {
                return (DateTime.UtcNow - _startUtc).TotalSeconds;
            }

            public void Next(int emitted)
            {
                Emitted = emitted;
                _startUtc = DateTime.UtcNow;
            }
        }

        private sealed class XmlImportRun
        {
            private readonly object _statusGate = new();

            public int DataSourceId { get; set; }
            public int MinCycle { get; set; }
            public string Directory { get; set; }
            public string SourcePath { get; set; }
            public Guid JobId { get; set; }
            public string StatusPath { get; set; }
            public string CancelPath { get; set; }
            public string JobPath { get; set; }
            public Process Process { get; set; }
            public DateTime StartedUtc { get; set; }
            public bool CancelRequested { get; set; }
            public CancellationTokenSource ApplyCancel { get; } = new();
            public OpcUaBrowseJobStatus Status { get; } = new();

            public void SetCurrent(string text, double? percent = null)
            {
                lock (_statusGate)
                {
                    if (Status.Lines.Count == 0 || !Status.Lines[^1].ReplaceLast)
                    {
                        Status.Lines.Add(new OpcUaBrowseStatusLine { Text = text, ReplaceLast = true, Percent = percent });
                    }
                    else
                    {
                        Status.Lines[^1].Text = text;
                        Status.Lines[^1].Percent = percent;
                    }

                    Trim();
                }
            }

            public void FinishCurrent(string text)
            {
                lock (_statusGate)
                {
                    var finished = text + " · done";
                    if (Status.Lines.Count > 0 && Status.Lines[^1].ReplaceLast)
                    {
                        Status.Lines[^1].Text = finished;
                        Status.Lines[^1].ReplaceLast = false;
                        Status.Lines[^1].Percent = null;
                    }
                    else
                    {
                        Status.Lines.Add(new OpcUaBrowseStatusLine { Text = finished, ReplaceLast = false });
                    }

                    Trim();
                }
            }

            public void AddMilestone(string text)
            {
                lock (_statusGate)
                {
                    if (Status.Lines.Count > 0 && Status.Lines[^1].ReplaceLast)
                    {
                        Status.Lines[^1].ReplaceLast = false;
                        Status.Lines[^1].Percent = null;
                    }

                    Status.Lines.Add(new OpcUaBrowseStatusLine { Text = text, ReplaceLast = false });
                    Trim();
                }
            }

            public void Complete(string line, string error)
            {
                lock (_statusGate)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        AddMilestone(line);
                    }

                    Status.Completed = true;
                    Status.Error = error;
                }
            }

            // The run counts as running until its stage is cleared, so no other import starts in between.
            public void Finish()
            {
                lock (_statusGate)
                {
                    Status.Running = false;
                    Status.Completed = true;
                }
            }

            public OpcUaBrowseJobStatus CopyStatus()
            {
                lock (_statusGate)
                {
                    return new OpcUaBrowseJobStatus
                    {
                        Running = Status.Running,
                        Completed = Status.Completed,
                        Error = Status.Error,
                        Lines = Status.Lines.Select(line => new OpcUaBrowseStatusLine
                        {
                            Text = line.Text,
                            ReplaceLast = line.ReplaceLast,
                            Percent = line.Percent
                        }).ToList()
                    };
                }
            }

            private void Trim()
            {
                while (Status.Lines.Count > XmlImportLimits.StatusLineCap)
                {
                    Status.Lines.RemoveAt(0);
                }
            }
        }
    }
}
