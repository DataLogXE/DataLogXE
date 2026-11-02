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

namespace DataLogLib.OpcUaNodeset
{
    internal struct XmlImportRow
    {
        public string Address;
        public string Name;
        public string DisplayName;
        public string DataType;
        public int TypeId;
    }

    internal sealed class XmlImportCounters
    {
        private long _bytesRead;
        private int _variablesRead;
        private int _itemsAccepted;
        private int _waitingForWriter;
        private int _slowed;
        private long _workTicks;
        private long _phaseDone;
        private long _phaseTotal;

        public long FileBytes { get; set; }

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public int VariablesRead => Volatile.Read(ref _variablesRead);

        public int ItemsAccepted => Volatile.Read(ref _itemsAccepted);

        public bool WaitingForWriter => Volatile.Read(ref _waitingForWriter) != 0;

        public bool Slowed => Volatile.Read(ref _slowed) != 0;

        public long WorkTicks => Interlocked.Read(ref _workTicks);

        public long PhaseDone => Interlocked.Read(ref _phaseDone);

        public long PhaseTotal => Interlocked.Read(ref _phaseTotal);

        public void SetBytes(long value)
        {
            Interlocked.Exchange(ref _bytesRead, value);
            TickWork();
        }

        public void SetVariables(int value)
        {
            Volatile.Write(ref _variablesRead, value);
        }

        public void AddAccepted(int count)
        {
            Interlocked.Add(ref _itemsAccepted, count);
            TickWork();
        }

        public void SetAccepted(int value)
        {
            Volatile.Write(ref _itemsAccepted, value);
        }

        public void SetWaitingForWriter(bool value)
        {
            Volatile.Write(ref _waitingForWriter, value ? 1 : 0);
            TickWork();
        }

        public void SetSlowed(bool value)
        {
            Volatile.Write(ref _slowed, value ? 1 : 0);
        }

        public void TickWork()
        {
            Interlocked.Increment(ref _workTicks);
        }

        public void SetPhaseProgress(long done, long total)
        {
            Interlocked.Exchange(ref _phaseDone, done);
            Interlocked.Exchange(ref _phaseTotal, total);
            TickWork();
        }

        public void AddPhaseDone(long count)
        {
            if (count <= 0)
            {
                return;
            }

            var total = Interlocked.Read(ref _phaseTotal);
            while (true)
            {
                var current = Interlocked.Read(ref _phaseDone);
                var next = current + count;
                if (total > 0 && next > total)
                {
                    next = total;
                }

                if (Interlocked.CompareExchange(ref _phaseDone, next, current) == current)
                {
                    TickWork();
                    return;
                }
            }
        }
    }

    public static class OpcUaNodesetImportParser
    {
        internal static void Parse(
            string fileName,
            XmlImportTypeGraph types,
            BlockingCollection<List<XmlImportRow>> batches,
            ConcurrentBag<List<XmlImportRow>> pool,
            XmlImportCounters counters,
            CancellationToken cancellationToken,
            List<KeyValuePair<string, string>> renames = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("The nodeset file path is missing.", nameof(fileName));
            }

            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(batches);

            var batch = Rent(pool);
            var inNamespaceUris = false;
            var inUri = false;
            var inDataType = false;
            var aliasName = "";
            var inAlias = false;
            var inSuperReference = false;
            var dataTypeId = "";
            var variables = 0;
            var arrays = new XmlImportArrays();

            using var scanner = new NodesetByteScanner(fileName, cancellationToken);
            scanner.OnBytes = counters.SetBytes;
            counters.SetBytes(0);

