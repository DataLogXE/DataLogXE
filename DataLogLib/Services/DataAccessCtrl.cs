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

using DataLogLib.Models;
using DataLogLib.Services.DataAccess;
using Microsoft.Extensions.Configuration;

namespace DataLogLib.Services
{
    public class DataAccessCtrl : IDataAccess, ITagUidLookup
    {
        private const int MaxCachedSources = 2;

        #region public properties
        public static IConfiguration Configuration { get; private set; }
        #endregion

        #region private fields
        private readonly PostgresConnection _postgres = new();
        private readonly object _listCacheLock = new();
        private readonly Dictionary<int, DataTagListIndex> _listCache = new();
        private readonly List<int> _listCacheLru = new();
        private readonly Dictionary<int, ListIndexBuild> _builds = new();
        private readonly Dictionary<int, int> _listGenerations = new();
        #endregion

        #region public methods
        public void Init(IConfiguration configuration)
        {
            Configuration = configuration;
            _postgres.Init(configuration);
        }

        public void Close()
        {
            DropAllListCaches();
            _postgres.Close();
        }

        public List<DataSourceModel> DataSourceGetAll() => _postgres.DataSourceGetAll();

        public int AppUserCount() => _postgres.AppUserCount();
        public List<AppUserModel> AppUserGetAll() => _postgres.AppUserGetAll();
        public AppUserModel AppUserGetById(int id) => _postgres.AppUserGetById(id);
        public AppUserModel AppUserGetByName(string name) => _postgres.AppUserGetByName(name);
        public bool AppUserCreate(AppUserModel user, bool onlyIfNone = false) => _postgres.AppUserCreate(user, onlyIfNone);
        public void AppUserUpdate(AppUserModel user) => _postgres.AppUserUpdate(user);
        public void AppUserDelete(int id) => _postgres.AppUserDelete(id);
        public void AppUserTouchLogin(int id) => _postgres.AppUserTouchLogin(id);

        public DataSourceModel DataSourceGetById(int id) => _postgres.DataSourceGetById(id);

        public void DataSourceCreate(DataSourceModel dataSourceModel) => _postgres.DataSourceCreate(dataSourceModel);

        public void DataSourceUpdate(DataSourceModel dataSourceModel)
        {
            var before = _postgres.DataSourceGetById(dataSourceModel.Id);
            _postgres.DataSourceUpdate(dataSourceModel);
            // The tag list holds the comments in the language of the source.
            if (!string.Equals(before?.S7Locale ?? "", dataSourceModel.S7Locale ?? "", StringComparison.OrdinalIgnoreCase))
            {
                DropListCache(dataSourceModel.Id);
            }
        }

        public void DataSourceMinCycleSet(int id, int minCycle) => _postgres.DataSourceMinCycleSet(id, minCycle);

        public void DataSourceDelete(int id)
        {
            _postgres.DataSourceDelete(id);
            DropListCache(id);
        }

        /// <summary>Data tags deleted per statement when a data source is removed (a few seconds on a Pi).</summary>
        public const int DataSourceDeleteChunkRows = 5000;

        public bool DataSourceMarkDeleting(int id)
        {
            var marked = _postgres.DataSourceMarkDeleting(id);
            DropListCache(id);
            return marked;
        }

        public List<int> DataSourcesDeleting() => _postgres.DataSourcesDeleting();

        public int DataSourceDeleteTagChunk(int id, int limit) => _postgres.DataSourceDeleteTagChunk(id, limit);

        public int DataTagsCountActive() => _postgres.DataTagsCountActive();

        public void DataSourceDeleteRemoved(int id)
        {
            _postgres.DataSourceDeleteRemoved(id);
            DropListCache(id);
        }

        public DataTagModel DataTagGetById(int id) => _postgres.DataTagGetById(id);

        public IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds) => _postgres.DataTagUidsGet(dataTagIds);

        public List<DataTagModel> DataTagsGetByDataSourceId(int dataSourceId, int? deleted = null, int? active = null) => _postgres.DataTagsGetByDataSourceId(dataSourceId, deleted, active);

