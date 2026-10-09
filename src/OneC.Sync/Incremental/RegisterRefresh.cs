using System.Security.Cryptography;
using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Incremental;

/// <summary>
/// <c>refresh_register</c> for an independent information register (§8): its log events name no row
/// (S0: <c>{"U"}</c>), so any number of them coalesce into one refresh of the table. The refresh
/// walks the register by natural key, compares each row's hash with the one last sent, uploads only
/// the changed or new rows, and — once every changed row was accepted — deletes the rows 1C no
/// longer has. At most one refresh per table per <see cref="TablePlan.RefreshEvery"/> (D-6).
/// This is the only per-row state the engine keeps (<c>register_rows</c>), and only for this family.
/// </summary>
public sealed class RefreshRegisterHandler(SyncDb db, IOneCReader reader, IBackendSyncTarget target, Uploader uploader,
                                           IReadOnlyDictionary<string, TablePlan> tables, IPartitioner partitions) : IWorkHandler
{
    public int PageSize { get; init; } = 1000;

    public static string LastRefreshKey(string baseId, string table) => $"refresh_at:{baseId}:{table}";

    /// <summary>Set when the user approved a refresh's delete the backend refused over its cap (§10): the next refresh sends it approved.</summary>
    public static string ApprovedDeleteKey(string baseId, string table) => $"refresh_delete_approved:{baseId}:{table}";

    public static string Hash(SyncRow row) => Convert.ToHexString(SHA256.HashData(row.Json), 0, 16);

    /// <summary>Thrown when the table's interval has not passed yet: the executor retries the item later, no error.</summary>
    public sealed class NotDueException(DateTimeOffset due) : Exception($"refresh due at {due:O}")
    {
        public DateTimeOffset Due { get; } = due;
    }

    public async Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct)
    {
        var t = tables.GetValueOrDefault(item.Table) ?? throw new OneCMetadataMissingException($"table {item.Table} is not configured");
        if (t.RefreshEvery is { } every && db.Read(tx => tx.GetMeta(LastRefreshKey(baseId, t.Table))) is { } last &&
            SyncDb.ParseIso(last) + every is var due && due > db.Now)
            throw new NotDueException(due);

        long run = db.Write(tx => tx.StartRun(baseId, "refresh " + t.Table));
        long read = 0, sent = 0;
        string? after = null;
        int offset = 0;
        bool chart = t.Family == Families.Chart;
        while (true)
        {
            // A chart of accounts has no natural-key register read: it pages by offset in code order
            // and is keyed by code (2026-10-01 review: charts landed here and threw on every change).
            var page = chart ? await reader.ChartPageAsync(baseId, t.Name, offset, PageSize, ct)
                             : await reader.RegisterPageAsync(baseId, t, null, 0, after, PageSize, null, ct);
            offset += page.Rows.Count;
            // Rows before the window are not seen, so a copy sent earlier is removed with the gone ones.
            var mapped = page.Rows.Where(r => CanonicalMapper.InWindow(t, r)).Select(r => CanonicalMapper.Map(t, r)).ToList();
            read += mapped.Count;
            var changed = db.Write(tx => tx.DiffRegisterRows(baseId, t.Table, run, mapped.Select(m => (m.Key, Hash(m.Row))).ToList()))
                            .ToHashSet(StringComparer.Ordinal);
            foreach (var group in mapped.Where(m => changed.Contains(m.Key)).GroupBy(m => partitions.PartitionOf(t, m) ?? ""))
            {
                if (group.Key.Length == 0)
                {
                    // Still in 1C, just not sendable yet: stamped (so it is not taken for deleted) with a
                    // hash that differs from any real one, so it goes out once its organisation is bound.
                    db.Write(tx =>
                    {
                        foreach (var org in group.GroupBy(c => c.OrgRef ?? "")) tx.CountUnmappedOrg(baseId, org.Key, t.Table, org.Count());
                        foreach (var c in group) tx.SetRegisterRow(baseId, t.Table, c.Key, "unmapped", run);
                    });
                    continue;
                }
                foreach (var chunk in group.Chunk(2000))
                {
                    var r = await uploader.UploadAsync(baseId, new UploadBatch(Guid.NewGuid().ToString("N"), group.Key, t.Table, chunk.Select(c => c.Row).ToList()), ct);
                    if (r.Rejected.Count > 0) throw new SyncPausedException(Outcome.Validation, $"{t.Table}: {r.Rejected[0].Key} {r.Rejected[0].Reason}", 400);
                    db.Write(tx => { foreach (var c in chunk) tx.SetRegisterRow(baseId, t.Table, c.Key, Hash(c.Row), run); });
                    sent += chunk.Length;
                }
            }
            if (!page.HasMore) break;
            if (!chart)
            {
                if (page.NextKey is null) break;
                after = page.NextKey;
            }
        }

        // Every changed row was accepted (or this threw): what was not seen is gone from 1C. The row's
        // partition is not kept (an organisation's row may sit in its binding), so every partition is
        // asked — each call stays in the partition it names. A delete the backend refuses over its cap
        // waits for the user's approval (needs_approval on this refresh); once approved it goes approved.
        bool approved = db.Read(tx => tx.GetMeta(ApprovedDeleteKey(baseId, t.Table))) is not null;
        long deleted = 0;
        while (true)
        {
            var gone = db.Read(tx => tx.UnseenRegisterRows(baseId, t.Table, run, 500));
            if (gone.Count == 0) break;
            foreach (var p in partitions.All)
            {
                var r = await target.DeleteRowsAsync(new DeleteBatch(Guid.NewGuid().ToString("N"), p, t.Table, gone, "removed in 1C", Approved: approved), ct);
                if (!r.Ok) throw new SyncPausedException(r.Outcome, $"{t.Table}: delete {gone.Count} rows in {p}: {r.Outcome} {r.Message}", r.HttpStatus);
            }
            db.Write(tx => { foreach (var k in gone) tx.DeleteRegisterRow(baseId, t.Table, k); });
            deleted += gone.Count;
        }
        return new HandlerOutcome(tx =>
        {
            if (approved) tx.DeleteMeta(ApprovedDeleteKey(baseId, t.Table));
            tx.SetMeta(LastRefreshKey(baseId, t.Table), SyncDb.Iso(tx.Now));
            tx.FinishRun(run, "ok", read, sent, 0, deleted == 0 ? null : $"{deleted} removed");
        }, Skipped: sent == 0 && deleted == 0);
    }
}
