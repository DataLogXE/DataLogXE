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
using DataLogApi.Hosting;
using DataLogLib.Services;
using DataLogLib.Services.EventLog;
using DataLogService;
using Serilog;

namespace DataLogApi.Services
{
    public class ArchiveSettingsAppService
    {
        private readonly ArchiveStatsSnapshot _stats;
        private readonly ServiceCtrl _service;
        private readonly EventLogCtrl _eventLog;

        public ArchiveSettingsAppService(
            ArchiveStatsSnapshot stats,
            ServiceCtrl service,
            EventLogCtrl eventLog)
        {
            _stats = stats;
            _service = service;
            _eventLog = eventLog;
        }

        public ArchiveSettingsDto Get()
        {
            var stats = _stats.Get();
            var dto = new ArchiveSettingsDto
            {
                ValuesPerSec = stats.ValuesPerSec,
                ValueCountSumMio = stats.ValueCountSumMio,
                DatabaseDiskSizeGb = stats.DatabaseDiskSizeGb,
                StatsUpdatedAt = stats.StatsUpdatedAt
            };

            if (_service.ArchiveAccess is ArchiveAccessCtrl archive)
            {
                try
                {
                    var metadata = archive.ReadArchiveMetadata();
                    dto.DatabaseVersion = metadata.Version;
                    dto.RetentionSeconds = metadata.RetentionSeconds;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to read the archive settings");
                }
            }

            return dto;
        }

        /// <summary>
        /// The one retention of the archive and the process events.
        /// </summary>
        public ArchiveSettingsDto SetRetention(long everySeconds)
        {
            if (_service.ArchiveAccess is not ArchiveAccessCtrl archive)
            {
                throw new InvalidOperationException("Archive access is unavailable.");
            }

            archive.SetRetentionSeconds(everySeconds);
            if (_eventLog.SetRetentionSeconds(everySeconds) != everySeconds)
            {
                throw new InvalidOperationException("TimescaleDB did not confirm the new retention of the process events.");
            }

            var dto = Get();
            if (dto.RetentionSeconds != everySeconds)
            {
                throw new InvalidOperationException("TimescaleDB did not confirm the new retention.");
            }

            return dto;
        }
    }
}
