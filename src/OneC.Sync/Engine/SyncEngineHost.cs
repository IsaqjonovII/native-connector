using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Errors;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Engine;

public sealed record BaseSyncStatus(string BaseId, string Mode, string? Reason, long PendingWork, int DeadLetters, bool Warning,
                                    string? Cursor, DateTimeOffset? LastEventAt, IReadOnlyList<(string OrgRef, string Table, long Rows)> UnmappedOrgs,
                                    string? LastError, bool Active)
{
    /// <summary>Configured tables 1C does not have (§22 missing config): table → reason.</summary>
    public IReadOnlyDictionary<string, string> MissingTables { get; init; } = new Dictionary<string, string>();
    /// <summary>Tables whose first snapshot is complete, of the configured ones.</summary>
    public int TablesDone { get; init; }
    public int TablesTotal { get; init; }
}

/// <summary>
/// One configured table, for the base's sync screen. <see cref="State"/>: missing (not in this 1C),
/// waiting (first copy not started), copying (first copy running), synced.
/// </summary>
public sealed record TableSyncStatus(string Table, string Family, string State, string? Missing,
                                     long Rows, long CopiedSoFar, long Pending, int Failed, DateTimeOffset? LastSentAt);

/// <summary>
/// The sync engine as the Supervisor runs it: one <see cref="SyncDb"/>, one global scheduler, one
/// agent per configured base, and the control surface the edge exposes (§24, S13) — status,
/// pause/resume, dead-letter retry/approve/dismiss, per-table rebuild (D-3, confirmed per table).
/// </summary>
public sealed class SyncEngineHost : IDisposable
{
    private readonly SyncDb _db;
    private readonly Dictionary<string, BaseSyncAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BasePlan> _plans = new(StringComparer.Ordinal);
    private readonly IBackendSyncTarget _target;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SyncEngineHost(SyncDb db, IBackendSyncTarget target, IOneCReader reader, IEnumerable<BasePlan> bases,
                          SyncBudgets? budgets = null, EngineOptions? options = null, IFeedStat? stat = null, Func<DateTimeOffset>? clock = null)
    {
        _db = db;
        _target = target;
        var b = budgets ?? new SyncBudgets();
        var gate = new UploadGate(b);
        var o = options ?? new EngineOptions();
        foreach (var p in bases)
        {
            _plans[p.BaseId] = p;
            _agents[p.BaseId] = new BaseSyncAgent(db, p, target, reader, gate, o);
        }
        Scheduler = new SyncScheduler(b, stat ?? new FileFeedStat(), new DbDemand(db), id => _agents[id], clock ?? (() => DateTimeOffset.UtcNow));
        foreach (var p in _plans.Values) Scheduler.AddBase(p.BaseId, p.IsFile, p.LogDir);
        db.Write(tx => tx.ReleaseAllLeases());                                  // left by a process that died (S15)
        db.Write(tx => tx.PruneRuns(TimeSpan.FromDays(30)));                    // run history is bounded (it had no caller, 2026-10-01 review)
        // A table added to the plan of a base already past its first copy: copy that table first.
        // The feed cursor stays where it is (Handshake keeps it), so no change of the others is lost.
        db.Write(tx =>
        {
            foreach (var p in _plans.Values)
            {
                if (tx.GetBase(p.BaseId) is not { Mode: SyncModes.Incremental or SyncModes.Fallback }) continue;
                var added = p.Tables.Where(t => tx.GetMeta(BaseSyncAgent.SnapshotDoneKey(p.BaseId, t.Table)) is null).Select(t => t.Name).ToList();
                if (added.Count > 0) tx.SetMode(p.BaseId, SyncModes.Snapshot, "first copy of added table(s): " + string.Join(", ", added));
            }
        });
        // Lost state (§13): every base goes through Recovery, never "start from the tail".
        if (db.RecoveryRequired is not null)
            db.Write(tx =>
            {
                foreach (var p in _plans.Values)
                    tx.UpsertBase(new SyncBaseRow(p.BaseId, "target", p.ConnectionId, null, SyncModes.Recovery, "state lost: " + db.QuarantinedTo, false,
                                                  string.Join(",", p.Tables.Select(t => t.Table)), tx.Now));
                tx.DeleteMeta("recovery_required");
            });
    }

    public SyncScheduler Scheduler { get; }
    public SyncDb Db => _db;

