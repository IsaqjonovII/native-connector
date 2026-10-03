namespace OneC.Sync.Abstractions;

/// <summary>
/// The engine's only view of a backend (SYNC_ENGINE_ARCHITECTURE §17). Semantics are canonical: every
/// target maps its own quirks onto these results, and the engine branches on
/// <see cref="TargetCapabilities"/> only — never on <see cref="Kind"/>, which is for logs and UI.
/// Every call is idempotent for the same <c>BatchId</c>; the engine retries freely.
/// </summary>
public interface IBackendSyncTarget
{
    /// <summary>python-v2 | python-v3 | rust | stub — display only.</summary>
    string Kind { get; }

    Task<TargetCapabilities> GetCapabilitiesAsync(CancellationToken ct);

    /// <summary>Tables, partitions and org bindings for one cloud connection (§21).</summary>
    Task<SyncConfig> GetSyncConfigAsync(string connectionId, CancellationToken ct);

    Task<UploadResult> UploadRowsAsync(UploadBatch batch, CancellationToken ct);

    /// <summary>Removes rows by key. A target with a delete cap answers <see cref="Outcome.Policy"/> above it unless the batch is approved.</summary>
    Task<DeleteResult> DeleteRowsAsync(DeleteBatch batch, CancellationToken ct);

    /// <summary>Removes the recorder's rows in one table and partition that are not in <c>LiveKeys</c> (§9).</summary>
    Task<ReconcileResult> ReconcileRecorderAsync(RecorderReconcile reconcile, CancellationToken ct);

    /// <summary>Document + all its movements in one backend transaction. Only when <see cref="TargetCapabilities.AtomicRecorder"/>.</summary>
    Task<RecorderSyncResult> SyncRecorderAtomicAsync(RecorderSync sync, CancellationToken ct);

    /// <summary>Heartbeat, totals, coverage for the cloud UI.</summary>
    Task<TargetResult> ReportStatusAsync(BaseStatus status, CancellationToken ct);

    /// <summary>
    /// Is another connector (the old Connector) serving this connection right now? D-1: the engine
    /// refuses to sync a base the backend sees served by one. Null <see cref="Presence.OtherConnectorOnline"/> = unknown.
    /// </summary>
    Task<Presence> GetPresenceAsync(string connectionId, CancellationToken ct);

    /// <summary>
    /// D-3 rebuild: remove every row of one table in one partition. Destructive; the engine calls it
    /// only after the user confirmed that table. A target without such a route answers
    /// <see cref="Outcome.Policy"/> (backend/1c v2 has none a user token may call).
    /// </summary>
    Task<DeleteResult> PurgeTableAsync(string partitionId, string table, CancellationToken ct);
}

public sealed record Presence(bool? OtherConnectorOnline, string? Detail);

public enum DuplicateKeyPolicy
{
    /// <summary>The backend keeps the first row of a key in a batch and says nothing (v2). The engine dedupes before sending.</summary>
    KeepFirstSilently,
    /// <summary>The backend rejects the batch listing the keys (v3).</summary>
    Reject
}

/// <param name="PerRowResults">Rejected rows are named; without it a batch is accepted or failed as a whole.</param>
/// <param name="AtomicRecorder"><see cref="IBackendSyncTarget.SyncRecorderAtomicAsync"/> is supported.</param>
/// <param name="SourceVersionGuard">The backend refuses a row older than the one it has (<c>stale</c>).</param>
/// <param name="DeleteCapFraction">Deletes above this share of the stored rows need approval (v2: 0.05); null = no cap.</param>
/// <param name="ReportsTrustworthyCounts">False on v2: its reported totals can hide a lost row (a match is not proof).</param>
public sealed record TargetCapabilities(
    bool PerRowResults,
    bool AtomicRecorder,
    bool SourceVersionGuard,
    long MaxBatchBytes,
    int MaxBatchRows,
    double? DeleteCapFraction,
    bool Gzip,
    DuplicateKeyPolicy DuplicateKeys,
    bool ReportsTrustworthyCounts);
