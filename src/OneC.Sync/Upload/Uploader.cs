using OneC.Sync.Abstractions;
using OneC.Sync.Scheduling;

namespace OneC.Sync.Upload;

/// <summary>Work cannot continue right now for a reason the base (or table) must wait out or show (§22).</summary>
public sealed class SyncPausedException(Outcome outcome, string message, int? http = null) : Exception(message)
{
    public Outcome Outcome { get; } = outcome;
    public int? HttpStatus { get; } = http;
}

/// <summary>
/// The global upload budget (§15): at most <see cref="SyncBudgets.UploadConcurrency"/> uploads in
/// flight, <see cref="SyncBudgets.UploadConcurrencyPerBase"/> per base, and
/// <see cref="SyncBudgets.BufferedBytesMax"/> bytes of pages and batches held in memory. Shared by
/// every base's pipeline.
/// </summary>
public sealed class UploadGate
{
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, SemaphoreSlim> _perBase = new(StringComparer.Ordinal);
    private readonly int _perBaseLimit;

    public UploadGate(SyncBudgets budgets)
    {
        _global = new SemaphoreSlim(budgets.UploadConcurrency, budgets.UploadConcurrency);
        _perBaseLimit = budgets.UploadConcurrencyPerBase;
        Bytes = new ByteBudget(budgets.BufferedBytesMax);
    }

    public ByteBudget Bytes { get; }
    public int InFlight { get; private set; }
    public int PeakInFlight { get; private set; }

    public async Task<IDisposable> EnterAsync(string baseId, CancellationToken ct)
    {
        SemaphoreSlim mine;
        lock (_perBase)
            if (!_perBase.TryGetValue(baseId, out mine!)) _perBase[baseId] = mine = new SemaphoreSlim(_perBaseLimit, _perBaseLimit);
        await mine.WaitAsync(ct);
        try { await _global.WaitAsync(ct); }
        catch { mine.Release(); throw; }
        lock (_perBase) { InFlight++; PeakInFlight = Math.Max(PeakInFlight, InFlight); }
        return new Exit(() => { lock (_perBase) InFlight--; _global.Release(); mine.Release(); });
    }

    private sealed class Exit(Action a) : IDisposable
    {
        private Action? _a = a;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}

/// <summary>A counting semaphore in bytes. A single request larger than the budget waits for an empty budget, then passes alone.</summary>
public sealed class ByteBudget(long max)
{
    private readonly Lock _gate = new();
    private readonly Queue<(long Bytes, TaskCompletionSource Tcs)> _waiting = new();
    private long _used;

    public long Max { get; } = max;
    public long Used { get { lock (_gate) return _used; } }
    public long Peak { get; private set; }

    public Task AcquireAsync(long bytes, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_waiting.Count == 0 && Fits(bytes)) { Take(bytes); return Task.CompletedTask; }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue((bytes, tcs));
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
    }

    public void Release(long bytes)
    {
        lock (_gate)
        {
            _used -= bytes;
            while (_waiting.Count > 0 && (_waiting.Peek().Tcs.Task.IsCanceled || Fits(_waiting.Peek().Bytes)))
            {
                var (b, t) = _waiting.Dequeue();
                if (t.Task.IsCanceled) continue;
                Take(b);
                t.TrySetResult();
            }
        }
    }

    private bool Fits(long bytes) => _used == 0 || _used + bytes <= Max;
    private void Take(long bytes) { _used += bytes; Peak = Math.Max(Peak, _used); }
}

/// <summary>
/// Sends one batch with the retry rules of §22: transient failures and 429 back off in place (honouring
/// Retry-After) a bounded number of times, then pause the work; auth, gone, policy and whole-batch
/// validation pause it at once. Per-row rejections and stale rows are returned for the caller to record.
/// </summary>
public sealed class Uploader(IBackendSyncTarget target, UploadGate gate)
{
    // Short on purpose: a blip is ridden out here, an outage waits in the queue (§22 — backoff lives
    // in sync.db, not in a task holding a base's slot; S15 saw 31 s per item with 6 attempts).
    public int MaxAttempts { get; init; } = 3;
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(n);

    private long _rows, _bytes, _mismatches;
    public long RowsSent => Interlocked.Read(ref _rows);
    public long BytesSent => Interlocked.Read(ref _bytes);
    /// <summary>v2 only: batches whose reported count differed from the rows sent — a hint, not proof (§18).</summary>
    public long CountMismatches => Interlocked.Read(ref _mismatches);

    public async Task<UploadResult> UploadAsync(string baseId, UploadBatch batch, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            UploadResult r;
            using (await gate.EnterAsync(baseId, ct))
                r = await target.UploadRowsAsync(batch, ct);
            switch (r.Outcome)
            {
                case Outcome.Ok:
                    Interlocked.Add(ref _rows, batch.Rows.Count);
                    Interlocked.Add(ref _bytes, batch.Rows.Sum(x => (long)x.Json.Length));
                    if (r.ReportedCount is { } n && n != batch.Rows.Count) Interlocked.Increment(ref _mismatches);
                    return r;
                case Outcome.Transient or Outcome.RateLimited when attempt < MaxAttempts:
                    await Task.Delay(r.RetryAfter ?? Backoff(attempt), ct);
                    continue;
                default:
                    throw new SyncPausedException(r.Outcome, $"upload {batch.PartitionId}/{batch.Table}: {r.Outcome} {r.HttpStatus} {r.Message}", r.HttpStatus);
            }
        }
    }
}
