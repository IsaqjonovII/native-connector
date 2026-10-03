using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Source;

namespace OneC.Sync.Verify;

public sealed record VerifyResult(long Scanned, int Changed, int Gone, int Queued);

/// <summary>
/// The verify pass (§12), shared by Recovery, Fallback and a newly bound organisation: a narrow scan
/// of <c>(Ссылка, ВерсияДанных)</c> in 1C's own order, page by page against the stored versions —
/// new or changed objects become sync items, keys 1C no longer has become deletes. Memory stays
/// one page; each page's stamps and the items it produced commit together, so a crash mid-pass
/// loses nothing (the next pass starts over with a new run id). Only objects that differ cost an
/// upload: after a feed reset this replaces re-sending the whole table.
/// </summary>
public sealed class VerifyRunner(SyncDb db, IOneCReader reader)
{
    public int PageSize { get; init; } = 5000;

    /// <param name="recordedDocuments">Documents whose movements are synced: a changed one needs a recorder sync, not only its row.</param>
    public async Task<VerifyResult> VerifyTableAsync(string baseId, TablePlan t, bool recordedDocuments, CancellationToken ct)
    {
        if (t.Family is not (Families.Catalog or Families.Document))
        {
            // Registers follow their documents; charts and independent registers are refreshed whole.
            if (t.Family is Families.Chart or Families.IndependentInfoRegister)
                db.Write(tx => tx.UpsertWork(new WorkRequest(baseId, t.Table, t.Table, WorkFlags.Refresh, ItemPriority.Verify)));
            return new VerifyResult(0, 0, 0, t.Family is Families.Chart or Families.IndependentInfoRegister ? 1 : 0);
        }
        bool doc = t.Family == Families.Document;
        var changedFlags = doc && recordedDocuments ? WorkFlags.Changed | WorkFlags.Movements : WorkFlags.Changed;
        long run = db.Write(tx => tx.StartRun(baseId, "verify " + t.Table));
        long scanned = 0;
        int changed = 0, queued = 0;
        string? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (rows, next) = await reader.VersionPageAsync(baseId, t, after, PageSize, ct);
            scanned += rows.Count;
            db.Write(tx =>
            {
                var diff = tx.MarkSeen(baseId, t.Table, run, rows);
                foreach (var key in diff) tx.UpsertWork(new WorkRequest(baseId, t.Table, key, changedFlags, ItemPriority.Verify));
                changed += diff.Count;
                queued += diff.Count;
            });
            if (next is null) break;
            after = next;
        }
        // The scan saw every key 1C has: stored keys it did not see are gone.
        int gone = 0;
        while (true)
        {
            var missing = db.Read(tx => tx.UnseenKeys(baseId, t.Table, run, 1000));
            if (missing.Count == 0) break;
            db.Write(tx =>
            {
                foreach (var key in missing)
                {
                    tx.UpsertWork(new WorkRequest(baseId, t.Table, key, WorkFlags.Deleted, ItemPriority.Destructive));
                    // Stamped so the loop moves on; the delete item removes the version when it runs.
                    tx.SetVersion(baseId, t.Table, key, tx.GetVersion(baseId, t.Table, key), run);
                }
            });
            gone += missing.Count;
            queued += missing.Count;
        }
        db.Write(tx => tx.FinishRun(run, "ok", scanned, 0, 0, $"{changed} changed, {gone} gone"));
        return new VerifyResult(scanned, changed, gone, queued);
    }
}
