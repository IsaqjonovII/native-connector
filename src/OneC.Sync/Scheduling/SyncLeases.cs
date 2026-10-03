namespace OneC.Sync.Scheduling;

/// <summary>
/// The sync lease class (§14 admission 2): how many 1C sessions sync may use, per base and in
/// total, on top of the host's own <c>SessionManager</c> budgets. Foreground work outranks it: a
/// user read waiting on a base, or a recent write there, makes <see cref="ShouldYield"/> true and
/// sync readers stop at their next page boundary. Thread-safe.
/// </summary>
public sealed class SyncLeases
{
    private readonly Lock _gate = new();
    private readonly SyncBudgets _budgets;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _foreground = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _quietUntil = new(StringComparer.Ordinal);
    private int _total;

    public SyncLeases(SyncBudgets budgets, Func<DateTimeOffset> now)
    {
        _budgets = budgets;
        _now = now;
    }

    public int Total { get { lock (_gate) return _total; } }
    public int HeldBy(string baseId) { lock (_gate) return _held.GetValueOrDefault(baseId); }

    /// <summary>A session lease for sync on this base, or null when a cap or foreground work says no.</summary>
    public Lease? TryAcquire(string baseId, bool isFile)
    {
        lock (_gate)
        {
            if (Yielding(baseId)) return null;
            int held = _held.GetValueOrDefault(baseId);
            if (held >= _budgets.SessionsFor(isFile) || _total >= _budgets.SyncSessionsGlobal) return null;
            _held[baseId] = held + 1;
            _total++;
            return new Lease(this, baseId);
        }
    }

    private void Release(string baseId)
    {
        lock (_gate)
        {
            if (_held.GetValueOrDefault(baseId) is var h && h > 0)
            {
                if (h == 1) _held.Remove(baseId); else _held[baseId] = h - 1;
                _total--;
            }
        }
    }

    /// <summary>A user read on the base is waiting: sync yields until the returned scope ends.</summary>
    public IDisposable Foreground(string baseId)
    {
        lock (_gate) _foreground[baseId] = _foreground.GetValueOrDefault(baseId) + 1;
        return new Scope(() =>
        {
            lock (_gate)
            {
                int n = _foreground.GetValueOrDefault(baseId) - 1;
                if (n <= 0) _foreground.Remove(baseId); else _foreground[baseId] = n;
            }
        });
    }

    /// <summary>A user write on the base: no new sync work starts during the quiet window.</summary>
    public void NoteWrite(string baseId)
    {
        lock (_gate) _quietUntil[baseId] = _now() + _budgets.WriteQuiet;
    }

    /// <summary>Checked by readers at every page boundary and by the executor before each item.</summary>
    public bool ShouldYield(string baseId)
    {
        lock (_gate) return Yielding(baseId);
    }

    private bool Yielding(string baseId) =>
        _foreground.ContainsKey(baseId) || (_quietUntil.TryGetValue(baseId, out var q) && q > _now());

    public sealed class Lease : IDisposable
    {
        private SyncLeases? _owner;
        internal Lease(SyncLeases owner, string baseId) { _owner = owner; BaseId = baseId; }
        public string BaseId { get; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(BaseId);
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
