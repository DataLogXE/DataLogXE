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
using S7CommPlusDriver;
using S7CommPlusDriver.Symbols;
using Xunit;

namespace DataLogLib.Tests.S7CommPlus
{
    public class S7CommPlusCommentTests
    {
        /// <summary>Interface and comments of a captured explore response, decompressed like the connection does.</summary>
        private static S7CommentSet Load(string hex)
        {
            var res = ExploreResponse.DeserializeFromPdu(new MemoryStream(Convert.FromHexString(hex)), true);
            Assert.NotNull(res);
            string interfaceXml = null, commentsXml = null;
            foreach (var obj in res.Objects)
            {
                if (obj.Attributes.TryGetValue(Ids.DataInterface_InterfaceDescription, out var d)) interfaceXml = S7CommPlusConnection.DecompressBlobValue(d);
                if (obj.Attributes.TryGetValue(Ids.DataInterface_LineComments, out var c)) commentsXml = S7CommPlusConnection.DecompressBlobValue(c);
            }
            return interfaceXml == null ? null : S7CommentParser.Parse(interfaceXml, commentsXml);
        }

        /// <summary>
        /// The comment of a symbol path such as "myArrayOfStruct[4].a2", walked like SymbolBrowser does: ids from the
        /// interface, an index on the array, a scope for the comments of a UDT or FB.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CommentOf(S7CommentSet root, IReadOnlyDictionary<string, S7CommentSet> blocks, string path)
        {
            var ids = new List<(int Id, string Index)>();
            var scopes = new List<(Dictionary<string, Dictionary<string, string>>, int)> { (root.Comments, 0) };
            var node = root.Interface;
            if (node.BlockName != null) scopes.Add((blocks[node.BlockName].Comments, 0));
            foreach (var part in path.Split('.'))
            {
                int bracket = part.IndexOf('[');
                string name = bracket < 0 ? part : part[..bracket];
                node = node.Find(name);
                Assert.NotNull(node);
                ids.Add((node.Id, bracket < 0 ? null : part[bracket..]));
                if (node.BlockName != null) scopes.Add((blocks[node.BlockName].Comments, ids.Count));
            }
            return S7CommentParser.Resolve(ids, scopes);
        }

        private static string Text(IReadOnlyDictionary<string, string> comments, string language) =>
            comments != null && comments.TryGetValue(language, out var text) ? text : null;

        [Fact]
        public void Interface_ids_are_read_from_the_description_and_not_from_the_position()
        {
            var db = Load(S7CommPlusCommentFixtures.DbTestSignals);
            var iface = db.Interface;
            Assert.Equal(new[] { 51, 52, 53, 54, 55, 59, 56, 57, 60 }, iface.Children.Values.Select(m => m.Id));
            Assert.Equal(17u, iface.Find("myTestUdt").Lid);
            Assert.Equal("testUdt_1", iface.Find("myTestUdt").BlockName);
            // Sub levels resolve through the SubParts of the DB, also for a structure inside a structure.
            Assert.Equal(53, iface.Find("myStructL1").Find("structL2").Find("structL3").Id);
            Assert.Equal(new[] { "c1", "c2" }, iface.Find("myStructL1").Find("structL2").Find("structL3").Children.Keys);
            // The members of the embedded UDT interface.
            Assert.Equal(new[] { "c1", "c2" }, iface.Find("myTestUdt").Find("structL2").Find("structL3").Children.Keys);
        }

        [Fact]
        public void Comments_of_nested_structures_come_in_all_languages()
        {
            var db = Load(S7CommPlusCommentFixtures.DbTestSignals);
            var c1 = CommentOf(db, null, "myStructL1.structL2.structL3.c1");
            Assert.Equal("Bit c1 in structure level 3", Text(c1, "en-US"));
            Assert.Equal("Bit c1 in Struktur Ebene 3", Text(c1, "de-DE"));
            Assert.Equal("Status: signal generation enabled", Text(CommentOf(db, null, "Enable"), "en-US"));
            Assert.Null(Text(CommentOf(db, null, "Enable"), "de-DE"));
        }

        [Fact]
        public void An_element_override_replaces_only_its_languages()
        {
            var db = Load(S7CommPlusCommentFixtures.DbTestSignals);
            var element4 = CommentOf(db, null, "myArrayOfStruct[4].a2");
            Assert.Equal("override comment - only en!", Text(element4, "en-US"));
            // TIA shows the German text of the declaration for this element.
            Assert.Equal("Bit a2 in Struktur Ebene 1", Text(element4, "de-DE"));
            var element3 = CommentOf(db, null, "myArrayOfStruct[3].a2");
            Assert.Equal("Bit a2 in structure level 1", Text(element3, "en-US"));
        }

