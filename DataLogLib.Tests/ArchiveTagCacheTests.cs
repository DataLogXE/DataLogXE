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

using DataLogLib.Services;
using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public class ArchiveTagCacheTests
    {
        private sealed class FakeLookup : ITagUidLookup
        {
            public Dictionary<int, string> Uids { get; } = [];
            public List<int[]> Calls { get; } = [];

            public IReadOnlyDictionary<int, string> DataTagUidsGet(IReadOnlyCollection<int> dataTagIds)
            {
                Calls.Add(dataTagIds.ToArray());
                return Uids.Where(p => dataTagIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            }
        }

        /// <summary>The archivetag table: ids in the order the uids were first seen.</summary>
        private sealed class FakeArchiveTags
        {
            public Dictionary<string, int> Ids { get; } = new(StringComparer.Ordinal);
            public List<string[]> Calls { get; } = [];

            public IReadOnlyDictionary<string, int> Resolve(IReadOnlyCollection<string> uids)
            {
                Calls.Add(uids.ToArray());
                foreach (var uid in uids)
                {
                    if (!Ids.ContainsKey(uid))
                    {
                        Ids[uid] = Ids.Count + 1;
                    }
                }
                return uids.ToDictionary(uid => uid, uid => Ids[uid]);
            }
        }

        [Fact]
        public void Unknown_tags_are_resolved_in_one_lookup_and_then_served_from_the_cache()
        {
            var lookup = new FakeLookup();
            lookup.Uids[1] = "00000000000000000000000000000001";
            lookup.Uids[56] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var tags = new FakeArchiveTags();
            var cache = new ArchiveTagCache(tags.Resolve);
            cache.SetLookup(lookup);

            cache.Prefetch([1, 56, 1]);

            Assert.Single(lookup.Calls);
            Assert.Single(tags.Calls);
            Assert.True(cache.TryGet(56, out var archiveId));
            Assert.Equal(tags.Ids["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"], archiveId);
            Assert.True(cache.TryGetUid(56, out var uid));
            Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", uid);
            Assert.Single(lookup.Calls);
            Assert.Single(tags.Calls);
        }

        [Fact]
        public void Tag_without_uid_is_not_resolved()
        {
            var cache = new ArchiveTagCache(new FakeArchiveTags().Resolve);
            cache.SetLookup(new FakeLookup());

            Assert.False(cache.TryGet(7, out _));
            Assert.False(cache.TryGetUid(7, out var uid));
            Assert.Null(uid);
        }

        [Fact]
        public void Uid_of_a_removed_tag_gets_the_same_archive_id_in_any_case()
        {
            var lookup = new FakeLookup();
            lookup.Uids[56] = "ABCDEF0123456789ABCDEF0123456789";
            var tags = new FakeArchiveTags();
            var cache = new ArchiveTagCache(tags.Resolve);
            cache.SetLookup(lookup);
            Assert.True(cache.TryGet(56, out var archiveId));

            // The repair knows only the uid, also of tags that no longer exist
            Assert.Equal(archiveId, cache.GetId("abcdef0123456789abcdef0123456789"));
            Assert.Single(tags.Calls);
            Assert.NotEqual(archiveId, cache.GetId("11111111111111111111111111111111"));
        }

        [Fact]
        public void Same_id_in_different_databases_gets_different_archive_ids()
        {
            var first = new FakeLookup();
            first.Uids[56] = "11111111111111111111111111111111";
            var second = new FakeLookup();
            second.Uids[56] = "22222222222222222222222222222222";
            var tags = new FakeArchiveTags();
            var a = new ArchiveTagCache(tags.Resolve);
            var b = new ArchiveTagCache(tags.Resolve);
            a.SetLookup(first);
            b.SetLookup(second);

            Assert.True(a.TryGet(56, out var idA));
            Assert.True(b.TryGet(56, out var idB));
            Assert.NotEqual(idA, idB);
        }
    }
}
