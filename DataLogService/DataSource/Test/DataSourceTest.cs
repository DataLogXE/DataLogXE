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
using Serilog;
using System.Diagnostics;

namespace DataLogService.DataSource.Test
{
    internal class DataSourceTest : DataSourceBase
    {
        #region private fields
        private const int WorkerTickMs = 10;

        private TestParameter _parameter;
        private bool _running = true;
        private List<TestValue> _values = [];
        private readonly object _valuesLock = new();
        private string _dataTagChangeMarker = "";
        #endregion

        #region construction
        public DataSourceTest(DataSourceModel dataSource) : base(dataSource) { }

        public override void Dispose()
        {
            _running = false;
            base.Dispose();
        }
        #endregion

        #region public methods
        public override void Init()
        {
            base.Init();

            _parameter = new TestParameter
            {
                CycleMs = _dataSource.TestCycleMs,
                Count = _dataSource.TestTagCount
            };

            Log.Information(
                LogPfx + "Init test values: {TestParameter}",
                Name,
                Id,
                $"CycleMs={_parameter.CycleMs} Count={_parameter.Count}");
            _state = DataSourceState.Connected;

            // Start worker thread
            new Thread(Worker) { IsBackground = true }.Start();
        }
        #endregion

        #region protected methods
        protected override void InitializeCycleList()
        {
            Cycles = [10, 50, 100, 500, 1000, 5000, 10000];
        }
        #endregion

