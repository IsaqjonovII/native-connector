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

public sealed record MovementSet(string Table, IReadOnlyList<SyncRow> Rows);

/// <param name="Document">Null when the document itself is gone (then all movements are removed).</param>
public sealed record RecorderSync(string BatchId, string PartitionId, string RecorderKey, long? SourceVersion,
                                  string DocumentTable, SyncRow? Document, IReadOnlyList<MovementSet> Movements);

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

public sealed record RecorderSyncResult(Outcome Outcome, bool StaleDocument, int MovementsWritten, int MovementsRemoved,
                                        int? HttpStatus = null, string? Message = null)
    : TargetResult(Outcome, HttpStatus, Message);
