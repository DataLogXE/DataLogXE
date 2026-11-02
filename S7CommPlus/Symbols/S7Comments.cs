#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

using System.Xml.Linq;

namespace S7CommPlusDriver.Symbols
{
    /// <summary>
    /// A member of a block interface (DataInterface_InterfaceDescription) or a tag of a memory area, with the
    /// internal id the line comments refer to. The ids are only valid together with the comments read in the same
    /// request: TIA can renumber a block on a download.
    /// </summary>
    public sealed class S7InterfaceMember
    {
        /// <summary>Internal id (from 51 in blocks), for a tag of a memory area its LID.</summary>
        public int Id;
        public string Name;
        public uint Lid;
        /// <summary>UDT or FB whose own comments apply below this member (a UDT instance, a multi-instance, the FB of an instance DB).</summary>
        public string BlockName;
        public readonly Dictionary<string, S7InterfaceMember> Children = new Dictionary<string, S7InterfaceMember>(StringComparer.Ordinal);

        public S7InterfaceMember Find(string name)
        {
            if (name == null) return null;
            if (Children.TryGetValue(name, out var child)) return child;
            foreach (var kv in Children)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }
    }

    /// <summary>Comments of one block or area: key (id path such as "57:53:51", "60[4]:52", or the RefID of a tag) -> culture -> text.</summary>
    public sealed class S7CommentSet
    {
        public S7InterfaceMember Interface;
        public Dictionary<string, Dictionary<string, string>> Comments = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Parses the interface description and the line comments XML of the PLC.
    /// Blocks: &lt;BlockInterface&gt;&lt;Part&gt;&lt;Payload&gt;&lt;Root&gt;&lt;Member ID="51" Name="x" LID="9" SubPartIndex="0"/&gt; where
    /// SubPartIndex points into the SubParts of the part the member is declared in (structures, the embedded
    /// interface of a UDT or FB, sections of an FB); comments &lt;InterfaceLineComments&gt;&lt;Comment Path="57:53"&gt;&lt;DictEntry Language="de-DE"&gt;.
    /// Memory areas: &lt;IdentContainer&gt;&lt;Ident Name="m1" LID="15"&gt; with &lt;CommentDictionary&gt;&lt;TagLineComments&gt;&lt;Comment RefID="15"&gt;.
    /// </summary>
    public static class S7CommentParser
    {
        private static readonly HashSet<string> InstanceSections = new HashSet<string>(StringComparer.Ordinal) { "Input", "Output", "InOut", "Static" };

        public static S7InterfaceMember ParseInterface(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var doc = XDocument.Parse(xml);
            var root = new S7InterfaceMember { Name = "" };
            var top = doc.Root;
            if (top == null) return null;
            if (top.Name.LocalName == "IdentContainer")
            {
                foreach (var ident in top.Elements().Where(e => e.Name.LocalName == "Ident"))
                {
                    string name = (string)ident.Attribute("Name");
                    if (string.IsNullOrEmpty(name) || !uint.TryParse((string)ident.Attribute("LID"), out uint lid)) continue;
                    root.Children[name] = new S7InterfaceMember { Id = (int)lid, Name = name, Lid = lid };
                }
                return root;
            }
            var part = top.Elements().FirstOrDefault(e => e.Name.LocalName == "Part");
            if (part == null) return null;
            Fill(root, part, null, 0);
            return root;
        }

        /// <param name="ownerSubParts">The sub parts of the enclosing source part (DB, UDT, FB): the SubPartIndex of a
        /// member in a structure or section points there, a source part has its own.</param>
        private static void Fill(S7InterfaceMember node, XElement part, List<XElement> ownerSubParts, int depth)
        {
            if (depth > 64) return;
            var own = Child(part, "SubParts")?.Elements().Where(e => e.Name.LocalName == "Part").ToList();
            var subParts = own ?? ownerSubParts ?? new List<XElement>();
            if ((string)part.Attribute("Kind") == "Values")
            {
                // Instance DB: the interface is the one of its FB (or UDT), embedded as sub part.
                var source = subParts.FirstOrDefault(p => (string)p.Attribute("Kind") == "BlockSource" || (string)p.Attribute("Kind") == "DataTypeSource");
                if (source != null)
                {
                    node.BlockName = (string)source.Attribute("Block");
                    Fill(node, source, subParts, depth + 1);
                }
                return;
            }

            foreach (var m in Members(part))
            {
                string name = (string)m.Attribute("Name");
                if (string.IsNullOrEmpty(name) || !int.TryParse((string)m.Attribute("ID"), out int id)) continue;
                XElement target = null;
                if (int.TryParse((string)m.Attribute("SubPartIndex"), out int index) && index >= 0 && index < subParts.Count)
                {
                    target = subParts[index];
                }
                string targetKind = (string)target?.Attribute("Kind") ?? "";
                if (targetKind.EndsWith("Section", StringComparison.Ordinal))
                {
                    // FB sections: the members of Input, Output, InOut and Static are the variables of the instance.
                    if (InstanceSections.Contains(name)) Fill(node, target, subParts, depth + 1);
                    continue;
                }
                uint.TryParse((string)m.Attribute("LID"), out uint lid);
                var child = new S7InterfaceMember { Id = id, Name = name, Lid = lid };
                if (target != null)
                {
                    if (targetKind == "DataTypeSource" || targetKind == "BlockSource")
                    {
                        child.BlockName = (string)target.Attribute("Block");
                    }
                    Fill(child, target, subParts, depth + 1);
                }
                node.Children[name] = child;
            }
        }

