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

using DataLogLib.Services.EventLog;
using Xunit;

namespace DataLogLib.Tests
{
    public class EventSortTextTests
    {
        private static readonly DateTime SharedTime = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void KeyBytes_orders_like_EventSortValues_Compare_for_sample_labels()
        {
            var labels = new[]
            {
                "Pump",
                "pump",
                "alarm 2",
                "alarm 10",
                "alarm 10a",
                "Event type name",
                "Line A",
                "Line B",
                "Namespace::Boiler",
                "zzz",
                "aaa",
                "item1",
                "item02",
                "item2"
            };

            AssertPairwise(labels, ascending: true);
            AssertPairwise(labels, ascending: false);
        }

        [Fact]
        public void KeyBytes_orders_random_pairs_like_Compare()
        {
            var random = new Random(42);
            for (var attempt = 0; attempt < 200; attempt += 1)
            {
                var left = RandomLabel(random);
                var right = RandomLabel(random);
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                {
                    continue;
                }

                var leftValue = new EventSortValue(SharedTime, "tie", null, left);
                var rightValue = new EventSortValue(SharedTime, "tie", null, right);
                var compare = EventSortValues.Compare(leftValue, rightValue, ascending: true);
                var keyCompare = CompareKeyBytes(EventSortText.KeyBytes(left), EventSortText.KeyBytes(right));
                Assert.Equal(Math.Sign(compare), Math.Sign(keyCompare));
            }
        }

        private static void AssertPairwise(IReadOnlyList<string> labels, bool ascending)
        {
            for (var leftIndex = 0; leftIndex < labels.Count; leftIndex += 1)
            {
                for (var rightIndex = 0; rightIndex < labels.Count; rightIndex += 1)
                {
                    if (leftIndex == rightIndex)
                    {
                        continue;
                    }

                    var left = labels[leftIndex];
                    var right = labels[rightIndex];
                    var leftValue = new EventSortValue(SharedTime, "tie", null, left);
                    var rightValue = new EventSortValue(SharedTime, "tie", null, right);
                    var compare = EventSortValues.Compare(leftValue, rightValue, ascending);
                    var keyCompare = CompareKeyBytes(EventSortText.KeyBytes(left), EventSortText.KeyBytes(right));
                    if (!ascending)
                    {
                        keyCompare = -keyCompare;
                    }

                    Assert.Equal(Math.Sign(compare), Math.Sign(keyCompare));
                }
            }
        }

        private static int CompareKeyBytes(byte[] left, byte[] right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index += 1)
            {
                var diff = left[index].CompareTo(right[index]);
                if (diff != 0)
                {
                    return diff;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static string RandomLabel(Random random)
        {
            var words = new[] { "alarm", "Pump", "Line", "msg", "10", "2", "Boiler", "", "A", "aa" };
            var count = random.Next(0, 4);
            if (count == 0)
            {
                return random.Next(3) == 0 ? "" : words[random.Next(words.Length)];
            }

            var parts = new List<string>();
            for (var index = 0; index < count; index += 1)
            {
                parts.Add(words[random.Next(words.Length)]);
                if (random.Next(4) == 0)
                {
                    parts.Add(random.Next(100).ToString());
                }
            }

            return string.Join(" ", parts);
        }
    }
}
