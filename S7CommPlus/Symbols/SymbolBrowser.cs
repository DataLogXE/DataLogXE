#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 *
 * Type info walk based on S7CommPlusDriver (Browser.cs, getPlcTagBySymbol) by Thomas Wiens.
 /****************************************************************************/
#endregion

using S7CommPlusDriver.Crc;
using Serilog;

namespace S7CommPlusDriver.Symbols
{
    /// <summary>A resolved, CRC secured variable of the PLC program.</summary>
    public sealed class S7Symbol
    {
        /// <summary>Symbolic path in TIA notation, e.g. "DB_Test".Motor[3].Speed</summary>
        public string Path;
        /// <summary>Block (DB name) or memory area (I, Q, M) the symbol belongs to.</summary>
        public string Root;
        public uint AccessArea;
        public uint AccessSubArea;
        public uint[] Lids;
        public uint SymbolCrc;
        public uint Softdatatype;
        /// <summary>Comment per culture ("en-US" -> text) when the browse read comments, otherwise null.</summary>
        public IReadOnlyDictionary<string, string> Comments;

        public ItemAddress CreateAddress()
        {
            var adr = new ItemAddress(AccessArea, AccessSubArea) { SymbolCrc = SymbolCrc };
            adr.LID.AddRange(Lids);
            return adr;
        }

        public string AccessString
        {
            get
            {
                var parts = new List<string> { AccessArea.ToString("X") };
                parts.AddRange(Lids.Select(l => l.ToString("X")));
                return string.Join(".", parts);
            }
        }

        public override string ToString() => $"{Path} [{AccessString}] crc={SymbolCrc:X8} type={Softdatatype}";
    }

    /// <summary>Thrown when the PLC could not be asked (connection or protocol error).</summary>
    public sealed class S7CommunicationException : Exception
    {
        public int ErrorCode { get; }

