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
using DataLogLib.Common;
using DataLogLib.Models;
using S7CommPlusDriver;
using S7CommPlusDriver.Subscriptions;
using S7CommPlusDriver.Symbols;
using Serilog;

namespace DataLogService.DataSource.S7CommPlus
{
    /// <summary>
    /// Siemens S7-1200 / S7-1500 over S7CommPlus (TLS). Values are read by subscriptions only.
    /// One worker thread owns the connection: connect, resolve the symbolic tag addresses (CRC secured),
    /// subscribe, ping, heal stale addresses after a program change, reconnect with backoff.
    /// </summary>
    internal sealed partial class DataSourceS7CommPlus : DataSourceBase, ITagBrowseSource
    {
        #region constants
        internal static readonly int[] StandardCycles = [100, 500, 1000, 5000, 10000];
        internal const int DefaultTagCycle = 1000;
        internal const int MaxItemsPerSubscription = 100;
        private const int TimeoutMs = 10000;
        private const int PingIntervalMs = 10000;
        private const int AttributeReserve = 0;
        // Pause before each type information request of a browse: a program with many types is read gently.
        private const int BrowseRequestPauseMs = 20;
        internal const string PlcLimitIssue = "No subscription: PLC limit of subscribed values reached";
        internal const string ShareLimitIssue = "No subscription: DataLogXE uses at most half of the subscribed values of the PLC";

        // DataLogXE may hold at most half of the subscribed values (PLC attributes) of a CPU, summed over all
        // S7CommPlus sources on that host; the other half stays free for HMI, SCADA and other clients.
        private static readonly object ShareLock = new();
        private static readonly Dictionary<int, (string Host, int Values)> SharedUsage = [];
        internal const int ShareDivisor = 2;
        internal const string NotLoggableIssue = "Data type cannot be logged (only bool, numbers, durations, date and time)";
        private static readonly TimeSpan PendingRetryInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan UnresolvedRetryInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MinHealInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxHealInterval = TimeSpan.FromMinutes(10);
        #endregion

        #region private fields
        private readonly object _lock = new();
        private readonly Dictionary<int, S7Tag> _tags = [];             // by data tag id
        private readonly List<SubscriptionGroup> _groups = [];
        private readonly ConcurrentQueue<HealRequest> _healRequests = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private volatile bool _running = true;
        private Thread _worker;

        private S7CommPlusConnection _conn;
        private NotificationDispatcher _dispatcher;
        private SymbolBrowser _resolver;
        private string _dataTagChangeMarker = "";
        private bool _forceTagSync = true;
        private DateTime _nextPingUtc;
        private DateTime _nextPendingRetryUtc;
        private DateTime _nextUnresolvedRetryUtc;
        private DateTime _nextVerifyUtc;
        private DateTime _lastHealUtc = DateTime.MinValue;
        private TimeSpan _healInterval = MinHealInterval;
        // Local clock minus PLC clock, from the time stamps of data notifications and live alarms.
        private readonly ClockOffsetEstimator _plcClock = new();
        private readonly bool _clockCompensate;
        // Without compensation: the PLC was refused for a clock too far ahead (rejoin with hysteresis), and the
        // notification thread asks the worker to drop the session (ms ahead, 0 = no).
        private bool _clockRefused;
        private long _clockDropAheadMs;
        private string _lastConnectError;

        // browse
        private readonly object _browseLock = new();
        private CancellationTokenSource _browseCts;
        private OpcUaBrowseJobStatus _browseStatus = new();
        #endregion

        #region construction
        public DataSourceS7CommPlus(DataSourceModel dataSource) : base(dataSource)
        {
            _clockCompensate = dataSource.ClockCompensate;
            InitAlarms(dataSource);
        }

        public override void Init()
        {
            base.Init();
            _state = DataSourceState.Connecting;
            _worker = new Thread(Worker) { IsBackground = true, Name = $"S7CommPlus-{Id}" };
            _worker.Start();
        }

        public override void Dispose()
        {
            _running = false;
            CancelBrowse();
            _wake.Set();
            if (_worker != null && !_worker.Join(TimeoutMs + 5000))
            {
                Log.Warning(LogPfx + "S7 worker did not stop in time", Name, Id);
            }
            CloseConnection(deleteSubscriptions: false);
            base.Dispose();
        }
        #endregion

        #region cycles
        protected override void InitializeCycleList()
        {
            Cycles = [.. StandardCycles];
        }
        #endregion

