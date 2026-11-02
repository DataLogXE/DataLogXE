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
using DataLogService;
using Xunit;

namespace DataLogLib.Tests
{
    /// <summary>
    /// The status lines of the XML import name only what changed, so they fit the dialog without being cut.
    /// </summary>
    public class XmlImportStatusTextTests
    {
        [Fact]
        public void The_finished_check_names_only_the_changes()
        {
            var text = XmlImportSupervisor.FormatSync(new DataTagBrowseSyncProgress
            {
                Phase = "existing", Scanned = 593999, Total = 593999, Finished = true, Deleted = 54
            });

            Assert.Equal("Check existing tags … 54 deleted", text);
        }

        [Fact]
        public void A_check_without_changes_says_so()
        {
            var text = XmlImportSupervisor.FormatSync(new DataTagBrowseSyncProgress
            {
                Phase = "existing", Scanned = 10, Total = 10, Finished = true
            });

            Assert.Equal("Check existing tags … no changes", text);
        }

        [Fact]
        public void A_running_check_shows_the_progress_and_the_changes_so_far()
        {
            var text = XmlImportSupervisor.FormatSync(new DataTagBrowseSyncProgress
            {
                Phase = "existing", Scanned = 300000, Total = 593999, Restored = 3, Updated = 89531
            });

            Assert.Equal("Check existing tags … 300\u202F000 / 593\u202F999 · 3 restored · 89\u202F531 updated", text);
        }

        [Fact]
        public void Adding_tags_names_the_created_ones()
        {
            var text = XmlImportSupervisor.FormatSync(new DataTagBrowseSyncProgress
            {
                Phase = "new", Scanned = 105493, Total = 105493, Finished = true, Created = 296
            });

            Assert.Equal("Add new tags … 296 created", text);
        }

        [Fact]
        public void A_long_file_name_loses_its_middle()
        {
            Assert.Equal("TiaLocalHome…512SP.OPCUA.xml", XmlImportSupervisor.ShortFileName("TiaLocalHomeProject.MyPlc1512SP.OPCUA.xml"));
            Assert.Equal("plc.xml", XmlImportSupervisor.ShortFileName("plc.xml"));
            Assert.Equal("file", XmlImportSupervisor.ShortFileName(" "));
        }
    }
}
