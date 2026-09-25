using System.Text.Json.Nodes;
using OneC.EventLog;

namespace OneC.Sync;

/// <summary>What one pass did, per table.</summary>
public sealed class SyncReport
{
    public Dictionary<string, (int Uploaded, int Pruned, int Reconciled)> Tables { get; } = new(StringComparer.Ordinal);
    public int FeedEvents { get; set; }
    public bool FeedReset { get; set; }
    public List<string> Warnings { get; } = new();

    public void Add(string table, int uploaded = 0, int pruned = 0, int reconciled = 0)
    {
        var (u, p, r) = Tables.GetValueOrDefault(table);
        Tables[table] = (u + uploaded, p + pruned, r + reconciled);
    }
}

/// <summary>
/// One base's 1C → cloud sync (plan 5.9, D39). A pass:
/// <list type="number">
/// <item>the first time, take the change feed's tail cursor BEFORE anything is read — changes
/// made during the cold read are then replayed, never lost;</item>
/// <item>cold-read every table not yet done — catalogs by keyset, documents and registers by
/// date cursor — uploading page by page and saving progress after each accepted page;</item>
/// <item>replay the change feed: changed documents and catalog items are re-read by id and
/// uploaded; a posted, unposted or deleted document's register rows are re-read by recorder and
/// reconciled (the backend drops that recorder's rows no longer live); deleted objects are
/// pruned. A feed reset (log recreated or rotated past the cursor) re-reads every table.</item>
/// </list>
/// The connector's count gate, hash maps and two-sweep delete detection are not needed: the
/// feed names every change (D36). Organisation partitions (multi-org) are not done here yet —
/// everything goes to the base's one oneCId.
/// </summary>
public sealed class SyncEngine
{
    public int PageSize { get; init; } = 500;
    public const int IdsPerBatch = 200;

    private readonly IOneCSource _source;
    private readonly IUploadTarget _target;

    public SyncEngine(IOneCSource source, IUploadTarget target)
    {
        _source = source;
        _target = target;
    }

    public async Task<SyncReport> RunOnceAsync(string baseName, string oneCId, IReadOnlyList<SyncTable> tables, BaseState state,
                                               string statePath, CancellationToken ct = default)
    {
        var report = new SyncReport();
        var supported = tables.Where(t => t.Kind != TableKind.ChartOfAccounts).ToList();
        foreach (var t in tables.Except(supported)) report.Warnings.Add($"{t.Name}: chart-of-accounts reads are not ported yet; skipped");

        if (state.FeedCursor is null)
        {
            var tail = await _source.ChangesAsync(baseName, null, ct);
            state.FeedCursor = tail.Cursor?.ToString();
            state.Save(statePath);
        }

        foreach (var t in supported.Where(t => !state.Table(t.Name).ColdDone))
            await ColdReadAsync(baseName, oneCId, t, state, statePath, report, ct);

        await ReplayFeedAsync(baseName, oneCId, supported, state, statePath, report, ct);
        return report;
    }

    // ---------------- cold read ----------------

