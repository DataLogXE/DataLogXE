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
using DataLogLib.Services;
using Xunit;

namespace DataLogLib.Tests
{
    [Collection(PostgresTestConfiguration.Collection)]
    public class PostgresChartTests : IDisposable
    {
        private const int RootId = 1;

        private readonly DataAccessCtrl _access;
        private readonly int _dataSourceId;
        private readonly int _folderId;
        private readonly List<int> _tagIds = [];

        public PostgresChartTests()
        {
            if (!PostgresTestConfiguration.IsAvailable)
            {
                return;
            }

            _access = new DataAccessCtrl();
            _access.Init(PostgresTestConfiguration.Create());
            var source = new DataSourceModel
            {
                Name = "chart-test-" + Guid.NewGuid().ToString("N"),
                TypeId = 2,
                IsActive = 0,
                Host = "localhost",
                Pass = ""
            };
            _access.DataSourceCreate(source);
            _dataSourceId = source.Id;

            var items = new List<DataTagBrowseImportItem>
            {
                new() { Address = "ns=2;s=A", Name = "A", TypeId = 2 },
                new() { Address = "ns=2;s=B", Name = "B", TypeId = 1 },
                new() { Address = "ns=2;s=C", Name = "C", TypeId = 2 }
            };
            _access.DataTagsSyncFromBrowse(_dataSourceId, 1000, items, null, CancellationToken.None);
            _tagIds.AddRange(_access.DataTagsGetByDataSourceId(_dataSourceId)
                .OrderBy(t => t.Name)
                .Select(t => t.Id));

            // Charts are not tied to the data source, so every test works below a folder of its own
            _folderId = _access.ChartCreate(
                new ChartModel { Name = "test-" + Guid.NewGuid().ToString("N"), Type = ChartItemType.Folder, ParentId = RootId },
                []).Id;
        }

        public void Dispose()
        {
            if (_access == null)
            {
                return;
            }

            _access.ChartDelete(_folderId);
            _access.DataSourceDelete(_dataSourceId);
            _access.Close();
        }

        [Fact]
        public void Create_stores_the_plots_in_the_given_order()
        {
            if (_access == null)
            {
                return;
            }

            var chart = _access.ChartCreate(
                new ChartModel { Name = "Chart", Type = ChartItemType.Chart, ParentId = _folderId },
                [_tagIds[2], _tagIds[0], _tagIds[1]]);

            var plots = _access.ChartPlotDetailsGet(chart.Id);
            Assert.Equal([_tagIds[2], _tagIds[0], _tagIds[1]], plots.Select(p => p.DataTagId));
            Assert.Equal([0, 1, 2], plots.Select(p => p.Sort));
            Assert.Equal(["C", "A", "B"], plots.Select(p => p.TagName));
            Assert.All(plots, p => Assert.Equal(_dataSourceId, p.DataSourceId));
            Assert.Equal((int)DataTagType.Bool, plots[2].TypeId);

            var item = _access.ChartTreeGet().Single(n => n.Id == chart.Id);
            Assert.Equal(3, item.PlotCount);
            Assert.Equal(_folderId, item.ParentId);
        }

        [Fact]
        public void Save_renames_moves_and_replaces_the_plots()
        {
            if (_access == null)
            {
                return;
            }

            var sub = _access.ChartCreate(
                new ChartModel { Name = "Sub", Type = ChartItemType.Folder, ParentId = _folderId }, []);
            var chart = _access.ChartCreate(
                new ChartModel { Name = "Chart", Type = ChartItemType.Chart, ParentId = _folderId },
                [_tagIds[0], _tagIds[1]]);

            _access.ChartSave(
                new ChartModel { Id = chart.Id, Name = "Renamed", Type = ChartItemType.Chart, ParentId = sub.Id },
                [_tagIds[1], _tagIds[2]]);

            var saved = _access.ChartGetById(chart.Id);
            Assert.Equal("Renamed", saved.Name);
            Assert.Equal(sub.Id, saved.ParentId);
            Assert.Equal([_tagIds[1], _tagIds[2]], _access.ChartPlotDetailsGet(chart.Id).Select(p => p.DataTagId));

            // Without ids the plots stay
            _access.ChartSave(
                new ChartModel { Id = chart.Id, Name = "Again", Type = ChartItemType.Chart, ParentId = sub.Id },
                null);
            Assert.Equal(2, _access.ChartPlotDetailsGet(chart.Id).Count);

            _access.ChartPlotsSet(chart.Id, []);
            Assert.Empty(_access.ChartPlotDetailsGet(chart.Id));
            Assert.Throws<KeyNotFoundException>(() => _access.ChartPlotsSet(int.MaxValue, [_tagIds[0]]));
        }

