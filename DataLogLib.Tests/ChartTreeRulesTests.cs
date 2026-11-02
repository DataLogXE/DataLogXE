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
using Xunit;

namespace DataLogLib.Tests
{
    public class ChartTreeRulesTests
    {
        // Root 1 (points at itself) → folder 2 → folder 3 → chart 4; folder 5 below the root
        private static readonly Dictionary<int, int> ParentById = new()
        {
            [1] = 1,
            [2] = 1,
            [3] = 2,
            [4] = 3,
            [5] = 1
        };

        [Fact]
        public void Names_sort_without_regard_to_case_and_numbers_by_value()
        {
            string[] names = ["b10", "B2", "a", "Chart 10", "chart 9", "Chart 009a", "chart 1", "x01", "x1", "", "z-sub"];
            var sorted = names.OrderBy(n => n, Comparer<string>.Create(ChartTreeRules.CompareNames)).ToList();
            Assert.Equal(["", "a", "B2", "b10", "chart 1", "chart 9", "Chart 009a", "Chart 10", "x01", "x1", "z-sub"], sorted);
            Assert.Equal(0, ChartTreeRules.CompareNames("Abc", "aBC"));
            Assert.True(ChartTreeRules.CompareNames("a2", "a10") < 0);
            Assert.True(ChartTreeRules.CompareNames("a", "ab") < 0);
            Assert.True(ChartTreeRules.CompareNames(null, "a") < 0);
        }

        [Theory]
        [InlineData(2, 2, true)]
        [InlineData(2, 3, true)]
        [InlineData(2, 4, true)]
        [InlineData(3, 5, false)]
        [InlineData(3, 1, false)]
        [InlineData(5, 3, false)]
        public void Moving_into_the_own_subtree_is_a_cycle(int nodeId, int newParentId, bool expected)
        {
            Assert.Equal(expected, ChartTreeRules.WouldCreateCycle(ParentById, nodeId, newParentId));
        }

        [Fact]
        public void A_loop_in_the_stored_tree_ends_the_walk()
        {
            var broken = new Dictionary<int, int> { [1] = 1, [7] = 8, [8] = 7, [9] = 1 };

            Assert.False(ChartTreeRules.WouldCreateCycle(broken, 9, 7));
            Assert.True(ChartTreeRules.WouldCreateCycle(broken, 8, 7));
        }

        [Fact]
        public void An_unknown_parent_ends_the_walk()
        {
            Assert.False(ChartTreeRules.WouldCreateCycle(ParentById, 2, 99));
        }

        [Theory]
        [InlineData(ChartItemType.Root, true)]
        [InlineData(ChartItemType.Folder, true)]
        [InlineData(ChartItemType.Chart, false)]
        public void Only_root_and_folders_hold_nodes(ChartItemType type, bool expected)
        {
            Assert.Equal(expected, ChartTreeRules.IsContainer(type));
        }

        [Fact]
        public void Ids_keep_their_order_without_duplicates_and_invalid_values()
        {
            Assert.Equal([5, 3, 9], ChartTreeRules.NormalizeIds([5, 0, 3, 5, -1, 9, 3]));
            Assert.Empty(ChartTreeRules.NormalizeIds(null));
        }
    }
}
