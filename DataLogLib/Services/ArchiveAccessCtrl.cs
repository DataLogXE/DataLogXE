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

using DataLogLib.Common;
using DataLogLib.Models;
using DataLogLib.Services.ArchiveAccess;
using DataLogLib.Services.EventLog;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Globalization;

namespace DataLogLib.Services
{
    /// <summary>
    /// Controller class for data archive management. The data archive writes the raw data into
    /// TimescaleDB hypertables. In addition, the data is reduced to the M4 compression levels,
    /// which are also stored in the database.
    /// Archive statistics are collected and made available via events.
    /// </summary>
    public class ArchiveAccessCtrl : IArchiveAccess, ITagActualLookup
    {
        #region public properties
        public static IConfiguration Configuration { get; private set; }
        #endregion

        #region public events
        public event EventHandler<(DateTime, long)> OnValueCountPerSec
        {
            add { _statsWorker.OnValueCountPerSec += value; }
            remove { _statsWorker.OnValueCountPerSec -= value; }
        }

        public event EventHandler<(DateTime, double)> OnValueCountSumMio
        {
            add { _statsWorker.OnValueCountSumMio += value; }
            remove { _statsWorker.OnValueCountSumMio -= value; }
        }

        public event EventHandler<(DateTime, double)> OnDatabaseDiskSizeGb
        {
            add { _statsWorker.OnDatabaseDiskSizeGb += value; }
            remove { _statsWorker.OnDatabaseDiskSizeGb -= value; }
        }

        public event EventHandler<long> OnArchiveCompTimeMs
        {
            add { _compressionWorker.OnArchiveCompTimeMs += value; }
            remove { _compressionWorker.OnArchiveCompTimeMs -= value; }
        }

        public event EventHandler<int> OnArchiveCompCountRaw
        {
            add { _compressionWorker.OnArchiveCompCountRaw += value; }
            remove { _compressionWorker.OnArchiveCompCountRaw -= value; }
        }

        public event EventHandler<int> OnArchiveCompCount1Sec
        {
            add { _compressionWorker.OnArchiveCompCount1Sec += value; }
            remove { _compressionWorker.OnArchiveCompCount1Sec -= value; }
        }
        #endregion

        #region private fields
        private readonly TimescaleArchive _archive = new();
        private readonly ArchiveInsertWorker _insertWorker = new();
        private readonly ArchiveStatsWorker _statsWorker = new();
        private readonly CompressionWorker _compressionWorker = new();
        private CompressionRepairWorker _repairWorker;
        private Timer _loadLevelTimer;
        private static readonly TimeSpan LoadLevelRecheck = TimeSpan.FromMinutes(1);
        #endregion

        #region public methods
        public void Init(IConfiguration configuration)
        {
            Configuration = configuration;
            _archive.Init(configuration);
            _insertWorker.Init(_archive, OpenJournal());
            LoadGuard.Shared.Start(() => _insertWorker.Backlog);
            // The compression and retention policies of TimescaleDB pause with the background work of the app (those
            // paused by the last run are scheduled again in SetUidLookup, once the schema is there). The level when the task runs, so changes in quick succession end in the newest one
            LoadGuard.Shared.LevelChanged += _ => Task.Run(ApplyLoadLevel);
            // Again every minute while the level lasts: a policy that ran when the level was entered is paused once it is
            // done, and a compression is cancelled once the recording is at risk
            _loadLevelTimer = new Timer(_ =>
            {
                if (LoadGuard.Shared.Level != LoadLevel.Normal)
                {
                    ApplyLoadLevel();
                }
            }, null, LoadLevelRecheck, LoadLevelRecheck);
            _statsWorker.Init(_archive, _insertWorker);
            // Compressed values that cannot be written are rebuilt from the raw values, as are the
            // levels missing before this startup and incomplete hours of the last days
            _repairWorker = new CompressionRepairWorker(_archive, GetRepairDays(configuration), maxLoad: GetRepairMaxLoad(configuration));
            _compressionWorker.Init(_archive, _insertWorker, _repairWorker.Enqueue);
            _repairWorker.Start(DateTime.Now);
            new ArchiveRetentionJob(_archive).Start();
            new ArchiveCountWorker(_archive, GetCountBackfillDays(configuration), new LoadPacer(LoadPacer.MaxLoadFromEnvironment(), work: "Archive value counters")).Start();
        }

