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
using System.Diagnostics;
using DataLogApi.Dtos;
using DataLogLib.Services;
using DataLogLib.Services.DataAccess;

namespace DataLogApi.Hosting
{
    /// <summary>
    /// What a query costs the database, and so for which slots it waits. Small reads have a bounded result (a cursor
    /// value, a preview, a count from the counters) and never wait behind the long ones; large reads (many raw values)
    /// run one or two at a time, so they cannot take all slots of a small device.
    /// </summary>
    public enum QueryCost
    {
        Small,
        Medium,
        Large
    }

    /// <summary>
    /// Runs the database reads of the UI as background jobs. A job has no deadline: it ends when the
    /// query is done or when the client cancels it; each of its statements ends at the statement timeout
    /// of the UI pool. The client starts a job, polls its state and fetches the result, so no HTTP
    /// request stays open long enough to time out on a slow machine. A job waits for a slot of its cost
    /// class (DatabaseProfile: 2 / 1 / 1 on a small device, 8 / 4 / 2 on a PC) and tells meanwhile what
    /// it waits for. The same request of a second client (a second tab, a cursor at the same time) joins
    /// the job that runs already; the job is cancelled once all of its clients cancelled it. The load guard holds
    /// queries back while the device is busy: large ones wait while it is tense, medium ones while it is critical. When
    /// it stays critical for a minute, the oldest medium or large query is stopped with a hint, one every 30 s, as the
    /// last step to protect the recording of values.
    /// </summary>
    public sealed class QueryJobService : IDisposable
    {
        public static readonly TimeSpan MaxPollWait = TimeSpan.FromSeconds(5);

        // A job nobody polls any more belongs to a closed tab or a lost client.
        private static readonly TimeSpan AbandonAfter = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
        // A cancel can overtake the start of its job; the job then starts cancelled.
        private static readonly TimeSpan KeepCancelledIds = TimeSpan.FromSeconds(60);
        // Critical for this long: waiting and pausing did not help
        private static readonly TimeSpan StopAfter = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan StopInterval = TimeSpan.FromSeconds(30);
        // A job that ran this long is logged, with what it read
        private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(10);

        // By the id of each client: a shared job is there once per client
        private readonly ConcurrentDictionary<string, QueryJob> _jobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _cancelledIds = new(StringComparer.Ordinal);
        // The unfinished jobs by their request, for a second client of the same request
        private readonly ConcurrentDictionary<string, QueryJob> _byRequest = new(StringComparer.Ordinal);
        private readonly object _shareLock = new();
        // The jobs read from the UI pool (see DatabaseLimits), the slots of all classes take fewer connections than it
        // has; further jobs wait for a slot. The inserts have a pool of their own.
        private readonly Dictionary<QueryCost, CostSlots> _slots;
        // The jobs that hold a slot, with their class and since when
        private readonly ConcurrentDictionary<QueryJob, (QueryCost Cost, long Since)> _running = new();
        private readonly LoadGuard _guard;
        private readonly Timer _sweep;
        private long _lastStop;

        public QueryJobService()
            : this(DatabaseLimits.Profile, LoadGuard.Shared)
        {
        }

