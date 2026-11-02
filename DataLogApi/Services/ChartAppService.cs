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
using DataLogLib.Models;
using DataLogService;

namespace DataLogApi.Services
{
    /// <summary>
    /// The saved charts: a tree of folders and charts below the root, each chart with an ordered list of data tags.
    /// </summary>
    public class ChartAppService
    {
        // Every plot loads its points in a query job of its own; a Raspberry Pi reads up to
        // DataTagAppService.MaxPlotReadPoints values per plot and range change.
        public const int MaxPlotsPerChart = 16;
        public const int MaxNodes = 1000;
        public const int MaxNameLength = 100;

        private readonly ServiceCtrl _service;
        // The checks of a change (unique name, no cycle, plot limit) and the change itself run as one step.
        private readonly object _writeLock = new();

        public ChartAppService(ServiceCtrl service)
        {
            _service = service;
        }

        public ChartTreeDto GetTree()
        {
            return new ChartTreeDto
            {
                MaxPlots = MaxPlotsPerChart,
                MaxNodes = MaxNodes,
                Items = _service.DataAccess.ChartTreeGet().Select(ToTreeItem).ToList()
            };
        }

        public ChartDetailDto GetChart(int id)
        {
            var node = GetNodeOrThrow(id);
            if (node.Type != ChartItemType.Chart)
            {
                throw new KeyNotFoundException($"Chart {id} was not found.");
            }

            return ToDetail(node);
        }

        public ChartTreeItemDto Create(ChartCreateDto dto)
        {
            if (dto == null)
            {
                throw new ArgumentException("The request is empty.");
            }

            var type = ParseType(dto.Type);
            lock (_writeLock)
            {
                var tree = _service.DataAccess.ChartTreeGet();
                if (tree.Count >= MaxNodes)
                {
                    throw new InvalidOperationException($"At most {MaxNodes} charts and folders are allowed.");
                }

                var parent = ValidateParent(tree, dto.ParentId);
                var name = ValidateName(dto.Name, type, tree, parent.Id, excludeId: null);
                List<int> tagIds = [];
                if (dto.DataTagIds is { Count: > 0 })
                {
                    if (type != ChartItemType.Chart)
                    {
                        throw new ArgumentException("A folder cannot have data tags.");
                    }

                    tagIds = ValidateTags(dto.DataTagIds, new HashSet<int>());
                }

                var created = _service.DataAccess.ChartCreate(
                    new ChartModel { Name = name, Type = type, ParentId = parent.Id },
                    tagIds);
                return ToTreeItem(new ChartTreeItemModel
                {
                    Id = created.Id,
                    Name = created.Name,
                    TypeId = created.TypeId,
                    ParentId = created.ParentId,
                    PlotCount = tagIds.Count
                });
            }
        }

        public ChartTreeItemDto Update(int id, ChartUpdateDto dto)
        {
            if (dto == null)
            {
                throw new ArgumentException("The request is empty.");
            }

            lock (_writeLock)
            {
                var tree = _service.DataAccess.ChartTreeGet();
                var node = tree.FirstOrDefault(n => n.Id == id)
                    ?? throw new KeyNotFoundException($"Chart {id} was not found.");
                if (node.Type == ChartItemType.Root)
                {
                    throw new InvalidOperationException("The root folder cannot be changed.");
                }

                var parentId = node.ParentId;
                if (dto.ParentId.HasValue && dto.ParentId.Value != node.ParentId)
                {
                    parentId = ValidateParent(tree, dto.ParentId.Value).Id;
                    var parentById = tree.ToDictionary(n => n.Id, n => n.ParentId);
                    if (ChartTreeRules.WouldCreateCycle(parentById, id, parentId))
                    {
                        throw new ArgumentException("A folder cannot be moved into itself or one of its subfolders.");
                    }
                }

                // A move checks the name against the new siblings; only plots keep an old name as it is.
                var name = node.Name;
                if (dto.Name != null || parentId != node.ParentId)
                {
                    name = ValidateName(dto.Name ?? node.Name, node.Type, tree, parentId, excludeId: id);
                }

                List<int> tagIds = null;
                if (dto.DataTagIds != null)
                {
                    if (node.Type != ChartItemType.Chart)
                    {
                        throw new ArgumentException("A folder cannot have data tags.");
                    }

                    var current = _service.DataAccess.ChartPlotsGetByChartId(id)
                        .Select(p => p.DataTagId)
                        .ToHashSet();
                    tagIds = ValidateTags(dto.DataTagIds, current);
                }

                _service.DataAccess.ChartSave(
                    new ChartModel { Id = id, Name = name, TypeId = node.TypeId, ParentId = parentId },
                    tagIds);
                return ToTreeItem(new ChartTreeItemModel
                {
                    Id = id,
                    Name = name,
                    TypeId = node.TypeId,
                    ParentId = parentId,
                    PlotCount = tagIds?.Count ?? node.PlotCount
                });
            }
        }

