using OneC.EventLog;
using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Errors;
using OneC.Sync.Incremental;
using OneC.Sync.Mapping;
using OneC.Sync.Routing;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using OneC.Sync.Verify;

namespace OneC.Sync.Engine;

/// <summary>One base's configuration, as the engine runs it.</summary>
/// <param name="LogDir">Its event-log directory; null = no readable feed (Fallback).</param>
public sealed record BasePlan(string BaseId, bool IsFile, string? LogDir, string ConnectionId, IReadOnlyList<TablePlan> Tables);

public sealed record EngineOptions
{
    /// <summary>D-1: a developer override for controlled tests only; never a user setting.</summary>
    public bool AllowWithOldConnector { get; init; }
    public TimeSpan PresenceCheckEvery { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FallbackEvery { get; init; } = TimeSpan.FromMinutes(15);
    public SnapshotOptions Snapshot { get; init; } = new();
}

/// <summary>
/// A base's work while the scheduler keeps it active (§3 mode machine): Snapshot (handshake first,
/// then every table, the feed drained between tables so a long cold read never outlives the log),
/// Incremental (feed → queue → executor), Recovery (verify, then resume the feed), Fallback (one
/// verify turn), Paused (nothing). Every step persists its progress, so being stopped at any page
/// boundary and activated again later continues where it was.
/// </summary>
public sealed class BaseSyncAgent : IBaseAgent
{
    private readonly SyncDb _db;
    private readonly BasePlan _plan;
    private readonly IBackendSyncTarget _target;
    private readonly IOneCReader _reader;
    private readonly UploadGate _gate;
    private readonly EngineOptions _options;
    private readonly EventLogReader _log = new();
    private DateTimeOffset _presenceCheckedAt = DateTimeOffset.MinValue;

    public BaseSyncAgent(SyncDb db, BasePlan plan, IBackendSyncTarget target, IOneCReader reader, UploadGate gate, EngineOptions options)
    {
        _db = db; _plan = plan; _target = target; _reader = reader; _gate = gate; _options = options;
    }

    public string? LastError { get; private set; }