        [Fact]
        public void New_items_and_moved_items_come_last_and_the_children_take_the_given_order()
        {
            if (_access == null)
            {
                return;
            }

            ChartModel Create(string name, ChartItemType type, int parentId)
                => _access.ChartCreate(new ChartModel { Name = name, Type = type, ParentId = parentId }, []);
            List<int> Order(int parentId) => _access.ChartTreeGet()
                .Where(n => n.ParentId == parentId && n.Type != ChartItemType.Root)
                .OrderBy(n => n.SortOrder).ThenBy(n => n.Id)
                .Select(n => n.Id).ToList();

            var a = Create("A", ChartItemType.Chart, _folderId);
            var b = Create("B", ChartItemType.Folder, _folderId);
            var c = Create("C", ChartItemType.Chart, _folderId);
            Assert.Equal([0, 1, 2], new[] { a, b, c }.Select(n => _access.ChartGetById(n.Id).SortOrder));
            Assert.Equal([a.Id, b.Id, c.Id], Order(_folderId));

            // The tree comes in the order of the parents and then of the children
            var tree = _access.ChartTreeGet().Where(n => n.ParentId == _folderId).Select(n => n.Id).ToList();
            Assert.Equal([a.Id, b.Id, c.Id], tree);

            // A new order within the folder
            _access.ChartChildrenOrderSet(_folderId, [c.Id, a.Id, b.Id], null);
            Assert.Equal([c.Id, a.Id, b.Id], Order(_folderId));

            // A move below another folder: that folder gets the order, the item is last when it is saved there
            var d = Create("D", ChartItemType.Chart, b.Id);
            _access.ChartChildrenOrderSet(b.Id, [a.Id, d.Id], a.Id);
            Assert.Equal(b.Id, _access.ChartGetById(a.Id).ParentId);
            Assert.Equal([a.Id, d.Id], Order(b.Id));
            Assert.Equal([c.Id, b.Id], Order(_folderId));

            // A chart that is saved into another folder comes last there
            _access.ChartSave(new ChartModel { Id = c.Id, Name = "C", Type = ChartItemType.Chart, ParentId = b.Id }, null);
            Assert.Equal([a.Id, d.Id, c.Id], Order(b.Id));
            // and a save in the same folder keeps the place
            _access.ChartSave(new ChartModel { Id = a.Id, Name = "A2", Type = ChartItemType.Chart, ParentId = b.Id }, null);
            Assert.Equal([a.Id, d.Id, c.Id], Order(b.Id));
        }

        [Fact]
        public void Deleted_tags_stay_in_the_chart_and_are_flagged()
        {
            if (_access == null)
            {
                return;
            }

            var chart = _access.ChartCreate(
                new ChartModel { Name = "Chart", Type = ChartItemType.Chart, ParentId = _folderId },
                [_tagIds[0], _tagIds[1]]);
            _access.DataTagsUpdateDeleted([_tagIds[0]], 1);

            var plots = _access.ChartPlotDetailsGet(chart.Id);
            Assert.Equal(1, plots[0].IsDeleted);
            Assert.Equal(0, plots[1].IsDeleted);

            var tags = _access.DataTagsGetByIdList([_tagIds[0], _tagIds[1], int.MaxValue]);
            Assert.Equal(2, tags.Count);
            Assert.Equal(1, tags.Single(t => t.Id == _tagIds[0]).IsDeleted);
        }

        [Fact]
        public void Missing_chart_is_null()
        {
            if (_access == null)
            {
                return;
            }

            Assert.Null(_access.ChartGetById(int.MaxValue));
        }

        [Fact]
        public void Deleting_a_folder_deletes_its_subtree_and_plots()
        {
            if (_access == null)
            {
                return;
            }

            var sub = _access.ChartCreate(
                new ChartModel { Name = "Sub", Type = ChartItemType.Folder, ParentId = _folderId }, []);
            var chart = _access.ChartCreate(
                new ChartModel { Name = "Chart", Type = ChartItemType.Chart, ParentId = sub.Id },
                [_tagIds[0]]);

            _access.ChartDelete(sub.Id);

            Assert.Null(_access.ChartGetById(sub.Id));
            Assert.Null(_access.ChartGetById(chart.Id));
            Assert.Empty(_access.ChartPlotDetailsGet(chart.Id));
            Assert.NotNull(_access.ChartGetById(_folderId));
        }

        [Fact]
        public void Deleting_the_data_source_removes_its_plots()
        {
            if (_access == null)
            {
                return;
            }

            var chart = _access.ChartCreate(
                new ChartModel { Name = "Chart", Type = ChartItemType.Chart, ParentId = _folderId },
                [_tagIds[0], _tagIds[1]]);
            var other = new DataSourceModel
            {
                Name = "chart-test-" + Guid.NewGuid().ToString("N"),
                TypeId = 2,
                IsActive = 0,
                Host = "localhost",
                Pass = ""
            };
            _access.DataSourceCreate(other);
            _access.DataTagsSyncFromBrowse(
                other.Id,
                1000,
                [new DataTagBrowseImportItem { Address = "ns=2;s=X", Name = "X", TypeId = 2 }],
                null,
                CancellationToken.None);
            var otherTag = _access.DataTagsGetByDataSourceId(other.Id).Single().Id;
            _access.ChartPlotsSet(chart.Id, [_tagIds[0], otherTag, _tagIds[1]]);

            _access.DataSourceDelete(other.Id);

            Assert.Equal([_tagIds[0], _tagIds[1]], _access.ChartPlotDetailsGet(chart.Id).Select(p => p.DataTagId));
            Assert.Equal(2, _access.ChartTreeGet().Single(n => n.Id == chart.Id).PlotCount);
        }
    }
}