        #region private methods
        private void Worker(object obj)
        {
            try
            {
                InitTestDataTags();
                _dataTagChangeMarker = Global.Service.DataAccess.ParameterGet(Consts.DATA_TAG_CHANGE_MARKER_KEY);
            }
            catch (Exception ex)
            {
                Log.Error(ex, LogPfx + "Init test values failed", Name, Id);
                _state = DataSourceState.Error;
                return;
            }

            var lastSent = new Dictionary<int, long>();
            var writeFilter = new TestWriteFilter();
            var sw = Stopwatch.StartNew();

            while (_running)
            {
                try
                {
                    if (DatabaseDataTagsChanged())
                    {
                        SyncTestValues();
                    }

                    var now = sw.ElapsedMilliseconds;
                    List<TestValue> values;
                    lock (_valuesLock)
                    {
                        values = [.. _values];
                    }

                    foreach (var value in values)
                    {
                        var cycle = Math.Max(value.Cycle, WorkerTickMs);
                        if (lastSent.TryGetValue(value.Id, out var last) && now - last < cycle)
                        {
                            continue;
                        }

                        lastSent[value.Id] = now;

                        // Read once per cycle, the spike types change their state on every read.
                        var current = value.Value;
                        if (!writeFilter.ShouldWrite(value.Id, current, now))
                        {
                            continue;
                        }

                        InsertArchiveValue(new ArchiveValue()
                        {
                            Id = value.Id,
                            State = ValueState.Valid,
                            Time = DateTime.Now,
                            Value = current,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogPfx + "Test value worker failed", Name, Id);
                }

                Thread.Sleep(WorkerTickMs);
            }
        }

        private void InitTestDataTags()
        {
            var sw = Stopwatch.StartNew();
            IDataAccess db = Global.Service.DataAccess;

            // Read existing datatags from database
            List<DataTagModel> dataTags = LoadUserDataTags();

            // Create new datatags when needed (Count applies per type). Address must be unique per source at insert time.
            var usedAddresses = new HashSet<string>(
                dataTags.Select(tag => tag.Address ?? ""),
                StringComparer.Ordinal);
            List<DataTagModel> toAdd = [];
            foreach (var type in TestTypes.All)
            {
                int existing = dataTags.Count(tag => GetType(tag) == type);
                int number = 1;
                for (int added = existing; added < _parameter.Count; number++)
                {
                    var name = GetDataTagName(type, number);
                    if (!usedAddresses.Add(name))
                    {
                        continue;
                    }

                    toAdd.Add(CreateDataTag(name, type));
                    added++;
                }
            }

            if (toAdd.Count > 0)
            {
                db.DataTagsCreate(toAdd);
                dataTags = LoadUserDataTags();
            }

            var missingIdCount = dataTags.Count(dt => dt.Id <= 0);
            if (missingIdCount > 0)
            {
                Log.Error(LogPfx + "Skipped {Count} data tags without a valid id", Name, Id, missingIdCount);
                dataTags = dataTags.Where(dt => dt.Id > 0).ToList();
            }

            var assignments = AssignTypes(dataTags);

            // Move addresses aside first when the final names are not already in place.
            // A direct rename can hit the unique (DataSourceId, Address) index mid-update.
            if (assignments.Any(a => !IsNamedCorrectly(a.Tag, a.Type, a.Number)))
            {
                foreach (var dt in dataTags)
                {
                    var temporary = TemporaryAddress(dt.Id);
                    dt.Name = temporary;
                    dt.Address = temporary;
                }

                db.DataTagsUpdate(dataTags);
            }

            // Update DataTag names and active flags; keep existing cycles
            foreach (var (dt, type, number) in assignments)
            {
                var name = GetDataTagName(type, number);
                dt.Name = name;
                dt.Address = name;
                dt.IsActive = number <= _parameter.Count ? 1 : 0;
            }
            db.DataTagsUpdate(dataTags);

            // Insert startup value in archive
            var activeAssignments = assignments.Where(a => a.Tag.IsActive > 0).ToList();
            foreach (var assignment in activeAssignments)
            {
                InsertStartupArchiveValue(assignment.Tag.Id);
            }

            lock (_valuesLock)
            {
                _values = activeAssignments.Select(a => new TestValue(a.Tag, a.Type)).ToList();
            }

            Log.Information(LogPfx + "Init test values complete in {ElapsedMs}ms", Name, Id, sw.ElapsedMilliseconds);
        }

        private void SyncTestValues()
        {
            var dbTags = Global.Service.DataAccess
                .DataTagsGetByDataSourceId(_dataSource.Id, deleted: 0, active: 1)
                .Where(dt => dt.Id > 0 && dt.IsSys == 0)
                .ToList();

            lock (_valuesLock)
            {
                var existing = _values.ToDictionary(v => v.Id);
                var next = new List<TestValue>(dbTags.Count);
                foreach (var tag in dbTags)
                {
                    if (existing.TryGetValue(tag.Id, out var value))
                    {
                        value.Cycle = tag.Cycle;
                        next.Add(value);
                    }
                    else
                    {
                        next.Add(new TestValue(tag, GetType(tag)));
                    }
                }

                _values = next;
            }

            Log.Information(LogPfx + "Synced {Count} active test tags", Name, Id, dbTags.Count);
        }

        private bool DatabaseDataTagsChanged()
        {
            var marker = Global.Service.DataAccess.ParameterGet(Consts.DATA_TAG_CHANGE_MARKER_KEY) ?? "";
            var markerChanged = !marker.Equals(_dataTagChangeMarker);
            _dataTagChangeMarker = marker;
            return markerChanged;
        }

        private List<DataTagModel> LoadUserDataTags()
        {
            return Global.Service.DataAccess.DataTagsGetByDataSourceId(_dataSource.Id)
                .Where(tag => tag.IsSys == 0)
                .OrderBy(tag => tag.Id)
                .ToList();
        }

        private TestTypeDef GetType(DataTagModel tag)
        {
            var address = tag.Address ?? "";
            foreach (var type in TestTypes.All)
            {
                if (type.Kind != TestSignalKind.Sine
                    && address.StartsWith($"Test {_dataSource.Id} {type.Name} ", StringComparison.Ordinal))
                {
                    return type;
                }
            }

            return TestTypes.All[0];
        }

        /// <summary>Numbers the tags per type in id order; unknown names count as plain sine.</summary>
        private List<(DataTagModel Tag, TestTypeDef Type, int Number)> AssignTypes(List<DataTagModel> dataTags)
        {
            var counters = new Dictionary<TestTypeDef, int>();
            var result = new List<(DataTagModel, TestTypeDef, int)>(dataTags.Count);
            foreach (var tag in dataTags)
            {
                var type = GetType(tag);
                counters[type] = counters.GetValueOrDefault(type) + 1;
                result.Add((tag, type, counters[type]));
            }

            return result;
        }

        private bool IsNamedCorrectly(DataTagModel tag, TestTypeDef type, int number)
        {
            var name = GetDataTagName(type, number);
            return string.Equals(tag.Name, name, StringComparison.Ordinal)
                && string.Equals(tag.Address, name, StringComparison.Ordinal);
        }

        private static string TemporaryAddress(int id)
        {
            return $"~tmp-{id}";
        }

        private DataTagModel CreateDataTag(string name, TestTypeDef type)
        {
            return new DataTagModel()
            {
                DataSourceId = _dataSource.Id,
                TypeId = type.IsBool ? (int)DataTagType.Bool : (int)DataTagType.Number,
                Name = name,
                Address = name,
                Cycle = GetInitialCycle(type),
                IsActive = 0,
                IsSys = 0,
            };
        }

        /// <summary>A blinking tag is read at least ten times per period, otherwise the edges would alias away.</summary>
        private int GetInitialCycle(TestTypeDef type)
        {
            if (type.Kind != TestSignalKind.BoolBlink)
            {
                return _parameter.CycleMs;
            }

            var limit = Math.Min(_parameter.CycleMs, type.PeriodSec * 100);
            return Cycles.Where(c => c <= limit).DefaultIfEmpty(WorkerTickMs).Max();
        }

        private string GetDataTagName(TestTypeDef type, int number)
        {
            return $"Test {_dataSource.Id} {type.Name} {number}";
        }
        #endregion
    }
}
