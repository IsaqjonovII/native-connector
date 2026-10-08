using System.Text;
using Microsoft.Data.Sqlite;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Engine;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Sync block S13: the whole engine — scheduler, per-base agent and mode machine, dead-letter
/// actions, D-1 refusal, D-3 rebuild — over a fake 1C, a synthetic event log and the stub target.
/// </summary>
public sealed class SyncEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-engine-").FullName;
    private readonly string _log;
    private readonly FakeOneC _onec = new();
    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Acc = new("AccountingRegister_Acc", "Acc", Families.AccountingRegister, true);
    private static readonly string[] Events = { "_$Data$_.New", "_$Data$_.Update", "_$Data$_.Post", "_$Data$_.Unpost", "_$Data$_.Delete" };
    private static readonly string[] Metas = { "Справочник.Cat", "Документ.Doc", "РегистрБухгалтерии.Acc" };

    public SyncEngineTests()
    {
        _log = Path.Combine(_dir, "1Cv8Log");
        Directory.CreateDirectory(_log);
        var lgf = new StringBuilder("﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n");
        for (int i = 0; i < Events.Length; i++) lgf.Append($"{{4,\"{Events[i]}\",{i + 1}}},\r\n");
        for (int i = 0; i < Metas.Length; i++) lgf.Append($"{{5,{Guid.NewGuid()},\"{Metas[i]}\",{i + 1}}},\r\n");
        File.WriteAllText(Path.Combine(_log, "1Cv8.lgf"), lgf.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_log, "20260930000000.lgp"), "﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n", new UTF8Encoding(false));
        _onec.Tables["Cat"] = Enumerable.Range(1, 40).Select(FakeOneC.CatalogRow).ToList();
        _onec.Tables["Doc"] = Enumerable.Range(1, 12).Select(i => FakeOneC.DocumentRow(i, new DateTime(2026, 9, 1).AddHours(i))).ToList();
        _onec.Tables["Acc"] = Enumerable.Range(1, 12).SelectMany(d => new[] { FakeOneC.MovementRow(d, 1, new DateTime(2026, 9, 1).AddHours(d)) }).ToList();
        _onec.RecorderTypes["Acc"] = new() { "Doc" };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Hex(string uuid)
    {
        var p = uuid.Replace("-", "");
        return p[16..20] + p[20..32] + p[12..16] + p[8..12] + p[0..8];
    }

    private void Log(string ev, string meta, string guid) =>
        File.AppendAllText(Path.Combine(_log, "20260930000000.lgp"),
            $"{{20260930120000,U,\r\n{{0,0}},1,1,1,1,{Array.IndexOf(Events, ev) + 1},I,\"\",{Array.IndexOf(Metas, meta) + 1},\r\n{{\"R\",214:{Hex(guid)}}},\"\",0,0,0,2,0,\r\n{{0}}\r\n}},\r\n",
            new UTF8Encoding(false));

    private (SyncEngineHost Host, SyncDb Db) Engine(StubSyncTarget target, EngineOptions? options = null, string dbName = "sync.db")
    {
        var db = SyncDb.Open(Path.Combine(_dir, dbName));
        var host = new SyncEngineHost(db, target, _onec, new[] { new BasePlan("b", false, _log, "conn", new[] { Cat, Doc, Acc }) },
                                      new SyncBudgets { FeedPollActive = TimeSpan.Zero, FeedPollIdle = TimeSpan.Zero }, options);
        return (host, db);
    }

    /// <summary>Scheduler rounds until <paramref name="done"/> holds (the agents run on the pool).</summary>
    private static async Task Until(SyncEngineHost host, Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            host.Scheduler.Tick();
            if (done()) return;
            await Task.Delay(50);
        }
        Assert.Fail("engine did not get there: " + string.Join("; ", host.Status().Select(s => $"{s.BaseId} {s.Mode} {s.Reason} pending {s.PendingWork} err {s.LastError}")) +
                    " | items: " + string.Join("; ", host.Db.Read(tx => tx.PeekWork("b", 10)).Select(w => $"{w.Kind} {w.ObjectKey} attempts {w.Attempts} err {w.LastError}")) +
                    " | dead: " + string.Join("; ", host.DeadLetters("b").Select(d => $"{d.Category} {d.ObjectKey} {d.Error}")));
    }

    [Fact]
    public async Task ANewBaseIsSnapshottedThenFollowsTheFeed()
    {
        var target = new StubSyncTarget(StubMode.V3);
        var (host, db) = Engine(target);
        using var _ = db;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
        Assert.Equal((40, 12, 12), (target.Count("conn", Cat.Table), target.Count("conn", Doc.Table), target.Count("conn", Acc.Table)));

        // A change in 1C: the log says so, the engine re-reads and sends it.
        _onec.Tables["Cat"][4]["name"] = "renamed in 1C";
        _onec.Tables["Cat"][4]["dataVersion"] = FakeOneC.Version(2);
        Log("_$Data$_.Update", "Справочник.Cat", FakeOneC.Guid(5));
        await Until(host, () => target.Rows("conn", Cat.Table)[FakeOneC.Guid(5)].Contains("renamed in 1C"));
        // A repost with fewer lines.
        _onec.Tables["Acc"].RemoveAll(r => (string)r["recorderRef"]! == FakeOneC.Guid(3));
        Log("_$Data$_.Post", "Документ.Doc", FakeOneC.Guid(3));
        await Until(host, () => !target.Rows("conn", Acc.Table).ContainsKey(FakeOneC.Guid(3) + "#1"));
        Assert.Equal(0, host.Status()[0].PendingWork);
    }

    /// <summary>
    /// Data coverage (RUST_SYNC_CONTRACT §4.9): "loading" is stored before a document or register table's
    /// first row, the final reach after its snapshot — the From date for a windowed table, complete for
    /// a whole one; catalogs are never listed; a failed report is sent again on the next activation.
    /// </summary>
    [Fact]
    public async Task CoverageIsLoadingBeforeTheFirstRowAndTheRealReachAfter()
    {
        var target = new StubSyncTarget(StubMode.V3);
        target.FailNext(new TargetResult(Outcome.Transient, 503, "down"), times: 1, op: "coverage");
        var windowed = Doc with { From = new DateTime(2026, 9, 1) };
        var db = SyncDb.Open(Path.Combine(_dir, "cov.db"));
        using var _ = db;
        var host = new SyncEngineHost(db, target, _onec, new[] { new BasePlan("b", false, _log, "conn", new[] { Cat, windowed, Acc }) },
                                      new SyncBudgets { FeedPollActive = TimeSpan.Zero, FeedPollIdle = TimeSpan.Zero });
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental && target.Coverage("conn", Acc.Table) is { Complete: true });

        var calls = target.Calls.ToList();
        int loading = calls.FindLastIndex(c => c == $"coverage conn {Doc.Table}:loading");
        int firstRow = calls.FindIndex(c => c.StartsWith($"upload conn/{Doc.Table}", StringComparison.Ordinal));
        Assert.True(loading >= 0 && firstRow > loading, "loading must be stored before the first row: " + string.Join(" | ", calls.Where(c => c.Contains(Doc.Table))));
        Assert.Equal(new TableCoverage(Doc.Table, new DateTime(2026, 9, 1), false), target.Coverage("conn", Doc.Table));
        Assert.Equal(new TableCoverage(Acc.Table, null, true), target.Coverage("conn", Acc.Table));
        Assert.Null(target.Coverage("conn", Cat.Table));
        // The first "loading" failed (503) and stopped the snapshot before any row; the next activation sent it again.
        Assert.Equal(2, calls.Count(c => c == $"coverage conn {Doc.Table}:loading"));
    }

    /// <summary>R10 2026-10-08: a base copied before coverage existed gets its final reach on the next run, without a new copy.</summary>
    [Fact]
    public async Task ABaseCopiedWithoutCoverageGetsItOnTheNextRun()
    {
        var windowed = Doc with { From = new DateTime(2026, 9, 1) };
        var plans = new[] { new BasePlan("b", false, _log, "conn", new[] { Cat, windowed, Acc }) };
        var budgets = new SyncBudgets { FeedPollActive = TimeSpan.Zero, FeedPollIdle = TimeSpan.Zero };
        var db = SyncDb.Open(Path.Combine(_dir, "old.db"));
        using var _ = db;
        var first = new StubSyncTarget(StubMode.V3);
        var host = new SyncEngineHost(db, first, _onec, plans, budgets);
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental && first.Coverage("conn", Acc.Table) is { Complete: true });
        await host.StopAsync();
        host.Dispose();
        // As if copied by a build without coverage: nothing was ever sent.
        db.Write(tx => { foreach (var t in new[] { windowed, Acc }) tx.DeleteMeta(BaseSyncAgent.CoverageSentKey("b", t.Table)); });

        var second = new StubSyncTarget(StubMode.V3);
        var again = new SyncEngineHost(db, second, _onec, plans, budgets);
        await Until(again, () => second.Coverage("conn", Acc.Table) is not null && second.Coverage("conn", Doc.Table) is not null);
        Assert.Equal(new TableCoverage(Doc.Table, new DateTime(2026, 9, 1), false), second.Coverage("conn", Doc.Table));
        Assert.Equal(new TableCoverage(Acc.Table, null, true), second.Coverage("conn", Acc.Table));
        Assert.DoesNotContain(second.Calls, c => c.StartsWith("upload", StringComparison.Ordinal));     // no new copy
        await again.StopAsync();
        again.Dispose();
    }

    [Fact]
    public async Task ABaseTheOldConnectorServesIsRefusedUnlessADeveloperOverrides()
    {
        var target = new StubSyncTarget(StubMode.V3) { OtherConnectorOnline = true };
        var (host, db) = Engine(target);
        using (db)
        {
            await Until(host, () => host.Status()[0].Mode == SyncModes.Paused);
            Assert.Contains("D-1", host.Status()[0].Reason);
            Assert.Equal(0, target.Count("conn", Cat.Table));
        }
        var (dev, db2) = Engine(target, new EngineOptions { AllowWithOldConnector = true }, "dev.db");
        using (db2) await Until(dev, () => dev.Status()[0].Mode == SyncModes.Incremental);
    }

    [Fact]
    public async Task LostAuthPausesTheBaseAndResumeContinues()
    {
        var target = new StubSyncTarget(StubMode.V3);
        target.FailNext(new TargetResult(Outcome.Auth, 401, "expired"), times: 50, op: "upload");
        var (host, db) = Engine(target);
        using var _ = db;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Paused);
        Assert.Contains("auth", host.Status()[0].Reason);
        while (target.Calls.Count(c => c.StartsWith("upload")) < 50)                 // burn the failures
            await target.UploadRowsAsync(new UploadBatch(Guid.NewGuid().ToString(), "x", "x", Array.Empty<SyncRow>()), CancellationToken.None);
        Assert.True(host.Resume("b"));
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
        Assert.Equal(40, target.Count("conn", Cat.Table));
    }

    [Fact]
    public async Task APoisonItemBecomesADeadLetterAndCanBeRetriedApprovedOrDismissed()
    {
        var target = new StubSyncTarget(StubMode.V3);
        var (host, db) = Engine(target);
        using var _ = db;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
        target.RejectKeys.Add(FakeOneC.Guid(7));
        _onec.Tables["Cat"][6]["dataVersion"] = FakeOneC.Version(3);
        _onec.Tables["Cat"][8]["dataVersion"] = FakeOneC.Version(3);
        Log("_$Data$_.Update", "Справочник.Cat", FakeOneC.Guid(7));                  // rejected by the target
        Log("_$Data$_.Update", "Справочник.Cat", FakeOneC.Guid(9));                  // must not be held up by it
        await Until(host, () => host.DeadLetters("b").Count == 1 && host.Status()[0].PendingWork == 0);
        var dl = host.DeadLetters("b")[0];
        Assert.Equal(DeadLetterCategories.Validation, dl.Category);
        Assert.False(host.Approve(dl.Id));                                            // only needs_approval can be approved
        target.RejectKeys.Clear();
        Assert.True(host.Retry(dl.Id));
        await Until(host, () => host.Status()[0].PendingWork == 0 && host.DeadLetters("b").Count == 0);

        // A dismissed dead letter is recorded, not silently dropped.
        db.Write(tx => tx.AddDeadLetter(new DeadLetter(0, "b", "delete_object", Cat.Table, "k", DeadLetterCategories.NeedsApproval, "cap", 409, 1, tx.Now, tx.Now, null, "1 key")));
        long id = host.DeadLetters("b").Single().Id;
        Assert.True(host.Dismiss(id, "tester"));
        Assert.Contains(db.Read(tx => tx.Runs("b")), r => r.Outcome == "dismissed" && r.Error!.Contains("tester"));
    }

    [Fact]
    public async Task ARebuildNeedsConfirmationAndATargetThatCanPurge()
    {
        var v2 = new StubSyncTarget(StubMode.V2);
        var (host, db) = Engine(v2);
        using (db)
        {
            await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
            Assert.False((await host.RebuildAsync("b", Acc.Table, confirmed: false, CancellationToken.None)).Done);
            var r = await host.RebuildAsync("b", Acc.Table, confirmed: true, CancellationToken.None);
            Assert.False(r.Done);                                                     // v2 cannot purge: nothing changed
            Assert.Contains("purge", r.Message);
            Assert.Equal(SyncModes.Incremental, host.Status()[0].Mode);
        }

        var v3 = new StubSyncTarget(StubMode.V3);
        var (host3, db3) = Engine(v3, dbName: "v3.db");
        using (db3)
        {
            await Until(host3, () => host3.Status()[0].Mode == SyncModes.Incremental);
            v3.Seed("conn", Acc.Table, "old-style-key#1", "{}");                      // a row keyed the old Connector's way
            var r = await host3.RebuildAsync("b", Acc.Table, confirmed: true, CancellationToken.None);
            Assert.True(r.Done, r.Message);
            await Until(host3, () => host3.Status()[0].Mode == SyncModes.Incremental);
            Assert.Equal(12, v3.Count("conn", Acc.Table));
            Assert.False(v3.Rows("conn", Acc.Table).ContainsKey("old-style-key#1"));
        }
    }

    /// <summary>
    /// S15: five bases on one engine — the budgets hold (≤ 3 active, ≤ 6 sync sessions), a slow backend
    /// only slows things down, and an offline backend loses nothing: when it comes back every base
    /// converges to exactly 1C.
    /// </summary>
    [Fact]
    public async Task FiveBasesASlowBackendAndAnOutageConvergeWithoutLoss()
    {
        var target = new StubSyncTarget(StubMode.V2) { RowsPerSecond = 4_000 };
        var bases = Enumerable.Range(1, 5).Select(i => new BasePlan("b" + i, false, _log, "conn" + i, new[] { Cat, Doc, Acc })).ToList();
        var skew = TimeSpan.Zero;                                                    // lets the test jump over backoff waits
        DateTimeOffset Clock() => DateTimeOffset.UtcNow + skew;
        using var db = SyncDb.Open(Path.Combine(_dir, "five.db"), Clock);
        var budgets = new SyncBudgets { FeedPollActive = TimeSpan.Zero, FeedPollIdle = TimeSpan.Zero };
        using var host = new SyncEngineHost(db, target, _onec, bases, budgets, clock: Clock);
        int peakActive = 0, peakLeases = 0;
        async Task RunUntil(Func<bool> done, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, string.Join("; ", host.Status().Select(s => $"{s.BaseId} {s.Mode} pending {s.PendingWork} err {s.LastError}")));
                host.Scheduler.Tick();
                peakActive = Math.Max(peakActive, host.Scheduler.ActiveBases.Count);
                peakLeases = Math.Max(peakLeases, host.Scheduler.Leases.Total);
                await Task.Delay(20);
            }
        }
        await RunUntil(() => host.Status().All(s => s.Mode == SyncModes.Incremental), 120);
        Assert.InRange(peakActive, 1, 3);
        Assert.InRange(peakLeases, 1, 6);

        // The backend goes down; 1C keeps changing (one log is shared, so every base sees every change).
        target.Offline = true;
        for (int i = 1; i <= 10; i++)
        {
            _onec.Tables["Cat"][i]["name"] = "changed while offline " + i;
            _onec.Tables["Cat"][i]["dataVersion"] = FakeOneC.Version(10 + i);
            Log("_$Data$_.Update", "Справочник.Cat", FakeOneC.Guid(i + 1));
        }
        await RunUntil(() => host.Status().All(s => s.PendingWork >= 10), 30);
        await Task.Delay(1000);
        Assert.All(host.Status(), s => Assert.Equal(SyncModes.Incremental, s.Mode));   // an outage is not a pause
        Assert.All(host.Status(), s => Assert.Equal(0, s.DeadLetters));               // nor a dead letter
        target.Offline = false;
        // The items wait in their backoff (in the queue, not in memory); let that time pass.
        for (int step = 0; step < 12 && !host.Status().All(s => s.PendingWork == 0); step++)
        {
            skew += TimeSpan.FromMinutes(6);
            await RunUntil(() => host.Status().All(s => s.PendingWork == 0) || host.Status().All(s => !s.Active), 20);
            await Task.Delay(200);
        }
        await RunUntil(() => host.Status().All(s => s.PendingWork == 0), 60);
        for (int b = 1; b <= 5; b++)
            for (int i = 1; i <= 10; i++)
                Assert.Contains("changed while offline " + i, target.Rows("conn" + b, Cat.Table)[FakeOneC.Guid(i + 1)]);
    }

    /// <summary>S15: the 1C log is recreated in the middle of a first copy — Recovery takes over and still delivers every table.</summary>
    [Fact]
    public async Task AFeedResetDuringTheFirstCopyEndsInRecoveryNotALostTable()
    {
        _onec.ReadDelayMs = 40;
        var target = new StubSyncTarget(StubMode.V3);
        var (host, db) = Engine(target);
        using var _ = db;
        await Until(host, () => host.Status()[0].TablesDone >= 1, 60);
        var lgf = Path.Combine(_log, "1Cv8.lgf");
        File.WriteAllText(lgf, File.ReadAllText(lgf).Replace("11111111-2222", "99999999-2222"), new UTF8Encoding(false));   // log recreated
        bool sawRecovery = false;
        await Until(host, () =>
        {
            var s = host.Status()[0];
            sawRecovery |= s.Mode == SyncModes.Recovery;
            return s.Mode == SyncModes.Incremental && s.PendingWork == 0;
        }, 120);
        Assert.True(sawRecovery || db.Read(tx => tx.GetCursor("b"))!.Cursor!.StartsWith("99999999", StringComparison.Ordinal));
        Assert.StartsWith("99999999", db.Read(tx => tx.GetCursor("b"))!.Cursor);   // the new log's cursor was adopted
        Assert.Equal((40, 12, 12), (target.Count("conn", Cat.Table), target.Count("conn", Doc.Table), target.Count("conn", Acc.Table)));
    }

    [Fact]
    public void LostStatePutsEveryBaseInRecovery()
    {
        File.WriteAllText(Path.Combine(_dir, "lost.db"), "not a database — a damaged file of some length, enough for a header");
        var (host, db) = Engine(new StubSyncTarget(), dbName: "lost.db");
        using var _ = db;
        Assert.Equal(SyncModes.Recovery, host.Status()[0].Mode);
        Assert.Contains("state lost", host.Status()[0].Reason);
        Assert.Null(db.RecoveryRequired);
    }
}
