namespace OneC.SyncState;

public sealed record DeadLetter(long Id, string BaseId, string WorkKind, string Table, string ObjectKey, string Category,
                                string Error, int? HttpStatus, int Attempts, DateTimeOffset FirstFailedAt,
                                DateTimeOffset LastFailedAt, DateTimeOffset? NextRetryAt, string? PayloadHint);

public static class DeadLetterCategories
{
    public const string TransientExhausted = "transient_exhausted";
    public const string Validation = "validation";
    public const string MissingConfig = "missing_config";
    public const string UnmappedOrg = "unmapped_org";
    public const string NeedsApproval = "needs_approval";
    public const string BackendBug = "backend_bug";
}

public sealed record SyncRun(long Id, string BaseId, string Kind, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
                             string? Outcome, long? RowsRead, long? RowsUploaded, long? BytesUploaded, string? Error);

/// <summary>
/// One table of a base as the UI shows it: objects (or register rows) the target holds, the latest
/// snapshot's rows so far, work waiting, dead letters, when an object of it was last sent.
/// </summary>
public sealed record TableStats(string Table, long Rows, long SnapshotRows, bool SnapshotStarted, long Pending, int Failed, DateTimeOffset? LastSentAt);

/// <summary>Per-object state: versions, partitions, independent-register rows, dead letters, runs (§12, §21, §22).</summary>
public sealed partial class SyncTx
{
    // ---------------- object versions ----------------

    /// <summary>Records the version an object was last synced at (with the item's completion, §13).</summary>
    public void SetVersion(string baseId, string table, string key, string? version, long seenRun = 0) => Run(
        "INSERT INTO object_versions(base_id,table_name,object_key,data_version,synced_at,seen_run) VALUES($b,$t,$k,$v,$at,$r) " +
        "ON CONFLICT(base_id,table_name,object_key) DO UPDATE SET data_version=excluded.data_version, synced_at=excluded.synced_at, " +
        "seen_run=MAX(object_versions.seen_run, excluded.seen_run)",
        ("$b", baseId), ("$t", table), ("$k", key), ("$v", version), ("$at", NowIso), ("$r", seenRun));

    public string? GetVersion(string baseId, string table, string key) =>
        Scalar("SELECT data_version FROM object_versions WHERE base_id=$b AND table_name=$t AND object_key=$k",
               ("$b", baseId), ("$t", table), ("$k", key)) as string;

    public bool HasVersion(string baseId, string table, string key) =>
        Scalar("SELECT 1 FROM object_versions WHERE base_id=$b AND table_name=$t AND object_key=$k",
               ("$b", baseId), ("$t", table), ("$k", key)) is not null;

    public void DeleteVersion(string baseId, string table, string key) =>
        Run("DELETE FROM object_versions WHERE base_id=$b AND table_name=$t AND object_key=$k", ("$b", baseId), ("$t", table), ("$k", key));

    /// <summary>
    /// Verify pass, one 1C page: stamps every key 1C still has with <paramref name="run"/> and returns
    /// the keys whose stored version differs (or that are new) — the objects to re-sync.
    /// </summary>
    public List<string> MarkSeen(string baseId, string table, long run, IReadOnlyList<(string Key, string? Version)> page)
    {
        var changed = new List<string>();
        foreach (var (key, version) in page)
        {
            string? stored = GetVersion(baseId, table, key);
            bool known = stored is not null || HasVersion(baseId, table, key);
            if (!known || stored != version) changed.Add(key);
            if (known)
                Run("UPDATE object_versions SET seen_run=$r WHERE base_id=$b AND table_name=$t AND object_key=$k",
                    ("$r", run), ("$b", baseId), ("$t", table), ("$k", key));
        }
        return changed;
    }