        #region worker
        private void Worker()
        {
            int backoffMs = 1000;
            while (_running)
            {
                try
                {
                    if (_conn == null || !_conn.Connected)
                    {
                        if (_conn != null)
                        {
                            OnConnectionLost(_conn.ConnectionLostReason ?? "connection lost");
                        }
                        if (!Connect())
                        {
                            _state = DataSourceState.Error;
                            WaitOrStop(backoffMs);
                            backoffMs = Math.Min(backoffMs * 2, 30000);
                            continue;
                        }
                        backoffMs = 1000;
                        _forceTagSync = true;
                        StartAlarms();
                    }

                    long dropAheadMs = Interlocked.Exchange(ref _clockDropAheadMs, 0);
                    if (dropAheadMs != 0)
                    {
                        // Reconnects at the top of the loop, where Connect() measures the clock again.
                        _lastConnectError = ClockGuard.RefusalText(dropAheadMs);
                        _clockRefused = true;
                        Log.Warning(LogPfx + "{Error}", Name, Id, _lastConnectError);
                        _conn.Disconnect();
                        continue;
                    }

                    if (_forceTagSync || DatabaseDataTagsChanged())
                    {
                        _forceTagSync = false;
                        SyncTags();
                    }

                    ProcessHealRequests();

                    var now = DateTime.UtcNow;
                    if (now >= _nextPendingRetryUtc)
                    {
                        _nextPendingRetryUtc = now + PendingRetryInterval;
                        RetryPending();
                    }
                    if (now >= _nextVerifyUtc)
                    {
                        _nextVerifyUtc = now + VerifyInterval;
                        VerifySubscriptions();
                    }
                    if (now >= _nextUnresolvedRetryUtc)
                    {
                        _nextUnresolvedRetryUtc = now + UnresolvedRetryInterval;
                        RetryUnresolved();
                    }
                    if (now >= _nextPingUtc)
                    {
                        _nextPingUtc = now.AddMilliseconds(PingIntervalMs);
                        int res = _conn.Ping();
                        if (res != 0)
                        {
                            Log.Warning(LogPfx + "PLC does not answer ({Error}), reconnecting", Name, Id, S7Consts.ErrorText(res));
                            _conn.Disconnect();
                            continue;
                        }
                    }

                    AlarmWork(now);
                    CheckOutdatedValues();
                }
                catch (S7CommunicationException ex)
                {
                    Log.Warning(LogPfx + "{Error}, reconnecting", Name, Id, ex.Message);
                    CloseConnection(deleteSubscriptions: false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, LogPfx + "S7 worker error, reconnecting", Name, Id);
                    CloseConnection(deleteSubscriptions: false);
                    WaitOrStop(5000);
                }
                WaitOrStop(1000);
            }
        }

        private void WaitOrStop(int ms)
        {
            if (!_running) return;
            _wake.Wait(ms);
            _wake.Reset();
        }

        private bool Connect()
        {
            _state = DataSourceState.Connecting;
            var conn = new S7CommPlusConnection(SourceLog);
            int port = _dataSource.Port > 0 ? _dataSource.Port : 102;
            int res = conn.Connect(_dataSource.Host, "", "", TimeoutMs, port);
            if (res != 0)
            {
                string error = S7Consts.ErrorText(res);
                if (error != _lastConnectError)
                {
                    Log.Warning(LogPfx + "Connect to PLC {Host}:{Port} failed: {Error}", Name, Id, _dataSource.Host, port, error);
                    _lastConnectError = error;
                }
                conn.Disconnect();
                return false;
            }
            if (!_clockCompensate && RefusedForClock(conn))
            {
                conn.Disconnect();
                return false;
            }
            _lastConnectError = null;
            conn.ConnectionLost += reason =>
            {
                Log.Warning(LogPfx + "Connection to the PLC lost: {Reason}", Name, Id, reason);
                _wake.Set();
            };
            _conn = conn;
            _dispatcher = new NotificationDispatcher(conn, Log.Logger);
            _resolver = new SymbolBrowser(conn, Log.Logger);
            _plcClock.Reset();
            _nextPingUtc = DateTime.UtcNow.AddMilliseconds(PingIntervalMs);
            _nextPendingRetryUtc = DateTime.UtcNow + PendingRetryInterval;
            _nextUnresolvedRetryUtc = DateTime.UtcNow + UnresolvedRetryInterval;
            _nextVerifyUtc = DateTime.UtcNow + VerifyInterval;
            _healInterval = MinHealInterval;
            _state = DataSourceState.Connected;
            Log.Information(LogPfx + "Connected to PLC {Host}:{Port} ({Paom}, protection level {Level})",
                Name, Id, _dataSource.Host, port, conn.SessionPaom, conn.EffectiveProtectionLevel);
            return true;
        }

        private void OnConnectionLost(string reason)
        {
            CloseConnection(deleteSubscriptions: false);
            _state = DataSourceState.Disconnected;
        }

        /// <summary>Drops the session; all tags are attached again (with a startup value) after the reconnect.</summary>
        private void CloseConnection(bool deleteSubscriptions)
        {
            var conn = _conn;
            var dispatcher = _dispatcher;
            _conn = null;
            _dispatcher = null;
            _resolver = null;
            lock (_lock)
            {
                if (deleteSubscriptions && conn != null && conn.Connected)
                {
                    foreach (var g in _groups.Where(g => g.ObjectId != 0))
                    {
                        conn.SubscriptionDelete(g.ObjectId);
                    }
                }
                _groups.Clear();
                _tags.Clear();
            }
            PublishUsage();
            StopAlarms();
            while (_healRequests.TryDequeue(out _)) { }
            dispatcher?.Dispose();
            try
            {
                conn?.Disconnect();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, LogPfx + "Disconnect failed", Name, Id);
            }
            if (_running)
            {
                _state = DataSourceState.Disconnected;
            }
        }
        #endregion

        #region tag sync
        private bool DatabaseDataTagsChanged()
        {
            var marker = Global.Service.DataAccess.ParameterGet(Consts.DATA_TAG_CHANGE_MARKER_KEY) ?? "";
            var changed = !marker.Equals(_dataTagChangeMarker);
            _dataTagChangeMarker = marker;
            return changed;
        }

