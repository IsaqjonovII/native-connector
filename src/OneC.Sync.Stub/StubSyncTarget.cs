using System.Text;
using OneC.Sync.Abstractions;

namespace OneC.Sync.Stub;

/// <summary>Which backend the stub behaves like.</summary>
public enum StubMode
{
    /// <summary>
    /// backend/1c v2 as audited: duplicate keys in a batch keep the first silently while the
    /// reported count says every row was stored; deletes above 5 % of the table (or on an empty
    /// table) are refused; no version guard, no per-row results, no atomic recorder.
    /// </summary>
    V2,
    /// <summary>The proposed v3 (§19): duplicates rejected, per-row results, stale source versions refused, atomic recorder.</summary>
    V3
}

/// <summary>
/// In-memory <see cref="IBackendSyncTarget"/> for tests and local runs (§17). Stores rows by
/// (partition, table, key), records every call, replays a repeated <c>BatchId</c>, and can be told to
/// fail, go offline, throttle, or report the connection as deleting. Thread-safe.
/// </summary>
public sealed class StubSyncTarget : IBackendSyncTarget
{
    private sealed record Stored(byte[] Json, long? SourceVersion);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Partition, string Table), Dictionary<string, Stored>> _rows = new();
    private readonly Dictionary<string, TargetResult> _batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SyncConfig> _configs = new(StringComparer.Ordinal);
    private readonly Queue<(string? Op, TargetResult Result)> _failures = new();
    private readonly Bounded _calls = new();

    /// <summary>
    /// The call log stays bounded: the desktop's sync preview runs on this target for days, and an
    /// unbounded log grew with every call (2026-10-01 review). Tests read far fewer than the cap.
    /// </summary>
    private sealed class Bounded : List<string>
    {
        private const int Cap = 100_000;
        public new void Add(string s)
        {
            if (Count >= Cap) RemoveRange(0, Cap / 2);
            base.Add(s);
        }
    }

    /// <summary>Batch ids already answered, for idempotent retries; only recent ones matter, so the cache is bounded too.</summary>
    private void Remember(string batchId, TargetResult result)
    {
        if (_batches.Count >= 100_000) _batches.Clear();
        _batches[batchId] = result;
    }

    public StubSyncTarget(StubMode mode = StubMode.V3) => Mode = mode;

    public StubMode Mode { get; }
    public string Kind => "stub";

    /// <summary>Every call fails as transient (network down).</summary>
    public bool Offline { get; set; }
    /// <summary>The cloud connection is being deleted: every call answers Gone.</summary>
    public bool Deleting { get; set; }
    /// <summary>Upload throttle (S15 slow backend); null = unlimited.</summary>
    public double? RowsPerSecond { get; set; }
    /// <summary>False: rows are counted by key but their JSON is dropped (benchmarks, so the stub's memory does not hide the engine's).</summary>
    public bool KeepJson { get; init; } = true;
    /// <summary>Keys a v3 target rejects as invalid (per-row validation).</summary>
    public HashSet<string> RejectKeys { get; } = new(StringComparer.Ordinal);
    public BaseStatus? LastStatus { get; private set; }
    /// <summary>What <see cref="GetPresenceAsync"/> answers: another connector online (D-1).</summary>
    public bool? OtherConnectorOnline { get; set; } = false;

    public Task<Presence> GetPresenceAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult(new Presence(OtherConnectorOnline, "stub"));

    /// <summary>V3 purges (§19 <c>tables/{t}/rebuild</c>); V2 refuses like backend/1c.</summary>
    public Task<DeleteResult> PurgeTableAsync(string partitionId, string table, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add($"purge {partitionId}/{table}");
            if (Mode == StubMode.V2) return Task.FromResult(new DeleteResult(Outcome.Policy, 0, null, "no table purge on v2"));
            int n = Table(partitionId, table).Count;
            Table(partitionId, table).Clear();
            return Task.FromResult(new DeleteResult(Outcome.Ok, n));
        }
    }

    public TargetCapabilities Capabilities => Mode == StubMode.V2
        ? new(PerRowResults: false, AtomicRecorder: false, SourceVersionGuard: false, MaxBatchBytes: 34L * 1024 * 1024,
              MaxBatchRows: 10_000, DeleteCapFraction: 0.05, Gzip: false, DuplicateKeys: DuplicateKeyPolicy.KeepFirstSilently,
              ReportsTrustworthyCounts: false)
        : new(PerRowResults: true, AtomicRecorder: true, SourceVersionGuard: true, MaxBatchBytes: 8L * 1024 * 1024,
              MaxBatchRows: 2_000, DeleteCapFraction: null, Gzip: true, DuplicateKeys: DuplicateKeyPolicy.Reject,
              ReportsTrustworthyCounts: true);

    // ---------------- test controls and inspection ----------------

    /// <summary>The next call (of <paramref name="op"/>, or any) returns <paramref name="result"/>, <paramref name="times"/> times.</summary>
    public void FailNext(TargetResult result, int times = 1, string? op = null)
    {
        lock (_gate) for (int i = 0; i < times; i++) _failures.Enqueue((op, result));
    }

    public void SetConfig(SyncConfig config)
    {
        lock (_gate) _configs[config.ConnectionId] = config;
    }

    public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>Stored rows of one table in one partition, key → JSON text.</summary>
    public IReadOnlyDictionary<string, string> Rows(string partition, string table)
    {
        lock (_gate)
            return _rows.TryGetValue((partition, table), out var t)
                ? t.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value.Json), StringComparer.Ordinal)
                : new Dictionary<string, string>();
    }

    public int Count(string partition, string table)
    {
        lock (_gate) return _rows.TryGetValue((partition, table), out var t) ? t.Count : 0;
    }

    /// <summary>Seeds a row directly (e.g. rows the old Connector stored).</summary>
    public void Seed(string partition, string table, string key, string json, long? version = null)
    {
        lock (_gate) Table(partition, table)[key] = new Stored(Encoding.UTF8.GetBytes(json), version);
    }

    // ---------------- IBackendSyncTarget ----------------

    public Task<TargetCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(Capabilities);

    public Task<SyncConfig> GetSyncConfigAsync(string connectionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add($"config {connectionId}");
            return Task.FromResult(_configs.TryGetValue(connectionId, out var c)
                ? c
                : new SyncConfig(connectionId, null, connectionId, Array.Empty<SyncPartition>(), Array.Empty<SyncTableConfig>()));
        }
    }

    public async Task<UploadResult> UploadRowsAsync(UploadBatch b, CancellationToken ct)
    {
        if (RowsPerSecond is { } rps && rps > 0) await Task.Delay(TimeSpan.FromSeconds(b.Rows.Count / rps), ct);
        lock (_gate)
        {
            _calls.Add($"upload {b.PartitionId}/{b.Table} {b.Rows.Count}");
            if (Precheck("upload") is { } f) return UploadResult.Failed(f.Outcome, f.HttpStatus, f.Message, f.RetryAfter);
            if (_batches.TryGetValue(b.BatchId, out var done)) return (UploadResult)done;

            UploadResult result;
            var table = Table(b.PartitionId, b.Table);
            if (Mode == StubMode.V2)
            {
                // First row of a key wins, silently; the reported count is every row sent (audit: over-reported inserts).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in b.Rows)
                    if (seen.Add(r.Key)) table[r.Key] = new Stored(Keep(r.Json), r.SourceVersion);
                result = new UploadResult(Outcome.Ok, seen.Count, Array.Empty<string>(), Array.Empty<RowRejection>(), ReportedCount: b.Rows.Count);
            }
            else
            {
                var dups = b.Rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dups.Count > 0)
                    return UploadResult.Failed(Outcome.Validation, 400, "duplicate keys in batch: " + string.Join(",", dups));
                var stale = new List<string>();
                var rejected = new List<RowRejection>();
                int applied = 0;
                foreach (var r in b.Rows)
                {
                    if (RejectKeys.Contains(r.Key)) { rejected.Add(new RowRejection(r.Key, "invalid row", false)); continue; }
                    if (table.TryGetValue(r.Key, out var old) && IsStale(old.SourceVersion, r.SourceVersion)) { stale.Add(r.Key); continue; }
                    table[r.Key] = new Stored(Keep(r.Json), r.SourceVersion);
                    applied++;
                }
                result = new UploadResult(Outcome.Ok, applied, stale, rejected);
            }
            Remember(b.BatchId, result);
            return result;
        }
    }

    public Task<DeleteResult> DeleteRowsAsync(DeleteBatch b, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add($"delete {b.PartitionId}/{b.Table} {b.Keys.Count}{(b.Approved ? " approved" : "")}");
            if (Precheck("delete") is { } f) return Task.FromResult(new DeleteResult(f.Outcome, 0, f.HttpStatus, f.Message));
            if (_batches.TryGetValue(b.BatchId, out var done)) return Task.FromResult((DeleteResult)done);
            var table = Table(b.PartitionId, b.Table);
            if (Capabilities.DeleteCapFraction is { } cap && !b.Approved)
            {
                // backend/1c refuses to prune a table with nothing stored; the v2 target reads that as
                // "already gone" for a delete, and the stub answers what the target would.
                if (table.Count == 0) return Task.FromResult(new DeleteResult(Outcome.Ok, 0));
                // backend/1c: max_allowed = max(1, int(stored_count * PRUNE_MAX_FRACTION)) (v1 onec.py prune_missing_rows).
                int allowed = Math.Max(1, (int)Math.Floor(table.Count * cap));
                int present = b.Keys.Count(table.ContainsKey);
                if (present > allowed)
                    return Task.FromResult(new DeleteResult(Outcome.Policy, 0, 409, $"{present} of {table.Count} rows is over the {cap:P0} cap"));
            }
            int n = b.Keys.Count(table.Remove);
            var result = new DeleteResult(Outcome.Ok, n);
            Remember(b.BatchId, result);
            return Task.FromResult(result);
        }
    }

    public Task<ReconcileResult> ReconcileRecorderAsync(RecorderReconcile r, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add($"reconcile {r.PartitionId}/{r.Table} {r.RecorderKey} live={r.LiveKeys.Count}");
            if (Precheck("reconcile") is { } f) return Task.FromResult(new ReconcileResult(f.Outcome, 0, f.HttpStatus, f.Message));
            var result = new ReconcileResult(Outcome.Ok, RemoveStale(Table(r.PartitionId, r.Table), r.RecorderKey, r.LiveKeys));
            return Task.FromResult(result);
        }
    }

    public Task<RecorderSyncResult> SyncRecorderAtomicAsync(RecorderSync s, CancellationToken ct)
    {
        if (!Capabilities.AtomicRecorder) throw new NotSupportedException("this target has no atomic recorder sync");
        lock (_gate)
        {
            _calls.Add($"recorder {s.PartitionId} {s.RecorderKey} movements={s.Movements.Sum(m => m.Rows.Count)}");
            if (Precheck("recorder") is { } f) return Task.FromResult(new RecorderSyncResult(f.Outcome, false, 0, 0, f.HttpStatus, f.Message));
            if (_batches.TryGetValue(s.BatchId, out var done)) return Task.FromResult((RecorderSyncResult)done);
            var docs = Table(s.PartitionId, s.DocumentTable);
            if (docs.TryGetValue(s.RecorderKey, out var old) && IsStale(old.SourceVersion, s.SourceVersion))
                return Task.FromResult(new RecorderSyncResult(Outcome.Ok, true, 0, 0));
            // All in one step under the lock: nobody observes the document without its movements.
            if (s.Document is null) docs.Remove(s.RecorderKey);
            else docs[s.RecorderKey] = new Stored(s.Document.Json, s.SourceVersion);
            int written = 0, removed = 0;
            foreach (var m in s.Movements)
            {
                var t = Table(s.PartitionId, m.Table);
                removed += RemoveStale(t, s.RecorderKey, m.Rows.Select(r => r.Key).ToList());
                foreach (var r in m.Rows) { t[r.Key] = new Stored(Keep(r.Json), s.SourceVersion); written++; }
            }
            var result = new RecorderSyncResult(Outcome.Ok, false, written, removed);
            Remember(s.BatchId, result);
            return Task.FromResult(result);
        }
    }

    public Task<TargetResult> ReportStatusAsync(BaseStatus status, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add($"status {status.ConnectionId} {status.State}");
            if (Precheck("status") is { } f) return Task.FromResult(f);
            LastStatus = status;
            return Task.FromResult(TargetResult.Success);
        }
    }

    // ---------------- helpers ----------------

    private Dictionary<string, Stored> Table(string partition, string table)
    {
        if (!_rows.TryGetValue((partition, table), out var t)) _rows[(partition, table)] = t = new(StringComparer.Ordinal);
        return t;
    }

    private TargetResult? Precheck(string op)
    {
        if (Deleting) return new TargetResult(Outcome.Gone, 409, "connection is being deleted");
        if (Offline) return new TargetResult(Outcome.Transient, null, "offline");
        if (_failures.Count > 0 && (_failures.Peek().Op is null || _failures.Peek().Op == op)) return _failures.Dequeue().Result;
        return null;
    }

    /// <summary>A recorder's rows are the keys <c>recorderKey#lineNo</c> (§7).</summary>
    private static int RemoveStale(Dictionary<string, Stored> table, string recorderKey, IReadOnlyList<string> live)
    {
        var keep = new HashSet<string>(live, StringComparer.Ordinal);
        string prefix = recorderKey + "#";
        var gone = table.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !keep.Contains(k)).ToList();
        foreach (var k in gone) table.Remove(k);
        return gone.Count;
    }

    private byte[] Keep(byte[] json) => KeepJson ? json : Array.Empty<byte>();

    private static bool IsStale(long? stored, long? incoming) => stored is { } s && incoming is { } i && i < s;
}
