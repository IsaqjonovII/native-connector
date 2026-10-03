namespace OneC.SyncState;

public static class SyncModes
{
    public const string Unconfigured = "unconfigured";
    public const string Snapshot = "snapshot";
    public const string Incremental = "incremental";
    public const string Recovery = "recovery";
    public const string Fallback = "fallback";
    public const string Paused = "paused";
}

public sealed record SyncBaseRow(string BaseId, string TargetKind, string ConnectionId, string? CompanyId,
                                 string Mode, string? ModeReason, bool PausedByUser, string ConfigHash, DateTimeOffset UpdatedAt);

public sealed record SyncTableRow(string BaseId, string Table, string Metadata, string Family, string KeyStrategy, bool Enabled);

public sealed record FeedCursorRow(string BaseId, string? Cursor, DateTimeOffset? CursorAt, DateTimeOffset? LastEventAt,
                                   string? LogPath, string? LastResetReason);

public sealed record SnapshotSlice(string BaseId, string Table, long RunId, int SliceNo, string? From, string? To,
                                   string? Cursor, long? CursorSkip, long RowsDone, bool Done);

/// <summary>Bases, their tables, feed cursors and snapshot checkpoints (§11, §13).</summary>
public sealed partial class SyncTx
{
    public void UpsertBase(SyncBaseRow b) => Run(
        "INSERT INTO sync_bases(base_id,target_kind,connection_id,company_id,mode,mode_reason,paused_by_user,config_hash,updated_at) " +
        "VALUES($b,$tk,$c,$co,$m,$r,$p,$h,$u) ON CONFLICT(base_id) DO UPDATE SET target_kind=excluded.target_kind, " +
        "connection_id=excluded.connection_id, company_id=excluded.company_id, mode=excluded.mode, mode_reason=excluded.mode_reason, " +
        "paused_by_user=excluded.paused_by_user, config_hash=excluded.config_hash, updated_at=excluded.updated_at",
        ("$b", b.BaseId), ("$tk", b.TargetKind), ("$c", b.ConnectionId), ("$co", b.CompanyId), ("$m", b.Mode),
        ("$r", b.ModeReason), ("$p", b.PausedByUser ? 1 : 0), ("$h", b.ConfigHash), ("$u", NowIso));

    public SyncBaseRow? GetBase(string baseId) => Bases("WHERE base_id=$b", ("$b", baseId)).FirstOrDefault();

    public List<SyncBaseRow> AllBases() => Bases("");

    private List<SyncBaseRow> Bases(string where, params (string, object?)[] ps) => Query(
        "SELECT base_id,target_kind,connection_id,company_id,mode,mode_reason,paused_by_user,config_hash,updated_at FROM sync_bases " + where,
        x => new SyncBaseRow(x.GetString(0), x.GetString(1), x.GetString(2), Str(x, 3), x.GetString(4), Str(x, 5),
                             x.GetInt32(6) != 0, x.GetString(7), SyncDb.ParseIso(x.GetString(8))), ps);

    /// <summary>A mode transition (§3 mode machine). Call it in the same transaction as the cursor when both move.</summary>
    public void SetMode(string baseId, string mode, string? reason)
    {
        if (Run("UPDATE sync_bases SET mode=$m, mode_reason=$r, updated_at=$u WHERE base_id=$b",
                ("$m", mode), ("$r", reason), ("$u", NowIso), ("$b", baseId)) != 1)
            throw new InvalidOperationException($"no sync base '{baseId}'");
    }

    public void ReplaceTables(string baseId, IEnumerable<SyncTableRow> tables)
    {
        Run("DELETE FROM sync_tables WHERE base_id=$b", ("$b", baseId));
        foreach (var t in tables)
            Run("INSERT INTO sync_tables(base_id,table_name,metadata,family,key_strategy,enabled) VALUES($b,$t,$m,$f,$k,$e)",
                ("$b", baseId), ("$t", t.Table), ("$m", t.Metadata), ("$f", t.Family), ("$k", t.KeyStrategy), ("$e", t.Enabled ? 1 : 0));
    }

    public List<SyncTableRow> GetTables(string baseId) => Query(
        "SELECT base_id,table_name,metadata,family,key_strategy,enabled FROM sync_tables WHERE base_id=$b ORDER BY table_name",
        x => new SyncTableRow(x.GetString(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4), x.GetInt32(5) != 0),
        ("$b", baseId));

    // ---------------- feed cursor ----------------

