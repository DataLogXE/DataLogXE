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

namespace DataLogLib.Services
{
    public interface IDataAccess
    {
        #region data source
        List<DataSourceModel> DataSourceGetAll();

        int AppUserCount();
        List<AppUserModel> AppUserGetAll();
        AppUserModel AppUserGetById(int id);
        AppUserModel AppUserGetByName(string name);
        bool AppUserCreate(AppUserModel user, bool onlyIfNone = false);
        void AppUserUpdate(AppUserModel user);
        void AppUserDelete(int id);
        void AppUserTouchLogin(int id);
        DataSourceModel DataSourceGetById(int id);
        void DataSourceCreate(DataSourceModel dataSourceModel);
        void DataSourceUpdate(DataSourceModel dataSourceModel);
        void DataSourceMinCycleSet(int id, int minCycle);
        void DataSourceDelete(int id);
        bool DataSourceMarkDeleting(int id);
        List<int> DataSourcesDeleting();
        int DataSourceDeleteTagChunk(int id, int limit);
        int DataTagsCountActive();
        void DataSourceDeleteRemoved(int id);
        #endregion

        #region data tag
        DataTagModel DataTagGetById(int dataTagId);
        List<DataTagModel> DataTagsGetByDataSourceId(int dataSourceId, int? deleted = null, int? active = null);
        DataTagListPageModel DataTagsGetPage(int dataSourceId, int? active, string q, int offset, int limit, IReadOnlyList<DataTagSortLevel> sorts = null, TagLiveSort live = default);
        List<DataTagListItemModel> DataTagsGetByIds(int dataSourceId, IReadOnlyList<int> ids);
        /// <summary>
        /// All columns of the tags of the source that are not deleted, ordered by Id.
        /// </summary>
        List<DataTagModel> DataTagDetailsGetByIds(int dataSourceId, IReadOnlyList<int> ids);
        /// <summary>The stored descriptions (JSON culture -> text) of the given tags, tags without one are left out.</summary>
        IReadOnlyDictionary<int, string> DataTagDescriptionsGet(int dataSourceId, IReadOnlyList<int> ids);
        List<DataTagDetailModel> DataTagDetailsGet(int? dataTagId = null, int? dataSourceId = null, int? active = null);
        DataTagModel DataTagCreate(DataTagModel dataTagModel);
        /// <summary>
        /// Inserts the tags and writes the generated Id onto each model. After a successful
        /// call every item has Id greater than 0.
        /// </summary>
        void DataTagsCreate(List<DataTagModel> dataTagModels);
        DataTagModel DataTagGetOrCreateByAddress(DataTagModel dataTagModel);
        void DataTagsUpdate(List<DataTagModel> dataTagModels);
        void DataTagsUpdateDeleted(List<int> ids, int deleted);
        void DataTagsUpdateActive(List<int> ids, int active);
        void DataTagsUpdateCycle(List<int> ids, int cycle);
        /// <summary>
        /// Sets active and cycle of the tags in one statement; a null value keeps the stored one.
        /// </summary>
        void DataTagsUpdateActiveAndCycle(List<int> ids, int? active, int? cycle);
        DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            IReadOnlyList<DataTagBrowseImportItem> items,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken);
        DataTagBrowseSyncResult DataTagsSyncFromBrowse(
            int dataSourceId,
            int cycle,
            Action<Action<IReadOnlyList<DataTagBrowseImportItem>>> fill,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken);
        bool BrowseCheckpointExists(int dataSourceId);
        void BrowseCheckpointCreate(int dataSourceId);
        void BrowseCheckpointTouch(int dataSourceId);
        void BrowseCheckpointDelete(int dataSourceId);
        int BrowseCheckpointPurgeStale();
        void BrowseCheckpointSetInflight(int dataSourceId, string phase, IReadOnlyList<string> nodeKeys);
        void BrowseCheckpointCommitBrowse(
            int dataSourceId,
            IReadOnlyList<string> finishedParents,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<BrowseCheckpointNode> newVariables);
        void BrowseCheckpointCommitClassify(
            int dataSourceId,
            IReadOnlyList<string> finishedVariables,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<DataTagBrowseImportItem> scalars);
        int DataTagsRenameAddresses(int dataSourceId, IReadOnlyList<KeyValuePair<string, string>> renames);
        void BrowseCheckpointSetPhase(int dataSourceId, string phase);
        void BrowseCheckpointSetResumeOnConnect(int dataSourceId, bool resumeOnConnect);
        bool BrowseCheckpointResumeOnConnect(int dataSourceId);
        BrowseCheckpointState BrowseCheckpointLoad(int dataSourceId);
        DataTagBrowseSyncResult DataTagsSyncFromBrowseCheckpoint(
            int dataSourceId,
            int cycle,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken,
            bool allowDelete = true);
        DataTagBrowseSyncResult DataTagsSyncFromXmlStage(
            int dataSourceId,
            int cycle,
            Guid jobId,
            Action<DataTagBrowseSyncProgress> progress,
            CancellationToken cancellationToken);
        void XmlImportStageDelete(Guid jobId);
        void XmlImportStageClear();
        #endregion

        #region parameter
        string ParameterGet(string key);
        void ParameterSet(string key, string value);
        #endregion

        #region alarm class
        List<AlarmClassModel> AlarmClassGetAll();
        void AlarmClassInsert(string name, string symbol);
        #endregion

        #region chart
        List<ChartModel> ChartGetAll();
        ChartModel ChartGetById(int id);
        ChartModel ChartCreate(ChartModel chart);
        void ChartUpdate(ChartModel chart);
        void ChartDelete(int chartId);
        List<ChartPlotModel> ChartPlotsGetByChartId(int chartId);
        ChartPlotModel ChartPlotCreate(ChartPlotModel chartPlot);
        void ChartPlotUpdate(ChartPlotModel chartPlot);
        void ChartPlotDelete(int chartPlotId);

        /// <summary>All charts and folders with the number of plots of each chart.</summary>
        List<ChartTreeItemModel> ChartTreeGet();

        /// <summary>The plots of a chart with their data tag and data source, in plot order.</summary>
        List<ChartPlotDetailModel> ChartPlotDetailsGet(int chartId);

        /// <summary>The data tags of any data source with the given ids, deleted ones included.</summary>
        List<DataTagModel> DataTagsGetByIdList(IReadOnlyCollection<int> ids);

        /// <summary>Creates a chart or folder and the plots of the data tags in one transaction.</summary>
        ChartModel ChartCreate(ChartModel chart, IReadOnlyList<int> dataTagIds);

        /// <summary>Saves name and parent and, unless the ids are null, replaces the plots in one transaction.</summary>
        void ChartSave(ChartModel chart, IReadOnlyList<int> dataTagIds);

        /// <summary>Replaces the plots of a chart by the data tags in this order.</summary>
        void ChartPlotsSet(int chartId, IReadOnlyList<int> dataTagIds);

        /// <summary>
        /// Gives the children of a folder the order of the ids (sort order 0, 1, 2, ...) and, when a moved id is given,
        /// puts that one below the folder first, in one transaction.
        /// </summary>
        void ChartChildrenOrderSet(int parentId, IReadOnlyList<int> orderedIds, int? movedId);
        #endregion
    }
}
