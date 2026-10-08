namespace OneC.Sync.Abstractions;

/// <summary>
/// One canonical row: its key (§7), an optional per-base monotonic source version (§19: the
/// engine's own sequence, so a stale retry can be refused), and the canonical JSON the mapper wrote
/// once (UTF-8). Targets never re-serialise it.
/// </summary>
public sealed record SyncRow(string Key, long? SourceVersion, byte[] Json);

/// <summary>Rows for one table in one partition. <c>BatchId</c> makes a retry of the same batch a no-op.</summary>
public sealed record UploadBatch(string BatchId, string PartitionId, string Table, IReadOnlyList<SyncRow> Rows);

/// <param name="Approved">The user approved a delete the target refused on policy (§10).</param>
public sealed record DeleteBatch(string BatchId, string PartitionId, string Table, IReadOnlyList<string> Keys, string Reason, bool Approved = false);

/// <param name="LiveKeys">The complete current set of the recorder's row keys in this table (empty = remove all).</param>
public sealed record RecorderReconcile(string BatchId, string PartitionId, string Table, string RecorderKey, IReadOnlyList<string> LiveKeys);

/// <summary>The recorder's current rows in one register of one partition.</summary>
public sealed record PartitionMovements(string PartitionId, string Table, IReadOnlyList<SyncRow> Rows);

/// <summary>
/// One document and all its movements, every partition and register at once (RUST_SYNC_CONTRACT §4.5,
/// §7): the target upserts the document in <c>DocumentPartition</c> and removes it from the other
/// partitions, upserts the movements, and removes the recorder's rows that are not live — in exactly
/// <c>Partitions × MovementTables</c>, nowhere else — in one transaction.
/// </summary>
/// <param name="DocumentTable">Empty when the document's own table is not synced (then <c>Document</c> is null and no document row is touched).</param>
/// <param name="Document">Null when the document is gone or not synced: it is removed from every partition in scope.</param>
/// <param name="Partitions">Reconcile scope: where the recorder has or had rows (the engine's touch set).</param>
/// <param name="MovementTables">Reconcile scope: every configured register the recorder may post to.</param>
public sealed record RecorderSync(string BatchId, string RecorderKey, long? SourceVersion,
                                  string DocumentTable, string? DocumentPartition, SyncRow? Document,
                                  IReadOnlyList<string> Partitions, IReadOnlyList<string> MovementTables,
                                  IReadOnlyList<PartitionMovements> Movements);

public sealed record SyncPartition(string PartitionId, string? OrgRef, string? CompanyId);

/// <param name="IsMovement">Rows are routed by their organisation (§21); must match the backend's own classification.</param>
public sealed record SyncTableConfig(string Table, string Metadata, string Family, bool IsMovement, bool Enabled = true);

/// <param name="SharedPartitionId">Where reference rows (catalogs, charts) go; also every row of a single-org base.</param>
public sealed record SyncConfig(string ConnectionId, string? Provider, string SharedPartitionId,
                                IReadOnlyList<SyncPartition> Partitions, IReadOnlyList<SyncTableConfig> Tables)
{
    /// <summary>False: the backend keeps no table list for this connection (backend/1c answers
    /// <c>tables: null</c> until one is stored) — <see cref="Tables"/> is then empty, not "none allowed".</summary>
    public bool TableListStored { get; init; } = true;
}

/// <summary>
/// How far back a partition's copy of one table reaches (RUST_SYNC_CONTRACT §4.9), in the meaning its
/// consumer reads (aiba-next sotuv.rs <c>mirror_reach_from_onec</c>, backend/1c coverage.py): no entry =
/// whole history; <c>Complete</c> = whole history; not complete with <c>DataFrom</c> = rows from that day
/// on only; not complete without <c>DataFrom</c> = unknown (every miss doubted).
/// </summary>
public sealed record TableCoverage(string Table, DateTime? DataFrom, bool Complete)
{
    /// <summary>A first copy in progress: nothing may be read as absent yet.</summary>
    public static TableCoverage Loading(string table) => new(table, null, false);
}

/// <summary>A target that stores data coverage. The engine reports it for every document and register table it copies.</summary>
public interface ICoverageTarget
{
    Task<TargetResult> ReportCoverageAsync(string partitionId, IReadOnlyList<TableCoverage> tables, CancellationToken ct);
}

public sealed record BaseStatus(string ConnectionId, string State, long? TotalCount, int? Percentage, string? LastError);

/// <summary>The canonical outcome classes (§22). Targets map HTTP and their own errors onto these.</summary>
public enum Outcome
{
    Ok,
    Transient,      // network, 5xx, timeout
    RateLimited,    // 429: honour RetryAfter
    Auth,           // 401 after one refresh, 403
    Gone,           // the connection is being deleted
    Validation,     // 400: the payload is wrong; no automatic retry
    Policy          // refused by rule (delete cap, empty table): needs the user's approval
}

public record TargetResult(Outcome Outcome, int? HttpStatus = null, string? Message = null, TimeSpan? RetryAfter = null)
{
    public bool Ok => Outcome == Outcome.Ok;
    public static TargetResult Success { get; } = new(Outcome.Ok);
}

public sealed record RowRejection(string Key, string Reason, bool Retryable);

/// <param name="Applied">Rows the target says it stored.</param>
/// <param name="Stale">Keys refused because the target holds a newer source version (not an error: newer data won).</param>
/// <param name="Rejected">Named rejections (only with per-row results).</param>
/// <param name="ReportedCount">What the target's own counters said, when it has no per-row results (v2); compare with rows sent.</param>
public sealed record UploadResult(Outcome Outcome, int Applied, IReadOnlyList<string> Stale, IReadOnlyList<RowRejection> Rejected,
                                  long? ReportedCount = null, int? HttpStatus = null, string? Message = null, TimeSpan? RetryAfter = null)
    : TargetResult(Outcome, HttpStatus, Message, RetryAfter)
{
    public static UploadResult Failed(Outcome o, int? http = null, string? msg = null, TimeSpan? retryAfter = null) =>
        new(o, 0, Array.Empty<string>(), Array.Empty<RowRejection>(), null, http, msg, retryAfter);
}

public sealed record DeleteResult(Outcome Outcome, int Deleted, int? HttpStatus = null, string? Message = null)
    : TargetResult(Outcome, HttpStatus, Message);

public sealed record ReconcileResult(Outcome Outcome, int Removed, int? HttpStatus = null, string? Message = null)
    : TargetResult(Outcome, HttpStatus, Message);

/// <param name="MovementsWritten">Rows inserted or changed (an unchanged row is not counted).</param>
public sealed record RecorderSyncResult(Outcome Outcome, bool StaleDocument, int MovementsWritten, int MovementsRemoved,
                                        int? HttpStatus = null, string? Message = null)
    : TargetResult(Outcome, HttpStatus, Message);