        private void ApplyLoadLevel() => _archive.ApplyLoadLevel(LoadGuard.Shared.Level, LoadGuard.Shared.RecordingAtRisk);

        /// <summary>
        /// The journal on disk for the values the database cannot take in time; without a writable data folder the
        /// values are dropped on an overflow, as before the journal.
        /// </summary>
        private static ArchiveJournal OpenJournal()
        {
            var folder = ArchiveJournal.DefaultFolder;
            try
            {
                return new ArchiveJournal(folder, DataAccess.DatabaseLimits.Profile.JournalMaxBytes);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Archive journal in {Folder} is not available; an insert buffer overflow drops values", folder);
                return null;
            }
        }

        /// <summary>
        /// Sets the lookup that resolves the data tag uid, which identifies the tag in the archive.
        /// Call before the first value is inserted or queried.
        /// </summary>
        public void SetUidLookup(ITagUidLookup lookup)
        {
            _archive.SetUidLookup(lookup);
            _insertWorker.StartReplay();
            Task.Run(() =>
            {
                try
                {
                    _archive.ResumePausedJobs();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "The TimescaleDB policies paused by the last run could not be scheduled again");
                }
            });
        }

        /// <summary>
        /// Writes the buffered raw values and the open compression windows. Call on shutdown after
        /// the data sources were disposed, so the last second of values and the open windows of the
        /// compression levels are not lost.
        /// </summary>
        public void Flush()
        {
            _insertWorker.Flush();
            try
            {
                // After the raw flush, which hands its last values to the compression
                _compressionWorker.Flush();
            }
            catch (Exception ex)
            {
                // The raw values must still reach the archive
                Log.Error(ex, "Compression flush on shutdown failed");
            }
        }

        /// <summary>
        /// ARCHIVE_REPAIR_DAYS: days before the startup in which incomplete hours of the compression
        /// levels are rebuilt when the archive was not checked before (0 = no check of older hours).
        /// </summary>
        private static int GetRepairDays(IConfiguration configuration)
        {
            var text = configuration["ARCHIVE_REPAIR_DAYS"];
            if (string.IsNullOrWhiteSpace(text))
            {
                return CompressionRepairWorker.DefaultCheckDays;
            }
            if (!int.TryParse(text, out var days) || days < 0)
            {
                Log.Error("Invalid ARCHIVE_REPAIR_DAYS {Value}, using {Default}", text, CompressionRepairWorker.DefaultCheckDays);
                return CompressionRepairWorker.DefaultCheckDays;
            }
            return days;
        }

        /// <summary>
        /// ARCHIVE_COUNT_BACKFILL_DAYS: days before now back to which the raw values are counted into the raw value
        /// counters in the background (0 = only the values from the upgrade to schema 12 on).
        /// </summary>
        private static int GetCountBackfillDays(IConfiguration configuration)
        {
            var text = configuration["ARCHIVE_COUNT_BACKFILL_DAYS"];
            if (string.IsNullOrWhiteSpace(text))
            {
                return ArchiveCountWorker.DefaultBackfillDays;
            }
            if (!int.TryParse(text, out var days) || days < 0)
            {
                Log.Error("Invalid ARCHIVE_COUNT_BACKFILL_DAYS {Value}, using {Default}", text, ArchiveCountWorker.DefaultBackfillDays);
                return ArchiveCountWorker.DefaultBackfillDays;
            }
            return days;
        }