        /// <summary>The declared members of a part: in Payload/Root, or in the (unnamed) Payload/Member wrapper.</summary>
        private static IEnumerable<XElement> Members(XElement part)
        {
            var payload = Child(part, "Payload");
            if (payload == null) yield break;
            foreach (var e in payload.Elements())
            {
                if (e.Name.LocalName == "Root" || (e.Name.LocalName == "Member" && e.Attribute("Name") == null))
                {
                    foreach (var m in e.Elements().Where(x => x.Name.LocalName == "Member")) yield return m;
                }
            }
        }

        private static XElement Child(XElement e, string localName) => e.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

        /// <summary>Line comments by Path (blocks) or RefID (memory areas); empty texts are left out.</summary>
        public static Dictionary<string, Dictionary<string, string>> ParseComments(string xml)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(xml)) return result;
            var doc = XDocument.Parse(xml);
            foreach (var c in doc.Descendants().Where(e => e.Name.LocalName == "Comment"))
            {
                string key = (string)c.Attribute("Path") ?? (string)c.Attribute("RefID");
                if (string.IsNullOrEmpty(key)) continue;
                var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in c.Elements().Where(e => e.Name.LocalName == "DictEntry"))
                {
                    string language = (string)entry.Attribute("Language");
                    string text = entry.Value;
                    if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(text)) continue;
                    texts[language] = text;
                }
                if (texts.Count > 0) result[key] = texts;
            }
            return result;
        }

        public static S7CommentSet Parse(string interfaceXml, string commentsXml)
        {
            return new S7CommentSet
            {
                Interface = ParseInterface(interfaceXml),
                Comments = ParseComments(commentsXml),
            };
        }

        /// <summary>
        /// The keys a variable can have a comment under, most specific first: with the indexes of all its arrays
        /// (an override for one element, e.g. "60[4]:52"), then with fewer indexes down to the comment of the
        /// declaration ("60:52").
        /// </summary>
        public static IEnumerable<string> Keys(IReadOnlyList<(int Id, string Index)> path, int start)
        {
            var arrays = new List<int>();
            for (int i = start; i < path.Count; i++)
            {
                if (path[i].Index != null) arrays.Add(i);
            }
            if (arrays.Count == 0)
            {
                yield return Key(path, start, null);
                yield break;
            }
            int full = (1 << Math.Min(arrays.Count, 30)) - 1;
            IEnumerable<int> masks = arrays.Count > 4
                ? new[] { full, 0 }
                : Enumerable.Range(0, full + 1).OrderByDescending(BitCount).ThenByDescending(m => m);
            foreach (int mask in masks)
            {
                var with = new HashSet<int>();
                for (int b = 0; b < arrays.Count; b++)
                {
                    if ((mask & (1 << b)) != 0) with.Add(arrays[b]);
                }
                yield return Key(path, start, with);
            }
        }

        private static int BitCount(int v)
        {
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }

        private static string Key(IReadOnlyList<(int Id, string Index)> path, int start, HashSet<int> withIndex)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < path.Count; i++)
            {
                if (i > start) sb.Append(':');
                sb.Append(path[i].Id);
                if (withIndex != null && withIndex.Contains(i)) sb.Append(path[i].Index);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The comment of a variable per language. The scopes are tried from the outermost (the block that holds the
        /// variable) inwards (the UDT or FB it is declared in), each with its keys from the most specific one; a
        /// language missing in a more specific entry comes from the next one, as TIA shows it.
        /// </summary>
        public static Dictionary<string, string> Resolve(
            IReadOnlyList<(int Id, string Index)> path,
            IReadOnlyList<(Dictionary<string, Dictionary<string, string>> Comments, int Start)> scopes)
        {
            Dictionary<string, string> result = null;
            foreach (var (comments, start) in scopes)
            {
                if (comments == null || comments.Count == 0 || start >= path.Count) continue;
                foreach (var key in Keys(path, start))
                {
                    if (!comments.TryGetValue(key, out var texts)) continue;
                    foreach (var kv in texts)
                    {
                        result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result.TryAdd(kv.Key, kv.Value);
                    }
                }
            }
            return result;
        }
    }
}
