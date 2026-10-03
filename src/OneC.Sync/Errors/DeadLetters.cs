using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Source;

namespace OneC.Sync.Errors;

/// <summary>
/// The user-facing side of §22: retry, approve and dismiss dead letters, and the timed retry of
/// transient ones. A dead letter never advances anything by itself — it <em>is</em> the pending
/// work, so every action here turns it back into a work item or, for dismissal, records that the
/// user accepted the cloud keeping those rows.
/// </summary>
public sealed class DeadLetterService(SyncDb db)
{
    public const int WarningThreshold = 50;

    /// <summary>Back to the queue with a fresh attempt count (a destructive one keeps its intent).</summary>
    public bool Retry(long id)
    {
        return db.Write(tx =>
        {
            var d = Find(tx, id);
            if (d is null) return false;
            tx.UpsertWork(d.WorkKind == "snapshot" ? SnapshotRetry(d)
                                                   : new WorkRequest(d.BaseId, d.Table, d.ObjectKey, FlagsFor(d.WorkKind), ItemPriority.Destructive));
            tx.DeleteDeadLetter(id);
            return true;
        });
    }

    /// <summary>
    /// A row the first copy could not store, back as the work that sends it again: a catalog or
    /// document row → its object; a recorded register line (<c>recorder#line</c>) → its recorder,
    /// whose document type the handler looks up (table "?"); an independent register row or a chart
    /// account → a refresh of its table (2026-10-01 review: every snapshot dead letter became an
    /// object sync, which reads a register row as if it were a catalog).
    /// </summary>
    private static WorkRequest SnapshotRetry(DeadLetter d)
    {
        int hash = d.ObjectKey.IndexOf('#');
        if (hash > 0 && Guid.TryParse(d.ObjectKey[..hash], out var recorder))
            return new WorkRequest(d.BaseId, "?", recorder.ToString("D"), WorkFlags.Movements | WorkFlags.Changed, ItemPriority.Destructive);
        if (d.Table.StartsWith("InformationRegister_", StringComparison.Ordinal) || d.Table.StartsWith("ChartOfAccounts_", StringComparison.Ordinal))
            return new WorkRequest(d.BaseId, d.Table, d.Table, WorkFlags.Refresh, ItemPriority.Destructive);
        return new WorkRequest(d.BaseId, d.Table, d.ObjectKey, WorkFlags.Changed, ItemPriority.Destructive);
    }

    /// <summary>
    /// A <c>needs_approval</c> delete the user confirmed: an approved delete, which the target may
    /// split into cap-sized calls (§10). Only that category can be approved.
    /// </summary>
    public bool Approve(long id)
    {
        return db.Write(tx =>
        {
            var d = Find(tx, id);
            if (d is null || d.Category != DeadLetterCategories.NeedsApproval) return false;
            if (d.WorkKind == WorkKinds.RefreshRegister)
            {
                // The refused delete was a refresh's (rows gone from an independent register): the key
                // is the table, not a row. Approve the refresh's delete and run the refresh again
                // (2026-10-01 review: it sent an approved delete of a row keyed by the table name).
                tx.SetMeta(RefreshRegisterHandler.ApprovedDeleteKey(d.BaseId, d.Table), SyncDb.Iso(tx.Now));
                tx.DeleteMeta(RefreshRegisterHandler.LastRefreshKey(d.BaseId, d.Table));
                tx.UpsertWork(new WorkRequest(d.BaseId, d.Table, d.Table, WorkFlags.Refresh, ItemPriority.Destructive));
                tx.DeleteDeadLetter(id);
                return true;
            }
            tx.UpsertWork(new WorkRequest(d.BaseId, d.Table, d.ObjectKey, WorkFlags.ApprovedDelete | WorkFlags.Deleted, ItemPriority.Destructive));
            tx.DeleteDeadLetter(id);
            return true;
        });
    }

    /// <summary>
    /// The user accepts the cloud as it is for this item. Destructive items are dismissed only this
    /// way — never automatically (§22) — and the decision is kept in the run history.
    /// </summary>
    public bool Dismiss(long id, string who)
    {
        return db.Write(tx =>
        {
            var d = Find(tx, id);
            if (d is null) return false;
            long run = tx.StartRun(d.BaseId, "dismissed " + d.WorkKind);
            tx.FinishRun(run, "dismissed", 0, 0, 0, $"{who} accepted: {d.Table}/{d.ObjectKey}: {d.Category}: {d.Error}");
            tx.DeleteDeadLetter(id);
            return true;
        });
    }

    /// <summary>Transient dead letters whose retry time came: back to the queue (§22, every 30 min).</summary>
    public int RetryDue(string baseId)
    {
        var due = db.Read(tx => tx.DeadLetters(baseId).Where(d => d.NextRetryAt is { } at && at <= tx.Now).Select(d => d.Id).ToList());
        return due.Count(Retry);
    }

    public bool OverThreshold(string baseId) => db.Read(tx => tx.CountDeadLetters(baseId)) > WarningThreshold;

    private static DeadLetter? Find(SyncTx tx, long id) => tx.GetDeadLetter(id);

    private static WorkFlags FlagsFor(string kind) => kind switch
    {
        WorkKinds.DeleteObject => WorkFlags.Deleted,
        WorkKinds.SyncRecorder => WorkFlags.Movements | WorkFlags.Changed,
        WorkKinds.RefreshRegister => WorkFlags.Refresh,
        WorkKinds.VerifyTable => WorkFlags.Verify,
        WorkKinds.ApprovedDelete => WorkFlags.ApprovedDelete | WorkFlags.Deleted,
        _ => WorkFlags.Changed
    };
}

/// <summary><c>approved_delete</c>: the delete the backend refused on policy, sent again as approved (§10).</summary>
public sealed class ApprovedDeleteHandler(SyncDb db, IBackendSyncTarget target, IReadOnlyDictionary<string, TablePlan> tables,
                                          IPartitioner partitions, Func<string, TablePlan, string, CancellationToken, Task>? clearMovements) : IWorkHandler
{
    public async Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct)
    {
        var t = tables.GetValueOrDefault(item.Table) ?? throw new OneCMetadataMissingException($"table {item.Table} is not configured");
        var where = db.Read(tx => tx.GetPartitions(baseId, item.ObjectKey));
        if (where.Count == 0) where = partitions.All.ToList();                 // history unknown: every partition (scope=self)
        foreach (var p in where)
        {
            var r = await target.DeleteRowsAsync(new DeleteBatch(Guid.NewGuid().ToString("N"), p, t.Table, new[] { item.ObjectKey }, "deleted in 1C, approved", Approved: true), ct);
            if (!r.Ok) throw new Upload.SyncPausedException(r.Outcome, $"approved delete {p}/{t.Table}/{item.ObjectKey}: {r.Outcome} {r.Message}", r.HttpStatus);
        }
        if (t.Family == Families.Document && clearMovements is not null) await clearMovements(baseId, t, item.ObjectKey, ct);
        return new HandlerOutcome(tx =>
        {
            tx.DeleteVersion(baseId, t.Table, item.ObjectKey);
            foreach (var p in where) tx.RemovePartition(baseId, item.ObjectKey, p);
        });
    }
}
