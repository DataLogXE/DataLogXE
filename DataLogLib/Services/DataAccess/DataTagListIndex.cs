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

namespace DataLogLib.Services.DataAccess
{
    internal sealed class DataTagListIndex : IComparer<int>
    {
        private int[] _ids;
        private string[] _names;
        private string[] _displayNames;
        private string[] _addresses;
        private string[] _comments;
        private int[] _cycles;
        private byte[] _flags;
        private int[] _matchBuffer;
        private const int ArchiveValueMinMs = 10000;
        private const int TimeoutFactor = 3;

        private IReadOnlyList<DataTagSortLevel> _sorts;
        private bool _sortHasId;
        private bool _sourceOffline;
        private long _nowUtcTicks;
        private ITagActualLookup _actual;

        public int Count { get; private set; }

        // True when inactive tags are in this index. An active-only index cannot serve "all".
        public bool IncludesInactive { get; set; }

        public static DataTagListIndex Create(int capacity)
        {
            var n = Math.Max(capacity, 0);
            return new DataTagListIndex
            {
                Count = n,
                _ids = new int[n],
                _names = new string[n],
                _displayNames = new string[n],
                _addresses = new string[n],
                _comments = new string[n],
                _cycles = new int[n],
                _flags = new byte[n],
                _matchBuffer = n == 0 ? [] : new int[n]
            };
        }

        public void Set(int index, int id, string name, string displayName, int cycle, int isActive, int isSys, string address, string comment = "")
        {
            _ids[index] = id;
            var strippedName = StripQuotes(name);
            _names[index] = strippedName;
            _displayNames[index] = VisibleDisplayName(displayName);
            _addresses[index] = address ?? "";
            _comments[index] = comment ?? "";
            _cycles[index] = cycle;
            _flags[index] = (byte)((isActive != 0 ? 1 : 0) | (isSys != 0 ? 2 : 0));
        }

        public void TrimCount(int count)
        {
            if (count < 0 || count > _ids.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            Count = count;
        }

        public void SortByName()
        {
            var n = Count;
            if (n <= 1)
            {
                return;
            }

            var order = new int[n];
            for (var i = 0; i < n; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                var cmp = string.Compare(_names[a], _names[b], StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : _ids[a].CompareTo(_ids[b]);
            });

            var ids = new int[n];
            var names = new string[n];
            var displayNames = new string[n];
            var addresses = new string[n];
            var comments = new string[n];
            var cycles = new int[n];
            var flags = new byte[n];
            for (var i = 0; i < n; i++)
            {
                var from = order[i];
                ids[i] = _ids[from];
                names[i] = _names[from];
                displayNames[i] = _displayNames[from];
                addresses[i] = _addresses[from];
                comments[i] = _comments[from];
                cycles[i] = _cycles[from];
                flags[i] = _flags[from];
            }

            _ids = ids;
            _names = names;
            _displayNames = displayNames;
            _addresses = addresses;
            _comments = comments;
            _cycles = cycles;
            _flags = flags;
        }

        public DataTagListPageModel GetPage(
            int? active,
            DataTagNameFilter filter,
            int offset,
            int limit,
            IReadOnlyList<DataTagSortLevel> sorts,
            TagLiveSort live = default)
        {
            filter ??= DataTagNameFilter.Parse(null);
            var matchCount = 0;
            var wantActive = active.HasValue && active.Value != 0;
            var filterActive = active.HasValue;
            var n = Count;
            for (var i = 0; i < n; i++)
            {
                if (filterActive && ((_flags[i] & 1) != 0) != wantActive)
                {
                    continue;
                }

                if (!filter.IsEmpty
                    && !filter.Matches(_names[i])
                    && !filter.Matches(_displayNames[i])
                    && !filter.Matches(_addresses[i])
                    && !filter.Matches(_comments[i])
                    && !filter.MatchesId(_ids[i]))
                {
                    continue;
                }

                _matchBuffer[matchCount++] = i;
            }

            if (!IsDefaultNameSort(sorts) && matchCount > 1)
            {
                _sorts = sorts;
                _sortHasId = HasIdSort(sorts);
                _sourceOffline = live.SourceOffline;
                _nowUtcTicks = live.NowUtc.Kind == DateTimeKind.Utc
                    ? live.NowUtc.Ticks
                    : live.NowUtc.ToUniversalTime().Ticks;
                _actual = live.Actual;
                try
                {
                    Array.Sort(_matchBuffer, 0, matchCount, this);
                }
                finally
                {
                    _actual = null;
                }
            }

            if (offset < 0)
            {
                offset = 0;
            }

            if (limit < 0)
            {
                limit = 0;
            }

            var items = new List<DataTagListItemModel>();
            if (limit > 0 && offset < matchCount)
            {
                var take = Math.Min(limit, matchCount - offset);
                items.Capacity = take;
                for (var i = 0; i < take; i++)
                {
                    var row = _matchBuffer[offset + i];
                    items.Add(new DataTagListItemModel
                    {
                        Id = _ids[row],
                        Name = _names[row],
                        DisplayName = _displayNames[row] ?? "",
                        Address = _addresses[row] ?? "",
                        Comment = _comments[row] ?? "",
                        Cycle = _cycles[row],
                        IsActive = _flags[row] & 1,
                        IsSys = (_flags[row] & 2) != 0 ? 1 : 0
                    });
                }
            }

            return new DataTagListPageModel
            {
                Total = matchCount,
                Items = items
            };
        }

        public void PatchActive(HashSet<int> ids, int active)
        {
            if (ids == null || ids.Count == 0 || Count == 0)
            {
                return;
            }

            var n = Count;
            var on = active != 0;
            for (var i = 0; i < n; i++)
            {
                if (!ids.Contains(_ids[i]))
                {
                    continue;
                }

                if (on)
                {
                    _flags[i] |= 1;
                }
                else
                {
                    _flags[i] &= unchecked((byte)~1);
                }
            }
        }

        public void PatchCycle(HashSet<int> ids, int cycle)
        {
            if (ids == null || ids.Count == 0 || Count == 0)
            {
                return;
            }

            var n = Count;
            for (var i = 0; i < n; i++)
            {
                if (ids.Contains(_ids[i]))
                {
                    _cycles[i] = cycle;
                }
            }
        }

        public int Compare(int a, int b)
        {
            if (_sorts != null)
            {
                var levels = Math.Min(_sorts.Count, 2);
                for (var i = 0; i < levels; i++)
                {
                    var sort = _sorts[i];
                    int cmp;
                    if (sort.Column == DataTagSortColumn.Value)
                    {
                        cmp = CompareValue(a, b, sort.Ascending);
                    }
                    else
                    {
                        cmp = sort.Column switch
                        {
                            DataTagSortColumn.Id => _ids[a].CompareTo(_ids[b]),
                            DataTagSortColumn.Name => string.Compare(_names[a], _names[b], StringComparison.OrdinalIgnoreCase),
                            DataTagSortColumn.DisplayName => string.Compare(_displayNames[a] ?? "", _displayNames[b] ?? "", StringComparison.OrdinalIgnoreCase),
                            DataTagSortColumn.Address => string.Compare(_addresses[a] ?? "", _addresses[b] ?? "", StringComparison.OrdinalIgnoreCase),
                            DataTagSortColumn.Comment => string.Compare(_comments[a] ?? "", _comments[b] ?? "", StringComparison.OrdinalIgnoreCase),
                            DataTagSortColumn.Cycle => _cycles[a].CompareTo(_cycles[b]),
                            DataTagSortColumn.Active => (_flags[a] & 1).CompareTo(_flags[b] & 1),
                            DataTagSortColumn.Status => string.Compare(StatusLabel(a), StatusLabel(b), StringComparison.OrdinalIgnoreCase),
                            _ => 0
                        };
                        if (!sort.Ascending)
                        {
                            cmp = -cmp;
                        }
                    }

                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
            }

            if (_sortHasId)
            {
                return 0;
            }

            return _ids[a].CompareTo(_ids[b]);
        }

        internal static string StripQuotes(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            return name.IndexOf('"') < 0 ? name : name.Replace("\"", "");
        }

        private static string VisibleDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return "";
            }

            return StripQuotes(displayName);
        }