    /// <summary>After a complete verify pass: keys the scan never saw — objects gone from 1C.</summary>
    public List<string> UnseenKeys(string baseId, string table, long run, int max) => Query(
        "SELECT object_key FROM object_versions WHERE base_id=$b AND table_name=$t AND seen_run < $r ORDER BY object_key LIMIT $n",
        x => x.GetString(0), ("$b", baseId), ("$t", table), ("$r", run), ("$n", max));

    public long CountVersions(string baseId, string table) =>
        (long)Scalar("SELECT COUNT(*) FROM object_versions WHERE base_id=$b AND table_name=$t", ("$b", baseId), ("$t", table))!;

    // ---------------- object partitions (org routing history, §21) ----------------

    public void AddPartition(string baseId, string key, string partition) =>
        Run("INSERT OR IGNORE INTO object_partitions(base_id,object_key,partition_id) VALUES($b,$k,$p)", ("$b", baseId), ("$k", key), ("$p", partition));

    public List<string> GetPartitions(string baseId, string key) =>
        Query("SELECT partition_id FROM object_partitions WHERE base_id=$b AND object_key=$k ORDER BY partition_id",
              x => x.GetString(0), ("$b", baseId), ("$k", key));

    public void RemovePartition(string baseId, string key, string partition) =>
        Run("DELETE FROM object_partitions WHERE base_id=$b AND object_key=$k AND partition_id=$p", ("$b", baseId), ("$k", key), ("$p", partition));

    // ---------------- independent information registers (§8) ----------------

    /// <summary>Refresh, one page: returns the rows whose hash is new or different, stamping all as seen.</summary>
    public List<string> DiffRegisterRows(string baseId, string table, long run, IReadOnlyList<(string Key, string Hash)> page)
    {
        var changed = new List<string>();
        foreach (var (key, hash) in page)
        {
            var stored = Scalar("SELECT row_hash FROM register_rows WHERE base_id=$b AND table_name=$t AND row_key=$k",
                                ("$b", baseId), ("$t", table), ("$k", key)) as string;
            if (stored != hash) changed.Add(key);
            else Run("UPDATE register_rows SET seen_run=$r WHERE base_id=$b AND table_name=$t AND row_key=$k",
                     ("$r", run), ("$b", baseId), ("$t", table), ("$k", key));
        }
        return changed;
    }

    /// <summary>After the target accepted a changed row.</summary>
    public void SetRegisterRow(string baseId, string table, string key, string hash, long run) => Run(
        "INSERT INTO register_rows(base_id,table_name,row_key,row_hash,seen_run) VALUES($b,$t,$k,$h,$r) " +
        "ON CONFLICT(base_id,table_name,row_key) DO UPDATE SET row_hash=excluded.row_hash, seen_run=excluded.seen_run",
        ("$b", baseId), ("$t", table), ("$k", key), ("$h", hash), ("$r", run));

    /// <summary>
    /// Keys the refresh never saw — rows gone from 1C. Only meaningful once every changed row of the
    /// pass was accepted (<see cref="SetRegisterRow"/> stamps it); a failed upload leaves its row
    /// unstamped and it would read as deleted, so a pass with failures must not ask.
    /// </summary>
    public List<string> UnseenRegisterRows(string baseId, string table, long run, int max) => Query(
        "SELECT row_key FROM register_rows WHERE base_id=$b AND table_name=$t AND seen_run < $r ORDER BY row_key LIMIT $n",
        x => x.GetString(0), ("$b", baseId), ("$t", table), ("$r", run), ("$n", max));

    public void DeleteRegisterRow(string baseId, string table, string key) =>
        Run("DELETE FROM register_rows WHERE base_id=$b AND table_name=$t AND row_key=$k", ("$b", baseId), ("$t", table), ("$k", key));

    // ---------------- dead letters (§22) ----------------