    public FeedCursorRow? GetCursor(string baseId) => Query(
        "SELECT base_id,cursor,cursor_at,last_event_at,log_path,last_reset_reason FROM feed_cursors WHERE base_id=$b",
        x => new FeedCursorRow(x.GetString(0), Str(x, 1), Str(x, 2) is { } a ? SyncDb.ParseIso(a) : null,
                               Str(x, 3) is { } l ? SyncDb.ParseIso(l) : null, Str(x, 4), Str(x, 5)),
        ("$b", baseId)).FirstOrDefault();

    /// <summary>
    /// Moves the base's feed cursor. §13: call it in the same transaction as the work items the
    /// events after the old cursor produced — then a crash leaves either both or neither.
    /// </summary>
    public void SetCursor(string baseId, string? cursor, string? logPath, DateTimeOffset? lastEventAt = null, string? resetReason = null)
    {
        _db.Fault("cursor");
        Run("INSERT INTO feed_cursors(base_id,cursor,cursor_at,last_event_at,log_path,last_reset_reason) VALUES($b,$c,$at,$le,$lp,$rr) " +
            "ON CONFLICT(base_id) DO UPDATE SET cursor=excluded.cursor, cursor_at=excluded.cursor_at, " +
            "last_event_at=COALESCE(excluded.last_event_at, feed_cursors.last_event_at), log_path=excluded.log_path, " +
            "last_reset_reason=COALESCE(excluded.last_reset_reason, feed_cursors.last_reset_reason)",
            ("$b", baseId), ("$c", cursor), ("$at", NowIso), ("$le", lastEventAt is { } e ? SyncDb.Iso(e) : null),
            ("$lp", logPath), ("$rr", resetReason));
    }

    // ---------------- snapshot checkpoints ----------------

    public void UpsertSlice(SnapshotSlice s) => Run(
        "INSERT INTO snapshot_slices(base_id,table_name,run_id,slice_no,from_value,to_value,cursor,cursor_skip,rows_done,done) " +
        "VALUES($b,$t,$r,$n,$f,$to,$c,$s,$rows,$d) ON CONFLICT(base_id,table_name,run_id,slice_no) DO UPDATE SET " +
        "from_value=excluded.from_value, to_value=excluded.to_value, cursor=excluded.cursor, cursor_skip=excluded.cursor_skip, " +
        "rows_done=excluded.rows_done, done=excluded.done",
        ("$b", s.BaseId), ("$t", s.Table), ("$r", s.RunId), ("$n", s.SliceNo), ("$f", s.From), ("$to", s.To),
        ("$c", s.Cursor), ("$s", s.CursorSkip), ("$rows", s.RowsDone), ("$d", s.Done ? 1 : 0));

    /// <summary>One accepted page: the slice's position and row count move together (§13, one transaction per page).</summary>
    public void CheckpointSlice(string baseId, string table, long runId, int sliceNo, string? cursor, long? skip, long rowsAccepted, bool done)
    {
        _db.Fault("checkpoint");
        if (Run("UPDATE snapshot_slices SET cursor=$c, cursor_skip=$s, rows_done=rows_done+$n, done=$d " +
                "WHERE base_id=$b AND table_name=$t AND run_id=$r AND slice_no=$no",
                ("$c", cursor), ("$s", skip), ("$n", rowsAccepted), ("$d", done ? 1 : 0),
                ("$b", baseId), ("$t", table), ("$r", runId), ("$no", sliceNo)) != 1)
            throw new InvalidOperationException($"no slice {baseId}/{table}/{runId}/{sliceNo}");
    }

    public List<SnapshotSlice> GetSlices(string baseId, string table, long runId) => Query(
        "SELECT base_id,table_name,run_id,slice_no,from_value,to_value,cursor,cursor_skip,rows_done,done FROM snapshot_slices " +
        "WHERE base_id=$b AND table_name=$t AND run_id=$r ORDER BY slice_no",
        x => new SnapshotSlice(x.GetString(0), x.GetString(1), x.GetInt64(2), x.GetInt32(3), Str(x, 4), Str(x, 5), Str(x, 6),
                               Long(x, 7), x.GetInt64(8), x.GetInt32(9) != 0),
        ("$b", baseId), ("$t", table), ("$r", runId));

    public void DeleteSlices(string baseId, string table) =>
        Run("DELETE FROM snapshot_slices WHERE base_id=$b AND table_name=$t", ("$b", baseId), ("$t", table));
}