    private async Task ColdReadAsync(string baseName, string oneCId, SyncTable t, BaseState state, string statePath,
                                     SyncReport report, CancellationToken ct)
    {
        var ts = state.Table(t.Name);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<JsonObject> rows;
            bool more;
            switch (t.Kind)
            {
                case TableKind.Catalog:
                    rows = await _source.CatalogPageAsync(baseName, t.OneCName, ts.After ?? Guid.Empty.ToString(), PageSize, ct);
                    more = rows.Count >= PageSize;
                    if (rows.Count > 0) ts.After = rows[^1]["id"]?.GetValue<string>();
                    break;
                case TableKind.Document:
                {
                    var page = await _source.DocumentPageAsync(baseName, t.OneCName, ts.CursorDate, ts.CursorSkip, PageSize, t.From, ct);
                    rows = page.Rows;
                    more = rows.Count >= PageSize;
                    if (page.NextCursorDate is not null) { ts.CursorDate = page.NextCursorDate; ts.CursorSkip = page.NextSkip; }
                    break;
                }
                default:
                {
                    var page = await _source.RegisterPageAsync(baseName, t, ts.CursorDate ?? Iso(t.From), ts.CursorSkip, PageSize, ct);
                    rows = page.Rows;
                    more = page.HasMore;
                    if (page.NextCursorDate is not null) { ts.CursorDate = page.NextCursorDate; ts.CursorSkip = page.NextSkip; }
                    break;
                }
            }
            if (rows.Count > 0)
            {
                await UploadAsync(oneCId, t, rows, ct);
                ts.Uploaded += rows.Count;
                report.Add(t.Name, uploaded: rows.Count);
            }
            if (!more) ts.ColdDone = true;
            state.Save(statePath);                                      // only after the backend accepted the page
            if (!more) return;
        }
    }

    // ---------------- change feed ----------------

    private async Task ReplayFeedAsync(string baseName, string oneCId, List<SyncTable> tables, BaseState state, string statePath,
                                       SyncReport report, CancellationToken ct)
    {
        var byMetadata = tables.GroupBy(t => t.Metadata).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var recorded = tables.Where(t => t.Kind is TableKind.AccountingRegister or TableKind.AccumulationRegister).ToList();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = state.FeedCursor is null ? null : LogCursor.Parse(state.FeedCursor);
            var batch = await _source.ChangesAsync(baseName, cursor, ct);
            if (batch.Reset)
            {
                // The log was recreated, truncated or rotated past us: changes may be lost, so read everything again.
                report.FeedReset = true;
                report.Warnings.Add($"change feed reset ({batch.ResetReason}); every table is read again");
                foreach (var t in tables) state.Tables[t.Name] = new TableState();
                state.FeedCursor = batch.Cursor?.ToString();
                state.Save(statePath);
                foreach (var t in tables) await ColdReadAsync(baseName, oneCId, t, state, statePath, report, ct);
                return;
            }

            report.FeedEvents += batch.Events.Count;
            foreach (var group in batch.Events.Where(e => byMetadata.ContainsKey(e.Metadata)).GroupBy(e => e.Metadata))
                foreach (var t in byMetadata[group.Key])
                    await ApplyAsync(baseName, oneCId, t, group.ToList(), recorded, state, report, ct);

            state.FeedCursor = batch.Cursor?.ToString() ?? state.FeedCursor;
            state.Save(statePath);
            if (!batch.More) return;
        }
    }

    private async Task ApplyAsync(string baseName, string oneCId, SyncTable t, List<ChangeEvent> events, List<SyncTable> recorded,
                                  BaseState state, SyncReport report, CancellationToken ct)
    {
        // The last event per object wins: New+Update+Delete in one batch is a delete.
        var last = events.Where(e => e.Ref is not null).GroupBy(e => e.Ref!).ToDictionary(g => g.Key, g => g.ToList());
        var deleted = last.Where(p => p.Value[^1].Kind == "Delete").Select(p => p.Key).ToList();
        var changed = last.Keys.Except(deleted).ToList();
        var movementsMoved = last.Where(p => p.Value.Any(e => e.Kind is "Post" or "Unpost")).Select(p => p.Key).Except(deleted).ToList();

        switch (t.Kind)
        {
            case TableKind.Document:
            {
                var found = new List<JsonObject>();
                foreach (var chunk in changed.Chunk(IdsPerBatch))
                    found.AddRange(await _source.DocumentsByIdsAsync(baseName, t.OneCName, chunk, ct));
                if (found.Count > 0) { await UploadAsync(oneCId, t, found, ct); report.Add(t.Name, uploaded: found.Count); }
                // Asked for but not there: deleted after the event was logged.
                var gone = changed.Except(found.Select(r => r["id"]!.GetValue<string>())).ToList();
                await PruneAsync(baseName, oneCId, t, deleted.Concat(gone).ToList(), report, ct);
                foreach (var id in movementsMoved.Except(gone))
                    foreach (var r in recorded) await ReconcileAsync(baseName, oneCId, r, t.OneCName, id, alive: true, report, ct);
                foreach (var id in deleted.Concat(gone))
                    foreach (var r in recorded) await ReconcileAsync(baseName, oneCId, r, t.OneCName, id, alive: false, report, ct);
                break;
            }
            case TableKind.Catalog:
            {
                var found = new List<JsonObject>();
                var gone = new List<string>();
                foreach (var id in changed)
                {
                    if (await _source.CatalogByIdAsync(baseName, t.OneCName, id, ct) is { } row) found.Add(row);
                    else gone.Add(id);
                }
                if (found.Count > 0) { await UploadAsync(oneCId, t, found, ct); report.Add(t.Name, uploaded: found.Count); }
                await PruneAsync(baseName, oneCId, t, deleted.Concat(gone).ToList(), report, ct);
                break;
            }
            default:
                // A register written on its own (no document): rows cannot be addressed by recorder.
                // Recorder-subordinate registers are handled through their documents' Post events.
                if (t.Kind == TableKind.InformationRegister || events.Any(e => e.Ref is null))
                {
                    state.Tables[t.Name] = new TableState();
                    report.Warnings.Add($"{t.Name}: changed outside a document; read again next pass");
                }
                break;
        }
    }

    private async Task ReconcileAsync(string baseName, string oneCId, SyncTable register, string document, string id, bool alive,
                                      SyncReport report, CancellationToken ct)
    {
        var rows = alive ? await _source.RegisterByRecorderAsync(baseName, register, document, id, ct) : new List<JsonObject>();
        if (rows.Count > 0) { await UploadAsync(oneCId, register, rows, ct); report.Add(register.Name, uploaded: rows.Count); }
        var live = rows.Select(r => RowIdentity.RowKey(register, r)).OfType<string>().ToList();
        int deleted = await _target.ReconcileRecorderAsync(oneCId, register.Name, id, live, ct);
        report.Add(register.Name, reconciled: deleted);
    }

    private async Task PruneAsync(string baseName, string oneCId, SyncTable t, List<string> keys, SyncReport report, CancellationToken ct)
    {
        if (keys.Count == 0) return;
        try
        {
            long live = await _source.CountAsync(baseName, t, ct);
            report.Add(t.Name, pruned: await _target.PruneAsync(oneCId, t.Name, keys, (int)Math.Min(live, int.MaxValue), ct));
        }
        catch (UploadException e) when (e.Status == 409)
        {
            // The backend's guard (nothing stored, or too many keys at once) — keep the rows, say so.
            report.Warnings.Add($"{t.Name}: prune of {keys.Count} refused by the backend: {e.Message}");
        }
    }

    // ---------------- upload ----------------

    private Task UploadAsync(string oneCId, SyncTable t, List<JsonObject> rows, CancellationToken ct)
    {
        foreach (var row in rows) Stamp(t, row);
        return _target.UploadAsync(oneCId, new Dictionary<string, List<JsonObject>> { [t.Name] = rows }, ct);
    }

    /// <summary><c>__rowKey</c> and <c>__rowHash</c> as the connector stamps them (hash of the row before stamping).</summary>
    public static void Stamp(SyncTable t, JsonObject row)
    {
        row.Remove("__rowKey");
        row.Remove("__rowHash");
        string hash = RowIdentity.Hash(row);
        if (RowIdentity.RowKey(t, row) is { } key) row["__rowKey"] = key;
        row["__rowHash"] = hash;
    }

    private static string? Iso(DateTime? d) => d?.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