    public async Task RunAsync(BaseActivation a)
    {
        try
        {
            EnsureBase();
            if (!await PresenceAllowsAsync(a.Stop)) return;
            var caps = await _target.GetCapabilitiesAsync(a.Stop);
            var config = await _target.GetSyncConfigAsync(_plan.ConnectionId, a.Stop);
            var router = new OrgRouter(config);
            if (router.MultiOrg) OrgRouter.Validate(config, _plan.Tables);
            var parts = BuildParts(caps, router);
            await CatchUpNewlyBoundAsync(router, a);
            await FlushCoverageAsync(router, a.Stop);
            await SeedShownAsync(a.Stop);

            string mode = Recovery.StartMode(_db, _plan.BaseId, _plan.LogDir is not null);
            switch (mode)
            {
                case SyncModes.Paused:
                    return;
                case SyncModes.Snapshot:
                    await SnapshotAsync(a, parts, router);
                    break;
                case SyncModes.Recovery when _plan.LogDir is not null:
                {
                    // Out of Fallback now that the log is readable (StartMode): Recovery commits the cursor.
                    _db.Write(tx =>
                    {
                        if (tx.GetBase(_plan.BaseId) is { Mode: SyncModes.Fallback, PausedByUser: false })
                            tx.SetMode(_plan.BaseId, SyncModes.Recovery, "event log readable again: verify pass, then the feed");
                    });
                    // A first copy the reset interrupted goes on with the tables it had not reached:
                    // the verify pass follows catalogs and documents, not registers (found by the S15 test).
                    bool unfinished = _plan.Tables.Any(t => _db.Read(tx => tx.GetMeta(SnapshotDoneKey(_plan.BaseId, t.Table))) is null);
                    await parts.Recovery.RecoverAsync(_plan.BaseId, _plan.Tables, _plan.LogDir, _log, a.Stop,
                                                      unfinished ? SyncModes.Snapshot : SyncModes.Incremental);
                    break;
                }
                case SyncModes.Recovery or SyncModes.Fallback:
                    SetMode(SyncModes.Fallback, "no readable event log");
                    if (_db.Read(tx => tx.GetMeta(FallbackDueKey(_plan.BaseId))) is not { } due || SyncDb.ParseIso(due) <= _db.Now)
                    {
                        await parts.Recovery.FallbackTurnAsync(_plan.BaseId, _plan.Tables, a.Stop);
                        _db.Write(tx => tx.SetMeta(FallbackDueKey(_plan.BaseId), SyncDb.Iso(tx.Now + _options.FallbackEvery)));
                    }
                    break;
            }
            if (Mode() is SyncModes.Incremental && _plan.LogDir is not null)
                parts.Feed.Drain(_plan.BaseId, _plan.LogDir, parts.Map, a.Stop);
            if (Mode() is SyncModes.Incremental or SyncModes.Fallback)
            {
                new DeadLetterService(_db).RetryDue(_plan.BaseId);
                await parts.Executor.RunAsync(a);
            }
            LastError = null;
            _db.Write(tx => { tx.DeleteMeta(BackoffKey(_plan.BaseId)); tx.DeleteMeta(FailuresKey(_plan.BaseId)); });
        }
        catch (OperationCanceledException) when (a.Stop.IsCancellationRequested) { }
        catch (SyncPausedException p) when (p.Outcome is Outcome.Auth or Outcome.Gone)
        {
            SetMode(SyncModes.Paused, p.Outcome == Outcome.Auth ? "auth: sign in again" : "the cloud connection is being deleted");
            LastError = p.Message;
        }
        catch (RoutingConfigException e)
        {
            SetMode(SyncModes.Paused, "config: " + e.Message);
            LastError = e.Message;
        }
        catch (Exception e)
        {
            // Anything else is transient at base level: the scheduler activates the base again — after
            // a backoff (5 s doubling to 5 min), not on the next 1 s tick: a backend that is down was
            // asked for its config twice a second per base (2026-10-01 review).
            LastError = $"{e.GetType().Name}: {e.Message}";
            _db.Write(tx =>
            {
                int n = int.TryParse(tx.GetMeta(FailuresKey(_plan.BaseId)), out int f) ? f + 1 : 1;
                tx.SetMeta(FailuresKey(_plan.BaseId), n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(n - 1, 6))));
                tx.SetMeta(BackoffKey(_plan.BaseId), SyncDb.Iso(tx.Now + wait));
            });
        }
    }

    /// <summary>While organisations wait for a binding, the base is activated this often to re-read its bindings.</summary>
    public static readonly TimeSpan UnmappedCheckEvery = TimeSpan.FromMinutes(5);
    public static string UnmappedCheckKey(string baseId) => "unmapped_check_at:" + baseId;

    /// <summary>
    /// §21: rows of an organisation that had no binding were counted, not sent, and kept no version.
    /// Once it is bound, they go out: a verify pass over catalogs and documents (those rows look new to
    /// it) and a fresh copy of every recorded register (its lines are found by period, not by object;
    /// uploads are idempotent) — then the organisation's counter is cleared. Interrupted, it repeats
    /// next activation. (2026-10-01 review: NewlyBound had no caller; the rows were never sent.)
    /// </summary>
    private async Task CatchUpNewlyBoundAsync(OrgRouter router, BaseActivation a)
    {
        var waiting = _db.Read(tx => tx.UnmappedOrgs(_plan.BaseId));
        _db.Write(tx => tx.SetMeta(UnmappedCheckKey(_plan.BaseId), SyncDb.Iso(tx.Now + UnmappedCheckEvery)));
        if (waiting.Count == 0 || !router.MultiOrg) return;
        var newly = OrgRouter.NewlyBound(waiting.Select(w => w.OrgRef), router);
        if (newly.Count == 0) return;
        bool recorded = _plan.Tables.Any(t => Families.IsRecorded(t.Family));
        var verify = new VerifyRunner(_db, _reader);
        foreach (var t in _plan.Tables.Where(t => t.Family is Families.Catalog or Families.Document))
            await verify.VerifyTableAsync(_plan.BaseId, t, recorded && t.IsMovement, a.Stop);
        _db.Write(tx =>
        {
            foreach (var t in _plan.Tables.Where(t => Families.IsRecorded(t.Family) || t.Family == Families.IndependentInfoRegister))
            {
                tx.DeleteMeta(SnapshotDoneKey(_plan.BaseId, t.Table));
                tx.DeleteSlices(_plan.BaseId, t.Table);
            }
            if (_plan.Tables.Any(t => Families.IsRecorded(t.Family) || t.Family == Families.IndependentInfoRegister) &&
                tx.GetBase(_plan.BaseId) is { PausedByUser: false } b && b.Mode is SyncModes.Incremental or SyncModes.Fallback)
                tx.SetMode(_plan.BaseId, SyncModes.Snapshot, "organisation(s) bound: their register rows are copied now");
            foreach (var org in newly) tx.ClearUnmappedOrg(_plan.BaseId, org);
        });
    }

    /// <summary>A base whose last activation failed is not due before this time (<see cref="DbDemand"/>).</summary>
    public static string BackoffKey(string baseId) => "base_backoff_until:" + baseId;
    private static string FailuresKey(string baseId) => "base_failures:" + baseId;

    public static string FallbackDueKey(string baseId) => "fallback_due:" + baseId;
    public static string SnapshotDoneKey(string baseId, string table) => $"snapshot_done:{baseId}:{table}";
    public static string MissingTableKey(string baseId, string table) => $"table_missing:{baseId}:{table}";

    public static string CoveragePendingKey(string baseId, string table) => $"coverage_pending:{baseId}:{table}";

    /// <summary>
    /// Data coverage (RUST_SYNC_CONTRACT §4.9) is load-bearing: avtoprovodka reads a table with NO entry as
    /// "the whole history is here" and treats a miss as absence (sotuv.rs mirror_reach_from_onec) — a
    /// windowed or half-copied table without an entry invites a duplicate document. So for documents and
    /// registers: "loading" (unknown reach) must be stored before the first row goes out, and the final
    /// reach after the snapshot completes. Catalogs and charts are never listed (they load whole).
    /// </summary>
    private static bool Covered(TablePlan t) => t.Family == Families.Document || Families.IsRegister(t.Family);

    private IEnumerable<string> CoveragePartitions(TablePlan t, IPartitioner router) => t.IsMovement ? router.All : new[] { router.Shared };

    private static TableCoverage FinalCoverage(TablePlan t) =>
        t.From is { } from ? new TableCoverage(t.Table, from, false) : new TableCoverage(t.Table, null, true);

    private async Task ReportCoverageAsync(TablePlan t, TableCoverage c, IPartitioner router, CancellationToken ct)
    {
        if (_target is not ICoverageTarget cov) return;
        foreach (var p in CoveragePartitions(t, router))
        {
            var r = await cov.ReportCoverageAsync(p, new[] { c }, ct);
            if (!r.Ok) throw new SyncPausedException(r.Outcome == Outcome.Ok ? Outcome.Transient : r.Outcome, $"coverage {p}/{t.Table}: {r.Outcome} {r.Message}", r.HttpStatus);
        }
    }

    public static string CoverageSentKey(string baseId, string table) => $"coverage_sent:{baseId}:{table}";

    private static string Describe(TableCoverage c) => $"{c.Table}|{c.DataFrom:yyyy-MM-dd}|{c.Complete}";

    /// <summary>
    /// Final reaches not yet stored in the target, sent before anything else in an activation: one a failed
    /// report left behind, and — for a copied table — one never sent at all or sent for another window. A
    /// base copied before coverage existed (R10, 2026-10-08: bilim's register, windowed from 2025-08-01, had
    /// no entry and so read as "whole history") gets its entry this way, without copying again.
    /// </summary>
    private async Task FlushCoverageAsync(IPartitioner router, CancellationToken ct)
    {
        foreach (var t in _plan.Tables.Where(Covered))
        {
            var (pending, done, sent) = _db.Read(tx => (tx.GetMeta(CoveragePendingKey(_plan.BaseId, t.Table)),
                                                        tx.GetMeta(SnapshotDoneKey(_plan.BaseId, t.Table)),
                                                        tx.GetMeta(CoverageSentKey(_plan.BaseId, t.Table))));
            var final = FinalCoverage(t);
            if (pending is null && (done is null || sent == Describe(final))) continue;
            await ReportCoverageAsync(t, final, router, ct);
            _db.Write(tx =>
            {
                tx.DeleteMeta(CoveragePendingKey(_plan.BaseId, t.Table));
                tx.SetMeta(CoverageSentKey(_plan.BaseId, t.Table), Describe(final));
            });
        }
    }

    /// <summary>
    /// A catalog copied before renames were followed has no stored names, and an item with none never
    /// triggers a referrer search (it reads as new). So once per such table: read the catalog and store
    /// each item's shown name — only where none is stored, so a rename already waiting in the queue is
    /// still seen as one. Uploads nothing.
    /// </summary>
    private async Task SeedShownAsync(CancellationToken ct)
    {
        foreach (var t in _plan.Tables.Where(t => t.Family == Families.Catalog))
        {
            if (_db.Read(tx => tx.GetMeta(SnapshotDoneKey(_plan.BaseId, t.Table)) is null ||
                               tx.GetMeta(CanonicalMapper.ShownSeededKey(_plan.BaseId, t.Table)) is not null)) continue;
            string? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _reader.CatalogPageAsync(_plan.BaseId, t.Name, after, 1000, ct);
                var shown = page.Rows.Select(r => (Key: CanonicalMapper.Key(t, r), Shown: CanonicalMapper.ShownOf(r)))
                                     .Where(x => x.Shown is not null).ToList();
                _db.Write(tx =>
                {
                    foreach (var (key, s) in shown)
                        if (tx.GetMeta(CanonicalMapper.ShownKey(_plan.BaseId, t.Table, key)) is null)
                            tx.SetMeta(CanonicalMapper.ShownKey(_plan.BaseId, t.Table, key), s!);
                });
                if (!page.HasMore || page.NextAfter is null) break;
                after = page.NextAfter;
            }
            _db.Write(tx => tx.SetMeta(CanonicalMapper.ShownSeededKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now)));
        }
    }

    private async Task SnapshotAsync(BaseActivation a, Parts parts, IPartitioner router)
    {
        if (_plan.LogDir is not null) parts.Feed.Handshake(_plan.BaseId, _plan.LogDir);
        // Reference data first (documents refer to it), then documents, then registers.
        foreach (var t in _plan.Tables.OrderBy(t => t.Family switch { Families.Catalog or Families.Chart => 0, Families.Document => 1, _ => 2 }))
        {
            if (Mode() != SyncModes.Snapshot) return;                              // paused (or recovering) meanwhile: stop after this table
            if (_db.Read(tx => tx.GetMeta(SnapshotDoneKey(_plan.BaseId, t.Table))) is not null) continue;
            // Stored before the first row: a half-copied table must never read as "whole history".
            if (Covered(t)) await ReportCoverageAsync(t, TableCoverage.Loading(t.Table), router, a.Stop);
            SnapshotResult r;
            try { r = await parts.Snapshot.RunTableAsync(a, t, 1); }
            catch (OneCMetadataMissingException e)
            {
                // §22 missing config: this table is off, with a visible reason; the others go on.
                _db.Write(tx =>
                {
                    tx.SetMeta(MissingTableKey(_plan.BaseId, t.Table), e.Message);
                    tx.SetMeta(SnapshotDoneKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now));
                    // Its "loading" entry is replaced on the next activation (a table 1C does not have holds nothing to miss).
                    if (Covered(t)) tx.SetMeta(CoveragePendingKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now));
                });
                continue;
            }
            if (r.Outcome != SnapshotOutcome.Complete) return;                     // stopped or no session: resume next activation
            _db.Write(tx =>
            {
                tx.SetMeta(SnapshotDoneKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now));
                if (t.Family == Families.Catalog) tx.SetMeta(CanonicalMapper.ShownSeededKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now));
                if (Covered(t)) tx.SetMeta(CoveragePendingKey(_plan.BaseId, t.Table), SyncDb.Iso(tx.Now));
            });
            if (Covered(t))
            {
                await ReportCoverageAsync(t, FinalCoverage(t), router, a.Stop);   // a failure leaves it pending: next activation sends it
                _db.Write(tx =>
                {
                    tx.DeleteMeta(CoveragePendingKey(_plan.BaseId, t.Table));
                    tx.SetMeta(CoverageSentKey(_plan.BaseId, t.Table), Describe(FinalCoverage(t)));
                });
            }
            // §11: drain the feed between tables so the cursor keeps up with the log during a long first sync.
            if (_plan.LogDir is not null) parts.Feed.Drain(_plan.BaseId, _plan.LogDir, parts.Map, a.Stop);
            // A reset or restore seen by that drain: Recovery takes over (its verify pass also covers
            // the tables not copied yet); never overwrite it with "snapshot complete".
            if (Mode() == SyncModes.Recovery) return;
        }
        // Only from Snapshot, and only when every table really is copied: a Pause or a table rebuild
        // made while this activation ran must not be overwritten (2026-10-01 review).
        // With no readable event log there is no feed to follow: the base goes on in Fallback (timed
        // verify passes). As "Incremental" it waited for log events that never come and was never
        // activated again (found by FallbackEndsOnceTheLogIsReadable, 2026-10-01).
        _db.Write(tx =>
        {
            if (tx.GetBase(_plan.BaseId) is { Mode: SyncModes.Snapshot } &&
                _plan.Tables.All(x => tx.GetMeta(SnapshotDoneKey(_plan.BaseId, x.Table)) is not null))
                tx.SetMode(_plan.BaseId, _plan.LogDir is null ? SyncModes.Fallback : SyncModes.Incremental,
                           _plan.LogDir is null ? "snapshot complete; no readable event log" : "snapshot complete");
        });
    }

    private async Task<bool> PresenceAllowsAsync(CancellationToken ct)
    {
        if (_options.AllowWithOldConnector || _db.Now - _presenceCheckedAt < _options.PresenceCheckEvery) return true;
        var p = await _target.GetPresenceAsync(_plan.ConnectionId, ct);
        if (p.OtherConnectorOnline is null)
        {
            // Unknown (the backend did not answer, or answered without the field): not proof that no
            // old Connector serves the base. Skip this activation and ask again next time — never
            // cached as "allowed" (D-1; 2026-10-01 review).
            LastError = "old Connector presence unknown: " + p.Detail;
            return false;
        }
        _presenceCheckedAt = _db.Now;
        if (p.OtherConnectorOnline != true) return true;
        SetMode(SyncModes.Paused, "old Connector serves this base (D-1): " + p.Detail);
        return false;
    }

    private sealed record Parts(FeedService Feed, TableMap Map, SnapshotRunner Snapshot, WorkExecutor Executor, Recovery Recovery);

    private Parts BuildParts(TargetCapabilities caps, IPartitioner router)
    {
        var tables = _plan.Tables.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var up = new Uploader(_target, _gate);
        var deletes = new DeleteObjectHandler(_db, _target, tables, router);
        var recorder = new SyncRecorderHandler(_db, _reader, _target, up, caps, tables, router, deletes);
        deletes.ClearMovements = recorder.ClearAsync;
        var executor = new WorkExecutor(_db, new Dictionary<string, IWorkHandler>
        {
            [WorkKinds.SyncObject] = new SyncObjectHandler(_db, _reader, up, tables, router, deletes),
            [WorkKinds.DeleteObject] = deletes,
            [WorkKinds.SyncRecorder] = recorder,
            [WorkKinds.RefreshRegister] = new RefreshRegisterHandler(_db, _reader, _target, up, tables, router),
            [WorkKinds.ApprovedDelete] = new ApprovedDeleteHandler(_db, _target, tables, router, recorder.ClearAsync)
        });
        return new Parts(new FeedService(_db, _log, new SyncBudgets()), new TableMap(_plan.Tables),
                         new SnapshotRunner(_db, _reader, up, _gate, caps, router, _options.Snapshot), executor,
                         new Recovery(_db, new VerifyRunner(_db, _reader)));
    }

    private void EnsureBase()
    {
        if (_db.Read(tx => tx.GetBase(_plan.BaseId)) is not null) return;
        _db.Write(tx => tx.UpsertBase(new SyncBaseRow(_plan.BaseId, "target", _plan.ConnectionId, null, SyncModes.Snapshot, "new base", false,
                                                      string.Join(",", _plan.Tables.Select(t => t.Table)), tx.Now)));
    }

    private string Mode() => _db.Read(tx => tx.GetBase(_plan.BaseId))?.Mode ?? SyncModes.Unconfigured;

    /// <summary>The agent's own mode changes never undo the user's Pause (only Resume does).</summary>
    private void SetMode(string mode, string reason) => _db.Write(tx =>
    {
        if (tx.GetBase(_plan.BaseId) is { } b && !(b.PausedByUser && mode != SyncModes.Paused) && (b.Mode != mode || b.ModeReason != reason))
            tx.SetMode(_plan.BaseId, mode, reason);
    });
}