        private void SyncTags()
        {
            var dbTags = Global.Service.DataAccess.DataTagsGetByDataSourceId(Id, deleted: 0, active: 1)
                .Where(t => t.IsSys == 0)
                .ToList();
            var wanted = dbTags.ToDictionary(t => t.Id);

            List<S7Tag> added = [];
            int removed = 0;
            lock (_lock)
            {
                foreach (var tag in _tags.Values.ToList())
                {
                    if (!wanted.TryGetValue(tag.Model.Id, out var model) || S7Tag.SyncKeyOf(model) != tag.SyncKey)
                    {
                        RemoveTagUnlocked(tag);
                        removed++;
                    }
                }
                foreach (var model in dbTags)
                {
                    if (!_tags.ContainsKey(model.Id))
                    {
                        var tag = new S7Tag(model);
                        _tags[model.Id] = tag;
                        added.Add(tag);
                    }
                }
            }

            ResolveTags(added);
            InsertStartupValues(added);
            ApplySubscriptions();

            if (removed > 0 || added.Count > 0)
            {
                int active, subscribed;
                lock (_lock)
                {
                    active = _tags.Count;
                    subscribed = _tags.Values.Count(t => t.Group != null);
                }
                Log.Information(LogPfx + "Data tags: {Added} added, {Removed} removed, {Active} active, {Subscribed} subscribed",
                    Name, Id, added.Count, removed, active, subscribed);
            }
        }

        // Caller holds _lock.
        private void RemoveTagUnlocked(S7Tag tag)
        {
            _tags.Remove(tag.Model.Id);
            if (tag.Group != null)
            {
                // Keep the positions: the reference ids of the running subscription are list positions.
                int index = tag.Group.Items.IndexOf(tag);
                if (index >= 0) tag.Group.Items[index] = null;
                tag.Group.Dirty = true;
                tag.Group = null;
            }
        }

        private void ResolveTags(IEnumerable<S7Tag> tags)
        {
            int unresolved = 0;
            foreach (var tag in tags)
            {
                if (!_running) return;
                var symbol = _resolver.Resolve(tag.Model.Address, out string error);
                lock (_lock)
                {
                    tag.Symbol = symbol;
                    tag.SubscribeError = null;
                    tag.NotLoggable = symbol != null && !SymbolBrowser.IsLoggable(symbol.Softdatatype);
                    tag.Issue = symbol == null ? "Not resolved: " + error : tag.NotLoggable ? NotLoggableIssue : null;
                }
                if (symbol == null)
                {
                    unresolved++;
                    if (unresolved <= 20)
                    {
                        Log.Warning(LogPfx + "Data tag {Address} not resolved: {Error}", Name, Id, tag.Model.Address, error);
                    }
                }
            }
            if (unresolved > 20)
            {
                Log.Warning(LogPfx + "{Count} data tags could not be resolved", Name, Id, unresolved);
            }
        }

        /// <summary>
        /// Writes the startup value (the gap since the last archive value) once per session for the tags that will be
        /// subscribed. A tag that is not resolved or cannot be logged never gets a value of its own, so its last archive
        /// value would stay the previous startup value: every connect wrote one more value 5 s after it, days back in a
        /// compressed chunk (on the Pi 2026-10-29 four active WString tags, which overloaded the compression).
        /// </summary>
        private void InsertStartupValues(IEnumerable<S7Tag> tags)
        {
            List<S7Tag> due;
            lock (_lock)
            {
                due = tags.Where(t => !t.StartupWritten && TakesStartupValue(t.Symbol)).ToList();
                due.ForEach(t => t.StartupWritten = true);
            }
            foreach (var tag in due)
            {
                InsertStartupArchiveValue(tag.Model.Id);
            }
        }

        /// <summary>Whether a tag with this symbol gets values: resolved and of a type that can be logged.</summary>
        internal static bool TakesStartupValue(S7Symbol symbol) => symbol != null && SymbolBrowser.IsLoggable(symbol.Softdatatype);
        #endregion

        #region subscriptions
        /// <summary>
        /// Recreates changed groups and subscribes resolved tags without a group, grouped by cycle,
        /// as far as the free PLC attributes allow (every subscribed item uses one attribute of the CPU).
        /// </summary>
        private void ApplySubscriptions()
        {
            var conn = _conn;
            if (conn == null || !conn.Connected) return;

            List<SubscriptionGroup> dirty;
            lock (_lock)
            {
                dirty = _groups.Where(g => g.Dirty).ToList();
            }
            foreach (var g in dirty)
            {
                DeleteGroup(g);
            }

            List<S7Tag> pending;
            lock (_lock)
            {
                // Tags of removed groups come back here and are subscribed again.
                pending = _tags.Values.Where(t => t.Symbol != null && t.Group == null && t.SubscribeError == null && !t.NotLoggable).ToList();
            }
            if (pending.Count == 0) return;

            // One source at a time decides, so two sources on the same PLC cannot both take the last share.
            lock (ShareLock)
            {
                int free = ReadFreeAttributes(out int max);
                int share = int.MaxValue;
                if (max > 0)
                {
                    int own;
                    lock (_lock)
                    {
                        own = _groups.Where(g => g.ObjectId != 0).Sum(g => g.Items.Count(t => t != null));
                    }
                    int others = SharedUsage.Where(kv => kv.Key != Id && SameHost(kv.Value.Host, Host)).Sum(kv => kv.Value.Values);
                    share = Math.Max(0, max / ShareDivisor - own - others);
                }

                foreach (var byCycle in pending.GroupBy(t => NormalizeCycle(t.Model.Cycle)).OrderBy(g => g.Key))
                {
                    var items = byCycle.OrderBy(t => t.Model.Id).ToList();
                    for (int i = 0; i < items.Count; i += MaxItemsPerSubscription)
                    {
                        var chunk = items.Skip(i).Take(MaxItemsPerSubscription).ToList();
                        int fitPlc = free >= 0 ? Math.Max(0, free - AttributeReserve) : int.MaxValue;
                        int fit = Math.Min(fitPlc, share);
                        if (chunk.Count > fit)
                        {
                            string issue = share <= fitPlc ? ShareLimitIssue : PlcLimitIssue;
                            lock (_lock)
                            {
                                foreach (var t in chunk.Skip(fit)) t.Issue = issue;
                            }
                            chunk = chunk.Take(fit).ToList();
                        }
                        if (chunk.Count == 0) continue;
                        int created = CreateGroup(byCycle.Key, chunk, 0);
                        if (free >= 0) free -= created;
                        share -= created;
                    }
                }
                PublishUsage();
            }
        }

