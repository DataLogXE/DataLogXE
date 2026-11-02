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

namespace DataLogLib.OpcUaNodeset
{
    public sealed class XmlImportJob
    {
        public string SourcePath { get; set; }
        public Guid JobId { get; set; }
        public string StatusPath { get; set; }
        public string CancelPath { get; set; }

        /// <summary>
        /// Array element tags that move to their index address (old -> new), written by the child next to its status
        /// file and applied by the app before the sync, so tags imported before keep their id and history.
        /// </summary>
        public static string RenamesPath(string statusPath) =>
            Path.Combine(Path.GetDirectoryName(statusPath) ?? "", "renames.json");

        public static void SaveRenames(string statusPath, List<KeyValuePair<string, string>> renames)
        {
            var path = RenamesPath(statusPath);
            var temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(renames));
            File.Move(temp, path, true);
        }

        public static List<KeyValuePair<string, string>> LoadRenames(string statusPath)
        {
            var path = RenamesPath(statusPath);
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(File.ReadAllText(path)) ?? []
                : [];
        }

        public void Save(string path)
        {
            var text =
                "source=" + SourcePath + "\n" +
                "jobId=" + JobId.ToString("D") + "\n" +
                "status=" + StatusPath + "\n" +
                "cancel=" + CancelPath + "\n";
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
        }

        public static XmlImportJob Load(string path)
        {
            var job = new XmlImportJob();
            foreach (var line in File.ReadLines(path))
            {
                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                var key = line[..split];
                var value = line[(split + 1)..];
                switch (key)
                {
                    case "source":
                        job.SourcePath = value;
                        break;
                    case "jobId":
                        if (!Guid.TryParse(value, out var jobId))
                        {
                            throw new InvalidDataException("The XML import job id is invalid.");
                        }

                        job.JobId = jobId;
                        break;
                    case "status":
                        job.StatusPath = value;
                        break;
                    case "cancel":
                        job.CancelPath = value;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(job.SourcePath) ||
                job.JobId == Guid.Empty ||
                string.IsNullOrWhiteSpace(job.StatusPath) ||
                string.IsNullOrWhiteSpace(job.CancelPath))
            {
                throw new InvalidDataException("The XML import job file is incomplete.");
            }

            return job;
        }
    }

    public sealed class XmlImportSnapshot
    {
        public string Phase { get; set; } = "parse";
        public long BytesRead { get; set; }
        public long FileBytes { get; set; }
        public int VariablesRead { get; set; }
        public int ItemsAccepted { get; set; }
        public bool WaitingForWriter { get; set; }
        public bool Slowed { get; set; }
        public long WorkTicks { get; set; }
        public long PhaseDone { get; set; }
        public long PhaseTotal { get; set; }
        public DateTime? AliveUtc { get; set; }
        public string Error { get; set; } = "";

        public static XmlImportSnapshot Read(string path)
        {
            var snapshot = new XmlImportSnapshot();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return snapshot;
            }

            foreach (var line in File.ReadLines(path))
            {
                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                var key = line[..split];
                var value = line[(split + 1)..];
                switch (key)
                {
                    case "phase":
                        snapshot.Phase = value;
                        break;
                    case "bytesRead":
                        if (long.TryParse(value, out var bytes))
                        {
                            snapshot.BytesRead = bytes;
                        }
                        break;
                    case "fileBytes":
                        if (long.TryParse(value, out var fileBytes))
                        {
                            snapshot.FileBytes = fileBytes;
                        }
                        break;
                    case "variablesRead":
                        if (int.TryParse(value, out var variables))
                        {
                            snapshot.VariablesRead = variables;
                        }
                        break;
                    case "itemsAccepted":
                        if (int.TryParse(value, out var items))
                        {
                            snapshot.ItemsAccepted = items;
                        }
                        break;
                    case "waiting":
                        snapshot.WaitingForWriter = value == "1" ||
                            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "slowed":
                        snapshot.Slowed = value == "1";
                        break;
                    case "work":
                        if (long.TryParse(value, out var work))
                        {
                            snapshot.WorkTicks = work;
                        }
                        break;
                    case "done":
                        if (long.TryParse(value, out var done))
                        {
                            snapshot.PhaseDone = done;
                        }
                        break;
                    case "total":
                        if (long.TryParse(value, out var total))
                        {
                            snapshot.PhaseTotal = total;
                        }
                        break;
                    case "aliveUtc":
                        if (DateTime.TryParse(
                                value,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind,
                                out var alive))
                        {
                            snapshot.AliveUtc = alive.ToUniversalTime();
                        }
                        break;
                    case "error":
                        snapshot.Error = value;
                        break;
                }
            }

            return snapshot;
        }
    }
}
