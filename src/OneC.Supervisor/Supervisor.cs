using OneC.Ipc;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Supervisor;

public sealed record SupervisorOptions
{
    public required string HostExe { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int HostIdleTimeoutSeconds { get; init; } = 300;
    public int HostMaxWorkingSetMb { get; init; } = 1200;

    /// <summary>Per-host session budget; the host's in-flight request limit is 4× this.</summary>
    public int HostGlobalMaxSessions { get; init; } = 8;

    /// <summary>
    /// Recycle a host that holds no sessions yet still has a working set above this
    /// (DECISIONS D24): churn leaves native memory in comcntr that only ending the process
    /// returns. 400 MB sits well above a fresh host (~35 MB) and the residue after a single
    /// session cycle (~75–100 MB), and below what a churned file base retains (385–540 MB).
    /// Zero disables it.
    /// </summary>
    public int RecycleIdleAboveMb { get; init; } = 400;

    /// <summary>Restart backoff after a crash: 1 s, 2 s, 4 s … capped.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record HostEvent(DateTime Utc, string Key, string What);

/// <summary>
/// Owns one OneC.Host process per host key from <see cref="HostPlan"/>, keeps them alive,
/// and recycles them. Never loads comcntr itself (DECISIONS D02): it only reads the disk to
/// find installs and talks to its children over pipes.
/// </summary>
public sealed class Supervisor : IDisposable
{
    private readonly SupervisorOptions _opt;
    private readonly Dictionary<string, HostProcess> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _restarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextStartUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HostEvent> _events = new();
    private readonly object _lock = new();
    private Timer? _monitor;
    private bool _disposed;

    public IReadOnlyList<(OneCBase Base, string Reason)> Unplaceable { get; private set; } = Array.Empty<(OneCBase, string)>();

    public Supervisor(SupervisorOptions opt)
    {
        if (!File.Exists(opt.HostExe)) throw new FileNotFoundException("OneC.Host.exe not found", opt.HostExe);
        _opt = opt;
    }

    public IReadOnlyList<HostEvent> Events { get { lock (_lock) return _events.ToList(); } }
    public IReadOnlyCollection<HostProcess> Hosts { get { lock (_lock) return _hosts.Values.ToList(); } }
    public int Restarts(string key) { lock (_lock) return _restarts.GetValueOrDefault(key); }

    /// <summary>Plans, starts every host, waits for each to report ready.</summary>
    public void Start(IEnumerable<OneCBase> bases, IReadOnlyList<PlatformInstall>? installs = null)
    {
        var (plan, bad) = HostPlan.Build(bases, installs ?? PlatformCatalog.Discover());
        Unplaceable = bad;
        foreach (var a in plan) StartHost(a);
        _monitor = new Timer(_ => Tick(), null, _opt.MonitorInterval, _opt.MonitorInterval);
    }

    public HostProcess HostFor(string baseName)
    {
        lock (_lock)
            return _hosts.Values.FirstOrDefault(h =>
                       h.Assignment.Bases.Any(b => b.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                   ?? throw new KeyNotFoundException($"no host serves base '{baseName}'");
    }

    /// <summary>Routes a request to the host that serves the base.</summary>
    public Task<IpcResponse> SendAsync(string baseName, string op, JsonObject? args = null,
                                       int? deadlineMs = null, CancellationToken ct = default)
    {
        HostProcess h;
        try { h = HostFor(baseName); }
        catch (KeyNotFoundException e)
        {
            return Task.FromResult(IpcResponse.Failure(0, IpcError.Of(Layers.Host, e.Message, kind: ErrorKinds.NotFound), 0));
        }
        return h.SendAsync(op, baseName, args, deadlineMs, ct);
    }

    public IpcResponse Send(string baseName, string op, JsonObject? args = null, int? deadlineMs = null)
        => SendAsync(baseName, op, args, deadlineMs).GetAwaiter().GetResult();

    private readonly OneC.EventLog.LogLocator _logs = new();
    private readonly OneC.EventLog.EventLogReader _logReader = new();

    /// <summary>
    /// Data changes from the base's 1C event log after <paramref name="cursor"/> (null = start at
    /// the tail). Served here, from files — no host round trip, no 1C session (D36).
    /// </summary>
    public OneC.EventLog.ChangeBatch Changes(string baseName, OneC.EventLog.LogCursor? cursor, long maxBytes)
    {
        OneCBase b;
        lock (_lock)
            b = _hosts.Values.SelectMany(h => h.Assignment.Bases).Concat(Unplaceable.Select(u => u.Base))
                      .FirstOrDefault(x => x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"base '{baseName}' not registered");
        var (dir, error) = _logs.Resolve(b.ConnectionString);
        if (dir is null) throw new InvalidOperationException($"event log of '{baseName}' is not readable here: {error}");
        return _logReader.Read(dir, cursor, maxBytes);
    }

    /// <summary>Every configured base, where it is served, and the ones that could not be placed.</summary>
    public IReadOnlyList<(string Base, string Kind, string? HostKey)> Bases()
    {
        lock (_lock)
            return _hosts.Values.SelectMany(h => h.Assignment.Bases.Select(b => (b.Name, b.Kind, (string?)h.Key)))
                         .Concat(Unplaceable.Select(u => (u.Base.Name, u.Base.Kind, (string?)null)))
                         .ToList();
    }

    private void StartHost(HostAssignment a)
    {
        var h = new HostProcess(a);
        h.Start(_opt.HostExe, _opt.HostIdleTimeoutSeconds, _opt.HostMaxWorkingSetMb,
                _opt.HostGlobalMaxSessions, _opt.StartTimeout);
        lock (_lock) _hosts[a.Key] = h;
        bool ok = h.WaitReady(_opt.StartTimeout);
        Log(a.Key, ok
            ? $"ready pid={h.Pid} comcntr={h.ReadyInfo?["comcntrVersion"]} bases={string.Join(",", a.Bases.Select(b => b.Name))}"
            : $"did not become ready: state={h.State} error={h.LastError}");
    }

    /// <summary>One monitor pass: restart dead hosts, recycle bloated idle ones.</summary>
    public void Tick()
    {
        if (_disposed) return;
        // A restart can take up to StartTimeout; never let the timer run two passes at once
        // and start two replacements for one dead host.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        // Runs on a Timer thread: an exception escaping here would terminate the supervisor
        // and, with it, every host. Log and carry on to the next pass.
        try { TickCore(); }
        catch (Exception ex) { Log("supervisor", $"monitor pass failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private int _ticking;

    private void TickCore()
    {
        List<HostProcess> hosts;
        lock (_lock) hosts = _hosts.Values.ToList();

        foreach (var h in hosts)
        {
            if (h.StuckInExit)
            {
                // Its Exited event fires once the kill lands; the next pass restarts it.
                Log(h.Key, "host stuck in its own exit (exit code set, process still running): killing it");
                h.Kill();
                continue;
            }
            if (h.State is HostState.Exited or HostState.Failed)
            {
                RestartWithBackoff(h, $"host exited (code {h.ExitCode}, state {h.State}, last error: {h.LastError ?? "-"})");
                continue;
            }
            if (h.State != HostState.Ready || _opt.RecycleIdleAboveMb <= 0) continue;

            var (ws, _) = h.Memory();
            if (ws <= _opt.RecycleIdleAboveMb) continue;

            // Only recycle a host with NO sessions at all: every base has gone quiet and been
            // idle-retired, so what is left is comcntr's churn residue (D24). Recycling a host
            // that still holds warm sessions would cause the very churn D24 says to avoid,
            // and cutting an in-flight request is never acceptable.
            var stats = h.Send(Ops.Stats, deadlineMs: 10_000);
            if (!stats.Ok) { Log(h.Key, "stats failed during recycle check: " + stats.Error?.Message); continue; }
            int sessions = stats.Result?["sessions"]?.GetValue<int>() ?? 1;
            if (sessions == 0) Recycle(h, $"no sessions, {ws} MB > {_opt.RecycleIdleAboveMb} MB");
        }
    }

    /// <summary>Graceful stop and fresh start. Returns the replacement.</summary>
    public HostProcess Recycle(HostProcess h, string why)
    {
        Log(h.Key, "recycle: " + why);
        h.Stop(TimeSpan.FromSeconds(15));
        h.Dispose();
        StartHost(h.Assignment);
        lock (_lock) return _hosts[h.Key];
    }

    private void RestartWithBackoff(HostProcess dead, string why)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_nextStartUtc.TryGetValue(dead.Key, out var at) && now < at) return;
            int n = _restarts[dead.Key] = _restarts.GetValueOrDefault(dead.Key) + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(_opt.MaxBackoff.TotalSeconds, Math.Pow(2, n - 1)));
            _nextStartUtc[dead.Key] = now + delay;
        }
        Log(dead.Key, "restart: " + why);
        dead.Dispose();
        StartHost(dead.Assignment);
    }

    private void Log(string key, string what)
    {
        lock (_lock) { _events.Add(new HostEvent(DateTime.UtcNow, key, what)); if (_events.Count > 1000) _events.RemoveAt(0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor?.Dispose();
        List<HostProcess> hosts;
        lock (_lock) { hosts = _hosts.Values.ToList(); _hosts.Clear(); }
        foreach (var h in hosts) h.Dispose();
    }
}