        private static bool SameHost(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Publishes the values this source holds, for the share limit of the other sources on the same PLC.</summary>
        private void PublishUsage()
        {
            var (_, values) = GetSubscriptionUsage();
            lock (ShareLock)
            {
                if (_running)
                {
                    SharedUsage[Id] = (Host, values);
                }
                else
                {
                    SharedUsage.Remove(Id);
                }
            }
        }

        /// <summary>Creates a subscription for the tags; on refusal it is split to find the item the PLC rejects.</summary>
        private int CreateGroup(int cycle, List<S7Tag> tags, int depth)
        {
            var conn = _conn;
            if (conn == null || !conn.Connected || tags.Count == 0) return 0;

            var group = new SubscriptionGroup { Cycle = cycle, Items = [.. tags] };
            var addresses = tags.Select(t => t.Symbol.CreateAddress()).ToList();
            int res = conn.SubscriptionCreate(addresses, (uint)cycle, out uint objectId, out ulong plcReturn);
            if (res == 0)
            {
                group.ObjectId = objectId;
                lock (_lock)
                {
                    _groups.Add(group);
                    foreach (var t in tags)
                    {
                        t.Group = group;
                        t.Issue = null;
                    }
                }
                _dispatcher.Register(objectId, n => OnNotification(group, n));
                return tags.Count;
            }

            if (res != S7Consts.errCliFunctionRefused)
            {
                throw new S7CommunicationException(res, "Creating a subscription failed");
            }

            if (IsResourceLimit(plcReturn))
            {
                Log.Warning(LogPfx + "PLC refused a subscription of {Count} values: resource limit (0x{Code:X16})", Name, Id, tags.Count, plcReturn);
                lock (_lock)
                {
                    foreach (var t in tags) t.Issue = PlcLimitIssue;
                }
                return 0;
            }

            if (tags.Count == 1 || depth > 12)
            {
                lock (_lock)
                {
                    foreach (var t in tags)
                    {
                        t.SubscribeError = $"Subscription refused by the PLC (0x{plcReturn:X16})";
                        t.Issue = t.SubscribeError;
                    }
                }
                Log.Warning(LogPfx + "PLC refused the subscription of {Address} (0x{Code:X16})", Name, Id, tags[0].Model.Address, plcReturn);
                return 0;
            }
            int half = tags.Count / 2;
            return CreateGroup(cycle, tags.Take(half).ToList(), depth + 1) + CreateGroup(cycle, tags.Skip(half).ToList(), depth + 1);
        }

        private void DeleteGroup(SubscriptionGroup group)
        {
            var conn = _conn;
            if (group.ObjectId != 0)
            {
                _dispatcher?.Unregister(group.ObjectId);
                if (conn != null && conn.Connected)
                {
                    int res = conn.SubscriptionDelete(group.ObjectId);
                    if (res != 0 && !conn.Connected)
                    {
                        throw new S7CommunicationException(res, "Deleting a subscription failed");
                    }
                }
            }
            lock (_lock)
            {
                _groups.Remove(group);
                foreach (var t in group.Items)
                {
                    if (t != null && t.Group == group) t.Group = null;
                }
                group.Items.Clear();
            }
        }

        private int ReadFreeAttributes(out int max)
        {
            max = 0;
            var conn = _conn;
            if (conn == null) return -1;
            var r = conn.Ressources;
            if (r.ReadFree(conn) != 0 || r.PlcAttributesMax <= 0)
            {
                return -1;
            }
            max = r.PlcAttributesMax;
            return r.PlcAttributesFree;
        }

        /// <summary>Error class 0xA6 of the PLC: out of resources (attributes / subscriptions).</summary>
        internal static bool IsResourceLimit(ulong plcReturn) => ((plcReturn >> 40) & 0xFF) == 0xA6;

        private void RetryPending()
        {
            bool any;
            lock (_lock)
            {
                any = _tags.Values.Any(t => t.Symbol != null && t.Group == null && t.SubscribeError == null && !t.NotLoggable);
            }
            if (any)
            {
                ApplySubscriptions();
            }
        }

        private void RetryUnresolved()
        {
            List<S7Tag> unresolved;
            lock (_lock)
            {
                unresolved = _tags.Values.Where(t => t.Symbol == null || t.SubscribeError != null).ToList();
            }
            if (unresolved.Count == 0) return;
            // The program may have been loaded meanwhile.
            _resolver.Invalidate();
            ResolveTags(unresolved);
            InsertStartupValues(unresolved);
            ApplySubscriptions();
        }

        internal static int NormalizeCycle(int cycle)
        {
            foreach (var c in StandardCycles)
            {
                if (cycle <= c) return c;
            }
            return StandardCycles[^1];
        }
        #endregion

        #region notifications
        // Dispatcher thread.
        private void OnNotification(SubscriptionGroup group, Notification n)
        {
            DateTime time = GetValueTime(n);
            List<ArchiveValue> values = new(n.Values.Count + 1);
            List<S7Tag> failed = null;
            lock (_lock)
            {
                if (group.ObjectId == 0 || !_groups.Contains(group)) return;
                foreach (var kv in n.Values)
                {
                    int index = (int)kv.Key - 1;
                    if (index < 0 || index >= group.Items.Count) continue;
                    var tag = group.Items[index];
                    if (tag == null) continue;
                    if (!ValueConverter.TryToDouble(kv.Value, tag.Symbol.Softdatatype, out double value))
                    {
                        continue;
                    }
                    if (!tag.Initialized)
                    {
                        tag.Initialized = true;
                        values.Add(new ArchiveValue { Id = tag.Model.Id, Value = value, Time = time.AddMilliseconds(-1), State = ValueState.Invalid });
                    }
                    var av = new ArchiveValue { Id = tag.Model.Id, Value = value, Time = time, State = ValueState.Valid };
                    values.Add(av);
                    tag.LastValue = av.Value;
                    tag.LastTime = time;
                    tag.HasLast = true;
                    tag.Issue = null;
                }
                foreach (var kv in n.ReturnValues)
                {
                    int index = (int)kv.Key - 1;
                    if (index < 0 || index >= group.Items.Count) continue;
                    var tag = group.Items[index];
                    if (tag == null) continue;
                    tag.Issue = $"PLC reports an access error (0x{kv.Value:X2})";
                    tag.HasLast = false;
                    (failed ??= []).Add(tag);
                }
            }
            foreach (var v in values)
            {
                InsertArchiveValue(v);
            }
            if (failed != null)
            {
                _healRequests.Enqueue(new HealRequest { Group = group, Tags = failed });
                _wake.Set();
            }
        }

        /// <summary>
        /// Time of the notification: the PLC time stamp (newer firmware), moved onto the local clock while the source
        /// compensates the clock difference, else the receive time.
        /// </summary>
        private DateTime GetValueTime(Notification n)
        {
            if (n.Add1Timestamp == default)
            {
                return DateTime.Now;
            }
            long shiftMs = AddPlcClockSample(n.Add1Timestamp, DateTime.UtcNow);
            var plcLocal = n.Add1Timestamp.ToLocalTime();
            return _clockCompensate ? plcLocal.AddMilliseconds(shiftMs) : plcLocal;
        }

        /// <summary>Adds receive time minus PLC time (UTC) as a clock sample and returns the estimate.</summary>
        private long AddPlcClockSample(DateTime plcTime, DateTime receivedUtc)
        {
            long sample = (long)(receivedUtc - plcTime.ToUniversalTime()).TotalMilliseconds;
            if (_plcClock.AddSample(sample, receivedUtc, out long shiftMs))
            {
                Log.Information(LogPfx + "PLC time offset: {Offset} ms", Name, Id, shiftMs);
            }
            // A PLC clock set far ahead during the session: the worker drops it.
            if (!_clockCompensate && ClockGuard.RefuseConnection(-shiftMs, refusedBefore: false))
            {
                Interlocked.CompareExchange(ref _clockDropAheadMs, -shiftMs, 0);
            }
            return shiftMs;
        }

        /// <summary>
        /// Without compensation, before the session is used: true when the PLC clock is too far ahead (see
        /// <see cref="ClockGuard"/>). A PLC without time stamps (older firmware) is not checked, its values take the
        /// receive time.
        /// </summary>
        private bool RefusedForClock(S7CommPlusConnection conn)
        {
            if (MeasureClockOffsetMs(conn) is not long aheadMs)
            {
                return false;
            }
            if (!ClockGuard.RefuseConnection(aheadMs, _clockRefused))
            {
                if (_clockRefused)
                {
                    Log.Information(LogPfx + "PLC clock back within the limit ({Offset} ms), connecting", Name, Id, aheadMs);
                }
                _clockRefused = false;
                return false;
            }

            string error = ClockGuard.RefusalText(aheadMs);
            if (error != _lastConnectError)
            {
                Log.Warning(LogPfx + "{Error}", Name, Id, error);
                _lastConnectError = error;
            }
            _clockRefused = true;
            return true;
        }

        /// <summary>Why the last connect failed, while the source is not connected.</summary>
        protected override string ConnectionErrorText => _state == DataSourceState.Connected ? null : _lastConnectError;

        private void CheckOutdatedValues()
        {
            List<ArchiveValue> resend = null;
            // Without compensation the values carry PLC times, their age counts on the PLC clock.
            var now = DateTime.Now.AddMilliseconds(_clockCompensate ? 0 : -(_plcClock.ShiftMs ?? 0));
            var maxAge = Consts.ARCHIVE_VALUE_MIN_MS;
            lock (_lock)
            {
                foreach (var tag in _tags.Values)
                {
                    if (!tag.HasLast || tag.Group == null) continue;
                    if ((now - tag.LastTime).TotalMilliseconds > maxAge * 2)
                    {
                        tag.LastTime = now.AddMilliseconds(-maxAge);
                        (resend ??= []).Add(new ArchiveValue { Id = tag.Model.Id, Value = tag.LastValue, Time = tag.LastTime, State = ValueState.Resend });
                    }
                }
            }
            if (resend == null) return;
            foreach (var v in resend)
            {
                InsertArchiveValue(v);
            }
        }
        #endregion

        #region subscription check
        /// <summary>
        /// Reads the subscribed values once a minute. A read error (e.g. CRC mismatch after a program download) starts
        /// the self healing also for values that never change and therefore never send a notification. The values
        /// themselves are not compared: signals faster than the cycle are sampled in phase by the subscription and
        /// legitimately differ from a read at another moment.
        /// </summary>
        private void VerifySubscriptions()
        {
            var conn = _conn;
            if (conn == null || !conn.Connected) return;
            List<(SubscriptionGroup Group, List<S7Tag> Tags)> snapshot;
            lock (_lock)
            {
                snapshot = _groups.Where(g => g.ObjectId != 0 && !g.Dirty)
                    .Select(g => (g, g.Items.Where(t => t != null && t.Symbol != null).ToList()))
                    .Where(x => x.Item2.Count > 0)
                    .ToList();
            }

            foreach (var (group, tags) in snapshot)
            {
                if (!_running) return;
                int res = conn.ReadValues(tags.Select(t => t.Symbol.CreateAddress()).ToList(), out _, out var errors);
                if (res != 0)
                {
                    throw new S7CommunicationException(res, "Checking the subscribed values failed");
                }

                List<S7Tag> failed = null;
                lock (_lock)
                {
                    if (!_groups.Contains(group)) continue;
                    for (int i = 0; i < tags.Count; i++)
                    {
                        if (errors[i] == 0) continue;
                        tags[i].Issue = $"PLC reports an access error (0x{errors[i]:X16})";
                        tags[i].HasLast = false;
                        (failed ??= []).Add(tags[i]);
                    }
                }
                if (failed != null)
                {
                    _healRequests.Enqueue(new HealRequest { Group = group, Tags = failed });
                }
            }
        }
        #endregion

        #region self healing
        /// <summary>
        /// Item errors mean the address of a symbol is no longer valid, normally because the PLC program was loaded.
        /// The type information is read again, the tags are resolved again and their subscriptions recreated.
        /// Rate limited with a growing interval, so a symbol that is really gone does not cause a loop.
        /// </summary>
        private void ProcessHealRequests()
        {
            if (_healRequests.IsEmpty) return;
            if (DateTime.UtcNow - _lastHealUtc < _healInterval) return;

            var groups = new HashSet<SubscriptionGroup>();
            var tags = new HashSet<S7Tag>();
            while (_healRequests.TryDequeue(out var req))
            {
                groups.Add(req.Group);
                foreach (var t in req.Tags) tags.Add(t);
            }
            _lastHealUtc = DateTime.UtcNow;

            Log.Information(LogPfx + "{Count} data tags report access errors, reading the PLC program again", Name, Id, tags.Count);
            _resolver.Invalidate();

            List<S7Tag> all;
            lock (_lock)
            {
                all = groups.SelectMany(g => g.Items).Concat(tags).Where(t => t != null).Distinct().Where(t => _tags.ContainsKey(t.Model.Id)).ToList();
            }
            int changed = 0;
            foreach (var tag in all)
            {
                var old = tag.Symbol;
                var symbol = _resolver.Resolve(tag.Model.Address, out string error);
                lock (_lock)
                {
                    if (symbol == null)
                    {
                        tag.Symbol = null;
                        tag.Issue = "Not resolved: " + error;
                        changed++;
                    }
                    else if (old == null || old.AccessString != symbol.AccessString || old.SymbolCrc != symbol.SymbolCrc)
                    {
                        tag.Symbol = symbol;
                        changed++;
                    }
                    tag.SubscribeError = null;
                    tag.NotLoggable = symbol != null && !SymbolBrowser.IsLoggable(symbol.Softdatatype);
                    if (tag.NotLoggable) tag.Issue = NotLoggableIssue;
                    if (tag.Group != null) tag.Group.Dirty = true;
                }
            }
            // A changed address heals at once; an unchanged one that still fails backs off.
            _healInterval = changed > 0 ? MinHealInterval : TimeSpan.FromTicks(Math.Min(_healInterval.Ticks * 2, MaxHealInterval.Ticks));
            Log.Information(LogPfx + "{Changed} of {Count} data tags got a new address", Name, Id, changed, all.Count);
            ApplySubscriptions();
        }
        #endregion

        #region usage
        internal string Host => _dataSource.Host ?? "";

        /// <summary>Subscriptions and subscribed values this source holds on its PLC right now.</summary>
        internal (int Subscriptions, int Values) GetSubscriptionUsage()
        {
            lock (_lock)
            {
                var active = _groups.Where(g => g.ObjectId != 0).ToList();
                return (active.Count, active.Sum(g => g.Items.Count(t => t != null)));
            }
        }
        #endregion

        #region tag issues
        internal override string GetTagIssue(int dataTagId)
        {
            lock (_lock)
            {
                if (_tags.TryGetValue(dataTagId, out var tag))
                {
                    return tag.Issue;
                }
            }
            return null;
        }
        #endregion

        #region browse
        bool ITagBrowseSource.IsBrowseRunning
        {
            get
            {
                lock (_browseLock) return _browseStatus.Running;
            }
        }

        void ITagBrowseSource.StartBrowse()
        {
            if (!_running)
            {
                throw new InvalidOperationException("The data source is not connected.");
            }
            CancellationToken token;
            lock (_browseLock)
            {
                if (_browseStatus.Running)
                {
                    throw new InvalidOperationException("A browse is already running.");
                }
                _browseCts?.Dispose();
                _browseCts = new CancellationTokenSource();
                token = _browseCts.Token;
                _browseStatus = new OpcUaBrowseJobStatus { Running = true };
            }
            new Thread(() => RunBrowse(token)) { IsBackground = true, Name = $"S7Browse-{Id}" }.Start();
        }

        OpcUaBrowseJobStatus ITagBrowseSource.GetBrowseStatus()
        {
            lock (_browseLock)
            {
                return new OpcUaBrowseJobStatus
                {
                    Running = _browseStatus.Running,
                    Paused = _browseStatus.Paused,
                    Completed = _browseStatus.Completed,
                    Error = _browseStatus.Error,
                    Lines = _browseStatus.Lines.Select(l => new OpcUaBrowseStatusLine { Text = l.Text, ReplaceLast = l.ReplaceLast }).ToList(),
                };
            }
        }

        public void CancelBrowse()
        {
            CancellationTokenSource cts;
            lock (_browseLock)
            {
                cts = _browseCts;
            }
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void ITagBrowseSource.CancelBrowse() => CancelBrowse();

        private void AddBrowseLine(string text, bool replaceLast = false)
        {
            lock (_browseLock)
            {
                if (replaceLast && _browseStatus.Lines.Count > 0 && _browseStatus.Lines[^1].ReplaceLast)
                {
                    _browseStatus.Lines[^1] = new OpcUaBrowseStatusLine { Text = text, ReplaceLast = true };
                }
                else
                {
                    _browseStatus.Lines.Add(new OpcUaBrowseStatusLine { Text = text, ReplaceLast = replaceLast });
                }
            }
        }

        /// <summary>
        /// The data tag of a browsed symbol, null for a type that cannot be logged (strings, pointers, system types):
        /// such a tag never gets a value. Like the OPC UA browse, the browse leaves them out, so a new browse removes the
        /// ones that browses from 0.0.175 on imported as type Unknown.
        /// </summary>
        internal static DataTagBrowseImportItem ToBrowseImportItem(S7Symbol symbol)
        {
            if (!SymbolBrowser.IsLoggable(symbol.Softdatatype))
            {
                return null;
            }

            return new DataTagBrowseImportItem
            {
                Address = symbol.Path,
                Name = symbol.Path,
                DisplayName = null,
                TypeId = SymbolBrowser.IsBoolType(symbol.Softdatatype) ? (int)DataTagType.Bool : (int)DataTagType.Number,
                Description = DataTagDescription.ToJson(symbol.Comments),
            };
        }

        /// <summary>
        /// Browses the PLC program on its own connection (the logging connection keeps running) and imports every
        /// loggable value as an inactive data tag; tags that no longer exist are removed.
        /// </summary>
        private void RunBrowse(CancellationToken ct)
        {
            S7CommPlusConnection conn = null;
            try
            {
                AddBrowseLine("Connect to the PLC …");
                conn = new S7CommPlusConnection(SourceLog);
                int port = _dataSource.Port > 0 ? _dataSource.Port : 102;
                int res = conn.Connect(_dataSource.Host, "", "", TimeoutMs, port);
                if (res != 0)
                {
                    throw new InvalidOperationException("Connect to the PLC failed: " + S7Consts.ErrorText(res));
                }
                ct.ThrowIfCancellationRequested();
                AddBrowseLine("Read the PLC program …");

                var browser = new SymbolBrowser(conn, Log.Logger) { RequestPauseMs = BrowseRequestPauseMs, ReadComments = true };
                int found = 0;
                var stats = new BrowseStatistics();
                var result = Global.Service.DataAccess.DataTagsSyncFromBrowse(
                    Id,
                    DefaultTagCycle,
                    write =>
                    {
                        var batch = new List<DataTagBrowseImportItem>(1000);
                        stats = browser.BrowseAll(symbol =>
                        {
                            found++;
                            var item = ToBrowseImportItem(symbol);
                            if (item == null)
                            {
                                return;
                            }
                            batch.Add(item);
                            if (batch.Count >= 1000)
                            {
                                write(batch);
                                batch = new List<DataTagBrowseImportItem>(1000);
                            }
                        },
                        line => AddBrowseLine(line, replaceLast: true),
                        ct);
                        if (batch.Count > 0)
                        {
                            write(batch);
                        }
                        AddBrowseLine($"{found} variables found in {stats.Roots} data blocks and areas");
                        if (stats.NotLoggable > 0)
                        {
                            AddBrowseLine($"{stats.NotLoggable} of them are strings or system types, which cannot be logged: not imported");
                        }
                        if (stats.CommentRoots == 0)
                        {
                            AddBrowseLine(conn.EffectiveProtectionLevel >= 3
                                ? "The PLC sent no comments: the protection level of the CPU allows HMI access only, comments need read access"
                                : "The PLC sent no comments");
                        }
                        else
                        {
                            AddBrowseLine(stats.CommentLanguages.Count > 0
                                ? $"{stats.CommentedSymbols} variables with a comment ({string.Join(", ", stats.CommentLanguages)})"
                                : "No variable has a comment");
                        }
                        AddBrowseLine("Update data tags …");
                    },
                    progress => { },
                    ct);

                AddBrowseLine($"Data tags: {result.Created} new, {result.Updated} updated, {result.Undeleted} restored, {result.Deleted} removed");
                AddBrowseLine("New data tags are inactive. Activate the values you want to log (the PLC limits the number of subscribed values).");
                AddBrowseLine("Browse completed successfully");
                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Completed = true;
                    _browseStatus.Error = null;
                }
            }
            catch (OperationCanceledException)
            {
                AddBrowseLine("The browse was cancelled.");
                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Completed = true;
                    _browseStatus.Error = "The browse was cancelled.";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, LogPfx + "S7 browse failed", Name, Id);
                string message = ex is S7CommunicationException || ex is InvalidOperationException ? ex.Message : "Browse failed: " + ex.Message;
                AddBrowseLine(message);
                lock (_browseLock)
                {
                    _browseStatus.Running = false;
                    _browseStatus.Completed = true;
                    _browseStatus.Error = message;
                }
            }
            finally
            {
                try
                {
                    conn?.Disconnect();
                }
                catch
                {
                    // best effort
                }
            }
        }
        #endregion