        internal QueryJobService(DatabaseProfile profile, LoadGuard guard)
        {
            _guard = guard;
            _slots = new()
            {
                [QueryCost.Small] = new CostSlots("small", profile.SmallQuerySlots),
                [QueryCost.Medium] = new CostSlots("medium", profile.MediumQuerySlots),
                [QueryCost.Large] = new CostSlots("large", profile.LargeQuerySlots)
            };
            LargeQueryRows = profile.LargeQueryRows;
            _sweep = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        /// <summary>From this many raw values on, a read of raw values is a large query.</summary>
        public long LargeQueryRows { get; }

        /// <summary>The class of a read of about rows raw values; medium when the count is not known.</summary>
        public QueryCost CostOfRows(long? rows)
            => rows >= LargeQueryRows ? QueryCost.Large : QueryCost.Medium;

        /// <summary>
        /// Starts a job, or joins the unfinished job of the same request. The client may choose its id (32 hex digits,
        /// random), so it can cancel the job even when it gave up before the answer to the start arrived. A start that
        /// is repeated with the same id (a retry after a lost answer) returns the job that already runs. Without a
        /// cost, estimate tells it before the job waits for its slot (from the counters of the archive).
        /// </summary>
        public (string Id, QueryJob Job) Start(
            string kind,
            Func<IQueryProgress, CancellationToken, object> work,
            string clientId = null,
            QueryCost cost = QueryCost.Medium,
            Func<CancellationToken, QueryCost> estimate = null,
            string request = null)
        {
            var id = IsValidClientId(clientId) ? clientId.ToLowerInvariant() : Guid.NewGuid().ToString("N");
            if (_jobs.TryGetValue(id, out var known))
            {
                known.Touch();
                return (id, known);
            }

            QueryJob job;
            lock (_shareLock)
            {
                if (request != null && _byRequest.TryGetValue(request, out var running) && running.TryJoin())
                {
                    _jobs[id] = running;
                    running.Touch();
                    return (id, running);
                }

                job = new QueryJob(id, kind);
                if (!_jobs.TryAdd(id, job))
                {
                    return (id, _jobs[id]);
                }
                if (request != null)
                {
                    _byRequest[request] = job;
                }
            }

            if (_cancelledIds.TryRemove(id, out _))
            {
                job.Cancel();
            }

            job.Request = request;
            job.Cost = cost;
            _ = Task.Run(() => RunAsync(job, work, cost, estimate, request));
            return (id, job);
        }

        /// <summary>
        /// The unfinished jobs of all clients, oldest first, for the activity panel. The ids of the clients are not
        /// shown; mine names those of the caller, so it can tell its own jobs.
        /// </summary>
        public List<RunningQueryDto> Running(IReadOnlyCollection<string> mine)
        {
            var own = new HashSet<string>((mine ?? []).Select(id => id.ToLowerInvariant()), StringComparer.Ordinal);
            return [.. _jobs
                .Where(entry => !entry.Value.IsFinished)
                .GroupBy(entry => entry.Value)
                .Select(group => group.Key.ToRunningDto(group.Any(entry => own.Contains(entry.Key))))
                .OrderByDescending(dto => dto.ElapsedMs)];
        }

        /// <summary>Stops the unfinished job with the key for all of its clients; false when there is none.</summary>
        public bool Stop(string key, string user)
        {
            var job = _jobs.Values.FirstOrDefault(j => !j.IsFinished && string.Equals(j.Key, key, StringComparison.Ordinal));
            if (job == null)
            {
                return false;
            }

            Serilog.Log.Warning("Query job {Kind} {Id} is stopped by {User} after {Seconds:0} s", job.Kind, job.Id, user,
                job.Elapsed.TotalSeconds);
            job.Stop("The query was stopped by an administrator.");
            return true;
        }

        public QueryJob Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !_jobs.TryGetValue(id.ToLowerInvariant(), out var job))
            {
                throw new KeyNotFoundException("The query is no longer available.");
            }

            job.Touch();
            return job;
        }

        public void Cancel(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var key = id.ToLowerInvariant();
            if (_jobs.TryRemove(key, out var job))
            {
                // A shared job runs on for its other clients
                job.Leave();
            }
            else if (IsValidClientId(key))
            {
                _cancelledIds[key] = Stopwatch.GetTimestamp();
            }
        }

        private static bool IsValidClientId(string id)
        {
            return id is { Length: 32 } && id.All(Uri.IsHexDigit);
        }

        public void Dispose()
        {
            _sweep.Dispose();
            foreach (var job in _jobs.Values)
            {
                job.Cancel();
            }
        }