        private int CompareValue(int a, int b, bool ascending)
        {
            var hasA = TryNumeric(a, out var valueA);
            var hasB = TryNumeric(b, out var valueB);
            if (hasA && hasB)
            {
                return ascending ? valueA.CompareTo(valueB) : valueB.CompareTo(valueA);
            }

            if (hasA == hasB)
            {
                return 0;
            }

            return hasA ? -1 : 1;
        }

        private bool TryNumeric(int index, out double value)
        {
            value = 0;
            if (!TryFreshActual(index, out value, out _))
            {
                value = 0;
                return false;
            }

            return true;
        }

        private string StatusLabel(int index)
        {
            if ((_flags[index] & 1) == 0)
            {
                return "Inactive";
            }

            if (_sourceOffline)
            {
                return "Offline";
            }

            if (!TryFreshActual(index, out _, out var state))
            {
                if (_actual == null || !_actual.TryGetActual(_ids[index], out _, out state, out _))
                {
                    return "Pending";
                }

                if (state == 0)
                {
                    return "Invalid";
                }

                return "Stale";
            }

            return state == 2 ? "Resend" : "Valid";
        }

        private bool TryFreshActual(int index, out double value, out int state)
        {
            value = 0;
            state = 0;
            if ((_flags[index] & 1) == 0 || _sourceOffline || _actual == null)
            {
                return false;
            }

            if (!_actual.TryGetActual(_ids[index], out value, out state, out var timeUtcTicks) || state == 0)
            {
                return false;
            }

            var ageMs = (_nowUtcTicks - timeUtcTicks) / TimeSpan.TicksPerMillisecond;
            var maxAgeMs = (long)Math.Max(_cycles[index], ArchiveValueMinMs) * TimeoutFactor;
            return ageMs <= maxAgeMs;
        }

        private static bool IsDefaultNameSort(IReadOnlyList<DataTagSortLevel> sorts)
        {
            if (sorts == null || sorts.Count == 0)
            {
                return true;
            }

            if (sorts[0].Column != DataTagSortColumn.Name || !sorts[0].Ascending)
            {
                return false;
            }

            if (sorts.Count == 1)
            {
                return true;
            }

            return sorts[1].Column == DataTagSortColumn.Id && sorts[1].Ascending;
        }

        private static bool HasIdSort(IReadOnlyList<DataTagSortLevel> sorts)
        {
            if (sorts == null)
            {
                return false;
            }

            var levels = Math.Min(sorts.Count, 2);
            for (var i = 0; i < levels; i++)
            {
                if (sorts[i].Column == DataTagSortColumn.Id)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
