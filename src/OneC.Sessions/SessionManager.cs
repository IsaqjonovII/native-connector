using System.Diagnostics;
using OneC.Interop;

namespace OneC.Sessions;

public sealed record PoolStats(
    string BaseName, string Kind, int Live, int Idle, int InUse,
    long Created, long Retired, long Recycled, long Evicted, long Broken, long Rents, long Waits,
    long ConnectRetries = 0, int ConnectFailuresInARow = 0);

/// <summary>
/// Warm sessions for one base. Lazy: nothing exists until the first Rent. Bounded: never
/// more than min(PerBaseMaxSessions, base.MaxConcurrency). Sessions that go idle past the
/// timeout are retired so an occasionally used base stops costing RAM.
/// </summary>
public sealed class BasePool : IDisposable
{
    private readonly object _connector;
    private readonly PoolOptions _opt;
    private readonly SemaphoreSlim _slots;
    private readonly Stack<SessionWorker> _idle = new();
    private readonly HashSet<SessionWorker> _all = new();
    private readonly object _lock = new();
    private readonly Func<OneCBase, string?> _tryTakeBudget;
    private readonly Action _returnBudget;
    private readonly Action<OneCBase> _connected;

    private int _nextId;
    private long _created, _retired, _recycled, _evicted, _broken, _rents, _waits;
    private bool _disposed;

    public OneCBase Base { get; }
    public int MaxSessions { get; }

    internal BasePool(object connector, OneCBase b, PoolOptions opt,
                      Func<OneCBase, string?> tryTakeBudget, Action returnBudget,
                      Action<OneCBase> connected)
    {
        _connector = connector;
        Base = b;
        _opt = opt;
        _tryTakeBudget = tryTakeBudget;
        _returnBudget = returnBudget;
        _connected = connected;
        MaxSessions = Math.Max(1, Math.Min(opt.PerBaseMaxSessions, b.MaxConcurrency));
        _slots = new SemaphoreSlim(MaxSessions, MaxSessions);
    }

    public PoolStats Stats()
    {
        lock (_lock)
            return new PoolStats(Base.Name, Base.Kind, _all.Count, _idle.Count,
                                 _all.Count - _idle.Count, _created, _retired, _recycled,
                                 _evicted, _broken,
                                 _rents, _waits, Interlocked.Read(ref _connectRetries),
                                 Volatile.Read(ref _connectFailures));
    }

    public SessionWorker Rent(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _rents);

        var sw = Stopwatch.StartNew();
        if (!_slots.Wait(_opt.RentTimeout, ct))
            throw OneCException.Host(
                $"no session slot for '{Base.Name}' within {_opt.RentTimeout.TotalSeconds:F0}s " +
                $"(cap {MaxSessions})", new ErrorContext(Base.Name, null), "Rent");
        if (sw.ElapsedMilliseconds > 1) Interlocked.Increment(ref _waits);