        private async Task RunAsync(
            QueryJob job,
            Func<IQueryProgress, CancellationToken, object> work,
            QueryCost cost,
            Func<CancellationToken, QueryCost> estimate,
            string request)
        {
            CostSlots slots = null;
            try
            {
                if (estimate != null)
                {
                    job.Report("Estimating the size of the query");
                    using (DatabaseLimits.Use(PostgresWork.Ui, job.Token))
                    {
                        cost = estimate(job.Token);
                    }
                    job.Cost = cost;
                }

                slots = await AdmitAsync(job, cost);
            }
            catch (Exception ex) when (job.Token.IsCancellationRequested
                && ex is OperationCanceledException or Npgsql.NpgsqlException)
            {
                MarkCancelledOrStopped(job);
                Unshare(job, request);
                return;
            }
            catch (Exception ex)
            {
                var (status, message) = ApiErrors.Describe(ex);
                job.MarkFailed(status, message);
                Unshare(job, request);
                return;
            }

            _running[job] = (cost, Stopwatch.GetTimestamp());
            try
            {
                job.MarkRunning();
                // The UI pool with its statement timeout; a cancelled job cancels its statement in the database
                using (DatabaseLimits.Use(PostgresWork.Ui, job.Token))
                {
                    job.MarkDone(work(job, job.Token));
                }
            }
            catch (Exception ex) when (job.Token.IsCancellationRequested
                && ex is OperationCanceledException or Npgsql.NpgsqlException)
            {
                MarkCancelledOrStopped(job);
            }
            catch (Exception ex)
            {
                var (status, message) = ApiErrors.Describe(ex);
                job.MarkFailed(status, message);
            }
            finally
            {
                _running.TryRemove(job, out _);
                slots.Release();
                Unshare(job, request);
                LogIfSlow(job);
            }
        }

        // A job that the load guard or an administrator stopped tells its clients why
        private static void MarkCancelledOrStopped(QueryJob job)
        {
            if (job.StopReason is string reason)
            {
                job.MarkFailed(503, reason);
            }
            else
            {
                job.MarkCancelled();
            }
        }

        private static void LogIfSlow(QueryJob job)
        {
            var ran = job.Ran;
            if (ran < SlowAfter)
            {
                return;
            }

            Serilog.Log.Information("Slow query job {Kind} ({Cost}): ran {Ran:0.0} s after waiting {Waited:0.0} s, {State}, {Request}",
                job.Kind, Describe(job.Cost), ran.TotalSeconds, (job.Elapsed - ran).TotalSeconds, job.ToDto().State, job.Request ?? "");
        }

        internal static string Describe(QueryCost cost) => cost switch
        {
            QueryCost.Small => "small",
            QueryCost.Large => "large",
            _ => "medium"
        };

        /// <summary>
        /// Waits until the load guard lets the class run, then for a slot of it. A guard that rose while the job waited
        /// for the slot sends it back to wait.
        /// </summary>
        private async Task<CostSlots> AdmitAsync(QueryJob job, QueryCost cost)
        {
            var slots = _slots[cost];
            if (cost == QueryCost.Small)
            {
                await slots.WaitAsync(job);
                return slots;
            }

            var holdAt = cost == QueryCost.Large ? LoadLevel.Tense : LoadLevel.Critical;
            while (true)
            {
                await _guard.WaitBelowAsync(holdAt, phase => job.Report(phase), job.Token);
                await slots.WaitAsync(job);
                if (_guard.Level < holdAt)
                {
                    return slots;
                }
                slots.Release();
            }
        }