        public DataTagListPageModel DataTagsGetPage(int dataSourceId, int? active, string q, int offset, int limit, IReadOnlyList<DataTagSortLevel> sorts = null, TagLiveSort live = default)
        {
            return ReadTagListPage(dataSourceId, active, q, offset, limit, sorts, live);
        }

        private DataTagListPageModel ReadTagListPage(int dataSourceId, int? active, string q, int offset, int limit, IReadOnlyList<DataTagSortLevel> sorts, TagLiveSort live = default)
        {
            var filter = DataTagNameFilter.Parse(q);
            var includeInactive = PageNeedsInactive(active);
            lock (_listCacheLock)
            {
                if (_listCache.TryGetValue(dataSourceId, out var cached)
                    && (cached.IncludesInactive || !includeInactive))
                {
                    TouchListCache(dataSourceId);
                    return cached.GetPage(active, filter, offset, limit, sorts, live);
                }

                if (_builds.TryGetValue(dataSourceId, out var build))
                {
                    if (build.Error != null)
                    {
                        _builds.Remove(dataSourceId);
                        throw build.Error;
                    }

                    if (build.IncludeInactive || !includeInactive)
                    {
                        return PreparingPage(build);
                    }

                    // An active-only build cannot serve every tag. Drop it and load the full list.
                    _builds.Remove(dataSourceId);
                    BumpListGeneration(dataSourceId);
                }

                if (_listCache.Remove(dataSourceId))
                {
                    _listCacheLru.Remove(dataSourceId);
                    BumpListGeneration(dataSourceId);
                }

                StartListIndexBuild(dataSourceId, includeInactive);
                return PreparingPage(_builds[dataSourceId]);
            }
        }

        public List<DataTagListItemModel> DataTagsGetByIds(int dataSourceId, IReadOnlyList<int> ids) => _postgres.DataTagsGetByIds(dataSourceId, ids);
        public List<DataTagModel> DataTagDetailsGetByIds(int dataSourceId, IReadOnlyList<int> ids) => _postgres.DataTagDetailsGetByIds(dataSourceId, ids);
        public IReadOnlyDictionary<int, string> DataTagDescriptionsGet(int dataSourceId, IReadOnlyList<int> ids) => _postgres.DataTagDescriptionsGet(dataSourceId, ids);

        public List<DataTagDetailModel> DataTagDetailsGet(int? dataTagId = null, int? dataSourceId = null, int? active = null) => _postgres.DataTagDetailsGet(dataTagId, dataSourceId, active);

        public DataTagModel DataTagCreate(DataTagModel dataTagModel)
        {
            var created = _postgres.DataTagCreate(dataTagModel);
            if (dataTagModel != null)
            {
                DropListCache(dataTagModel.DataSourceId);
            }
            return created;
        }

        /// <summary>
        /// Inserts the tags and writes the generated Id onto each model. After a successful
        /// call every item has Id greater than 0.
        /// </summary>
        public void DataTagsCreate(List<DataTagModel> dataTagModels)
        {
            _postgres.DataTagsCreate(dataTagModels);
            DropListCaches(dataTagModels);
        }

        public DataTagModel DataTagGetOrCreateByAddress(DataTagModel dataTagModel)
        {
            var result = _postgres.DataTagGetOrCreateByAddress(dataTagModel);
            if (dataTagModel != null)
            {
                DropListCache(dataTagModel.DataSourceId);
            }
            return result;
        }

        public void DataTagsUpdate(List<DataTagModel> dataTagModels)
        {
            _postgres.DataTagsUpdate(dataTagModels);
            DropListCaches(dataTagModels);
        }

        public void DataTagsUpdateDeleted(List<int> ids, int deleted)
        {
            _postgres.DataTagsUpdateDeleted(ids, deleted);
            DropAllListCaches();
        }

        public void DataTagsUpdateActive(List<int> ids, int active)
        {
            _postgres.DataTagsUpdateActive(ids, active);
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            var idSet = new HashSet<int>(ids);
            lock (_listCacheLock)
            {
                foreach (var index in _listCache.Values)
                {
                    index.PatchActive(idSet, active);
                }
            }
        }

