namespace OneC.Sync.Scheduling;

/// <summary>Work classes, highest first (§14 Priority). Lower number wins.</summary>
public static class SyncPriority
{
    public const int Destructive = 0;     // deletes, recorder reconciles
    public const int Incremental = 1;     // other feed work, reading the feed itself
    public const int Recovery = 2;        // verify pass after a feed reset or state loss
    public const int Fallback = 3;        // version scans of bases without a readable feed
    public const int Snapshot = 4;        // cold reads get what is left
}

/// <summary>What a base still has to do, from its durable state (sync.db). Null = nothing.</summary>
public readonly record struct BaseDemand(int Priority, DateTimeOffset DueAt);

public interface IBaseDemand
{
    BaseDemand? Get(string baseId, DateTimeOffset now);
}

/// <param name="Stop">Cancelled when the scheduler wants the base back (fairness, a higher priority waits, shutdown): stop at the next page boundary.</param>
public sealed record BaseActivation(string BaseId, bool IsFile, int Priority, bool FeedGrew, SyncLeases Leases, SyncBudgets Budgets, CancellationToken Stop);

/// <summary>A base's sync work while it is active: returns when it has nothing left or <c>Stop</c> fires.</summary>
public interface IBaseAgent
{
    Task RunAsync(BaseActivation activation);
}

/// <summary>
/// The one global scheduler (§14). A base is active only while it has something to do; an idle
/// base costs one stat of its newest log file per poll interval and nothing else. Admission caps
/// the active bases; the lease class caps sessions. Ready bases are served by priority, then least
/// recently served (round-robin); a base yields after <see cref="SyncBudgets.MaxSlice"/> while
/// others wait, and immediately when a higher-priority base waits and no slot is free.
///
/// <see cref="Tick"/> is deterministic (tests drive it with a fake clock); <see cref="RunAsync"/>
/// calls it on a timer.
/// </summary>
public sealed class SyncScheduler
{
    private sealed class BaseState
    {
        public required string BaseId;
        public required bool IsFile;
        public bool FeedGrew;
        public DateTimeOffset LastServedAt = DateTimeOffset.MinValue;
        public Active? Running;
    }

    private sealed record Active(int Priority, DateTimeOffset Since, CancellationTokenSource Stop, Task Task);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, BaseState> _bases = new(StringComparer.Ordinal);
    private readonly FeedStatPoller _poller;
    private readonly IBaseDemand _demand;
    private readonly Func<string, IBaseAgent> _agents;
    private readonly Func<DateTimeOffset> _now;

    public SyncScheduler(SyncBudgets budgets, IFeedStat stat, IBaseDemand demand, Func<string, IBaseAgent> agents, Func<DateTimeOffset> now)
    {
        Budgets = budgets;
        _poller = new FeedStatPoller(stat, budgets);
        _demand = demand;
        _agents = agents;
        _now = now;
        Leases = new SyncLeases(budgets, now);
    }

    public SyncBudgets Budgets { get; }
    public SyncLeases Leases { get; }
    public long StatCalls => _poller.StatCalls;
    public long Activations { get; private set; }
    public long Preemptions { get; private set; }

    public IReadOnlyList<string> ActiveBases { get { lock (_gate) return _bases.Values.Where(b => b.Running is not null).Select(b => b.BaseId).ToList(); } }

    /// <param name="logDir">The base's event-log directory, or null when it has no readable feed (fallback only).</param>
    public void AddBase(string baseId, bool isFile, string? logDir)
    {
        lock (_gate)
        {
            // FeedGrew starts true: after a restart the stored cursor may be far behind the log, and
            // the poller only sees growth from its own first look on (found by the S15 kill test —
            // a restarted engine sat idle with its cursor 51 KB behind). One drain that finds nothing
            // costs a stat and a read of zero bytes.
            _bases[baseId] = new BaseState { BaseId = baseId, IsFile = isFile, FeedGrew = logDir is not null };
            if (logDir is not null) _poller.Watch(baseId, logDir, _now());
        }
    }

    public void RemoveBase(string baseId)
    {
        lock (_gate)
        {
            if (_bases.Remove(baseId, out var b)) b.Running?.Stop.Cancel();
            _poller.Unwatch(baseId);
        }
    }