        /// <summary>
        /// The last step of the load guard: while it stays critical, stops the oldest medium or large query, one at a
        /// time. Its clients get the reason instead of a result.
        /// </summary>
        private void StopOldestWhenCritical(long now)
        {
            if (_guard.Level != LoadLevel.Critical || _guard.LevelFor < StopAfter
                || (_lastStop != 0 && Stopwatch.GetElapsedTime(_lastStop, now) < StopInterval))
            {
                return;
            }

            var oldest = _running
                .Where(entry => entry.Value.Cost != QueryCost.Small && entry.Key.StopReason == null && !entry.Key.IsFinished)
                .OrderBy(entry => entry.Value.Since)
                .Select(entry => entry.Key)
                .FirstOrDefault();
            if (oldest == null)
            {
                return;
            }

            _lastStop = now;
            var reason = _guard.Reason;
            Serilog.Log.Warning("Load guard: query job {Kind} {Id} is stopped after {Seconds:0} s to protect the system ({Reason})",
                oldest.Kind, oldest.Id, Stopwatch.GetElapsedTime(_running.TryGetValue(oldest, out var entry) ? entry.Since : now, now).TotalSeconds, reason);
            oldest.Stop("The query was stopped to protect the system: the device is overloaded"
                + (string.IsNullOrEmpty(reason) ? "" : $" ({reason})") + ". Please try again later or choose a shorter range.");
        }

        // A finished job takes no new clients: the next start of the request reads again
        private void Unshare(QueryJob job, string request)
        {
            if (request == null)
            {
                return;
            }

            lock (_shareLock)
            {
                if (_byRequest.TryGetValue(request, out var current) && ReferenceEquals(current, job))
                {
                    _byRequest.TryRemove(request, out _);
                }
            }
        }

        private void Sweep()
        {
            var now = Stopwatch.GetTimestamp();
            StopOldestWhenCritical(now);
            foreach (var entry in _cancelledIds)
            {
                if (Stopwatch.GetElapsedTime(entry.Value, now) > KeepCancelledIds)
                {
                    _cancelledIds.TryRemove(entry.Key, out _);
                }
            }

            foreach (var (id, job) in _jobs)
            {
                if (job.IsFinished)
                {
                    if (Stopwatch.GetElapsedTime(job.FinishedAt, now) > KeepFinished)
                    {
                        _jobs.TryRemove(id, out _);
                    }
                }
                else if (Stopwatch.GetElapsedTime(job.LastSeen, now) > AbandonAfter)
                {
                    Serilog.Log.Information("Query job {Kind} {Id} was abandoned by its clients and is cancelled", job.Kind, job.Id);
                    job.Cancel();
                }
            }
        }

        /// <summary>The slots of a cost class, with the jobs that run and wait for the hint of a waiting job.</summary>
        private sealed class CostSlots(string name, int count)
        {
            private readonly SemaphoreSlim _semaphore = new(count, count);
            private int _running;
            private int _waiting;

            public async Task WaitAsync(QueryJob job)
            {
                if (!_semaphore.Wait(0))
                {
                    var waiting = Interlocked.Increment(ref _waiting);
                    var running = Volatile.Read(ref _running);
                    job.Report($"Waiting: {running} {name} {(running == 1 ? "query runs" : "queries run")}"
                        + (waiting > 1 ? $", {waiting - 1} more waiting" : ""));
                    try
                    {
                        await _semaphore.WaitAsync(job.Token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _waiting);
                    }
                }
                Interlocked.Increment(ref _running);
            }