        /// <summary>
        /// Puts a chart or folder below a folder at a place: before one of its children, or last. Within the folder it
        /// only changes the order.
        /// </summary>
        public void Move(int id, ChartMoveDto dto)
        {
            if (dto == null)
            {
                throw new ArgumentException("The request is empty.");
            }

            lock (_writeLock)
            {
                var tree = _service.DataAccess.ChartTreeGet();
                var node = tree.FirstOrDefault(n => n.Id == id)
                    ?? throw new KeyNotFoundException($"Chart {id} was not found.");
                if (node.Type == ChartItemType.Root)
                {
                    throw new InvalidOperationException("The root folder cannot be moved.");
                }

                var parent = ValidateParent(tree, dto.ParentId);
                if (parent.Id != node.ParentId)
                {
                    var parentById = tree.ToDictionary(n => n.Id, n => n.ParentId);
                    if (ChartTreeRules.WouldCreateCycle(parentById, id, parent.Id))
                    {
                        throw new ArgumentException("A folder cannot be moved into itself or one of its subfolders.");
                    }

                    ValidateName(node.Name, node.Type, tree, parent.Id, excludeId: id);
                }

                var siblings = ChildrenInOrder(tree, parent.Id).Where(n => n.Id != id).Select(n => n.Id).ToList();
                var index = siblings.Count;
                if (dto.BeforeId.HasValue)
                {
                    index = siblings.IndexOf(dto.BeforeId.Value);
                    if (index < 0)
                    {
                        throw new ArgumentException("The chart or folder to go before is not in this folder.");
                    }
                }

                siblings.Insert(index, id);
                _service.DataAccess.ChartChildrenOrderSet(parent.Id, siblings, parent.Id != node.ParentId ? id : null);
            }
        }

        /// <summary>Sorts the charts and folders directly below a folder by name, numbers by their value (2 before 10).</summary>
        public void SortChildren(int id)
        {
            lock (_writeLock)
            {
                var tree = _service.DataAccess.ChartTreeGet();
                var node = tree.FirstOrDefault(n => n.Id == id)
                    ?? throw new KeyNotFoundException($"Chart {id} was not found.");
                if (!ChartTreeRules.IsContainer(node.Type))
                {
                    throw new ArgumentException("Only a folder can be sorted.");
                }

                var sorted = ChildrenInOrder(tree, id)
                    .OrderBy(n => n.Name ?? "", Comparer<string>.Create(ChartTreeRules.CompareNames))
                    .ThenBy(n => n.Id)
                    .Select(n => n.Id)
                    .ToList();
                _service.DataAccess.ChartChildrenOrderSet(id, sorted, null);
            }
        }

        public void Delete(int id)
        {
            lock (_writeLock)
            {
                var node = GetNodeOrThrow(id);
                if (node.Type == ChartItemType.Root)
                {
                    throw new InvalidOperationException("The root folder cannot be deleted.");
                }

                // The foreign keys delete the subfolders, their charts and all plots
                _service.DataAccess.ChartDelete(id);
            }
        }

        public ChartPlotsAppendResultDto AppendPlots(int id, ChartPlotsAppendDto dto)
        {
            var requested = ChartTreeRules.NormalizeIds(dto?.DataTagIds);
            if (requested.Count == 0)
            {
                throw new ArgumentException("Please select at least one data tag.");
            }

            lock (_writeLock)
            {
                var node = GetNodeOrThrow(id);
                if (node.Type != ChartItemType.Chart)
                {
                    throw new KeyNotFoundException($"Chart {id} was not found.");
                }

                var existing = ChartTreeRules.NormalizeIds(
                    _service.DataAccess.ChartPlotsGetByChartId(id).Select(p => p.DataTagId));
                var tags = _service.DataAccess.DataTagsGetByIdList(requested).ToDictionary(t => t.Id);
                var missing = requested.FirstOrDefault(tagId => !tags.ContainsKey(tagId));
                if (missing != 0)
                {
                    throw new ArgumentException($"Data tag {missing} was not found.");
                }

                var existingSet = existing.ToHashSet();
                var added = requested
                    .Where(tagId => !existingSet.Contains(tagId) && tags[tagId].IsDeleted == 0)
                    .ToList();
                if (existing.Count + added.Count > MaxPlotsPerChart)
                {
                    throw new ArgumentException(
                        $"The chart has {existing.Count} data tags; at most {MaxPlotsPerChart} are allowed.");
                }

                if (added.Count > 0)
                {
                    _service.DataAccess.ChartPlotsSet(id, existing.Concat(added).ToList());
                }

                return new ChartPlotsAppendResultDto
                {
                    Chart = ToDetail(node),
                    Added = added.Count,
                    Skipped = requested.Count - added.Count
                };
            }
        }