        /// <summary>
        /// ARCHIVE_REPAIR_MAX_LOAD: load average per core above which the rebuild of the compression
        /// levels pauses (0 = no limit).
        /// </summary>
        private static double GetRepairMaxLoad(IConfiguration configuration)
        {
            var text = configuration["ARCHIVE_REPAIR_MAX_LOAD"];
            if (string.IsNullOrWhiteSpace(text))
            {
                return CompressionRepairWorker.DefaultMaxLoad;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) || load < 0)
            {
                Log.Error("Invalid ARCHIVE_REPAIR_MAX_LOAD {Value}, using {Default}", text, CompressionRepairWorker.DefaultMaxLoad);
                return CompressionRepairWorker.DefaultMaxLoad;
            }
            return load;
        }

        public void InsertValue(ArchiveValue value)
        {
            InsertValues([value]);
        }

        public void InsertValues(List<ArchiveValue> values)
        {
            _insertWorker.InsertValues(values);
        }

        /// <summary>
        /// Returns the latest DataTag archive values.
        /// </summary>
        /// <param name="ids">A list of DataTag ids</param>
        /// <returns></returns>
        public List<ArchiveValue> GetActualValues(List<int> ids)
        {
            return _insertWorker.GetActualValues(ids);
        }

        public bool TryGetActual(int id, out double value, out int state, out long timeUtcTicks)
        {
            return _insertWorker.TryGetActual(id, out value, out state, out timeUtcTicks);
        }

        public DateTime? GetArchiveStartTime() => _archive.GetArchiveStartTime();

        public DateTime? GetArchiveEndTime(int dataTagId) => _archive.GetArchiveEndTime(dataTagId);

        public long? GetRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
            => _archive.GetRawValueCount(dataTagId, startTime, endTime, cancellationToken);

        public long? EstimateRawValueCount(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
            => _archive.EstimateRawValueCount(dataTagId, startTime, endTime, cancellationToken);

        public RawStatistics GetRawStatistics(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
            => _archive.GetRawStatistics(dataTagId, startTime, endTime, lookback, hold, cancellationToken);

        public BoolDurations GetBoolDurations(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
            => _archive.GetBoolDurations(dataTagId, startTime, endTime, lookback, hold, cancellationToken);

        public double? GetInvalidSeconds(int dataTagId, DateTime startTime, DateTime endTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
            => _archive.GetInvalidSeconds(dataTagId, startTime, endTime, lookback, hold, cancellationToken);

        public List<PlotPoint> GetPlotPoints(int dataTagId, CompressionType compression, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
            => _archive.GetPlotPoints(dataTagId, compression, startTime, endTime, cancellationToken);

        public List<PlotPoint> GetRawM4PlotPoints(int dataTagId, DateTime startTime, DateTime endTime, double origin, double bucketSeconds, CancellationToken cancellationToken = default)
            => _archive.GetRawM4PlotPoints(dataTagId, startTime, endTime, origin, bucketSeconds, cancellationToken);

        public List<PlotPoint> GetRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, int limit, CancellationToken cancellationToken = default, Action<long> onRead = null)
            => _archive.GetRawPlotPoints(dataTagId, startTime, endTime, limit, cancellationToken, onRead);

        public IAsyncEnumerable<PlotPoint> StreamRawPlotPoints(int dataTagId, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
            => _archive.StreamRawPlotPoints(dataTagId, startTime, endTime, cancellationToken);

        public PlotPoint GetPlotPointAt(int dataTagId, DateTime dateTime)
            => _archive.GetPlotPointAt(dataTagId, dateTime);

        public PlotPoint GetRawValueAt(int dataTagId, DateTime dateTime, TimeSpan lookback, TimeSpan hold, CancellationToken cancellationToken = default)
            => _archive.GetRawValueAt(dataTagId, dateTime, lookback, hold, cancellationToken);

        public (string Version, long? RetentionSeconds) ReadArchiveMetadata()
            => _archive.ReadMetadata();

        public void SetRetentionSeconds(long everySeconds)
            => _archive.SetRetentionSeconds(everySeconds);
        #endregion
    }
}