/// <summary>The scheduler's view of a base's durable state (§14): what is due, and how urgent.</summary>
public sealed class DbDemand(SyncDb db) : IBaseDemand
{
    public BaseDemand? Get(string baseId, DateTimeOffset now)
    {
        var b = db.Read(tx => tx.GetBase(baseId));
        if (b?.Mode == SyncModes.Paused) return null;
        // After a failed activation: nothing is due before the backoff ends.
        var backoff = db.Read(tx => tx.GetMeta(BaseSyncAgent.BackoffKey(baseId))) is { } s0 ? SyncDb.ParseIso(s0) : (DateTimeOffset?)null;
        if (backoff is { } until && until > now)
            return new BaseDemand(b?.Mode == SyncModes.Recovery ? SyncPriority.Recovery : SyncPriority.Snapshot, until);
        switch (b?.Mode)
        {
            case null or SyncModes.Snapshot: return new BaseDemand(SyncPriority.Snapshot, now);
            case SyncModes.Recovery: return new BaseDemand(SyncPriority.Recovery, now);
        }
        var next = db.Read(tx => tx.NextWorkAt(baseId));
        if (b!.Mode == SyncModes.Fallback)
        {
            var due = db.Read(tx => tx.GetMeta(BaseSyncAgent.FallbackDueKey(baseId))) is { } s ? SyncDb.ParseIso(s) : now;
            var at = next is { } n && n < due ? n : due;
            return new BaseDemand(SyncPriority.Fallback, at);
        }
        // Organisations waiting for a binding: activate now and then to re-read the bindings (§21).
        if (db.Read(tx => tx.UnmappedOrgs(baseId).Count > 0))
        {
            var check = db.Read(tx => tx.GetMeta(BaseSyncAgent.UnmappedCheckKey(baseId))) is { } c ? SyncDb.ParseIso(c) : now;
            if (next is null || check < next) next = check;
        }
        if (next is null) return null;
        int top = db.Read(tx => tx.PeekWork(baseId, 1)).FirstOrDefault()?.Priority ?? ItemPriority.Document;
        return new BaseDemand(top == ItemPriority.Destructive ? SyncPriority.Destructive : SyncPriority.Incremental, next.Value);
    }
}