        public void DataTagsUpdateCycle(List<int> ids, int cycle)
        {
            _postgres.DataTagsUpdateCycle(ids, cycle);
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            var idSet = new HashSet<int>(ids);
            lock (_listCacheLock)
            {
                foreach (var index in _listCache.Values)
                {
                    index.PatchCycle(idSet, cycle);
                }
            }
        }

        public void DataTagsUpdateActiveAndCycle(List<int> ids, int? active, int? cycle)
        {
            _postgres.DataTagsUpdateActiveAndCycle(ids, active, cycle);
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            var idSet = new HashSet<int>(ids);
            lock (_listCacheLock)
            {
                foreach (var index in _listCache.Values)
                {
                    if (active.HasValue)
                    {
                        index.PatchActive(idSet, active.Value);
                    }
                    if (cycle.HasValue)
                    {
                        index.PatchCycle(idSet, cycle.Value);
                    }
                }
            }
        }

        public DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            IReadOnlyList<DataTagBrowseImportItem> items,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = _postgres.DataTagsSyncFromBrowse(dataSourceId, cycle, items, progress, cancellationToken);
            DropListCache(dataSourceId);
            return result;
        }

        public DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            Action<Action<IReadOnlyList<DataTagBrowseImportItem>>> fill,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = _postgres.DataTagsSyncFromBrowse(dataSourceId, cycle, fill, progress, cancellationToken);
            DropListCache(dataSourceId);
            return result;
        }

        public bool BrowseCheckpointExists(int dataSourceId) => _postgres.BrowseCheckpointExists(dataSourceId);

        public void BrowseCheckpointCreate(int dataSourceId) => _postgres.BrowseCheckpointCreate(dataSourceId);

        public void BrowseCheckpointTouch(int dataSourceId) => _postgres.BrowseCheckpointTouch(dataSourceId);

        public void BrowseCheckpointDelete(int dataSourceId) => _postgres.BrowseCheckpointDelete(dataSourceId);

        public int BrowseCheckpointPurgeStale() => _postgres.BrowseCheckpointPurgeStale();

        public void BrowseCheckpointSetInflight(int dataSourceId, string phase, IReadOnlyList<string> nodeKeys) =>
            _postgres.BrowseCheckpointSetInflight(dataSourceId, phase, nodeKeys);

        public void BrowseCheckpointCommitBrowse(
            int dataSourceId,
            IReadOnlyList<string> finishedParents,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<BrowseCheckpointNode> newVariables) =>
            _postgres.BrowseCheckpointCommitBrowse(dataSourceId, finishedParents, newObjects, newVariables);

        public void BrowseCheckpointCommitClassify(
            int dataSourceId,
            IReadOnlyList<string> finishedVariables,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<DataTagBrowseImportItem> scalars) =>
            _postgres.BrowseCheckpointCommitClassify(dataSourceId, finishedVariables, newObjects, scalars);

        public int DataTagsRenameAddresses(int dataSourceId, IReadOnlyList<KeyValuePair<string, string>> renames) =>
            _postgres.DataTagsRenameAddresses(dataSourceId, renames);

        public void BrowseCheckpointSetPhase(int dataSourceId, string phase) =>
            _postgres.BrowseCheckpointSetPhase(dataSourceId, phase);

        public void BrowseCheckpointSetResumeOnConnect(int dataSourceId, bool resumeOnConnect) =>
            _postgres.BrowseCheckpointSetResumeOnConnect(dataSourceId, resumeOnConnect);

        public bool BrowseCheckpointResumeOnConnect(int dataSourceId) =>
            _postgres.BrowseCheckpointResumeOnConnect(dataSourceId);

        public BrowseCheckpointState BrowseCheckpointLoad(int dataSourceId) =>
            _postgres.BrowseCheckpointLoad(dataSourceId);

        public DataTagBrowseSyncResult DataTagsSyncFromBrowseCheckpoint(
            int dataSourceId,
            int cycle,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken,
            bool allowDelete = true)
        {
            var result = _postgres.DataTagsSyncFromBrowseCheckpoint(dataSourceId, cycle, progress, cancellationToken, allowDelete);
            DropListCache(dataSourceId);
            return result;
        }

        public DataTagBrowseSyncResult DataTagsSyncFromXmlStage(
            int dataSourceId,
            int cycle,
            Guid jobId,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = _postgres.DataTagsSyncFromXmlStage(
                dataSourceId,
                cycle,
                jobId,
                progress,
                cancellationToken);
            DropListCache(dataSourceId);
            return result;
        }

        public void XmlImportStageDelete(Guid jobId)
        {
            _postgres.XmlImportStageDelete(jobId);
        }

        public void XmlImportStageClear()
        {
            _postgres.XmlImportStageClear();
        }
        #endregion

        #region parameter
        public string ParameterGet(string key) => _postgres.ParameterGet(key);
        public void ParameterSet(string key, string value) => _postgres.ParameterSet(key, value);
        #endregion

        #region alarm class
        public List<AlarmClassModel> AlarmClassGetAll() => _postgres.AlarmClassGetAll();
        public void AlarmClassInsert(string name, string symbol) => _postgres.AlarmClassInsert(name, symbol);
        #endregion

        #region chart
        public List<ChartModel> ChartGetAll() => _postgres.ChartGetAll();
        public ChartModel ChartGetById(int id) => _postgres.ChartGetById(id);
        public ChartModel ChartCreate(ChartModel chart) => _postgres.ChartCreate(chart);
        public void ChartUpdate(ChartModel chart) => _postgres.ChartUpdate(chart);
        public void ChartDelete(int chartId) => _postgres.ChartDelete(chartId);
        public List<ChartPlotModel> ChartPlotsGetByChartId(int chartId) => _postgres.ChartPlotsGetByChartId(chartId);
        public ChartPlotModel ChartPlotCreate(ChartPlotModel chartPlot) => _postgres.ChartPlotCreate(chartPlot);
        public void ChartPlotUpdate(ChartPlotModel chartPlot) => _postgres.ChartPlotUpdate(chartPlot);
        public void ChartPlotDelete(int chartPlotId) => _postgres.ChartPlotDelete(chartPlotId);
        public List<ChartTreeItemModel> ChartTreeGet() => _postgres.ChartTreeGet();
        public List<ChartPlotDetailModel> ChartPlotDetailsGet(int chartId) => _postgres.ChartPlotDetailsGet(chartId);
        public List<DataTagModel> DataTagsGetByIdList(IReadOnlyCollection<int> ids) => _postgres.DataTagsGetByIdList(ids);
        public ChartModel ChartCreate(ChartModel chart, IReadOnlyList<int> dataTagIds) => _postgres.ChartCreate(chart, dataTagIds);
        public void ChartSave(ChartModel chart, IReadOnlyList<int> dataTagIds) => _postgres.ChartSave(chart, dataTagIds);
        public void ChartChildrenOrderSet(int parentId, IReadOnlyList<int> orderedIds, int? movedId) => _postgres.ChartChildrenOrderSet(parentId, orderedIds, movedId);
        public void ChartPlotsSet(int chartId, IReadOnlyList<int> dataTagIds) => _postgres.ChartPlotsSet(chartId, dataTagIds);
        #endregion

        private static bool PageNeedsInactive(int? active)
        {
            return !active.HasValue || active.Value == 0;
        }

        private void StartListIndexBuild(int dataSourceId, bool includeInactive)
        {
            var generation = CurrentListGeneration(dataSourceId);
            var build = new ListIndexBuild
            {
                IncludeInactive = includeInactive
            };
            _builds[dataSourceId] = build;

            // The build serves every request of the list, not only the one that started it: it runs without the
            // scope of that request, whose end cancelled its statements and handed "57014: canceling statement due to
            // user request" to the next requests (tag list, chart editor), and reads from the UI pool without a token.
            using var detached = ExecutionContext.SuppressFlow();
            build.Task = Task.Run(() =>
            {
                using var scope = DatabaseLimits.Use(PostgresWork.Ui);
                try
                {
                    var loaded = _postgres.DataTagsLoadListIndex(dataSourceId, includeInactive, (loadedCount, total, sorting) =>
                    {
                        Volatile.Write(ref build.Loaded, loadedCount);
                        Volatile.Write(ref build.Total, total);
                        Volatile.Write(ref build.Sorting, sorting ? 1 : 0);
                    });

                    lock (_listCacheLock)
                    {
                        if (_builds.TryGetValue(dataSourceId, out var current) && ReferenceEquals(current, build))
                        {
                            _builds.Remove(dataSourceId);
                        }

                        if (!IsListGenerationCurrent(dataSourceId, generation))
                        {
                            return;
                        }

                        while (_listCache.Count >= MaxCachedSources && _listCacheLru.Count > 0)
                        {
                            var evictId = _listCacheLru[_listCacheLru.Count - 1];
                            _listCacheLru.RemoveAt(_listCacheLru.Count - 1);
                            _listCache.Remove(evictId);
                        }

                        _listCache[dataSourceId] = loaded;
                        TouchListCache(dataSourceId);
                    }
                }
                catch (Exception ex)
                {
                    lock (_listCacheLock)
                    {
                        if (_builds.TryGetValue(dataSourceId, out var current) && ReferenceEquals(current, build))
                        {
                            build.Error = ex;
                        }
                    }
                }
            });
        }

        private static DataTagListPageModel PreparingPage(ListIndexBuild build)
        {
            return new DataTagListPageModel
            {
                Preparing = true,
                PreparePercent = ComputePreparePercent(build),
                Total = 0,
                Items = []
            };
        }

        private static int ComputePreparePercent(ListIndexBuild build)
        {
            if (Volatile.Read(ref build.Sorting) != 0)
            {
                return 95;
            }

            var total = Volatile.Read(ref build.Total);
            if (total <= 0)
            {
                return 0;
            }

            var loaded = Volatile.Read(ref build.Loaded);
            if (loaded < 0)
            {
                loaded = 0;
            }

            if (loaded > total)
            {
                loaded = total;
            }

            return (int)((long)loaded * 90 / total);
        }

        private int CurrentListGeneration(int dataSourceId)
        {
            return _listGenerations.TryGetValue(dataSourceId, out var generation) ? generation : 0;
        }

        private void BumpListGeneration(int dataSourceId)
        {
            _listGenerations[dataSourceId] = CurrentListGeneration(dataSourceId) + 1;
        }

        private bool IsListGenerationCurrent(int dataSourceId, int generation)
        {
            return CurrentListGeneration(dataSourceId) == generation;
        }

        private void TouchListCache(int dataSourceId)
        {
            _listCacheLru.Remove(dataSourceId);
            _listCacheLru.Insert(0, dataSourceId);
        }

        private void DropListCache(int dataSourceId)
        {
            lock (_listCacheLock)
            {
                _listCache.Remove(dataSourceId);
                _listCacheLru.Remove(dataSourceId);
                _builds.Remove(dataSourceId);
                BumpListGeneration(dataSourceId);
            }
        }

        private void DropListCaches(List<DataTagModel> dataTagModels)
        {
            if (dataTagModels == null || dataTagModels.Count == 0)
            {
                return;
            }

            lock (_listCacheLock)
            {
                foreach (var tag in dataTagModels)
                {
                    if (tag == null)
                    {
                        continue;
                    }

                    _listCache.Remove(tag.DataSourceId);
                    _listCacheLru.Remove(tag.DataSourceId);
                    _builds.Remove(tag.DataSourceId);
                    BumpListGeneration(tag.DataSourceId);
                }
            }
        }

        private void DropAllListCaches()
        {
            lock (_listCacheLock)
            {
                foreach (var dataSourceId in _listCache.Keys.ToList())
                {
                    BumpListGeneration(dataSourceId);
                }

                foreach (var dataSourceId in _builds.Keys.ToList())
                {
                    BumpListGeneration(dataSourceId);
                }

                _listCache.Clear();
                _listCacheLru.Clear();
                _builds.Clear();
            }
        }

        private sealed class ListIndexBuild
        {
            public Task Task;
            public bool IncludeInactive;
            public int Loaded;
            public int Total;
            public int Sorting;
            public Exception Error;
        }
    }
}