    /// <summary>One scheduling round: poll logs, find ready bases, preempt, admit. Returns the bases activated.</summary>
    public List<string> Tick()
    {
        lock (_gate)
        {
            var now = _now();
            foreach (var id in _poller.Tick(now))
                if (_bases.TryGetValue(id, out var g)) g.FeedGrew = true;

            // Finished agents free their slot.
            foreach (var b in _bases.Values)
                if (b.Running is { Task.IsCompleted: true }) { b.Running.Stop.Dispose(); b.Running = null; }

            var ready = new List<(BaseState Base, int Priority)>();
            foreach (var b in _bases.Values.Where(b => b.Running is null))
            {
                int? p = b.FeedGrew ? SyncPriority.Incremental : null;
                if (_demand.Get(b.BaseId, now) is { } d && d.DueAt <= now) p = p is null ? d.Priority : Math.Min(p.Value, d.Priority);
                if (p is not null) ready.Add((b, p.Value));
            }
            ready.Sort((x, y) => x.Priority != y.Priority ? x.Priority.CompareTo(y.Priority) : x.Base.LastServedAt.CompareTo(y.Base.LastServedAt));

            Preempt(ready, now);

            var activated = new List<string>();
            int active = _bases.Values.Count(b => b.Running is not null);
            foreach (var (b, p) in ready)
            {
                if (active >= Budgets.MaxActiveBases) break;
                Activate(b, p, now);
                activated.Add(b.BaseId);
                active++;
            }
            return activated;
        }
    }

    /// <summary>
    /// Frees slots for waiting bases: a running base of lower priority than the best waiting one
    /// stops now; any running base past its slice stops if someone waits at its priority or better.
    /// Stopping is cooperative (next page boundary); the slot frees when its task ends.
    /// </summary>
    private void Preempt(List<(BaseState Base, int Priority)> ready, DateTimeOffset now)
    {
        if (ready.Count == 0) return;
        var running = _bases.Values.Where(b => b.Running is { Stop.IsCancellationRequested: false }).ToList();
        int free = Math.Max(0, Budgets.MaxActiveBases - _bases.Values.Count(b => b.Running is not null));
        // Bases that will not get a slot this round, best first; each may stop one running base.
        foreach (var (_, wp) in ready.Skip(free))
        {
            var victim = running
                .Where(r => r.Running!.Priority > wp || (r.Running.Priority >= wp && now - r.Running.Since >= Budgets.MaxSlice))
                .OrderByDescending(r => r.Running!.Priority).ThenBy(r => r.Running!.Since)
                .FirstOrDefault();
            if (victim is null) break;
            victim.Running!.Stop.Cancel();
            running.Remove(victim);
            Preemptions++;
        }
    }

    private void Activate(BaseState b, int priority, DateTimeOffset now)
    {
        var stop = new CancellationTokenSource();
        bool grew = b.FeedGrew;
        b.FeedGrew = false;
        b.LastServedAt = now;
        var activation = new BaseActivation(b.BaseId, b.IsFile, priority, grew, Leases, Budgets, stop.Token);
        var agent = _agents(b.BaseId);
        Task task;
        try { task = Task.Run(() => agent.RunAsync(activation)); }
        catch (Exception e) { task = Task.FromException(e); }
        b.Running = new Active(priority, now, stop, task);
        Activations++;
    }

    public long TickFailures { get; private set; }
    public string? LastTickError { get; private set; }

    /// <summary>Drives <see cref="Tick"/> until cancelled; on exit stops every agent and waits for them.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A failed tick (sync.db busy or full, a stat that throws) is recorded and the next one
                // runs: it must never end the loop, which would stop every base's sync silently
                // (2026-10-01 review).
                try { Tick(); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    TickFailures++;
                    LastTickError = $"{DateTime.UtcNow:O} {e.GetType().Name}: {e.Message}";
                }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        List<Task> tasks;
        lock (_gate)
        {
            tasks = _bases.Values.Where(b => b.Running is not null).Select(b => { b.Running!.Stop.Cancel(); return b.Running.Task; }).ToList();
        }
        try { await Task.WhenAll(tasks); } catch { /* agents report their own failures */ }
    }
}
