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

using System.Collections.Concurrent;

namespace DataLogLib.Services.ArchiveAccess
{
    /// <summary>
    /// Maps a data tag id to its uid (datatag.guid) and to the id of the uid in the archivetag table,
    /// which the archive rows keep instead of the 32 characters of the uid. The archive stays bound
    /// to the uid, because ids are only unique per configuration database. Only tags that are written
    /// or queried are cached; unknown ids are resolved in one batch.
    /// </summary>
    internal sealed class ArchiveTagCache
    {
        // The cache is simply cleared when it grows beyond this; the entries are resolved again.
        internal const int MaxEntries = 200_000;

        private readonly ConcurrentDictionary<int, (string Uid, int ArchiveId)> _byTag = new();
        private readonly ConcurrentDictionary<string, int> _byUid = new(StringComparer.Ordinal);
        // Returns the archive ids of the uids and creates the missing ones
        private readonly Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, int>> _resolveIds;
        private volatile ITagUidLookup _lookup;

        internal ArchiveTagCache(Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, int>> resolveIds)
        {
            _resolveIds = resolveIds;
        }

        internal static string Normalize(string uid) => uid.ToUpperInvariant();

        internal void SetLookup(ITagUidLookup lookup)
        {
            _lookup = lookup;
            _byTag.Clear();
        }

        /// <summary>
        /// Resolves all given ids that are not cached yet with a single lookup.
        /// </summary>
        internal void Prefetch(IEnumerable<int> tagIds)
        {
            var lookup = _lookup;
            if (lookup == null)
            {
                return;
            }

            HashSet<int> missing = null;
            foreach (var id in tagIds)
            {
                if (!_byTag.ContainsKey(id))
                {
                    (missing ??= []).Add(id);
                }
            }

            if (missing == null)
            {
                return;
            }

            if (_byTag.Count + missing.Count > MaxEntries)
            {
                _byTag.Clear();
            }

            var uids = lookup.DataTagUidsGet(missing)
                .Where(item => !string.IsNullOrEmpty(item.Value))
                .ToDictionary(item => item.Key, item => Normalize(item.Value));
            if (uids.Count == 0)
            {
                return;
            }

            var archiveIds = ResolveIds(uids.Values.Distinct().ToList());
            foreach (var (tagId, uid) in uids)
            {
                if (archiveIds.TryGetValue(uid, out var archiveId))
                {
                    _byTag[tagId] = (uid, archiveId);
                }
            }
        }

        /// <summary>
        /// Returns false when the data tag has no uid (unknown tag or no lookup set).
        /// </summary>
        internal bool TryGet(int tagId, out int archiveId)
        {
            if (TryGetEntry(tagId, out var entry))
            {
                archiveId = entry.ArchiveId;
                return true;
            }

            archiveId = 0;
            return false;
        }

        internal bool TryGetUid(int tagId, out string uid)
        {
            if (TryGetEntry(tagId, out var entry))
            {
                uid = entry.Uid;
                return true;
            }

            uid = null;
            return false;
        }

        /// <summary>
        /// The archive id of the uid, also of tags that no longer exist; a new uid gets an id.
        /// </summary>
        internal int GetId(string uid)
        {
            uid = Normalize(uid);
            if (_byUid.TryGetValue(uid, out var id))
            {
                return id;
            }

            return ResolveIds([uid])[uid];
        }

        private bool TryGetEntry(int tagId, out (string Uid, int ArchiveId) entry)
        {
            if (_byTag.TryGetValue(tagId, out entry))
            {
                return true;
            }

            Prefetch([tagId]);
            return _byTag.TryGetValue(tagId, out entry);
        }

        private IReadOnlyDictionary<string, int> ResolveIds(IReadOnlyCollection<string> uids)
        {
            var missing = uids.Where(uid => !_byUid.ContainsKey(uid)).ToList();
            if (missing.Count > 0)
            {
                if (_byUid.Count + missing.Count > MaxEntries)
                {
                    _byUid.Clear();
                }

                foreach (var (uid, id) in _resolveIds(missing))
                {
                    _byUid[uid] = id;
                }
            }

            return uids
                .Where(_byUid.ContainsKey)
                .ToDictionary(uid => uid, uid => _byUid[uid], StringComparer.Ordinal);
        }
    }
}
