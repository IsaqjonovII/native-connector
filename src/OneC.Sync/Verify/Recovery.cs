using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Source;

namespace OneC.Sync.Verify;

/// <summary>
/// Recovery (§11) and Fallback (§12) on top of the verify pass.
/// <list type="bullet">
/// <item>Recovery — the feed was reset, the base restored, or the state lost: verify every table,
/// then adopt the cursor the feed service parked (or the current tail) and go back to Incremental,
/// all in one transaction. Until then the old cursor stays; a crash repeats the pass, it never
/// jumps to the tail.</item>
/// <item>Fallback — no readable log (remote server, .lgd, access denied): the same pass per table on
/// a cadence, one table per turn, latency minutes instead of seconds.</item>
/// </list>
/// </summary>
public sealed class Recovery(SyncDb db, VerifyRunner verify)
{
    public static string FallbackTurnKey(string baseId) => "fallback_next:" + baseId;

    /// <summary>
    /// The mode a base starts in. Lost state (a quarantined sync.db) is Recovery for every base,
    /// never "start from the tail" (§13 Corruption).
    /// </summary>
    public static string StartMode(SyncDb db, string baseId, bool feedReadable)
    {
        var b = db.Read(tx => tx.GetBase(baseId));
        if (db.RecoveryRequired is not null) return SyncModes.Recovery;
        if (b is null) return SyncModes.Snapshot;
        if (!feedReadable && b.Mode is SyncModes.Incremental) return SyncModes.Fallback;
        // The log is readable again (it was missing at one start: a new base, a share offline): leave
        // Fallback through a verify pass instead of scanning every 15 minutes forever (2026-10-01 review).
        if (feedReadable && b.Mode is SyncModes.Fallback) return SyncModes.Recovery;
        return b.Mode;
    }

    /// <param name="nextMode">The mode to commit with the cursor: Incremental, or Snapshot when a first copy was interrupted.</param>
    public async Task<IReadOnlyList<VerifyResult>> RecoverAsync(string baseId, IReadOnlyList<TablePlan> tables, string logDir,
                                                                 EventLogReader reader, CancellationToken ct, string nextMode = SyncModes.Incremental)
    {
        bool recorded = tables.Any(t => Families.IsRecorded(t.Family));
        // Where the feed resumes: the position parked at the reset, or — when none was parked (lost
        // state, leaving Fallback) — the log's end NOW, before the verify pass, so a change made while
        // the pass runs is still read afterwards (taken after the pass, it could be skipped).
        string parked = db.Read(tx => tx.GetMeta(FeedService.RecoveryCursorKey(baseId))) ?? reader.Handshake(logDir).ToString();
        var results = new List<VerifyResult>();
        foreach (var t in tables) results.Add(await verify.VerifyTableAsync(baseId, t, recorded && t.IsMovement, ct));
        // Every difference is now durable work: the feed may resume from the parked position.
        db.Write(tx =>
        {
            tx.SetCursor(baseId, parked, logDir, resetReason: "recovered");
            tx.DeleteMeta(FeedService.RecoveryCursorKey(baseId));
            // Only out of Recovery: a Pause made during the pass stays (2026-10-01 review).
            if (tx.GetBase(baseId) is { Mode: SyncModes.Recovery })
                tx.SetMode(baseId, nextMode, nextMode == SyncModes.Incremental ? "verify pass done" : "verify pass done; continuing the first copy");
        });
        return results;
    }

    /// <summary>One fallback turn: the next table in round-robin order; returns what it found.</summary>
    public async Task<VerifyResult?> FallbackTurnAsync(string baseId, IReadOnlyList<TablePlan> tables, CancellationToken ct)
    {
        var candidates = tables.Where(t => t.Family is Families.Catalog or Families.Document or Families.Chart or Families.IndependentInfoRegister).ToList();
        if (candidates.Count == 0) return null;
        int turn = int.TryParse(db.Read(tx => tx.GetMeta(FallbackTurnKey(baseId))), out var n) ? n : 0;
        var t = candidates[turn % candidates.Count];
        bool recorded = tables.Any(x => Families.IsRecorded(x.Family));
        var r = await verify.VerifyTableAsync(baseId, t, recorded && t.IsMovement, ct);
        db.Write(tx => tx.SetMeta(FallbackTurnKey(baseId), ((turn + 1) % candidates.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return r;
    }
}