    public long AddDeadLetter(DeadLetter d)
    {
        Run("INSERT INTO dead_letters(base_id,work_kind,table_name,object_key,category,error,http_status,attempts," +
            "first_failed_at,last_failed_at,next_retry_at,payload_hint) VALUES($b,$w,$t,$k,$c,$e,$h,$a,$ff,$lf,$nr,$p)",
            ("$b", d.BaseId), ("$w", d.WorkKind), ("$t", d.Table), ("$k", d.ObjectKey), ("$c", d.Category), ("$e", d.Error),
            ("$h", d.HttpStatus), ("$a", d.Attempts), ("$ff", SyncDb.Iso(d.FirstFailedAt)), ("$lf", SyncDb.Iso(d.LastFailedAt)),
            ("$nr", d.NextRetryAt is { } n ? SyncDb.Iso(n) : null), ("$p", d.PayloadHint));
        return (long)Scalar("SELECT last_insert_rowid()")!;
    }

    /// <summary>How many dead letters a base has, without loading them (status polls this).</summary>
    public int CountDeadLetters(string baseId) =>
        (int)(long)Scalar("SELECT COUNT(*) FROM dead_letters WHERE base_id=$b", ("$b", baseId))!;

    public List<DeadLetter> DeadLetters(string baseId, string? category = null) => Query(
        "SELECT id,base_id,work_kind,table_name,object_key,category,error,http_status,attempts,first_failed_at,last_failed_at," +
        "next_retry_at,payload_hint FROM dead_letters WHERE base_id=$b AND ($c IS NULL OR category=$c) ORDER BY id",
        x => new DeadLetter(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetString(5), x.GetString(6),
                            x.IsDBNull(7) ? null : x.GetInt32(7), x.GetInt32(8), SyncDb.ParseIso(x.GetString(9)), SyncDb.ParseIso(x.GetString(10)),
                            Str(x, 11) is { } n ? SyncDb.ParseIso(n) : null, Str(x, 12)),
        ("$b", baseId), ("$c", category));

    public void DeleteDeadLetter(long id) => Run("DELETE FROM dead_letters WHERE id=$id", ("$id", id));

    public DeadLetter? GetDeadLetter(long id) => Query(
        "SELECT id,base_id,work_kind,table_name,object_key,category,error,http_status,attempts,first_failed_at,last_failed_at," +
        "next_retry_at,payload_hint FROM dead_letters WHERE id=$id",
        x => new DeadLetter(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetString(5), x.GetString(6),
                            x.IsDBNull(7) ? null : x.GetInt32(7), x.GetInt32(8), SyncDb.ParseIso(x.GetString(9)), SyncDb.ParseIso(x.GetString(10)),
                            Str(x, 11) is { } n ? SyncDb.ParseIso(n) : null, Str(x, 12)),
        ("$id", id)).FirstOrDefault();

    // ---------------- unmapped organisations (§21) ----------------

    public void CountUnmappedOrg(string baseId, string orgRef, string table, long rows) => Run(
        "INSERT INTO unmapped_orgs(base_id,org_ref,table_name,rows_seen,first_seen_at,last_seen_at) VALUES($b,$o,$t,$n,$at,$at) " +
        "ON CONFLICT(base_id,org_ref,table_name) DO UPDATE SET rows_seen=unmapped_orgs.rows_seen+excluded.rows_seen, last_seen_at=excluded.last_seen_at",
        ("$b", baseId), ("$o", orgRef), ("$t", table), ("$n", rows), ("$at", NowIso));

    public List<(string OrgRef, string Table, long Rows)> UnmappedOrgs(string baseId) => Query(
        "SELECT org_ref,table_name,rows_seen FROM unmapped_orgs WHERE base_id=$b ORDER BY org_ref,table_name",
        x => (x.GetString(0), x.GetString(1), x.GetInt64(2)), ("$b", baseId));

    public void ClearUnmappedOrg(string baseId, string orgRef) =>
        Run("DELETE FROM unmapped_orgs WHERE base_id=$b AND org_ref=$o", ("$b", baseId), ("$o", orgRef));

    // ---------------- runs (history, pruned) ----------------