    public void Start(TimeSpan? tick = null) => _loop ??= Scheduler.RunAsync(tick ?? TimeSpan.FromSeconds(1), _stop.Token);

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
    }

    // ---------------- status ----------------

    public IReadOnlyList<BaseSyncStatus> Status()
    {
        var active = Scheduler.ActiveBases.ToHashSet(StringComparer.Ordinal);
        return _plans.Keys.Order(StringComparer.Ordinal).Select(id => _db.Read(tx =>
        {
            var b = tx.GetBase(id);
            var c = tx.GetCursor(id);
            int dl = tx.CountDeadLetters(id);
            var tables = _plans[id].Tables;
            return new BaseSyncStatus(id, b?.Mode ?? SyncModes.Unconfigured, b?.ModeReason, tx.CountWork(id), dl, dl > DeadLetterService.WarningThreshold,
                                      c?.Cursor, c?.LastEventAt, tx.UnmappedOrgs(id), _agents[id].LastError, active.Contains(id))
            {
                MissingTables = tables.Select(t => (t.Table, Why: tx.GetMeta(BaseSyncAgent.MissingTableKey(id, t.Table))))
                                      .Where(x => x.Why is not null).ToDictionary(x => x.Table, x => x.Why!),
                TablesDone = tables.Count(t => tx.GetMeta(BaseSyncAgent.SnapshotDoneKey(id, t.Table)) is not null),
                TablesTotal = tables.Count
            };
        })).ToList();
    }

    public IReadOnlyList<DeadLetter> DeadLetters(string baseId) => _db.Read(tx => tx.DeadLetters(baseId));

    /// <summary>Every configured table of a base with its counts, in the plan's order.</summary>
    public IReadOnlyList<TableSyncStatus> TableStatus(string baseId)
    {
        if (!_plans.TryGetValue(baseId, out var plan)) return Array.Empty<TableSyncStatus>();
        return _db.Read(tx =>
        {
            var stats = tx.TableStats(baseId);
            return plan.Tables.Select(t =>
            {
                var s = stats.GetValueOrDefault(t.Table);
                string? missing = tx.GetMeta(BaseSyncAgent.MissingTableKey(baseId, t.Table));
                bool done = tx.GetMeta(BaseSyncAgent.SnapshotDoneKey(baseId, t.Table)) is not null;
                string state = missing is not null ? "missing" : done ? "synced" : s?.SnapshotStarted == true ? "copying" : "waiting";
                // Movements sent with their recorder are not counted per row: their first copy's count stands in.
                long rows = s is null ? 0 : s.Rows > 0 ? s.Rows : s.SnapshotRows;
                return new TableSyncStatus(t.Table, t.Family, state, missing, rows, s?.SnapshotRows ?? 0,
                                           s?.Pending ?? 0, s?.Failed ?? 0, s?.LastSentAt);
            }).ToList();
        });
    }

    /// <summary>The configured table names of a base (the UI's rebuild choices).</summary>
    public IReadOnlyList<string> Tables(string baseId) => _plans.TryGetValue(baseId, out var p) ? p.Tables.Select(t => t.Table).ToList() : Array.Empty<string>();

    // ---------------- control ----------------

    public bool Pause(string baseId) => SetMode(baseId, SyncModes.Paused, "paused by the user", pausedByUser: true);

    /// <summary>Back to where it was going: a base that never finished its snapshot resumes it; otherwise Incremental.</summary>
    public bool Resume(string baseId)
    {
        var plan = _plans.GetValueOrDefault(baseId);
        if (plan is null) return false;
        bool snapshotDone = plan.Tables.All(t => _db.Read(tx => tx.GetMeta(BaseSyncAgent.SnapshotDoneKey(baseId, t.Table))) is not null);
        return SetMode(baseId, snapshotDone ? SyncModes.Incremental : SyncModes.Snapshot, "resumed by the user", pausedByUser: false);
    }

    public bool Retry(long deadLetter) => new DeadLetterService(_db).Retry(deadLetter);
    public bool Approve(long deadLetter) => new DeadLetterService(_db).Approve(deadLetter);
    public bool Dismiss(long deadLetter, string who) => new DeadLetterService(_db).Dismiss(deadLetter, who);

    /// <summary>
    /// D-3: rebuild one table, only with the user's explicit confirmation for that table. The cloud's
    /// rows go first where the target can purge; then the table is read again from scratch. On a
    /// target without a purge (v2) the answer says so and nothing is changed — the old rows would stay
    /// next to the new ones.
    /// </summary>
    /// <summary>Non-null: rebuilds are refused on this target, with this reason (the shared dev backend, S12).</summary>
    public string? RebuildRefusal { get; init; }

    public async Task<(bool Done, string Message)> RebuildAsync(string baseId, string table, bool confirmed, CancellationToken ct)
    {
        if (RebuildRefusal is { } no) return (false, no);
        if (!confirmed) return (false, "a rebuild deletes the table's rows in the cloud: confirm this table explicitly");
        var plan = _plans.GetValueOrDefault(baseId);
        var t = plan?.Tables.FirstOrDefault(x => x.Table == table);
        if (plan is null || t is null) return (false, $"{baseId}/{table} is not configured");
        var config = await _target.GetSyncConfigAsync(plan.ConnectionId, ct);
        var partitions = config.Partitions.Select(p => p.PartitionId).Append(config.SharedPartitionId).Distinct().ToList();
        foreach (var p in partitions)
        {
            var r = await _target.PurgeTableAsync(p, table, ct);
            if (!r.Ok) return (false, $"not rebuilt: {r.Message}");
        }
        _db.Write(tx =>
        {
            tx.DeleteMeta(BaseSyncAgent.SnapshotDoneKey(baseId, table));
            tx.DeleteSlices(baseId, table);
            if (tx.GetBase(baseId) is not null) tx.SetMode(baseId, SyncModes.Snapshot, $"rebuild of {table} (confirmed)");
        });
        return (true, $"{table}: cloud rows removed from {partitions.Count} partition(s); the table is read again");
    }

    private bool SetMode(string baseId, string mode, string reason, bool pausedByUser)
    {
        if (!_plans.ContainsKey(baseId)) return false;
        return _db.Write(tx =>
        {
            var b = tx.GetBase(baseId);
            if (b is null) return false;
            tx.UpsertBase(b with { Mode = mode, ModeReason = reason, PausedByUser = pausedByUser });
            return true;
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