        // The children of a folder in their order; the root is stored below itself and is not a child
        private static IEnumerable<ChartTreeItemModel> ChildrenInOrder(List<ChartTreeItemModel> tree, int parentId)
        {
            return tree
                .Where(n => n.ParentId == parentId && n.Type != ChartItemType.Root)
                .OrderBy(n => n.SortOrder)
                .ThenBy(n => n.Id);
        }

        private ChartModel GetNodeOrThrow(int id)
        {
            return _service.DataAccess.ChartGetById(id)
                ?? throw new KeyNotFoundException($"Chart {id} was not found.");
        }

        private static ChartTreeItemModel ValidateParent(List<ChartTreeItemModel> tree, int parentId)
        {
            var parent = tree.FirstOrDefault(n => n.Id == parentId);
            if (parent == null || !ChartTreeRules.IsContainer(parent.Type))
            {
                throw new ArgumentException("Please choose a valid folder.");
            }

            return parent;
        }

        private static string ValidateName(
            string name,
            ChartItemType type,
            List<ChartTreeItemModel> tree,
            int parentId,
            int? excludeId)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(type == ChartItemType.Folder
                    ? "Please enter a valid folder name."
                    : "Please enter a valid chart name.");
            }

            var trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength)
            {
                throw new ArgumentException($"The name can have at most {MaxNameLength} characters.");
            }

            // The root points at itself, so it is not a sibling of the nodes below it
            var clash = tree.Any(n =>
                n.ParentId == parentId &&
                n.Type != ChartItemType.Root &&
                n.Id != excludeId &&
                string.Equals((n.Name ?? "").Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (clash)
            {
                throw new InvalidOperationException("A chart or folder with this name already exists in this folder.");
            }

            return trimmed;
        }

        // Deleted data tags may stay in a chart that already shows them, but are not added again.
        private List<int> ValidateTags(IEnumerable<int> ids, IReadOnlySet<int> alreadyInChart)
        {
            var normalized = ChartTreeRules.NormalizeIds(ids);
            if (normalized.Count > MaxPlotsPerChart)
            {
                throw new ArgumentException($"A chart can show at most {MaxPlotsPerChart} data tags.");
            }

            var tags = _service.DataAccess.DataTagsGetByIdList(normalized).ToDictionary(t => t.Id);
            foreach (var tagId in normalized)
            {
                if (!tags.TryGetValue(tagId, out var tag))
                {
                    throw new ArgumentException($"Data tag {tagId} was not found.");
                }

                if (tag.IsDeleted != 0 && !alreadyInChart.Contains(tagId))
                {
                    throw new ArgumentException($"Data tag {tag.Name} is deleted.");
                }
            }

            return normalized;
        }

        private static ChartItemType ParseType(string type)
        {
            if (string.Equals(type, nameof(ChartItemType.Folder), StringComparison.OrdinalIgnoreCase) || type == "1")
            {
                return ChartItemType.Folder;
            }

            if (string.Equals(type, nameof(ChartItemType.Chart), StringComparison.OrdinalIgnoreCase) || type == "2")
            {
                return ChartItemType.Chart;
            }

            throw new ArgumentException("Please choose a valid type.");
        }

        private ChartDetailDto ToDetail(ChartModel node)
        {
            return new ChartDetailDto
            {
                Id = node.Id,
                Name = node.Name ?? "",
                ParentId = node.ParentId,
                Plots = _service.DataAccess.ChartPlotDetailsGet(node.Id).Select(ToPlot).ToList()
            };
        }

        private static ChartTreeItemDto ToTreeItem(ChartTreeItemModel node)
        {
            return new ChartTreeItemDto
            {
                Id = node.Id,
                Name = node.Name ?? "",
                Type = node.Type.ToString(),
                ParentId = node.ParentId,
                PlotCount = node.PlotCount,
                SortOrder = node.SortOrder
            };
        }

        private static ChartPlotDto ToPlot(ChartPlotDetailModel plot)
        {
            return new ChartPlotDto
            {
                Id = plot.Id,
                DataTagId = plot.DataTagId,
                DataSourceId = plot.DataSourceId,
                SourceName = plot.DataSourceName ?? "",
                TagName = plot.TagName ?? "",
                DisplayName = plot.DisplayName ?? "",
                IsBool = plot.TypeId == (int)DataTagType.Bool,
                IsActive = plot.IsActive != 0,
                IsSys = plot.IsSys != 0,
                IsDeleted = plot.IsDeleted != 0
            };
        }
    }
}