        try
        {
            return AcquireSession(ct);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Renters refused by the memory budget wait here, first come first served. A returned
    /// session is handed straight to the oldest waiter instead of going back on the idle
    /// stack. Before this, waiters polled every 25 ms while the active renter returned and
    /// re-rented its session in the same instant — a waiter starved for the full 60 s rent
    /// timeout (MultiBaseTests.PressureEvicts…, 2026-09-24).
    /// </summary>
    private readonly LinkedList<TaskCompletionSource<SessionWorker>> _waiters = new();

    private SessionWorker AcquireSession(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _opt.RentTimeout;
        TaskCompletionSource<SessionWorker>? ticket = null;
        LinkedListNode<TaskCompletionSource<SessionWorker>>? node = null;
        bool delivered = false;
        try
        {
            while (true)
            {
                // 1. An idle session — but never jump the queue of earlier waiters.
                SessionWorker? w = null;
                var discard = new List<SessionWorker>();
                lock (_lock)
                {
                    bool myTurn = node is null ? _waiters.Count == 0 : _waiters.First == node;
                    while (myTurn && _idle.Count > 0)
                    {
                        var c = _idle.Pop();
                        if (!c.IsBroken && !ShouldRecycle(c)) { w = c; break; }
                        discard.Add(c);
                        Unregister(c, c.IsBroken ? null : "recycle");
                    }
                }
                // Disposing joins the session thread, so it happens outside the lock.
                foreach (var victim in discard) victim.Dispose();

                // An idle session may have died underneath us (server restart, dropped
                // network). Probe it before handing it out — outside the lock, it is a COM call.
                if (w is not null && DateTime.UtcNow - w.LastUsedUtc > _opt.ValidateIdleAfter && !w.Probe())
                {
                    lock (_lock) Unregister(w, null);
                    w.Dispose();
                    Interlocked.Increment(ref _probeFailures);
                    continue;                                   // try the next idle one, or create
                }
                if (w is not null) { delivered = true; return w; }

                // 2. A new session, if the budget allows.
                var created = TryCreate(ct, out string? refusal);
                if (created is not null) { delivered = true; return created; }

                // 3. Memory pressure, not a permanent failure: queue for a handed-back session.
                // Safe from deadlock because the first session for a base is always allowed.
                if (ticket is null)
                {
                    ticket = new TaskCompletionSource<SessionWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_lock) node = _waiters.AddLast(ticket);
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw OneCException.Host(
                        $"cannot open a new session for '{Base.Name}': {refusal}",
                        new ErrorContext(Base.Name, null), "Rent");

                // Wake on a handoff, or every 500 ms to retry creation — memory can also free
                // up when another base's idle sessions retire.
                try { ticket.Task.Wait((int)Math.Min(500, remaining.TotalMilliseconds), ct); }
                catch (AggregateException) { }
                if (ticket.Task.IsCompletedSuccessfully) { delivered = true; return ticket.Task.Result; }
            }
        }
        finally
        {
            if (node is not null)
                lock (_lock) { if (node.List is not null) _waiters.Remove(node); }
            // A handoff can land just as this renter gives up (timeout / cancel). The session
            // is not lost: it goes on to the next waiter or back to idle.
            if (!delivered && ticket is { Task.IsCompletedSuccessfully: true }) Park(ticket.Task.Result);
        }
    }

    private bool ShouldRecycle(SessionWorker w) =>
        _opt.RecycleAfterOperations > 0 && w.OperationCount >= _opt.RecycleAfterOperations;

    /// <summary>
    /// Connect, retrying only failures classified retryable — in practice the file-base
    /// temp-database sharing violation when several sessions open at once. Deterministic
    /// failures (credentials, missing base, version mismatch) fail on the first attempt.
    /// </summary>
    private SessionWorker OpenWithRetry(CancellationToken ct)
    {
        const int attempts = 3;
        for (int i = 1; ; i++)
        {
            try { return SessionWorker.Open(_connector, Base, Interlocked.Increment(ref _nextId)); }
            catch (OneCException oe) when (oe.IsRetryable && i < attempts)
            {
                Interlocked.Increment(ref _connectRetries);
                if (ct.WaitHandle.WaitOne(250 * i)) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private long _connectRetries;
    public long ConnectRetries => Interlocked.Read(ref _connectRetries);

    // Circuit breaker, ported from the old adapter (main.os:2065 ЗафиксироватьНеудачуПодключения
    // / :2089 ПодключениеВОстывании): after 3 failed Connects in a row, fail fast for 60 s, then
    // let one attempt through (half-open). A dead server or a wrong password must not make every
    // request pay a full Connect — an unreachable server alone costs ~33 s per attempt.
    public const int BreakerThreshold = 3;
    public static readonly TimeSpan BreakerCooldown = TimeSpan.FromSeconds(60);
    private int _connectFailures;
    private long _probeFailures;
    public long ProbeFailures => Interlocked.Read(ref _probeFailures);
    private DateTime _lastConnectFailureUtc;
    private OneCException? _lastConnectError;

    public int ConsecutiveConnectFailures => Volatile.Read(ref _connectFailures);

    /// <summary>Returns a new session, or null plus the reason the budget said no.</summary>
    private SessionWorker? TryCreate(CancellationToken ct, out string? refusal)
    {
        OneCException? last;
        DateTime lastAt;
        lock (_lock) { last = _lastConnectError; lastAt = _lastConnectFailureUtc; }
        if (Volatile.Read(ref _connectFailures) >= BreakerThreshold && DateTime.UtcNow - lastAt < BreakerCooldown)
        {
            var retryAt = lastAt + BreakerCooldown;
            throw OneCException.Host(
                $"'{Base.Name}' failed to connect {_connectFailures} times in a row; not trying again until " +
                $"{retryAt.ToLocalTime():HH:mm:ss}. Last error: {last?.Message}",
                new ErrorContext(Base.Name, null), "Connect",
                category: last?.Category ?? ErrorCategory.Unknown, retryable: true);
        }

        refusal = _tryTakeBudget(Base);
        if (refusal is not null) return null;
        try
        {
            var w = OpenWithRetry(ct);
            lock (_lock) { _all.Add(w); _created++; }
            Volatile.Write(ref _connectFailures, 0);
            return w;
        }
        catch (Exception ex)
        {
            _returnBudget();
            if (ex is OneCException oe)
            {
                lock (_lock) { _lastConnectError = oe; _lastConnectFailureUtc = DateTime.UtcNow; }
                Interlocked.Increment(ref _connectFailures);
            }
            throw;
        }
        finally
        {
            // Connected or failed, the session's real cost is now in the working set (or
            // nowhere) — drop the up-front reservation either way.
            _connected(Base);
        }
    }

    public void Return(SessionWorker w)
    {
        Park(w);
        _slots.Release();
    }

    /// <summary>
    /// A session coming back: dropped if unhealthy, else handed to the oldest waiter, else put
    /// on the idle stack. Touches no rent slot — the caller owns that accounting.
    /// </summary>
    private void Park(SessionWorker w)
    {
        bool drop;
        lock (_lock)
        {
            drop = _disposed || w.IsBroken || ShouldRecycle(w);
            if (drop) Unregister(w, w.IsBroken ? null : "recycle");
            else
            {
                bool handed = false;
                while (!handed && _waiters.First is { } first)
                {
                    _waiters.RemoveFirst();
                    handed = first.Value.TrySetResult(w);
                }
                if (!handed) _idle.Push(w);
            }
        }
        if (drop) w.Dispose();          // joins a thread — never under the lock
    }

    /// <summary>
    /// Drops a session from the pool's books and gives its budget slot back. Does NOT
    /// dispose it: disposing joins the session thread, and holding <c>_lock</c> across a
    /// join would let one slow shutdown block every other rent on this base.
    /// Caller holds <c>_lock</c> and must dispose the worker afterwards.
    /// </summary>
    private void Unregister(SessionWorker w, string? reason)
    {
        if (!_all.Remove(w)) return;
        switch (reason)
        {
            case "recycle": _recycled++; break;
            case "evict": _evicted++; break;
            case null: _broken++; break;
            default: _retired++; break;       // "idle"
        }
        _returnBudget();
    }

    /// <summary>Retires idle sessions older than the timeout. Returns how many went away.</summary>
    public int Sweep(DateTime nowUtc)
    {
        var doomed = new List<SessionWorker>();
        lock (_lock)
        {
            if (_idle.Count == 0) return 0;
            var keep = new Stack<SessionWorker>();
            var ordered = _idle.ToArray();       // newest first
            _idle.Clear();
            int warmKept = 0;
            // Walk newest-first so the survivors are the most recently used ones.
            for (int i = 0; i < ordered.Length; i++)
            {
                var w = ordered[i];
                bool stale = nowUtc - w.LastUsedUtc > _opt.IdleTimeout;
                if (!stale || warmKept < _opt.PerBaseMinWarm) { keep.Push(w); warmKept++; }
                else doomed.Add(w);
            }
            // keep was pushed oldest-last; restore original newest-on-top ordering
            foreach (var w in keep) _idle.Push(w);
            foreach (var w in doomed) Unregister(w, "idle");
        }
        foreach (var w in doomed) w.Dispose();
        return doomed.Count;
    }

    /// <summary>Test hook: every idle session's 1C connection dies, as on a server restart.</summary>
    internal int KillIdleConnectionsForTest()
    {
        List<SessionWorker> idle;
        lock (_lock) idle = _idle.ToList();
        foreach (var w in idle) w.KillConnectionForTest();
        return idle.Count;
    }

    /// <summary>Retires the single least recently used idle session. Used to free budget.</summary>
    public bool RetireOneIdle()
    {
        SessionWorker victim;
        lock (_lock)
        {
            if (_idle.Count == 0) return false;
            victim = _idle.OrderBy(w => w.LastUsedUtc).First();
            var rest = _idle.Where(w => !ReferenceEquals(w, victim)).ToList();
            _idle.Clear();
            for (int i = rest.Count - 1; i >= 0; i--) _idle.Push(rest[i]);
            Unregister(victim, "evict");
        }
        victim.Dispose();
        return true;
    }

    public DateTime? OldestIdleUtc
    {
        get { lock (_lock) return _idle.Count == 0 ? null : _idle.Min(w => w.LastUsedUtc); }
    }

    public void Dispose()
    {
        List<SessionWorker> all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = _all.ToList();
            _all.Clear();
            _idle.Clear();
        }
        foreach (var w in all) { w.Dispose(); _returnBudget(); }
        _slots.Dispose();
    }
}

/// <summary>
/// Everything the host owns on the 1C side: one connector, one pool per base, one global
/// session budget, one sweeper. Bases are registered cheaply; sessions appear on demand.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly object _connector;
    private readonly PoolOptions _opt;
    private readonly object _admitLock = new();
    private long _pendingMb;
    private readonly Dictionary<string, BasePool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OneCBase> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Timer _sweeper;
    private int _budgetUsed;
    private bool _disposed;

    public PoolOptions Options => _opt;
    public string ComcntrPath { get; }

    private long _sweepFailures;
    public long SweepFailures => Interlocked.Read(ref _sweepFailures);
    public string? LastSweepError { get; private set; }
    public int BudgetUsed => Volatile.Read(ref _budgetUsed);

    public SessionManager(string comcntrPath, PoolOptions? options = null)
    {
        _opt = options ?? new PoolOptions();
        ComActivator.Bind(comcntrPath);
        ComcntrPath = ComActivator.BoundPath!;
        // The process-lifetime connector: never released, because releasing the last one and
        // creating another crashes comcntr (DECISIONS D20). Managers come and go; it stays.
        _connector = ComActivator.SharedConnector();
        if (_opt.ConnectorPoolCapacity > 0)
        {
            var ctx = new ErrorContext(null, null);
            ConnectorApi.SetPool(_connector, _opt.ConnectorPoolCapacity, 60, ctx);
        }
        // Timer thread: an escaping exception would terminate the host process.
        _sweeper = new Timer(_ =>
        {
            try { SweepIdle(); }
            catch (Exception ex) { Interlocked.Increment(ref _sweepFailures); LastSweepError = ex.Message; }
        }, null, _opt.SweepInterval, _opt.SweepInterval);
    }

    /// <summary>Registers a base. Creates no session and costs no 1C resources.</summary>
    public void Register(OneCBase b)
    {
        lock (_lock) _bases[b.Name] = b;
    }

    public IReadOnlyCollection<OneCBase> Bases { get { lock (_lock) return _bases.Values.ToList(); } }

    public OneCBase GetBase(string baseName)
    {
        lock (_lock)
            return _bases.TryGetValue(baseName, out var b)
                ? b
                : throw OneCException.Host($"base '{baseName}' is not registered",
                                           new ErrorContext(baseName, null), "GetBase");
    }

    private BasePool PoolFor(string baseName)
    {
        lock (_lock)
        {
            if (_pools.TryGetValue(baseName, out var p)) return p;
            if (!_bases.TryGetValue(baseName, out var b))
                throw OneCException.Host($"base '{baseName}' is not registered",
                                         new ErrorContext(baseName, null), "PoolFor");
            p = new BasePool(_connector, b, _opt, TryTakeBudget, ReturnBudget, Connected);
            _pools[baseName] = p;
            return p;
        }
    }

    /// <summary>
    /// Two ceilings, not one: a session count and a working-set budget. A file-base session
    /// costs about four times a server one, so counting alone would let one host reach
    /// 1.7 GB while another sits at 400 MB. Returns null on success, or why it refused.
    ///
    /// Admission is serialised and reserves the new session's estimated cost until it has
    /// connected. Without that, concurrent renters all read the working set before any new
    /// session had grown it, and a 600 MB ceiling let the host reach 753 MB (milestone 2.2).
    /// </summary>
    private string? TryTakeBudget(OneCBase b)
    {
        lock (_admitLock)
        {
            while (true)
            {
                int used = Volatile.Read(ref _budgetUsed);

                if (used >= _opt.GlobalMaxSessions)
                {
                    // Full. Reclaim somebody's idle session rather than failing outright: an
                    // idle session for another base must never block live work.
                    if (EvictOneIdleAnywhere(b.Name)) continue;
                    return $"host session budget exhausted ({_opt.GlobalMaxSessions} sessions)";
                }

                long ws = CurrentWorkingSetMb();
                long pending = Volatile.Read(ref _pendingMb);
                if (used > 0 && ws + pending + b.EstimatedSessionMb > _opt.MaxWorkingSetMb)
                {
                    if (EvictOneIdleAnywhere(b.Name)) continue;
                    return $"memory budget exhausted (working set {ws} MB + {pending} MB connecting " +
                           $"+ ~{b.EstimatedSessionMb} MB for a {b.Kind} session would pass " +
                           $"{_opt.MaxWorkingSetMb} MB)";
                }

                Interlocked.Increment(ref _budgetUsed);
                Interlocked.Add(ref _pendingMb, b.EstimatedSessionMb);
                return null;
            }
        }
    }

    private void Connected(OneCBase b) => Interlocked.Add(ref _pendingMb, -b.EstimatedSessionMb);

    private static long CurrentWorkingSetMb()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1024 / 1024;
    }

    private void ReturnBudget() => Interlocked.Decrement(ref _budgetUsed);

    /// <summary>
    /// Frees one idle session from any OTHER base. Never from the requesting base: evicting
    /// your own idle session to open a new one for yourself is net zero sessions plus a
    /// reconnect — milestone 2.2 caught a file base doing exactly that under pressure. The
    /// requester should wait for its own idle session instead, which Rent already does.
    /// </summary>
    private bool EvictOneIdleAnywhere(string requestingBase)
    {
        List<BasePool> pools;
        lock (_lock) pools = _pools.Values.ToList();
        foreach (var p in pools.Where(p => !p.Base.Name.Equals(requestingBase, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(p => p.OldestIdleUtc ?? DateTime.MaxValue))
            if (p.RetireOneIdle()) return true;
        return false;
    }

    /// <summary>Borrows a session for one base and guarantees it goes back.</summary>
    public T Use<T>(string baseName, Func<SessionContext, T> work, CancellationToken ct = default)
    {
        var pool = PoolFor(baseName);
        var w = pool.Rent(ct);
        try { return w.Execute(work, ct); }
        catch (OneCException oe) when (oe.IsSessionFatal) { w.MarkBroken(); throw; }
        finally { pool.Return(w); }
    }

    public void Use(string baseName, Action<SessionContext> work, CancellationToken ct = default)
        => Use<object?>(baseName, c => { work(c); return null; }, ct);

    public int SweepIdle()
    {
        if (_disposed) return 0;
        List<BasePool> pools;
        lock (_lock) pools = _pools.Values.ToList();
        var now = DateTime.UtcNow;
        return pools.Sum(p => p.Sweep(now));
    }

    public IReadOnlyList<PoolStats> Stats()
    {
        lock (_lock) return _pools.Values.Select(p => p.Stats()).ToList();
    }

    internal int KillIdleConnectionsForTest(string baseName)
    {
        BasePool? p;
        lock (_lock) _pools.TryGetValue(baseName, out p);
        return p?.KillIdleConnectionsForTest() ?? 0;
    }

    internal long ProbeFailures(string baseName)
    {
        BasePool? p;
        lock (_lock) _pools.TryGetValue(baseName, out p);
        return p?.ProbeFailures ?? 0;
    }

    /// <summary>Drops every session for a base but keeps it registered.</summary>
    public void DrainBase(string baseName)
    {
        BasePool? p;
        lock (_lock) { _pools.Remove(baseName, out p); }
        p?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper.Dispose();
        List<BasePool> pools;
        lock (_lock) { pools = _pools.Values.ToList(); _pools.Clear(); }
        foreach (var p in pools) p.Dispose();
        // The connector is deliberately not released: see ComActivator.SharedConnector.
    }
}
