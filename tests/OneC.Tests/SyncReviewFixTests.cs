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
/// Regressions for the engine defects the 2026-10-01 self-review found (MIGRATION_STATUS "Bugs found
/// overnight"): a user Pause overwritten, unknown D-1 presence taken as "allowed", a failed base
/// re-activated every tick, Fallback that never ended, and organisations bound later never sent.
/// </summary>
public sealed class SyncReviewFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-review-").FullName;
    private readonly string _log;
    private readonly FakeOneC _onec = new();
    private static readonly string OrgA = FakeOneC.Guid(9_000_000), OrgB = FakeOneC.Guid(9_000_001);
    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);

    public SyncReviewFixTests()
    {
        _log = Path.Combine(_dir, "1Cv8Log");
        Directory.CreateDirectory(_log);
        var lgf = new StringBuilder("﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n");
        lgf.Append("{4,\"_$Data$_.Update\",1},\r\n").Append($"{{5,{Guid.NewGuid()},\"Справочник.Cat\",1}},\r\n");
        File.WriteAllText(Path.Combine(_log, "1Cv8.lgf"), lgf.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_log, "20260930000000.lgp"), "﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n", new UTF8Encoding(false));
        _onec.Tables["Cat"] = Enumerable.Range(1, 40).Select(FakeOneC.CatalogRow).ToList();
        _onec.Tables["Doc"] = Enumerable.Range(1, 12).Select(i =>
        {
            var r = FakeOneC.DocumentRow(i, new DateTime(2026, 9, 1).AddHours(i));
            r["orgRef"] = i % 3 == 0 ? OrgB : OrgA;
            return r;
        }).ToList();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SyncEngineHost Engine(StubSyncTarget target, SyncDb db, string? logDir, params TablePlan[] tables) =>
        new(db, target, _onec, new[] { new BasePlan("b", false, logDir, "conn", tables.Length > 0 ? tables : new[] { Cat }) },
            new SyncBudgets { FeedPollActive = TimeSpan.Zero, FeedPollIdle = TimeSpan.Zero });

    private static async Task Until(SyncEngineHost host, Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            host.Scheduler.Tick();
            if (done()) return;
            await Task.Delay(50);
        }
        Assert.Fail("engine did not get there: " + string.Join("; ", host.Status().Select(s => $"{s.Mode} {s.Reason} pending {s.PendingWork} err {s.LastError}")));
    }

    private static async Task For(SyncEngineHost host, TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until) { host.Scheduler.Tick(); await Task.Delay(50); }
    }

    [Fact]
    public async Task APauseDuringTheFirstCopyIsKept()
    {
        var target = new StubSyncTarget(StubMode.V3) { RowsPerSecond = 40 };          // ~1 s per table page
        using var db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        using var host = Engine(target, db, _log, Cat, Doc);
        await Until(host, () => target.Calls.Any(c => c.StartsWith("upload", StringComparison.Ordinal)));
        Assert.True(host.Pause("b"));
        await For(host, TimeSpan.FromSeconds(4));                                     // the running activation ends meanwhile
        var s = host.Status()[0];
        Assert.Equal(SyncModes.Paused, s.Mode);                                       // not "snapshot complete" over it
        Assert.True(host.Resume("b"));
        target.RowsPerSecond = null;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
    }

    [Fact]
    public async Task UnknownOldConnectorPresenceSyncsNothingUntilItIsKnown()
    {
        var target = new StubSyncTarget(StubMode.V3) { OtherConnectorOnline = null };
        using var db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        using var host = Engine(target, db, _log);
        await For(host, TimeSpan.FromSeconds(2));
        Assert.Equal(0, target.Count("conn", Cat.Table));
        Assert.Contains("presence unknown", host.Status()[0].LastError);
        target.OtherConnectorOnline = false;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
        Assert.Equal(40, target.Count("conn", Cat.Table));
    }

    [Fact]
    public async Task AFailedActivationBacksOffInsteadOfRunningEveryTick()
    {
        int reads = 0;
        _onec.FailRead = t => { Interlocked.Increment(ref reads); return true; };
        var target = new StubSyncTarget(StubMode.V3);
        using var db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        using var host = Engine(target, db, _log);
        await Until(host, () => host.Status()[0].LastError is not null);
        int after = Volatile.Read(ref reads);
        await For(host, TimeSpan.FromSeconds(3));                                     // 60 ticks: inside the 5 s backoff
        Assert.Equal(after, Volatile.Read(ref reads));
        Assert.NotNull(db.Read(tx => tx.GetMeta(BaseSyncAgent.BackoffKey("b"))));
        _onec.FailRead = null;
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental &&
                                db.Read(tx => tx.GetMeta(BaseSyncAgent.BackoffKey("b"))) is null, seconds: 30);   // cleared by the good activation
    }

    [Fact]
    public async Task FallbackEndsOnceTheLogIsReadable()
    {
        var target = new StubSyncTarget(StubMode.V3);
        using var db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        using (var noLog = Engine(target, db, logDir: null))
            await Until(noLog, () => noLog.Status()[0].Mode == SyncModes.Fallback);   // copied, then timed verify passes
        Assert.Equal(40, target.Count("conn", Cat.Table));
        using var withLog = Engine(target, db, _log);                                  // next start: the log is there
        await Until(withLog, () => withLog.Status()[0].Mode == SyncModes.Incremental);
        Assert.NotNull(withLog.Status()[0].Cursor);
    }

    /// <summary>§21: documents of an organisation without a binding wait (counted, not sent, no version); once it is bound they go out.</summary>
    [Fact]
    public async Task AnOrganisationBoundLaterGetsItsRowsSent()
    {
        var target = new StubSyncTarget(StubMode.V3);
        var config = new SyncConfig("conn", "unisoft", "conn", new[] { new SyncPartition("conn:A", OrgA, "501") },
            new[] { new SyncTableConfig(Cat.Table, "Справочник.Cat", Families.Catalog, false), new SyncTableConfig(Doc.Table, "Документ.Doc", Families.Document, true) });
        target.SetConfig(config);
        using var db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        using var host = Engine(target, db, _log, Cat, Doc);
        await Until(host, () => host.Status()[0].Mode == SyncModes.Incremental);
        Assert.Equal(8, target.Count("conn:A", Doc.Table));
        Assert.Equal(0, target.Count("conn", Doc.Table));
        Assert.Equal(4, Assert.Single(host.Status()[0].UnmappedOrgs).Rows);

        target.SetConfig(config with { Partitions = config.Partitions.Append(new SyncPartition("conn:B", OrgB, "502")).ToList() });
        db.Write(tx => tx.DeleteMeta(BaseSyncAgent.UnmappedCheckKey("b")));             // its periodic re-check is due
        await Until(host, () => target.Count("conn:B", Doc.Table) == 4 && host.Status()[0].UnmappedOrgs.Count == 0 && host.Status()[0].PendingWork == 0);
        Assert.Equal(8, target.Count("conn:A", Doc.Table));
        Assert.Equal(0, target.Count("conn", Doc.Table));                              // nothing leaked into shared
    }
}