            public void Release()
            {
                Interlocked.Decrement(ref _running);
                _semaphore.Release();
            }
        }
    }

    public sealed class QueryJob : IQueryProgress
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _lastSeen = Stopwatch.GetTimestamp();
        private long _finishedAt;
        private string _state = "queued";
        private string _phase = "Waiting for the database";
        private double? _percent;
        private long? _count;
        private string _error;
        private int? _errorStatus;
        private object _result;
        private int _clients = 1;
        private volatile string _stopReason;
        private long _runningAt;

        public QueryJob(string id, string kind)
        {
            Id = id;
            Kind = kind;
        }

        public string Id { get; }
        public string Kind { get; }
        public CancellationToken Token => _cts.Token;
        public Task Completion => _done.Task;
        public long LastSeen => Interlocked.Read(ref _lastSeen);
        public long FinishedAt => Interlocked.Read(ref _finishedAt);
        public bool IsFinished => _done.Task.IsCompleted;

        /// <summary>An id of the job itself for the activity panel of all clients; the ids of its clients stay private.</summary>
        public string Key { get; } = Guid.NewGuid().ToString("N");

        /// <summary>The cost class, known once the job was estimated.</summary>
        public QueryCost Cost { get; set; }

        /// <summary>The path and query of the request, for the log.</summary>
        public string Request { get; set; }

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started, IsFinished ? FinishedAt : Stopwatch.GetTimestamp());

        /// <summary>How long the job held its slot, zero while it waits.</summary>
        public TimeSpan Ran
        {
            get
            {
                var at = Interlocked.Read(ref _runningAt);
                return at == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(at, IsFinished ? FinishedAt : Stopwatch.GetTimestamp());
            }
        }

        /// <summary>Why the load guard stopped the job; null when it did not.</summary>
        public string StopReason => _stopReason;

        public void Report(string phase, double? percent = null, long? count = null)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(phase))
                {
                    _phase = phase;
                }

                _percent = percent;
                _count = count;
            }
        }

        public void Touch()
        {
            Interlocked.Exchange(ref _lastSeen, Stopwatch.GetTimestamp());
        }

        /// <summary>Takes one more client, unless the job is cancelled or finished.</summary>
        public bool TryJoin()
        {
            lock (_gate)
            {
                if (_clients == 0 || _cts.IsCancellationRequested || IsFinished)
                {
                    return false;
                }
                _clients++;
                return true;
            }
        }

        /// <summary>A client no longer needs the job: cancelled once no client is left.</summary>
        public void Leave()
        {
            bool last;
            lock (_gate)
            {
                last = --_clients <= 0;
            }
            if (last)
            {
                Cancel();
            }
        }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Stops the job for all of its clients, with the reason as its error.</summary>
        public void Stop(string reason)
        {
            _stopReason = reason;
            Cancel();
        }

        public void MarkRunning()
        {
            lock (_gate)
            {
                _state = "running";
                _phase = "Querying the database";
            }
            Interlocked.Exchange(ref _runningAt, Stopwatch.GetTimestamp());
        }

        public RunningQueryDto ToRunningDto(bool mine)
        {
            lock (_gate)
            {
                return new RunningQueryDto
                {
                    Key = Key,
                    Kind = Kind,
                    Cost = QueryJobService.Describe(Cost),
                    State = _state,
                    Phase = _phase,
                    Percent = _percent,
                    ElapsedMs = (long)Elapsed.TotalMilliseconds,
                    RunningMs = (long)Ran.TotalMilliseconds,
                    Clients = Math.Max(1, _clients),
                    Mine = mine
                };
            }
        }

        public void MarkDone(object result)
        {
            lock (_gate)
            {
                _state = "done";
                _result = result;
            }

            Finish();
        }

        public void MarkFailed(int status, string message)
        {
            lock (_gate)
            {
                _state = "failed";
                _errorStatus = status;
                _error = message;
            }

            Finish();
        }

        public void MarkCancelled()
        {
            lock (_gate)
            {
                _state = "cancelled";
                _errorStatus = 499;
                _error = "The query was cancelled.";
            }

            Finish();
        }

        /// <summary>The state as the client with the id sees it (a shared job has one id per client).</summary>
        public QueryJobStatusDto ToDto(string id = null)
        {
            lock (_gate)
            {
                return new QueryJobStatusDto
                {
                    Id = id ?? Id,
                    State = _state,
                    ElapsedMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    Phase = _phase,
                    Percent = _percent,
                    Count = _count,
                    Cost = QueryJobService.Describe(Cost),
                    Error = _error,
                    ErrorStatus = _errorStatus,
                    Result = _result
                };
            }
        }

        private void Finish()
        {
            Interlocked.Exchange(ref _finishedAt, Stopwatch.GetTimestamp());
            _done.TrySetResult();
        }
    }
}
