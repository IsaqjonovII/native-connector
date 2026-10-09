namespace OneC.SyncState;

/// <summary>What happened to an object (§6). <see cref="Recreated"/> is input only: a New after a Delete.</summary>
[Flags]
public enum WorkFlags
{
    None = 0,
    Changed = 1,
    Movements = 2,
    Deleted = 4,
    Recreated = 8,
    Refresh = 16,           // table-level: independent information register
    Verify = 32,            // table-level: version scan
    ApprovedDelete = 64,    // a delete the user approved after a policy refusal (§10)
    Force = 128             // send even when 1C's version is unchanged: a referenced item's name changed
}

public static class WorkKinds
{
    public const string SyncObject = "sync_object";
    public const string SyncRecorder = "sync_recorder";
    public const string DeleteObject = "delete_object";
    public const string RefreshRegister = "refresh_register";
    public const string VerifyTable = "verify_table";
    public const string ApprovedDelete = "approved_delete";

    /// <summary>§6 "map flags to one work item per object"; the strongest intent decides.</summary>
    public static string For(WorkFlags f) =>
        f.HasFlag(WorkFlags.ApprovedDelete) ? ApprovedDelete :
        f.HasFlag(WorkFlags.Deleted) ? DeleteObject :
        f.HasFlag(WorkFlags.Movements) ? SyncRecorder :
        f.HasFlag(WorkFlags.Changed) ? SyncObject :
        f.HasFlag(WorkFlags.Refresh) ? RefreshRegister :
        f.HasFlag(WorkFlags.Verify) ? VerifyTable :
        throw new ArgumentException($"no work in flags {f}");

    /// <summary>
    /// §6 merging with a pending item: flags are OR-ed and Deleted wins, except that a New after a
    /// Delete of the same key (an object re-created with a supplied GUID) clears Deleted. Every item
    /// re-reads 1C when it runs, so a merge can only drop redundant reads, never state.
    /// </summary>
    public static WorkFlags Merge(WorkFlags pending, WorkFlags incoming)
    {
        // A re-created object cancels a pending delete — the approved kind too: an approved delete left
        // pending would delete the object that exists again (2026-10-01 review).
        if (incoming.HasFlag(WorkFlags.Recreated))
            return ((pending & ~(WorkFlags.Deleted | WorkFlags.ApprovedDelete)) | incoming | WorkFlags.Changed) & ~WorkFlags.Recreated;
        return (pending | incoming) & ~WorkFlags.Recreated;
    }
}

/// <summary>An intent from the coalescer (or recovery, or the user).</summary>
public sealed record WorkRequest(string BaseId, string Table, string ObjectKey, WorkFlags Flags, int Priority);

/// <summary>A claimed item. <see cref="Generation"/> is what completion is checked against.</summary>
public sealed record WorkItem(long Id, string BaseId, string Kind, string Table, string ObjectKey,
                              WorkFlags Flags, int Priority, long Generation, int Attempts, string? LastError);

public sealed partial class SyncTx
{
    /// <summary>
    /// Adds an intent, merging into the pending item for the same object (UNIQUE base, table, key).
    /// A merge into an item that is currently leased bumps its generation, so the running executor's
    /// completion keeps it for one more run (the new intent is not lost).
    /// </summary>
    public void UpsertWork(WorkRequest r)
    {
        var existing = Query("SELECT id, flags, priority FROM work_items WHERE base_id=$b AND table_name=$t AND object_key=$k",
                             x => (Id: x.GetInt64(0), Flags: (WorkFlags)x.GetInt32(1), Priority: x.GetInt32(2)),
                             ("$b", r.BaseId), ("$t", r.Table), ("$k", r.ObjectKey));
        if (existing.Count == 0)
        {
            var flags = r.Flags & ~WorkFlags.Recreated;
            if (r.Flags.HasFlag(WorkFlags.Recreated)) flags |= WorkFlags.Changed;
            Run("INSERT INTO work_items(base_id,kind,table_name,object_key,flags,priority) VALUES($b,$kind,$t,$k,$f,$p)",
                ("$b", r.BaseId), ("$kind", WorkKinds.For(flags)), ("$t", r.Table), ("$k", r.ObjectKey), ("$f", (int)flags), ("$p", r.Priority));
            return;
        }
        var e = existing[0];
        var merged = WorkKinds.Merge(e.Flags, r.Flags);
        Run("UPDATE work_items SET flags=$f, kind=$kind, priority=MIN(priority,$p), generation=generation+1, " +
            "next_attempt_at=NULL WHERE id=$id",
            ("$f", (int)merged), ("$kind", WorkKinds.For(merged)), ("$p", r.Priority), ("$id", e.Id));
    }

