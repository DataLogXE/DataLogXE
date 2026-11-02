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
using DataLogLib.Services.ArchiveAccess;
using Xunit;

namespace DataLogLib.Tests
{
    public class ArchiveInsertWorkerTests
    {
        [Fact]
        public void Values_of_a_failed_write_are_kept_and_written_with_the_next_try()
        {
            var archive = new FakeArchive { FailInserts = true };
            var worker = new ArchiveInsertWorker();
            worker.Init(archive);
            List<ArchiveValue> handed = [];
            worker.OnArchiveInsert += (_, values) => handed.AddRange(values);
            var time = DateTime.Now;
            worker.InsertValues([new ArchiveValue { Id = 1, Value = 2, Time = time, State = ValueState.Valid }]);

            // The archive is down: nothing reaches the actual values or the compression
            worker.Flush();
            Assert.Empty(ReadRaw(archive));
            Assert.False(worker.TryGetActual(1, out _, out _, out _));
            Assert.Empty(handed);

            archive.FailInserts = false;
            worker.Flush();

            Assert.Equal([2.0], ReadRaw(archive).Select(v => v.Value));
            Assert.True(worker.TryGetActual(1, out var actual, out _, out _));
            Assert.Equal(2, actual);
            Assert.Single(handed);
        }

        private static List<ArchiveValue> ReadRaw(FakeArchive archive)
        {
            List<ArchiveValue> values = [];
            archive.ReadRawValues(FakeArchive.Uid(1), DateTime.MinValue, DateTime.MaxValue, values.Add);
            return values;
        }
    }
}