        #region connection test
        /// <summary>Connects once and reports the CPU, its protection level and its limits.</summary>
        internal static S7ConnectionTestResult TestConnection(string host, int port, int timeoutMs)
        {
            var result = new S7ConnectionTestResult();
            var conn = new S7CommPlusConnection(Log.Logger);
            try
            {
                int res = conn.Connect(host, "", "", timeoutMs, port > 0 ? port : 102);
                if (res != 0)
                {
                    result.Error = S7Consts.ErrorText(res);
                    return result;
                }
                result.Success = true;
                ParsePaom(conn.SessionPaom, result);
                result.ProtectionLevel = (int)conn.EffectiveProtectionLevel;
                var r = conn.Ressources;
                r.ReadFree(conn);
                result.SubscriptionsMax = r.PlcSubscriptionsMax;
                result.SubscriptionsFree = r.PlcSubscriptionsFree;
                result.AttributesMax = r.PlcAttributesMax;
                result.AttributesFree = r.PlcAttributesFree;
                TestAlarms(conn, result);
                result.ClockOffsetMs = MeasureClockOffsetMs(conn);
                try
                {
                    var browser = new SymbolBrowser(conn, Log.Logger);
                    result.DataBlockCount = browser.GetDataBlockNames().Count;
                }
                catch (S7CommunicationException ex)
                {
                    result.Error = ex.Message;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try
                {
                    conn.Disconnect();
                }
                catch
                {
                    // best effort
                }
            }
        }

        // PLC clock minus local clock: a short subscription to one CPU value; the first notification carries the PLC
        // time (firmware with time stamps in notifications), null without one.
        private static long? MeasureClockOffsetMs(S7CommPlusConnection conn)
        {
            using var dispatcher = new NotificationDispatcher(conn, Log.Logger);
            var address = new ItemAddress { AccessArea = Ids.ObjectRoot, AccessSubArea = Ids.FreeItems };
            address.LID.Add(0);
            if (conn.SubscriptionCreate(new List<ItemAddress> { address }, 100, out uint objectId, out _) != 0)
            {
                return null;
            }
            long? offset = null;
            using var received = new ManualResetEventSlim();
            dispatcher.Register(objectId, n =>
            {
                if (n.Add1Timestamp != default)
                {
                    offset = (long)(n.Add1Timestamp - DateTime.UtcNow).TotalMilliseconds;
                }
                received.Set();
            });
            received.Wait(3000);
            conn.SubscriptionDelete(objectId);
            return offset;
        }

        // Whether the PLC takes an alarm subscription (created and deleted again) and how many alarms it holds.
        private static void TestAlarms(S7CommPlusConnection conn, S7ConnectionTestResult result)
        {
            int res = conn.AlarmSubscriptionCreate(new uint[] { (uint)S7AlarmMapper.DefaultLanguage }, out uint id, out _);
            result.AlarmsSupported = res == 0;
            if (res == 0)
            {
                conn.AlarmSubscriptionDelete(id);
            }
            if (conn.ReadActiveAlarms(out var alarms) == 0)
            {
                result.ActiveAlarmCount = alarms.Count;
            }
        }

        /// <summary>"1;6ES7 512-1DM03-0AB0;V4.1" → order number and firmware.</summary>
        internal static void ParsePaom(string paom, S7ConnectionTestResult result)
        {
            result.Paom = paom ?? "";
            if (string.IsNullOrWhiteSpace(paom)) return;
            var parts = paom.Split(';');
            if (parts.Length >= 2) result.OrderNumber = parts[1].Trim();
            if (parts.Length >= 3) result.Firmware = parts[2].Trim();
            var order = result.OrderNumber ?? "";
            var m = global::System.Text.RegularExpressions.Regex.Match(order, @"6ES7\s?(\d)(\d\d)");
            if (m.Success)
            {
                result.CpuFamily = m.Groups[1].Value == "5" ? "S7-1500" : m.Groups[1].Value == "2" ? "S7-1200" : "";
            }
        }
        #endregion

        #region helper classes
        private sealed class S7Tag
        {
            public readonly DataTagModel Model;
            public readonly string SyncKey;
            public S7Symbol Symbol;
            public SubscriptionGroup Group;
            public bool Initialized;
            public bool HasLast;
            public double LastValue;
            public DateTime LastTime;
            public string Issue;
            public string SubscribeError;
            public bool NotLoggable;
            public bool StartupWritten;

            public S7Tag(DataTagModel model)
            {
                Model = model;
                SyncKey = SyncKeyOf(model);
            }

            public static string SyncKeyOf(DataTagModel m) => $"{m.Id};{m.Address};{m.Cycle}";
        }

        private sealed class SubscriptionGroup
        {
            public uint ObjectId;
            public int Cycle;
            public List<S7Tag> Items = [];
            public bool Dirty;
        }

        private sealed class HealRequest
        {
            public SubscriptionGroup Group;
            public List<S7Tag> Tags;
        }
        #endregion
    }

    public sealed class S7ConnectionTestResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string Paom { get; set; } = "";
        public string CpuFamily { get; set; } = "";
        public string OrderNumber { get; set; } = "";
        public string Firmware { get; set; } = "";
        public int ProtectionLevel { get; set; }
        public int SubscriptionsMax { get; set; }
        public int SubscriptionsFree { get; set; }
        public int AttributesMax { get; set; }
        public int AttributesFree { get; set; }
        public int DataBlockCount { get; set; }
        /// <summary>Subscriptions / values held by the running DataLogXE sources on this PLC (counted as used above).</summary>
        public int DataLogXeSources { get; set; }
        public int DataLogXeSubscriptions { get; set; }
        public int DataLogXeValues { get; set; }
        /// <summary>The PLC takes an alarm subscription; the alarms it holds right now (active or to be acknowledged), -1 unknown.</summary>
        public bool AlarmsSupported { get; set; }
        public int ActiveAlarmCount { get; set; } = -1;
        /// <summary>PLC clock minus DataLogXE clock in ms (positive: the PLC is ahead), null when unknown.</summary>
        public long? ClockOffsetMs { get; set; }
    }
}