    /// <summary>
    /// Claims up to <paramref name="max"/> ready items of a base: not leased (or lease expired), not
    /// backing off; lowest priority number first, then feed order. One row per object is the
    /// per-object lock: a claimed row cannot be claimed again until its lease ends.
    /// </summary>
    public List<WorkItem> ClaimWork(string baseId, int max, TimeSpan lease)
    {
        string now = NowIso;
        var items = Query(
            "SELECT id, base_id, kind, table_name, object_key, flags, priority, generation, attempts, last_error FROM work_items " +
            "WHERE base_id=$b AND (lease_until IS NULL OR lease_until < $now) AND (next_attempt_at IS NULL OR next_attempt_at <= $now) " +
            "ORDER BY priority, id LIMIT $n",
            x => new WorkItem(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4),
                              (WorkFlags)x.GetInt32(5), x.GetInt32(6), x.GetInt64(7), x.GetInt32(8) + 1, Str(x, 9)),
            ("$b", baseId), ("$now", now), ("$n", max));
        string until = SyncDb.Iso(Now + lease);
        foreach (var i in items)
            Run("UPDATE work_items SET lease_until=$u, attempts=attempts+1 WHERE id=$id", ("$u", until), ("$id", i.Id));
        return items;
    }

    /// <summary>
    /// The target acknowledged the work: the item goes away — unless it was merged into while it
    /// ran (generation moved), then it is released for another run. Returns true when removed.
    /// </summary>
    public bool CompleteWork(WorkItem item)
    {
        _db.Fault("complete");
        int n = Run("DELETE FROM work_items WHERE id=$id AND generation=$g", ("$id", item.Id), ("$g", item.Generation));
        if (n == 1) return true;
        Run("UPDATE work_items SET lease_until=NULL, attempts=0, last_error=NULL WHERE id=$id", ("$id", item.Id));
        return false;
    }

    /// <summary>A retryable failure: the item stays, backs off until <paramref name="retryAt"/>.</summary>
    public void FailWork(WorkItem item, string error, DateTimeOffset retryAt) =>
        Run("UPDATE work_items SET lease_until=NULL, last_error=$e, next_attempt_at=$at WHERE id=$id",
            ("$e", error), ("$at", SyncDb.Iso(retryAt)), ("$id", item.Id));

    /// <summary>
    /// Moves a failed item to the dead letters (§22). Refused (returns false, item released) when
    /// the item was merged into while it ran: the newer intent — possibly a delete — must run, never
    /// be discarded with the failure.
    /// </summary>
    public bool DeadLetterWork(WorkItem item, string category, string error, int? httpStatus = null,
                               DateTimeOffset? nextRetry = null, string? hint = null)
    {
        int n = Run("DELETE FROM work_items WHERE id=$id AND generation=$g", ("$id", item.Id), ("$g", item.Generation));
        if (n == 0)
        {
            Run("UPDATE work_items SET lease_until=NULL, last_error=$e WHERE id=$id", ("$e", error), ("$id", item.Id));
            return false;
        }
        AddDeadLetter(new DeadLetter(0, item.BaseId, item.Kind, item.Table, item.ObjectKey, category, error, httpStatus,
                                     item.Attempts, Now, Now, nextRetry, hint));
        return true;
    }

    /// <summary>
    /// At engine start: leases belong to the process that took them, and one engine owns this file,
    /// so any lease now is left over from a process that died. Released, not waited out (S15: a
    /// killed engine's items stayed locked for the 5-minute lease). Returns how many.
    /// </summary>
    public int ReleaseAllLeases() => Run("UPDATE work_items SET lease_until=NULL WHERE lease_until IS NOT NULL");

    public long CountWork(string baseId) =>
        (long)Scalar("SELECT COUNT(*) FROM work_items WHERE base_id=$b", ("$b", baseId))!;

    public List<WorkItem> PeekWork(string baseId, int max = 100) => Query(
        "SELECT id, base_id, kind, table_name, object_key, flags, priority, generation, attempts, last_error FROM work_items " +
        "WHERE base_id=$b ORDER BY priority, id LIMIT $n",
        x => new WorkItem(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4),
                          (WorkFlags)x.GetInt32(5), x.GetInt32(6), x.GetInt64(7), x.GetInt32(8), Str(x, 9)),
        ("$b", baseId), ("$n", max));

    /// <summary>
    /// The earliest time an item of the base becomes claimable (null: none pending). An item that is
    /// claimable right away answers <see cref="DateTimeOffset.MinValue"/>, not "now": a caller that
    /// compares with its own earlier "now" must see it as due.
    /// </summary>
    public DateTimeOffset? NextWorkAt(string baseId)
    {
        var s = Scalar("SELECT MIN(MAX(COALESCE(next_attempt_at,''), COALESCE(lease_until,''))) FROM work_items WHERE base_id=$b",
                       ("$b", baseId)) as string;
        return s is null ? null : s.Length == 0 ? DateTimeOffset.MinValue : SyncDb.ParseIso(s);
    }
}
