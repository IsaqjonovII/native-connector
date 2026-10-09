using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Incremental;

/// <summary>
/// <c>sync_recorder</c> (§9): post, unpost, repost, or a movement change of a document — one
/// logical unit, retried from the start. Reads the document and its complete current movements in
/// every configured register it can post to (paged, R-8), sends them, then makes the cloud hold
/// exactly that set: every register, every partition the recorder ever used, reconciled against
/// the live keys — so fewer lines, changed amounts on the same keys (F1), registers outside the
/// document's config (F2) and an organisation change all leave nothing stale. On a target with an
/// atomic recorder call (v3) the whole unit is one request per partition.
/// </summary>
public sealed class SyncRecorderHandler(SyncDb db, IOneCReader reader, IBackendSyncTarget target, Uploader uploader,
                                        TargetCapabilities caps, IReadOnlyDictionary<string, TablePlan> tables,
                                        IPartitioner partitions, DeleteObjectHandler deletes) : IWorkHandler
{
    private readonly Dictionary<string, IReadOnlyList<string>> _types = new(StringComparer.Ordinal);

    public int PageSize { get; init; } = 1000;
    public long MovementReads { get; private set; }

    private IEnumerable<TablePlan> Registers => tables.Values.Where(t => Families.IsRecorded(t.Family));

    public async Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct)
    {
        string recorder = item.ObjectKey;
        var docTable = tables.GetValueOrDefault(item.Table);
        string? docType = docTable?.Name
            ?? (item.Table.StartsWith("?Документ.", StringComparison.Ordinal) ? item.Table["?Документ.".Length..] : null)
            ?? await reader.RecorderOfAsync(baseId, Registers.ToList(), recorder, ct);

        // The document itself (only when its table is synced).
        MappedRow? doc = null;
        bool docInWindow = true;
        if (docTable is not null)
        {
            var rows = await reader.ByIdsAsync(baseId, docTable, new[] { recorder }, ct);
            if (rows.Count == 0) return await deletes.HandleAsync(baseId, item with { Kind = WorkKinds.DeleteObject }, ct);
            docInWindow = CanonicalMapper.InWindow(docTable, rows[0]);
            doc = CanonicalMapper.Map(docTable, rows[0]);
        }

        // Its movements now, per register it can write to; a type we cannot resolve means the
        // recorder no longer exists: its movements are none.
        var movements = new Dictionary<TablePlan, List<MappedRow>>();
        foreach (var reg in Registers)
        {
            var list = new List<MappedRow>();
            if (docType is not null && (await TypesAsync(baseId, reg, ct)).Contains(docType))
            {
                int? after = null;
                while (true)
                {
                    var page = await reader.MovementsAsync(baseId, reg, docType, recorder, after, PageSize, ct);
                    MovementReads++;
                    // Rows before the register's window are not sent, so the reconcile below removes any copy.
                    list.AddRange(page.Rows.Where(r => CanonicalMapper.InWindow(reg, r)).Select(r => CanonicalMapper.Map(reg, r, doc?.Row.SourceVersion)));
                    if (!page.HasMore || page.NextLine is null) break;
                    after = page.NextLine;
                }
            }
            movements[reg] = list;
        }

        // Where everything goes; rows of an organisation without a binding are counted, not sent.
        var unmapped = new List<(string Org, string Table)>();
        // A document before its table's window is not sent (and leaves every partition it was in, below).
        string? docPartition = doc is null || !docInWindow ? null : partitions.PartitionOf(docTable!, doc);
        if (doc is not null && docInWindow && docPartition is null) unmapped.Add((doc.OrgRef ?? "", docTable!.Table));
        var byPartition = new Dictionary<string, Dictionary<TablePlan, List<MappedRow>>>(StringComparer.Ordinal);
        foreach (var (reg, rows) in movements)
            foreach (var r in rows)
            {
                if (partitions.PartitionOf(reg, r) is not { } p) { unmapped.Add((r.OrgRef ?? "", reg.Table)); continue; }
                if (!byPartition.TryGetValue(p, out var m)) byPartition[p] = m = new();
                if (!m.TryGetValue(reg, out var l)) m[reg] = l = new();
                l.Add(r);
            }
        var history = db.Read(tx => tx.GetPartitions(baseId, recorder));
        var current = byPartition.Keys.Append(docPartition).OfType<string>().Distinct().ToList();
        // Reconcile everywhere the recorder has or had rows; the shared partition too, for rows sent
        // before routing existed; every partition when its history is unknown (each reconcile stays
        // in the partition it names, so an unknown old copy is only found by asking each one).
        var touch = current.Concat(history).Append(partitions.Shared)
                           .Concat(history.Count == 0 ? partitions.All : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();

        if (caps.AtomicRecorder)
        {
            // One call, one backend transaction for the whole unit — every partition it touches,
            // so an organisation change never leaves the document in two places in between.
            var sets = byPartition.SelectMany(kv => kv.Value.Select(m => new PartitionMovements(kv.Key, m.Key.Table, m.Value.Select(x => x.Row).ToList())))
                                  .ToList();
            var r = await target.SyncRecorderAtomicAsync(new RecorderSync(Guid.NewGuid().ToString("N"), recorder, doc?.Row.SourceVersion,
                docTable?.Table ?? "", docPartition, docPartition is null ? null : doc!.Row, touch, Registers.Select(reg => reg.Table).ToList(), sets), ct);
            if (!r.Ok) throw new SyncPausedException(r.Outcome, $"recorder {recorder}: {r.Outcome} {r.Message}", r.HttpStatus);
        }
        else
        {
            if (doc is not null && docPartition is not null) await SendAsync(baseId, docPartition, docTable!, new[] { doc }, ct);
            foreach (var (p, regs) in byPartition)
                foreach (var (reg, rows) in regs)
                    await SendAsync(baseId, p, reg, rows, ct);
            // Only after the new rows are in: remove what is no longer there (a crash between the two
            // leaves extra old rows until the retry, never missing new ones).
            foreach (var p in touch)
                foreach (var reg in Registers)
                {
                    var live = byPartition.TryGetValue(p, out var m) && m.TryGetValue(reg, out var l) ? l.Select(x => x.Key).ToList() : new List<string>();
                    var r = await target.ReconcileRecorderAsync(new RecorderReconcile(Guid.NewGuid().ToString("N"), p, reg.Table, recorder, live), ct);
                    if (!r.Ok) throw new SyncPausedException(r.Outcome, $"reconcile {p}/{reg.Table}/{recorder}: {r.Outcome} {r.Message}", r.HttpStatus);
                }
            // The document row in a partition it left (organisation changed).
            if (docTable is not null)
                foreach (var p in history.Where(h => h != docPartition))
                    await deletes.DeleteRowAsync(baseId, docTable, p, recorder, ct);
        }

        return new HandlerOutcome(tx =>
        {
            // Only a document that was sent keeps its version: one of an unbound organisation must look
            // new to the verify pass after its binding (2026-10-01 review).
            if (doc is not null && docPartition is not null) tx.SetVersion(baseId, docTable!.Table, doc.Key, doc.DataVersion);
            foreach (var p in history.Except(current)) tx.RemovePartition(baseId, recorder, p);
            foreach (var p in current) tx.AddPartition(baseId, recorder, p);
            foreach (var (org, table) in unmapped.Distinct()) tx.CountUnmappedOrg(baseId, org, table, unmapped.Count(u => u == (org, table)));
        });
    }

    /// <summary>A deleted document's movements: every configured register, every partition it used, against an empty live set.</summary>
    public async Task ClearAsync(string baseId, TablePlan docTable, string recorder, CancellationToken ct)
    {
        var history = db.Read(tx => tx.GetPartitions(baseId, recorder));
        var where = history.Append(partitions.Shared).Concat(history.Count == 0 ? partitions.All : Array.Empty<string>())
                           .Distinct(StringComparer.Ordinal).ToList();
        foreach (var p in where)
            foreach (var reg in Registers)
            {
                var r = await target.ReconcileRecorderAsync(new RecorderReconcile(Guid.NewGuid().ToString("N"), p, reg.Table, recorder, Array.Empty<string>()), ct);
                if (!r.Ok) throw new SyncPausedException(r.Outcome, $"reconcile {p}/{reg.Table}/{recorder}: {r.Outcome} {r.Message}", r.HttpStatus);
            }
    }

    private async Task SendAsync(string baseId, string partition, TablePlan t, IReadOnlyList<MappedRow> rows, CancellationToken ct)
    {
        var unique = new Dictionary<string, MappedRow>(StringComparer.Ordinal);
        foreach (var r in rows) unique[r.Key] = r;
        foreach (var chunk in unique.Values.Chunk(Math.Min(2000, caps.MaxBatchRows)))
        {
            var r = await uploader.UploadAsync(baseId, new UploadBatch(Guid.NewGuid().ToString("N"), partition, t.Table, chunk.Select(x => x.Row).ToList()), ct);
            if (r.Rejected.Count > 0) throw new SyncPausedException(Outcome.Validation, $"{t.Table}: {r.Rejected[0].Key} {r.Rejected[0].Reason}", 400);
        }
    }

    private async Task<IReadOnlyList<string>> TypesAsync(string baseId, TablePlan reg, CancellationToken ct)
    {
        string key = baseId + "|" + reg.Table;
        lock (_types) if (_types.TryGetValue(key, out var hit)) return hit;
        var types = await reader.RecorderTypesAsync(baseId, reg, ct);
        lock (_types) _types[key] = types;
        return types;
    }
}