            while (scanner.TryRead(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Kind == NodesetEventKind.Text)
                {
                    var text = item.Text?.Trim() ?? "";
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (inAlias && aliasName.Length > 0)
                    {
                        types.Aliases[aliasName] = text;
                    }
                    else if (inSuperReference && !string.IsNullOrEmpty(dataTypeId))
                    {
                        types.SuperTypes[dataTypeId] = text;
                    }
                    else if (inUri)
                    {
                        types.Namespaces.Add(text);
                    }

                    continue;
                }

                if (item.Kind == NodesetEventKind.End)
                {
                    if (item.Tag == NodesetTagKind.Alias)
                    {
                        inAlias = false;
                        aliasName = "";
                        scanner.WantText = inUri || inSuperReference;
                    }
                    else if (item.Tag == NodesetTagKind.Reference)
                    {
                        inSuperReference = false;
                        scanner.WantText = inUri || inAlias;
                    }
                    else if (item.Tag == NodesetTagKind.DataType)
                    {
                        inDataType = false;
                        dataTypeId = "";
                    }
                    else if (item.Tag == NodesetTagKind.Uri)
                    {
                        inUri = false;
                        scanner.WantText = inAlias || inSuperReference;
                    }
                    else if (item.Tag == NodesetTagKind.NamespaceUris)
                    {
                        inNamespaceUris = false;
                        inUri = false;
                    }

                    continue;
                }

                if (item.Tag == NodesetTagKind.Variable)
                {
                    variables++;
                    if ((variables & 63) == 0)
                    {
                        counters.SetVariables(variables);
                        counters.SetBytes(scanner.BytesRead);
                    }

                    if (!string.IsNullOrEmpty(item.NodeId))
                    {
                        var displayName = item.IsEmpty ? null : scanner.ReadFirstDisplayName();
                        var typeId = types.Resolve(item.DataType, false);
                        var address = types.ToAddress(item.NodeId);
                        arrays.AddNode(item.NodeId);
                        var row = new XmlImportRow
                        {
                            Address = address,
                            Name = XmlImportTypeGraph.ToName(address ?? ""),
                            DisplayName = displayName,
                            DataType = item.DataType ?? "",
                            TypeId = typeId < 0 ? 0 : typeId
                        };
                        // An element node of an array is dropped or kept at the end, by the type of the array; an
                        // array gives its element tags at the end, when every type and element node is known.
                        var deferred = arrays.TryDefer(item.NodeId, item.ParentNodeId, item.BrowseName, item.IsArray, row);
                        if (item.IsArray)
                        {
                            arrays.AddArray(item.NodeId, item.ArrayDimensions, item.BrowseName, row);
                        }
                        else if (!deferred && !string.IsNullOrEmpty(address))
                        {
                            batch.Add(row);
                            if (batch.Count >= XmlImportLimits.BatchSize)
                            {
                                PublishBatch(counters, scanner, variables, batches, batch, cancellationToken);
                                batch = Rent(pool);
                            }
                        }
                    }
                    else if (!item.IsEmpty)
                    {
                        scanner.SkipRestOfElement();
                    }

                    continue;
                }

                if (item.Tag == NodesetTagKind.NamespaceUris)
                {
                    inNamespaceUris = !item.IsEmpty;
                }
                else if (item.Tag == NodesetTagKind.Uri && inNamespaceUris)
                {
                    inUri = !item.IsEmpty;
                    scanner.WantText = inUri;
                }
                else if (item.Tag == NodesetTagKind.Alias)
                {
                    aliasName = item.Alias ?? "";
                    inAlias = !item.IsEmpty && aliasName.Length > 0;
                    scanner.WantText = inAlias;
                }
                else if (item.Tag == NodesetTagKind.DataType)
                {
                    dataTypeId = item.NodeId ?? "";
                    inDataType = !item.IsEmpty;
                }
                else if (item.Tag == NodesetTagKind.Reference && inDataType)
                {
                    inSuperReference = !item.IsEmpty
                        && item.ReferenceType == "HasSubtype"
                        && string.Equals(item.IsForward, "false", StringComparison.Ordinal);
                    scanner.WantText = inSuperReference;
                }
            }

            foreach (var row in arrays.Finish(types, renames))
            {
                batch.Add(row);
                if (batch.Count >= XmlImportLimits.BatchSize)
                {
                    PublishBatch(counters, scanner, variables, batches, batch, cancellationToken);
                    batch = Rent(pool);
                }
            }

            counters.SetVariables(variables);
            counters.SetBytes(scanner.BytesRead);
            if (batch.Count > 0)
            {
                PublishBatch(counters, scanner, variables, batches, batch, cancellationToken);
            }
            else
            {
                Return(pool, batch);
            }

            counters.SetWaitingForWriter(false);
        }

        private static void PublishBatch(
            XmlImportCounters counters,
            NodesetByteScanner scanner,
            int variables,
            BlockingCollection<List<XmlImportRow>> batches,
            List<XmlImportRow> batch,
            CancellationToken cancellationToken)
        {
            counters.SetVariables(variables);
            counters.SetBytes(scanner.BytesRead);
            counters.SetWaitingForWriter(true);
            try
            {
                batches.Add(batch, cancellationToken);
            }
            finally
            {
                counters.SetWaitingForWriter(false);
            }
        }

        private static List<XmlImportRow> Rent(ConcurrentBag<List<XmlImportRow>> pool)
        {
            if (pool != null && pool.TryTake(out var list))
            {
                return list;
            }

            return new List<XmlImportRow>(XmlImportLimits.BatchSize);
        }

        internal static void Return(ConcurrentBag<List<XmlImportRow>> pool, List<XmlImportRow> batch)
        {
            if (batch == null)
            {
                return;
            }

            batch.Clear();
            pool?.Add(batch);
        }
    }
}
