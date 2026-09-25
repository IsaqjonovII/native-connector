using System.Text.Json;

namespace OneC.Sync;

/// <summary>One base to sync: its backend partition and the backend's table names.</summary>
public sealed record SyncBaseConfig(string Base, string OneCId, IReadOnlyList<string> Tables, DateTime? From = null);

/// <summary>
/// The sync's configuration (<c>--sync-config</c>). Target and credentials are the user's choice
/// (D39); until then the target is the local stub.
/// </summary>
public sealed record SyncConfig
{
    /// <summary>Service root, e.g. <c>https://aiba-1c-dev.aiba.uz/</c>; empty = start the local stub.</summary>
    public string? Target { get; init; }
    /// <summary>User JWT for uploads (backend/1c refuses a service secret on upload).</summary>
    public string? Token { get; init; }
    public string StateDir { get; init; } = Path.Combine(Path.GetTempPath(), "aiba-sync");
    public int IntervalSeconds { get; init; } = 30;
    public List<SyncBaseConfig> Bases { get; init; } = new();

    public static SyncConfig Load(string path) =>
        JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new ArgumentException($"{path} is not a sync config");
}

/// <summary>The last pass of one base, for the supervisor's status route.</summary>
public sealed record SyncStatus(string Base, DateTime? LastRunUtc, bool LastOk, string? LastError, int ConsecutiveFailures,
                                int Passes, int FeedEvents, IReadOnlyDictionary<string, int> Uploaded, IReadOnlyList<string> Warnings);

/// <summary>
/// Runs <see cref="SyncEngine"/> passes per base on an interval: one pass at a time per base (the
/// next starts after the previous ended — never an overlapping tick, the connector's
/// setInterval lesson), bases in parallel. A failed pass backs off 2, 4, 8 … up to 300 s and is
/// reported; the state on disk means a retry resumes where the failure left it.
/// </summary>
public sealed class SyncScheduler : IAsyncDisposable
{
    private readonly SyncEngine _engine;
    private readonly SyncConfig _config;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = new();
    private readonly Dictionary<string, SyncStatus> _status = new(StringComparer.Ordinal);

    public SyncScheduler(SyncEngine engine, SyncConfig config)
    {
        _engine = engine;
        _config = config;
    }

    public void Start()
    {
        foreach (var b in _config.Bases) _loops.Add(Task.Run(() => LoopAsync(b, _stop.Token)));
    }

    public IReadOnlyList<SyncStatus> Status()
    {
        lock (_status) return _status.Values.OrderBy(s => s.Base, StringComparer.Ordinal).ToList();
    }

    private async Task LoopAsync(SyncBaseConfig b, CancellationToken ct)
    {
        var tables = b.Tables.Select(t => SyncTable.Parse(t, b.From)).ToList();
        string path = Path.Combine(_config.StateDir, $"{Safe(b.Base)}.{Safe(b.OneCId)}.json");
        int failures = 0, passes = 0, feed = 0;
        var uploaded = new Dictionary<string, int>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var report = await _engine.RunOnceAsync(b.Base, b.OneCId, tables, BaseState.Load(path), path, ct);
                passes++;
                feed += report.FeedEvents;
                foreach (var (t, (u, _, _)) in report.Tables) uploaded[t] = uploaded.GetValueOrDefault(t) + u;
                failures = 0;
                Set(new SyncStatus(b.Base, DateTime.UtcNow, true, null, 0, passes, feed, new Dictionary<string, int>(uploaded), report.Warnings));
                wait = TimeSpan.FromSeconds(_config.IntervalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                failures++;
                Set(new SyncStatus(b.Base, DateTime.UtcNow, false, $"{e.GetType().Name}: {e.Message}", failures, passes, feed,
                                   new Dictionary<string, int>(uploaded), Array.Empty<string>()));
                wait = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failures)));
            }
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Set(SyncStatus s)
    {
        lock (_status) _status[s.Base] = s;
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