        [Fact]
        public void Udt_members_take_the_udt_comments_unless_the_db_overrides_them()
        {
            var db = Load(S7CommPlusCommentFixtures.DbTestSignals);
            var blocks = new Dictionary<string, S7CommentSet> { ["testUdt_1"] = Load(S7CommPlusCommentFixtures.UdtTestUdt1) };
            Assert.Equal("Bool-Signal a1 (Ebene 1)", Text(CommentOf(db, blocks, "myTestUdt.a1"), "de-DE"));
            Assert.Equal("Bool signal a2 (level 1) override", Text(CommentOf(db, blocks, "myTestUdt.a2"), "en-US"));
            Assert.Equal("Bool signal c2 (level 3)", Text(CommentOf(db, blocks, "myTestUdt.structL2.structL3.c2"), "en-US"));
            Assert.Equal("Nested structure, level 3 de override", Text(CommentOf(db, blocks, "myTestUdt.structL2.structL3"), "de-DE"));
        }

        [Fact]
        public void An_instance_db_takes_the_comments_of_its_fb()
        {
            var idb = Load(S7CommPlusCommentFixtures.InstanceDbTestSignalGenerator);
            Assert.Equal("FB_TestSignalGenerator", idb.Interface.BlockName);
            // Input and Static of the FB, Temp and Constant are not part of the instance.
            Assert.Contains("freqLow", idb.Interface.Children.Keys);
            Assert.Contains("time_s", idb.Interface.Children.Keys);
            Assert.DoesNotContain("cycle", idb.Interface.Children.Keys);
            var blocks = new Dictionary<string, S7CommentSet> { ["FB_TestSignalGenerator"] = Load(S7CommPlusCommentFixtures.FbTestSignalGenerator) };
            Assert.Equal("Frequency of the low group [Hz]", Text(CommentOf(idb, blocks, "freqLow"), "en-US"));
            Assert.Null(CommentOf(idb, blocks, "time_s"));
        }

        [Fact]
        public void Memory_area_tags_are_keyed_by_their_lid()
        {
            // Regression: the dictionaries of the tag interface (0xCE9B821B) and tag comments (0xE2729EA1) have the
            // high bit set, ZStream.adler came sign-extended and nothing was decompressed.
            var m = Load(S7CommPlusCommentFixtures.MArea);
            Assert.NotNull(m);
            Assert.Equal(15, m.Interface.Find("m1").Id);
            Assert.Equal("Komm de", Text(CommentOf(m, null, "m1"), "de-DE"));
            Assert.Null(Text(CommentOf(m, null, "m1"), "en-US"));
            Assert.Equal("Kommentar en M6.3", Text(CommentOf(m, null, "m2"), "en-US"));
        }

        [Fact]
        public void Without_read_access_the_plc_sends_no_interface()
        {
            Assert.Null(Load(S7CommPlusCommentFixtures.DbTestSignalsHmiAccess));
        }

        [Fact]
        public void Keys_go_from_all_indexes_to_none()
        {
            var keys = S7CommentParser.Keys(new List<(int, string)> { (60, "[4]"), (53, null), (54, "[2,1]"), (51, null) }, 0).ToList();
            Assert.Equal(new[] { "60[4]:53:54[2,1]:51", "60:53:54[2,1]:51", "60[4]:53:54:51", "60:53:54:51" }, keys);
            Assert.Equal(new[] { "54[2,1]:51", "54:51" }, S7CommentParser.Keys(new List<(int, string)> { (60, "[4]"), (53, null), (54, "[2,1]"), (51, null) }, 2));
        }

        [Fact]
        public void Description_json_is_sorted_and_resolves_exactly_one_language()
        {
            var json = DataTagDescription.ToJson(new Dictionary<string, string> { ["en-US"] = "Motor on", ["de-DE"] = "Motor an", ["fr-FR"] = "" });
            Assert.Equal("{\"de-DE\":\"Motor an\",\"en-US\":\"Motor on\"}", json);
            Assert.Equal("Motor an", DataTagDescription.Resolve(json, "de-de"));
            Assert.Equal("Motor on", DataTagDescription.Resolve(json, ""));
            // Not translated: empty, no fallback to English.
            Assert.Equal("", DataTagDescription.Resolve(json, "fr-FR"));
            Assert.Equal("", DataTagDescription.Resolve(null, "de-DE"));
            Assert.Equal("", DataTagDescription.Resolve("plain text", "de-DE"));
            Assert.Null(DataTagDescription.ToJson(new Dictionary<string, string>()));
            Assert.Null(DataTagDescription.ToJson(null));
        }
    }
}