    public long StartRun(string baseId, string kind)
    {
        Run("INSERT INTO sync_runs(base_id,kind,started_at) VALUES($b,$k,$at)", ("$b", baseId), ("$k", kind), ("$at", NowIso));
        return (long)Scalar("SELECT last_insert_rowid()")!;
    }

    public void FinishRun(long id, string outcome, long rowsRead, long rowsUploaded, long bytesUploaded, string? error = null) => Run(
        "UPDATE sync_runs SET finished_at=$at, outcome=$o, rows_read=$rr, rows_uploaded=$ru, bytes_uploaded=$bu, error=$e WHERE id=$id",
        ("$at", NowIso), ("$o", outcome), ("$rr", rowsRead), ("$ru", rowsUploaded), ("$bu", bytesUploaded), ("$e", error), ("$id", id));

    public List<SyncRun> Runs(string baseId, int max = 50) => Query(
        "SELECT id,base_id,kind,started_at,finished_at,outcome,rows_read,rows_uploaded,bytes_uploaded,error FROM sync_runs " +
        "WHERE base_id=$b ORDER BY id DESC LIMIT $n",
        x => new SyncRun(x.GetInt64(0), x.GetString(1), x.GetString(2), SyncDb.ParseIso(x.GetString(3)),
                         Str(x, 4) is { } f ? SyncDb.ParseIso(f) : null, Str(x, 5), Long(x, 6), Long(x, 7), Long(x, 8), Str(x, 9)),
        ("$b", baseId), ("$n", max));

    /// <summary>Per-table counts of a base, one grouped read per state table (the UI polls this).</summary>
    public Dictionary<string, TableStats> TableStats(string baseId)
    {
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (t, n, at) in Query("SELECT table_name, COUNT(*), MAX(synced_at) FROM object_versions WHERE base_id=$b GROUP BY table_name",
                                         x => (x.GetString(0), x.GetInt64(1), Str(x, 2)), ("$b", baseId)))
        {
            rows[t] = n;
            if (at is not null) last[t] = SyncDb.ParseIso(at);
        }
        foreach (var (t, n) in Query("SELECT table_name, COUNT(*) FROM register_rows WHERE base_id=$b GROUP BY table_name",
                                     x => (x.GetString(0), x.GetInt64(1)), ("$b", baseId)))
            rows[t] = rows.GetValueOrDefault(t) + n;
        var snap = Query("SELECT s.table_name, SUM(s.rows_done) FROM snapshot_slices s WHERE s.base_id=$b AND s.run_id = " +
                         "(SELECT MAX(run_id) FROM snapshot_slices WHERE base_id=s.base_id AND table_name=s.table_name) GROUP BY s.table_name",
                         x => (x.GetString(0), x.GetInt64(1)), ("$b", baseId)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var pending = Query("SELECT table_name, COUNT(*) FROM work_items WHERE base_id=$b GROUP BY table_name",
                            x => (x.GetString(0), x.GetInt64(1)), ("$b", baseId)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var failed = Query("SELECT table_name, COUNT(*) FROM dead_letters WHERE base_id=$b GROUP BY table_name",
                           x => (x.GetString(0), x.GetInt32(1)), ("$b", baseId)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        return rows.Keys.Concat(snap.Keys).Concat(pending.Keys).Concat(failed.Keys).Distinct(StringComparer.Ordinal).ToDictionary(
            t => t,
            t => new TableStats(t, rows.GetValueOrDefault(t), snap.GetValueOrDefault(t), snap.ContainsKey(t), pending.GetValueOrDefault(t),
                                failed.GetValueOrDefault(t), last.TryGetValue(t, out var a) ? a : null),
            StringComparer.Ordinal);
    }

    /// <summary>Drops finished runs older than <paramref name="age"/> (§ final review: nothing unbounded).</summary>
    public int PruneRuns(TimeSpan age) =>
        // Unfinished ones too: a run a failure interrupted is never finished, and was kept forever.
        Run("DELETE FROM sync_runs WHERE started_at < $cut", ("$cut", SyncDb.Iso(Now - age)));
}
