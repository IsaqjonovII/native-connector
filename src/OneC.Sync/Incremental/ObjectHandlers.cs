using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Incremental;

/// <summary>Where a row goes (§21). The shared partition takes reference rows and everything of a single-org base.</summary>
public interface IPartitioner
{
    string Shared { get; }
    /// <summary>
    /// Every partition of the base (the shared one and each binding). Where an object's partition
    /// history is unknown, a delete or reconcile must reach all of them: each target call stays in
    /// the one partition it names (the Python target's <c>scope=self</c>).
    /// </summary>
    IReadOnlyList<string> All { get; }
    /// <summary>The partition of a mapped row; null = its organisation has no binding yet (counted, not uploaded).</summary>
    string? PartitionOf(TablePlan t, MappedRow row);
}

/// <summary>Everything to one partition: single-org bases, and every base until S10's router.</summary>
public sealed class SinglePartition(string partition) : IPartitioner
{
    public string Shared => partition;
    public IReadOnlyList<string> All => new[] { partition };
    public string? PartitionOf(TablePlan t, MappedRow row) => partition;
}

/// <summary>
/// <c>sync_object</c> for catalogs and documents without movements (§6): read the object's current
/// state; unchanged version → nothing to send; gone from 1C (object-level not-found only) → a
/// delete; else upload it and remember the version and the partition it went to.
/// </summary>
public sealed class SyncObjectHandler(SyncDb db, IOneCReader reader, Uploader uploader, IReadOnlyDictionary<string, TablePlan> tables,
                                      IPartitioner partitions, DeleteObjectHandler deletes) : IWorkHandler
{
    public async Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct)
    {
        var t = tables.GetValueOrDefault(item.Table) ?? throw new OneCMetadataMissingException($"table {item.Table} is not configured");
        var rows = await reader.ByIdsAsync(baseId, t, new[] { item.ObjectKey }, ct);
        // Gone from 1C, or now before the table's window (the snapshot never sends it): no copy stays.
        if (rows.Count == 0 || !CanonicalMapper.InWindow(t, rows[0])) return await deletes.HandleAsync(baseId, item, ct);

        var mapped = CanonicalMapper.Map(t, rows[0]);
        string? stored = db.Read(tx => tx.GetVersion(baseId, t.Table, mapped.Key));
        // Force: unchanged in 1C, but an item it references was renamed, so its row shows a new name.
        if (mapped.DataVersion is not null && stored == mapped.DataVersion && !item.Flags.HasFlag(WorkFlags.Force))
            return new HandlerOutcome(Skipped: true);                               // an event, but this version was already sent

        string? partition = partitions.PartitionOf(t, mapped);
        if (partition is null)
            return new HandlerOutcome(tx => tx.CountUnmappedOrg(baseId, mapped.OrgRef ?? "", t.Table, 1));
        var r = await uploader.UploadAsync(baseId, new UploadBatch(Guid.NewGuid().ToString("N"), partition, t.Table, new[] { mapped.Row }), ct);
        if (r.Rejected.Count > 0) throw new SyncPausedException(Outcome.Validation, r.Rejected[0].Reason, 400);
        var old = db.Read(tx => tx.GetPartitions(baseId, mapped.Key)).Where(p => p != partition).ToList();
        // The object moved to another organisation: its row in the old partition goes (§9, §21).
        foreach (var p in old) await deletes.DeleteRowAsync(baseId, t, p, mapped.Key, ct);
        var follow = await RenamedAsync(baseId, t, mapped, ct);
        return new HandlerOutcome(tx =>
        {
            tx.SetVersion(baseId, t.Table, mapped.Key, mapped.DataVersion);
            tx.AddPartition(baseId, mapped.Key, partition);
            foreach (var p in old) tx.RemovePartition(baseId, mapped.Key, p);
            if (mapped.Shown is { } shown) tx.SetMeta(CanonicalMapper.ShownKey(baseId, t.Table, mapped.Key), shown);
            foreach (var w in follow) tx.UpsertWork(w);
        });
    }

    /// <summary>Most referrers one rename re-reads; more are reported as truncated (they keep the old name until they change).</summary>
    public int MaxReferrers { get; init; } = 50_000;

    /// <summary>
    /// A catalog item whose shown name changed (a rename): every synced row that shows it must be read
    /// again, though none of them changed in 1C — rows carry references as names (F-reference-presentation-
    /// staleness: sotuv's order guard and recon match on those names). 1C is asked for exactly the objects
    /// and recorders that reference this GUID (ReferrerSearch), never a table scan in the backend or a
    /// rescan of every document. Documents go as recorder items (their movements show the name too),
    /// recorders of other types likewise, catalogs as forced object items. An item seen for the first time
    /// (no stored name: new, or not yet seeded) triggers nothing.
    /// </summary>
    private async Task<List<WorkRequest>> RenamedAsync(string baseId, TablePlan t, MappedRow mapped, CancellationToken ct)
    {
        var follow = new List<WorkRequest>();
        if (t.Family != Families.Catalog || mapped.Shown is not { } shown) return follow;
        string? before = db.Read(tx => tx.GetMeta(CanonicalMapper.ShownKey(baseId, t.Table, mapped.Key)));
        if (before is null || before == shown) return follow;
        var found = await reader.ReferrersAsync(baseId, t, mapped.Key, tables.Values.ToList(), ct);
        if (found.Truncated)
            Console.Error.WriteLine($"sync {baseId}: rename of {t.Table}/{mapped.Key} — more than {found.Hits.Count} referrers; the rest keep the old name until they change");
        foreach (var (table, id) in found.Hits.Distinct())
        {
            if (table == t.Table && id == mapped.Key) continue;
            var plan = tables.GetValueOrDefault(table);
            bool recorder = table == EventCoalescer.UnknownRecorderTable || plan?.Family == Families.Document;
            if (!recorder && plan is null) continue;
            follow.Add(new WorkRequest(baseId, table, id,
                recorder ? WorkFlags.Changed | WorkFlags.Movements : WorkFlags.Changed | WorkFlags.Force,
                recorder ? ItemPriority.Document : ItemPriority.Catalog));
            if (follow.Count >= MaxReferrers) break;
        }
        return follow;
    }
}