        public S7CommunicationException(int errorCode, string message) : base(message + ": " + S7Consts.ErrorText(errorCode))
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class BrowseStatistics
    {
        public int Roots;
        public int Symbols;
        /// <summary>Variables whose type cannot be logged as a number (strings, pointers ...); they are emitted all the same.</summary>
        public int NotLoggable;
        public int TruncatedArrays;
        public int SkippedArrayElements;
        public int TypeInfoRequests;
        /// <summary>Data blocks and areas whose interface description the PLC sent (none without read access).</summary>
        public int CommentRoots;
        public int CommentRequests;
        /// <summary>Variables with a comment in at least one language.</summary>
        public int CommentedSymbols;
        public readonly SortedSet<string> CommentLanguages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves symbolic paths to CRC secured item addresses and browses the PLC program.
    /// Type information is requested per type (scoped explore) and cached until
    /// <see cref="Invalidate"/> is called, e.g. after the PLC rejected a CRC because the program changed.
    /// Not thread-safe: use it from one worker thread.
    /// </summary>
    public sealed class SymbolBrowser
    {
        private const uint IAreaTypeInfo = 0x90010000;
        private const uint QAreaTypeInfo = 0x90020000;
        private const uint MAreaTypeInfo = 0x90030000;
        private const int MaxDepth = 40;

        private readonly S7CommPlusConnection _conn;
        private readonly ILogger _log;
        private readonly Dictionary<uint, PObject> _types = new Dictionary<uint, PObject>();
        private Dictionary<string, RootInfo> _roots;

        public BrowseStatistics LastStatistics { get; private set; }

        /// <summary>Arrays with more elements are cut at this count during a browse; 0 takes every element.</summary>
        public int MaxArrayElements { get; set; }

        /// <summary>Pause before each type information request, so a browse of a large program does not load the CPU.</summary>
        public int RequestPauseMs { get; set; }

        /// <summary>Read the comments of the variables in <see cref="BrowseAll"/> (one request per data block, area, UDT and FB).</summary>
        public bool ReadComments { get; set; }

        private Dictionary<string, uint> _blocks;
        private readonly Dictionary<string, S7CommentSet> _blockComments = new Dictionary<string, S7CommentSet>(StringComparer.Ordinal);

        private sealed class RootInfo
        {
            public string Name;
            public uint AccessArea;
            public uint AccessSubArea;
            public uint TypeInfoRelId;
            public bool IsArea;
        }

        private static readonly RootInfo[] Areas =
        {
            new RootInfo { Name = "M", AccessArea = Ids.NativeObjects_theMArea_Rid, AccessSubArea = Ids.ControllerArea_ValueActual, TypeInfoRelId = MAreaTypeInfo, IsArea = true },
            new RootInfo { Name = "Q", AccessArea = Ids.NativeObjects_theQArea_Rid, AccessSubArea = Ids.ControllerArea_ValueActual, TypeInfoRelId = QAreaTypeInfo, IsArea = true },
            new RootInfo { Name = "I", AccessArea = Ids.NativeObjects_theIArea_Rid, AccessSubArea = Ids.ControllerArea_ValueActual, TypeInfoRelId = IAreaTypeInfo, IsArea = true },
        };

        public SymbolBrowser(S7CommPlusConnection conn, ILogger log = null)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
            _log = log ?? Log.Logger;
        }

        /// <summary>Drops all cached type information and the block list.</summary>
        public void Invalidate()
        {
            _types.Clear();
            _roots = null;
            _blocks = null;
            _blockComments.Clear();
        }

        /// <summary>Names of all data blocks with type information (cached).</summary>
        public IReadOnlyList<string> GetDataBlockNames()
        {
            EnsureRoots();
            return _roots.Values.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>True for softdatatypes that can be archived as a number.</summary>
        public static bool IsArchivable(uint softdatatype)
        {
            switch (softdatatype)
            {
                case Softdatatype.S7COMMP_SOFTDATATYPE_BOOL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_BYTE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_CHAR:
                case Softdatatype.S7COMMP_SOFTDATATYPE_WORD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_INT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DWORD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_REAL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_TIMEOFDAY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_TIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_S5TIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LREAL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_ULINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LWORD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_USINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_UINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_UDINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_SINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_WCHAR:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LTIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LTOD:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Date and time types, logged as milliseconds since 1970-01-01.</summary>
        public static bool IsDateType(uint softdatatype) =>
            softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DATE ||
            softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME ||
            softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_LDT ||
            softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DTL;

        /// <summary>True when the value can be stored in the archive (numbers, bool, durations, date and time).</summary>
        public static bool IsLoggable(uint softdatatype) => IsArchivable(softdatatype) || IsDateType(softdatatype);

        /// <summary>A member with type information that is still one value: DTL is a structure on the PLC, logged as one time stamp.</summary>
        private static bool HasMembers(PVartypeListElement vte) =>
            vte.OffsetInfoType.HasRelation() && vte.Softdatatype != Softdatatype.S7COMMP_SOFTDATATYPE_DTL;

        public static bool IsBoolType(uint softdatatype) =>
            softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BOOL || softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL;

        #region resolve

        /// <summary>
        /// Resolves a symbolic path. Returns null and an error text when the symbol does not exist
        /// (or is not a single archivable value); throws <see cref="S7CommunicationException"/> when the PLC could not be asked.
        /// </summary>
        public S7Symbol Resolve(string path, out string error)
        {
            error = null;
            List<PathToken> tokens;
            try
            {
                tokens = SymbolPath.Parse(path);
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return null;
            }

            EnsureRoots();
            if (tokens[0].Indexes == null && _roots.TryGetValue(tokens[0].Name, out var db))
            {
                if (tokens.Count < 2)
                {
                    error = "A data block is not a single value";
                    return null;
                }
                return ResolveIn(db, tokens, 1, out error);
            }

            string firstError = null;
            foreach (var area in Areas)
            {
                var sym = ResolveIn(area, tokens, 0, out string areaError);
                if (sym != null) return sym;
                firstError ??= areaError;
            }
            error = $"Symbol not found: '{tokens[0].Name}' is neither a data block nor a PLC tag";
            return null;
        }

        private S7Symbol ResolveIn(RootInfo root, List<PathToken> tokens, int start, out string error)
        {
            error = null;
            var lids = new List<uint>();
            var crc = new List<CrcSegment>();
            var pathParts = new List<string>();
            pathParts.Add(start == 1 ? SymbolPath.FormatRoot(root.Name) : null);

            PObject type = GetType(root.TypeInfoRelId);
            if (type == null)
            {
                error = $"No type information for '{root.Name}'";
                return null;
            }

            for (int i = start; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                bool last = i == tokens.Count - 1;
                var names = type.VarnameList?.Names;
                var elements = type.VartypeList?.Elements;
                int idx = names == null ? -1 : names.IndexOf(tok.Name);
                if (idx < 0 && names != null)
                {
                    idx = names.FindIndex(n => string.Equals(n, tok.Name, StringComparison.OrdinalIgnoreCase));
                }
                if (idx < 0 || elements == null || idx >= elements.Count)
                {
                    error = $"Member '{tok.Name}' not found";
                    return null;
                }
                string memberName = names[idx];
                var vte = elements[idx];
                var oit = vte.OffsetInfoType;
                lids.Add(vte.LID);
                string part = (i == 0 ? SymbolPath.FormatRoot(memberName) : SymbolPath.FormatName(memberName));

                if (oit.Is1Dim() || oit.IsMDim())
                {
                    if (tok.Indexes == null)
                    {
                        error = $"'{memberName}' is an array, an element index is required";
                        return null;
                    }
                    if (!TryGetArrayElementId(vte, tok.Indexes, out uint elementId, out int lowerBound, out error))
                    {
                        return null;
                    }
                    crc.Add(new CrcSegment(memberName, vte.Softdatatype, true, lowerBound, vte.SymbolCrc));
                    lids.Add(elementId);
                    part += SymbolPath.FormatIndexes(tok.Indexes);
                    if (oit.HasRelation() && (HasMembers(vte) || !last))
                    {
                        lids.Add(1);
                        if (last)
                        {
                            error = $"'{memberName}' is an array of structures, a member is required";
                            return null;
                        }
                        type = GetType(((IOffsetInfoType_Relation)oit).GetRelationId());
                        if (type == null)
                        {
                            error = $"No type information for '{memberName}'";
                            return null;
                        }
                        pathParts.Add(part);
                        continue;
                    }
                    if (!last)
                    {
                        error = $"'{memberName}' has no members";
                        return null;
                    }
                }
                else
                {
                    if (tok.Indexes != null)
                    {
                        error = $"'{memberName}' is not an array";
                        return null;
                    }
                    crc.Add(new CrcSegment(memberName, vte.Softdatatype, inner: vte.SymbolCrc));
                    if (oit.HasRelation() && (HasMembers(vte) || !last))
                    {
                        if (last)
                        {
                            error = $"'{memberName}' is a structure, not a single value";
                            return null;
                        }
                        type = GetType(((IOffsetInfoType_Relation)oit).GetRelationId());
                        if (type == null)
                        {
                            error = $"No type information for '{memberName}'";
                            return null;
                        }
                        pathParts.Add(part);
                        continue;
                    }
                    if (!last)
                    {
                        error = $"'{memberName}' has no members";
                        return null;
                    }
                }

                pathParts.Add(part);
                return new S7Symbol
                {
                    Path = string.Join(".", pathParts.Where(p => p != null)),
                    Root = root.Name,
                    AccessArea = root.AccessArea,
                    AccessSubArea = root.AccessSubArea,
                    Lids = lids.ToArray(),
                    SymbolCrc = SymbolCrc.Compute(crc),
                    Softdatatype = vte.Softdatatype,
                };
            }
            error = "Incomplete symbol";
            return null;
        }

        internal static bool TryGetArrayElementId(PVartypeListElement vte, int[] indexes, out uint id, out int lowerBound, out string error)
        {
            id = 0;
            lowerBound = 0;
            error = null;
            var oit = vte.OffsetInfoType;
            if (oit.Is1Dim())
            {
                var a = (IOffsetInfoType_1Dim)oit;
                lowerBound = a.GetArrayLowerBounds();
                uint count = a.GetArrayElementCount();
                if (indexes.Length != 1)
                {
                    error = "One-dimensional array needs exactly one index";
                    return false;
                }
                long pos = (long)indexes[0] - lowerBound;
                if (pos < 0 || pos >= count)
                {
                    error = $"Index {indexes[0]} out of range [{lowerBound}..{lowerBound + count - 1}]";
                    return false;
                }
                id = (uint)pos;
                return true;
            }

            var m = (IOffsetInfoType_MDim)oit;
            lowerBound = m.GetArrayLowerBounds();
            uint[] counts = m.GetMdimArrayElementCount();
            int[] lbs = m.GetMdimArrayLowerBounds();
            int dims = counts.Count(c => c > 0);
            if (indexes.Length != dims)
            {
                error = $"Array needs {dims} indexes";
                return false;
            }
            // Path index j (left to right) belongs to dimension dims-1-j; dimension 0 varies fastest.
            ulong stride = 1;
            ulong linear = 0;
            for (int d = 0; d < dims; d++)
            {
                int index = indexes[dims - 1 - d];
                long pos = (long)index - lbs[d];
                if (pos < 0 || pos >= counts[d])
                {
                    error = $"Index {index} out of range [{lbs[d]}..{lbs[d] + counts[d] - 1}]";
                    return false;
                }
                linear += (ulong)pos * stride;
                ulong dimSize = counts[d];
                if (d == 0 && IsBoolType(vte.Softdatatype) && dimSize % 8 != 0)
                {
                    // Rows of bool arrays (BBool optimized, Bool in standard blocks) are padded to whole bytes.
                    dimSize += 8 - dimSize % 8;
                }
                stride *= dimSize;
            }
            id = (uint)linear;
            return true;
        }

        #endregion

        #region browse

        /// <summary>
        /// Browses all data blocks and the I/Q/M areas; every variable (every array element) is passed to
        /// <paramref name="emit"/>, also those whose type cannot be logged (see <see cref="IsLoggable"/>).
        /// </summary>
        public BrowseStatistics BrowseAll(Action<S7Symbol> emit, Action<string> progress, CancellationToken ct)
        {
            var stats = new BrowseStatistics();
            LastStatistics = stats;
            EnsureRoots();
            // Comment ids are only valid for the interface read in the same browse.
            _blocks = null;
            _blockComments.Clear();
            var dbs = _roots.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var all = dbs.Concat(Areas.Reverse()).ToList();
            foreach (var root in all)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"{(root.IsArea ? "Area" : "Data block")} {stats.Roots + 1} of {all.Count}: {root.Name} ({stats.Symbols} values so far)");
                BrowseRoot(root, emit, stats, ct);
                stats.Roots++;
            }
            return stats;
        }

        private void BrowseRoot(RootInfo root, Action<S7Symbol> emit, BrowseStatistics stats, CancellationToken ct)
        {
            PObject type = GetType(root.TypeInfoRelId);
            if (type == null)
            {
                return;
            }
            var ctx = new WalkContext
            {
                Root = root,
                Emit = emit,
                Stats = stats,
                Ct = ct,
            };
            S7InterfaceMember iface = null;
            if (ReadComments)
            {
                var set = LoadComments(root.AccessArea, stats);
                if (set?.Interface != null)
                {
                    stats.CommentRoots++;
                    iface = set.Interface;
                    ctx.Scopes.Add((set.Comments, 0));
                    if (iface.BlockName != null && GetBlockComments(iface.BlockName, stats) is S7CommentSet fb)
                    {
                        ctx.Scopes.Add((fb.Comments, 0));
                    }
                }
            }
            Walk(ctx, type, root.IsArea ? null : SymbolPath.FormatRoot(root.Name), 0, iface);
        }

        private S7CommentSet LoadComments(uint relId, BrowseStatistics stats)
        {
            if (RequestPauseMs > 0)
            {
                Thread.Sleep(RequestPauseMs);
            }
            stats.CommentRequests++;
            int res = _conn.GetInterfaceWithComments(relId, out string interfaceXml, out string commentsXml);
            if (res != 0)
            {
                throw new S7CommunicationException(res, $"Reading the comments of 0x{relId:X8} failed");
            }
            if (interfaceXml == null)
            {
                return null;
            }
            try
            {
                return S7CommentParser.Parse(interfaceXml, commentsXml);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "S7 browse: the comments of 0x{RelId:X8} could not be read", relId);
                return null;
            }
        }

        /// <summary>Comments of a UDT or FB by name, read once per browse; null when the block is unknown or has no interface.</summary>
        private S7CommentSet GetBlockComments(string blockName, BrowseStatistics stats)
        {
            if (string.IsNullOrEmpty(blockName)) return null;
            if (_blockComments.TryGetValue(blockName, out var cached)) return cached;
            if (_blocks == null)
            {
                int res = _conn.GetBlockNames(out var blocks);
                if (res != 0)
                {
                    throw new S7CommunicationException(res, "Reading the list of blocks failed");
                }
                _blocks = blocks;
            }
            S7CommentSet set = null;
            if (_blocks.TryGetValue(blockName, out uint relId))
            {
                set = LoadComments(relId, stats);
            }
            _blockComments[blockName] = set;
            return set;
        }

        private sealed class WalkContext
        {
            public RootInfo Root;
            public Action<S7Symbol> Emit;
            public BrowseStatistics Stats;
            public CancellationToken Ct;
            public readonly List<uint> Lids = new List<uint>();
            public readonly List<CrcSegment> Crc = new List<CrcSegment>();
            /// <summary>Interface ids of the current variable (with the index of an array element) for the comments.</summary>
            public readonly List<(int Id, string Index)> CommentPath = new List<(int Id, string Index)>();
            /// <summary>Comment sets that apply, outermost first, each with the position in <see cref="CommentPath"/> its keys start at.</summary>
            public readonly List<(Dictionary<string, Dictionary<string, string>> Comments, int Start)> Scopes = new List<(Dictionary<string, Dictionary<string, string>>, int)>();
            /// <summary>Depth below a member without interface information: no comments there.</summary>
            public int Unknown;
        }

        private void Walk(WalkContext ctx, PObject type, string prefix, int depth, S7InterfaceMember iface)
        {
            if (depth > MaxDepth) return;
            var names = type.VarnameList?.Names;
            var elements = type.VartypeList?.Elements;
            if (names == null || elements == null) return;

            for (int i = 0; i < elements.Count && i < names.Count; i++)
            {
                if ((i & 63) == 0) ctx.Ct.ThrowIfCancellationRequested();
                var vte = elements[i];
                var oit = vte.OffsetInfoType;
                string name = names[i];
                string path = prefix == null ? SymbolPath.FormatRoot(name) : prefix + "." + SymbolPath.FormatName(name);
                int lidMark = ctx.Lids.Count;
                ctx.Lids.Add(vte.LID);
                var member = EnterComment(ctx, iface, name);
                int scopeMark = ctx.Scopes.Count;
                if (member?.BlockName != null && GetBlockComments(member.BlockName, ctx.Stats) is S7CommentSet block)
                {
                    ctx.Scopes.Add((block.Comments, ctx.CommentPath.Count));
                }

                if (oit.Is1Dim() || oit.IsMDim())
                {
                    int lowerBound = oit.Is1Dim() ? ((IOffsetInfoType_1Dim)oit).GetArrayLowerBounds() : ((IOffsetInfoType_MDim)oit).GetArrayLowerBounds();
                    ctx.Crc.Add(new CrcSegment(name, vte.Softdatatype, true, lowerBound, vte.SymbolCrc));
                    PObject elementType = null;
                    if (HasMembers(vte))
                    {
                        elementType = GetType(((IOffsetInfoType_Relation)oit).GetRelationId());
                    }
                    if (!HasMembers(vte) || elementType != null)
                    {
                        WalkArray(ctx, vte, path, elementType, depth, member);
                    }
                    ctx.Crc.RemoveAt(ctx.Crc.Count - 1);
                }
                else
                {
                    ctx.Crc.Add(new CrcSegment(name, vte.Softdatatype, inner: vte.SymbolCrc));
                    if (HasMembers(vte))
                    {
                        var sub = GetType(((IOffsetInfoType_Relation)oit).GetRelationId());
                        if (sub != null)
                        {
                            Walk(ctx, sub, path, depth + 1, member);
                        }
                    }
                    else
                    {
                        EmitLeaf(ctx, path, vte.Softdatatype);
                    }
                    ctx.Crc.RemoveAt(ctx.Crc.Count - 1);
                }
                ctx.Lids.RemoveRange(lidMark, ctx.Lids.Count - lidMark);
                ctx.Scopes.RemoveRange(scopeMark, ctx.Scopes.Count - scopeMark);
                LeaveComment(ctx, member);
            }
        }

        /// <summary>Goes down to the member <paramref name="name"/> of the interface; null below a member that is not described.</summary>
        private static S7InterfaceMember EnterComment(WalkContext ctx, S7InterfaceMember parent, string name)
        {
            var member = ctx.Unknown == 0 ? parent?.Find(name) : null;
            if (member == null)
            {
                ctx.Unknown++;
                return null;
            }
            ctx.CommentPath.Add((member.Id, null));
            return member;
        }

        private static void LeaveComment(WalkContext ctx, S7InterfaceMember member)
        {
            if (member == null)
            {
                ctx.Unknown--;
            }
            else
            {
                ctx.CommentPath.RemoveAt(ctx.CommentPath.Count - 1);
            }
        }

        private void WalkArray(WalkContext ctx, PVartypeListElement vte, string path, PObject elementType, int depth, S7InterfaceMember member)
        {
            var oit = vte.OffsetInfoType;
            uint total;
            uint[] counts;
            int[] lbs;
            if (oit.Is1Dim())
            {
                var a = (IOffsetInfoType_1Dim)oit;
                total = a.GetArrayElementCount();
                counts = new[] { total };
                lbs = new[] { a.GetArrayLowerBounds() };
            }
            else
            {
                var m = (IOffsetInfoType_MDim)oit;
                total = m.GetArrayElementCount();
                counts = m.GetMdimArrayElementCount().Where(c => c > 0).ToArray();
                lbs = m.GetMdimArrayLowerBounds().Take(counts.Length).ToArray();
            }
            if (counts.Length == 0) return;

            uint limit = total;
            if (MaxArrayElements > 0 && total > MaxArrayElements)
            {
                limit = (uint)MaxArrayElements;
                ctx.Stats.TruncatedArrays++;
                ctx.Stats.SkippedArrayElements += (int)(total - limit);
                _log.Information("S7 browse: array {Path} has {Count} elements, only the first {Limit} are taken", path, total, limit);
            }

            int dims = counts.Length;
            var pos = new uint[dims];               // pos[0] varies fastest
            var indexes = new int[dims];             // path order: highest dimension first
            for (uint n = 0; n < limit; n++)
            {
                if ((n & 255) == 0) ctx.Ct.ThrowIfCancellationRequested();
                for (int d = 0; d < dims; d++)
                {
                    indexes[dims - 1 - d] = (int)(pos[d] + lbs[d]);
                }
                if (!TryGetArrayElementId(vte, indexes, out uint elementId, out _, out _))
                {
                    break;
                }
                string elementPath = path + SymbolPath.FormatIndexes(indexes);
                int mark = ctx.Lids.Count;
                ctx.Lids.Add(elementId);
                if (member != null)
                {
                    // Comments of a single element carry its index in TIA notation: "60[4]:52".
                    var last = ctx.CommentPath[ctx.CommentPath.Count - 1];
                    ctx.CommentPath[ctx.CommentPath.Count - 1] = (last.Id, "[" + string.Join(",", indexes) + "]");
                }
                if (elementType != null)
                {
                    ctx.Lids.Add(1);
                    Walk(ctx, elementType, elementPath, depth + 1, member);
                }
                else
                {
                    EmitLeaf(ctx, elementPath, vte.Softdatatype);
                }
                ctx.Lids.RemoveRange(mark, ctx.Lids.Count - mark);
                if (member != null)
                {
                    var last = ctx.CommentPath[ctx.CommentPath.Count - 1];
                    ctx.CommentPath[ctx.CommentPath.Count - 1] = (last.Id, null);
                }

                for (int d = 0; d < dims; d++)
                {
                    pos[d]++;
                    if (pos[d] < counts[d]) break;
                    pos[d] = 0;
                }
            }
        }

        private void EmitLeaf(WalkContext ctx, string path, uint softdatatype)
        {
            ctx.Stats.Symbols++;
            if (!IsLoggable(softdatatype))
            {
                ctx.Stats.NotLoggable++;
            }
            Dictionary<string, string> comments = null;
            if (ctx.Unknown == 0 && ctx.Scopes.Count > 0 && ctx.CommentPath.Count > 0)
            {
                comments = S7CommentParser.Resolve(ctx.CommentPath, ctx.Scopes);
                if (comments != null)
                {
                    ctx.Stats.CommentedSymbols++;
                    foreach (var language in comments.Keys)
                    {
                        ctx.Stats.CommentLanguages.Add(language);
                    }
                }
            }
            ctx.Emit(new S7Symbol
            {
                Path = path,
                Root = ctx.Root.Name,
                AccessArea = ctx.Root.AccessArea,
                AccessSubArea = ctx.Root.AccessSubArea,
                Lids = ctx.Lids.ToArray(),
                SymbolCrc = SymbolCrc.Compute(ctx.Crc),
                Softdatatype = softdatatype,
                Comments = comments,
            });
        }

        #endregion

        #region type info

        private void EnsureRoots()
        {
            if (_roots != null) return;
            int res = _conn.GetListOfDatablocks(out var dbs);
            if (res != 0)
            {
                throw new S7CommunicationException(res, "Reading the list of data blocks failed");
            }
            var roots = new Dictionary<string, RootInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var db in dbs)
            {
                if (string.IsNullOrEmpty(db.db_name) || roots.ContainsKey(db.db_name)) continue;
                roots[db.db_name] = new RootInfo
                {
                    Name = db.db_name,
                    AccessArea = db.db_block_relid,
                    AccessSubArea = Ids.DB_ValueActual,
                    TypeInfoRelId = db.db_block_ti_relid,
                };
            }
            _roots = roots;
        }

        private PObject GetType(uint relId)
        {
            if (relId == 0) return null;
            if (_types.TryGetValue(relId, out var cached)) return cached;
            if (RequestPauseMs > 0)
            {
                Thread.Sleep(RequestPauseMs);
            }
            int res = _conn.GetTypeInformation(relId, out var objs);
            if (LastStatistics != null) LastStatistics.TypeInfoRequests++;
            if (res != 0)
            {
                throw new S7CommunicationException(res, $"Reading type information 0x{relId:X8} failed");
            }
            AddTypes(objs);
            if (!_types.TryGetValue(relId, out var obj))
            {
                // Remember the miss, e.g. an empty memory area has no type object.
                _types[relId] = null;
                return null;
            }
            return obj;
        }

        private void AddTypes(IEnumerable<PObject> objs)
        {
            if (objs == null) return;
            foreach (var o in objs)
            {
                if (o == null) continue;
                if (o.RelationId != 0 && (o.VartypeList != null || o.VarnameList != null) && !_types.ContainsKey(o.RelationId))
                {
                    _types[o.RelationId] = o;
                }
                AddTypes(o.GetObjects());
            }
        }

        #endregion
    }
}