/// <summary>
/// <c>delete_object</c> (§10): the row goes from every partition it was sent to; a document's
/// movements go too (reconciled against an empty live set by the recorder handler, S8). A delete
/// the target refuses on policy surfaces as <see cref="Outcome.Policy"/> → dead letter
/// <c>needs_approval</c>, never dropped.
/// </summary>
public sealed class DeleteObjectHandler(SyncDb db, IBackendSyncTarget target, IReadOnlyDictionary<string, TablePlan> tables,
                                        IPartitioner partitions) : IWorkHandler
{
    /// <summary>Set by S8: removes a deleted document's movements from every configured register.</summary>
    public Func<string, TablePlan, string, CancellationToken, Task>? ClearMovements { get; set; }

    public async Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct)
    {
        var t = tables.GetValueOrDefault(item.Table) ?? throw new OneCMetadataMissingException($"table {item.Table} is not configured");
        // Always sent, even for an object we have no record of sending: an upload can land and the
        // process die before its version is stored (S15 kill test left exactly such a row behind
        // when "never sent" was skipped). A delete of a row that is not there costs one call.
        var where = db.Read(tx => tx.GetPartitions(baseId, item.ObjectKey));
        // No history (sent by a snapshot before it recorded partitions, or never sent): every
        // partition — each call stays in the one it names, so nothing narrower can find it.
        if (where.Count == 0) where = partitions.All.ToList();
        foreach (var p in where) await DeleteRowAsync(baseId, t, p, item.ObjectKey, ct);
        if (t.Family == Families.Document && ClearMovements is { } clear) await clear(baseId, t, item.ObjectKey, ct);
        return new HandlerOutcome(tx =>
        {
            tx.DeleteVersion(baseId, t.Table, item.ObjectKey);
            if (t.Family == Families.Catalog) tx.DeleteMeta(CanonicalMapper.ShownKey(baseId, t.Table, item.ObjectKey));
            foreach (var p in where) tx.RemovePartition(baseId, item.ObjectKey, p);
        });
    }

    public async Task DeleteRowAsync(string baseId, TablePlan t, string partition, string key, CancellationToken ct)
    {
        var r = await target.DeleteRowsAsync(new DeleteBatch(Guid.NewGuid().ToString("N"), partition, t.Table, new[] { key }, "deleted in 1C"), ct);
        if (!r.Ok) throw new SyncPausedException(r.Outcome, $"delete {partition}/{t.Table}/{key}: {r.Outcome} {r.HttpStatus} {r.Message}", r.HttpStatus);
    }
}
