using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OneC.EventLog;
using OneC.Sessions;
using OneC.Supervisor;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using OneC.Sync.Verify;
using Xunit;
using Xunit.Abstractions;

namespace OneC.Tests;

/// <summary>
/// Sync blocks S7–S13 on real bases, through the Supervisor's pipe. The two scenarios that post,
/// unpost and delete a test document through a SessionManager while OneC.Host processes read the
/// same base run as <c>OneCLiveChild</c> scenarios (<c>SyncScenarios.cs</c>, steps and checks
/// unchanged): 1C's heap corruption killed this process inside each of them (2026-10-02 gate runs
/// 3 and 4, 0xC0000374). If such a child fails or dies, its documents are removed by a second,
/// guarded child (<see cref="LiveChild.CleanupOwned"/>). The rest only read 1C here.
/// </summary>
[Collection("onec-live")]
public sealed class SyncLiveTests(LiveFixture f, ITestOutputHelper output)
{
    private const string DocType = "ПоступлениеТоваровУслуг";

    /// <summary>Two bases × four steps, each polled up to 60 s, plus the bases' cold connects.</summary>
    private static readonly TimeSpan ScenarioLimit = TimeSpan.FromMinutes(20);

    /// <summary>
    /// S7/S8: a test-owned document is created, posted, unposted, reposted and deleted on each base;
    /// after each step the feed and the executor run and the stub must hold exactly what 1C holds —
    /// the document and its movements.
    /// </summary>
    [Fact]
    public void ATestDocumentsWholeLifeReachesTheStubExactly()
    {
        if (!f.Available) return;
        RunWriting("sync-lifecycle", "AIBA_REWRITE_S8_");
    }

    /// <summary>
    /// Runs a writing scenario in a child with a run tag; when the child fails or dies, the guarded
    /// cleanup child removes exactly that run's documents and its report joins the failure.
    /// </summary>
    private void RunWriting(string scenario, string prefix)
    {
        string tag = DateTime.Now.ToString("MMddHHmmssfff");
        try
        {
            output.WriteLine(LiveChild.Run(scenario, ScenarioLimit, tag));
        }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException(e.Message + LiveChild.CleanupOwned(DocType, prefix + tag));
        }
    }

    /// <summary>
    /// S9 live: independent information registers snapshot, then refresh — nothing changed, nothing
    /// sent — and the refresh's cost per table size (the D-6 numbers). Read-only on 1C.
    /// </summary>
    [Fact]
    public async Task AnIndependentRegisterRefreshSendsNothingWhenNothingChanged()
    {
        if (!f.Available) return;
        var candidates = new Dictionary<string, string[]>
        {
            ["bilim"] = new[] { "СтатусыДокументов" },
            // The independent registers the old Connector syncs (config.ts), plus two common ones.
            ["kansler"] = new[] { "ДокументыФизическихЛиц", "РегламентированныйПроизводственныйКалендарь", "ЗначенияСвойствОбъектов", "КурсыВалют" }
        };
        int refreshed = 0;
        foreach (var b in new[] { f.File, f.Server }.OfType<OneCBase>())
        {
            using var sup = new OneC.Supervisor.Supervisor(new SupervisorOptions
            {
                HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1)
            });
            sup.Start(new[] { b });
            var reader = new SupervisorReader(sup);
            foreach (var name in candidates.GetValueOrDefault(b.Name) ?? Array.Empty<string>())
            {
                var t = new TablePlan("InformationRegister_" + name, name, Families.IndependentInfoRegister, false);
                long count;
                try { count = await reader.CountAsync(b.Name, t, CancellationToken.None); }
                catch (OneCMetadataMissingException) { continue; }
                catch (OneCReadException) { continue; }
                string dbDir = Directory.CreateTempSubdirectory("aiba-v2reg-").FullName;
                try
                {
                    using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
                    var target = new StubSyncTarget(StubMode.V3) { KeepJson = false };
                    var budgets = new SyncBudgets();
                    var gate = new UploadGate(budgets);
                    var act = new BaseActivation(b.Name, b.IsFile, SyncPriority.Snapshot, false, new SyncLeases(budgets, () => DateTimeOffset.UtcNow), budgets, CancellationToken.None);
                    var sw = Stopwatch.StartNew();
                    var snap = await new SnapshotRunner(db, reader, new Uploader(target, gate), gate, target.Capabilities, new SinglePartition("p")).RunTableAsync(act, t, 1);
                    double snapS = sw.Elapsed.TotalSeconds;
                    Assert.Equal(count, target.Count("p", t.Table));
                    var up = new Uploader(target, gate);
                    var exec = new WorkExecutor(db, new Dictionary<string, IWorkHandler>
                    {
                        [WorkKinds.RefreshRegister] = new RefreshRegisterHandler(db, reader, target, up, new Dictionary<string, TablePlan> { [t.Table] = t }, new SinglePartition("p"))
                    });
                    db.Write(tx => tx.UpsertWork(new WorkRequest(b.Name, t.Table, t.Table, WorkFlags.Refresh, ItemPriority.Refresh)));
                    sw.Restart();
                    var s = await exec.RunAsync(act);
                    double refreshS = sw.Elapsed.TotalSeconds;
                    output.WriteLine($"{b.Name} {name}: {count} rows; snapshot {snapS:F1} s; refresh {refreshS:F1} s = {count / Math.Max(refreshS, 0.001):F0} rows/s scanned, " +
                                     $"sent {up.RowsSent}, outcome done {s.Done} skipped {s.Skipped}");
                    Assert.Equal(1, s.Skipped);                                  // a live base may change meanwhile; these are quiet tables
                    Assert.Equal(0, up.RowsSent);
                    refreshed++;
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    try { Directory.Delete(dbDir, true); } catch (IOException) { }
                }
            }
        }
        Gate.Skip(refreshed == 0, "none of the candidate independent registers exists in these bases");
    }

    /// <summary>
    /// S10 live: KAN holds two organisations. One is bound, one is not: every document of the bound one
    /// lands in its partition, none in the shared one, and the other's are counted as unmapped —
    /// both numbers equal to 1C's own COUNT by Организация.
    /// </summary>
    [Fact]
    public async Task DocumentsOfAMultiOrganisationBaseLandInTheirOrganisationsPartition()
    {
        if (!f.Available || f.Server is null) return;
        var b = f.Server;
        var byOrg = f.Manager!.Use(b.Name, ctx =>
        {
            using var s = new OneC.Interop.ComScope();
            var q = s.Track(OneC.Interop.Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            OneC.Interop.Dispatch.Set(q, "Текст", $"ВЫБРАТЬ Т.Организация КАК o, КОЛИЧЕСТВО(*) КАК n ИЗ Документ.{DocType} КАК Т СГРУППИРОВАТЬ ПО Т.Организация", ctx.Error);
            var sel = new OneC.Interop.DispatchMemo(s.Track(OneC.Interop.Dispatch.Call(s.Track(OneC.Interop.Dispatch.Call(q, "Выполнить", ctx.Error), "Р"), "Выбрать", ctx.Error), "В"));
            var d = new Dictionary<string, long>();
            while (sel.CallBool("Следующий", ctx.Error))
            {
                using var rs = new OneC.Interop.ComScope();
                var o = rs.Track(sel.Get("o", ctx.Error), "org");
                d[OneC.Host.OneCValue.RefGuid(o!, ctx)!] = Convert.ToInt64(sel.Get("n", ctx.Error));
            }
            return d;
        });
        Assert.True(byOrg.Count >= 2, $"KAN has documents of {byOrg.Count} organisation(s)");
        var bound = byOrg.OrderByDescending(kv => kv.Value).First().Key;

        using var sup = new OneC.Supervisor.Supervisor(new SupervisorOptions { HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1) });
        sup.Start(new[] { b });
        var reader = new SupervisorReader(sup);
        string dbDir = Directory.CreateTempSubdirectory("aiba-v2org-").FullName;
        try
        {
            using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
            var doc = new TablePlan("Document_" + DocType, DocType, Families.Document, true);
            var config = new OneC.Sync.Abstractions.SyncConfig("kan", null, "kan", new[] { new OneC.Sync.Abstractions.SyncPartition("kan:" + bound, bound, "501") },
                new[] { new OneC.Sync.Abstractions.SyncTableConfig(doc.Table, "Документ." + DocType, Families.Document, true) });
            OneC.Sync.Routing.OrgRouter.Validate(config, new[] { doc });
            var router = new OneC.Sync.Routing.OrgRouter(config);
            var target = new StubSyncTarget(StubMode.V2) { KeepJson = false };
            var budgets = new SyncBudgets();
            var gate = new UploadGate(budgets);
            var act = new BaseActivation(b.Name, false, SyncPriority.Snapshot, false, new SyncLeases(budgets, () => DateTimeOffset.UtcNow), budgets, CancellationToken.None);
            var r = await new SnapshotRunner(db, reader, new Uploader(target, gate), gate, target.Capabilities, router).RunTableAsync(act, doc, 1);
            Assert.Equal(OneC.Sync.Snapshot.SnapshotOutcome.Complete, r.Outcome);
            Assert.Equal(byOrg[bound], target.Count("kan:" + bound, doc.Table));
            Assert.Equal(0, target.Count("kan", doc.Table));
            var unmapped = db.Read(tx => tx.UnmappedOrgs(b.Name)).ToDictionary(u => u.OrgRef, u => u.Rows);
            foreach (var (org, n) in byOrg.Where(kv => kv.Key != bound)) Assert.Equal(n, unmapped[org]);
            output.WriteLine($"{b.Name} {DocType}: {string.Join(", ", byOrg.Select(kv => $"{kv.Key[..8]} 1C {kv.Value}"))}; " +
                             $"bound partition {target.Count("kan:" + bound, doc.Table)}, shared 0, unmapped {string.Join(", ", unmapped.Select(u => $"{u.Key[..8]} {u.Value}"))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// S11 live. KAN: a full verify pass of the largest document table through the engine, after the
    /// versions were recorded — nothing changed, nothing queued — timed. bilim: a feed reset on a
    /// <em>copy</em> of its log (the real log is never touched) sends the base to Recovery, whose pass
    /// queues only what differs and then adopts the new cursor.
    /// </summary>
    [Fact]
    public async Task AVerifyPassQueuesOnlyDifferencesAndRecoveryResumesTheFeed()
    {
        if (!f.Available) return;
        foreach (var b in new[] { f.Server, f.File }.OfType<OneCBase>())
        {
            using var sup = new OneC.Supervisor.Supervisor(new SupervisorOptions { HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1) });
            sup.Start(new[] { b });
            var reader = new SupervisorReader(sup);
            string dbDir = Directory.CreateTempSubdirectory("aiba-v2verify-").FullName;
            try
            {
                using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
                string name = b.IsFile ? DocType : "РеализацияТоваровУслуг";
                var t = new TablePlan("Document_" + name, name, Families.Document, true);
                // What a finished sync would have stored: the current versions.
                var sw = Stopwatch.StartNew();
                string? after = null;
                long seeded = 0;
                while (true)
                {
                    var (rows, next) = await reader.VersionPageAsync(b.Name, t, after, 5000, CancellationToken.None);
                    db.Write(tx => { foreach (var (id, v) in rows) tx.SetVersion(b.Name, t.Table, id, v); });
                    seeded += rows.Count;
                    if (next is null) break;
                    after = next;
                }
                double seedS = sw.Elapsed.TotalSeconds;
                sw.Restart();
                var r = await new VerifyRunner(db, reader).VerifyTableAsync(b.Name, t, true, CancellationToken.None);
                double verifyS = sw.Elapsed.TotalSeconds;
                output.WriteLine($"{b.Name} {name}: {seeded} objects; verify pass {verifyS:F1} s = {r.Scanned / Math.Max(verifyS, 0.001):F0} objects/s " +
                                 $"(seeding {seedS:F1} s); changed {r.Changed}, gone {r.Gone}");
                Assert.Equal(seeded, r.Scanned);
                Assert.True(r.Changed + r.Gone <= 3, $"a quiet table should differ by almost nothing, got {r.Changed} + {r.Gone}");  // KAN's own jobs may write meanwhile

                if (!b.IsFile) continue;
                // bilim: a reset on a copy of its log.
                var (logDir, _) = new LogLocator().Resolve(b.ConnectionString);
                string copy = Path.Combine(dbDir, "1Cv8Log");
                Directory.CreateDirectory(copy);
                foreach (var file in Directory.GetFiles(logDir!)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                db.Write(tx => tx.UpsertBase(new SyncBaseRow(b.Name, "stub", "c", null, SyncModes.Incremental, null, false, "h", tx.Now)));
                var feed = new FeedService(db, new EventLogReader(), new SyncBudgets());
                var guid = new EventLogReader().Handshake(copy).LgfGuid;
                db.Write(tx => tx.SetCursor(b.Name, $"{guid}|20200101000000.lgp|0", copy));    // a file that no longer exists
                var d = feed.Drain(b.Name, copy, new TableMap(new[] { t }));
                Assert.True(d.Reset);
                Assert.Equal(SyncModes.Recovery, db.Read(tx => tx.GetBase(b.Name))!.Mode);
                db.Write(tx => { foreach (var w in tx.PeekWork(b.Name, 100_000)) tx.CompleteWork(tx.ClaimWork(b.Name, 1, TimeSpan.FromMinutes(1)).Single()); });
                await new Recovery(db, new VerifyRunner(db, reader)).RecoverAsync(b.Name, new[] { t }, copy, new EventLogReader(), CancellationToken.None);
                Assert.Equal(SyncModes.Incremental, db.Read(tx => tx.GetBase(b.Name))!.Mode);
                Assert.DoesNotContain("20200101000000", db.Read(tx => tx.GetCursor(b.Name))!.Cursor);
                output.WriteLine($"{b.Name}: reset ({d.ResetReason}) → Recovery → verify queued {db.Read(tx => tx.CountWork(b.Name))} items → Incremental");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(dbDir, true); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// S13 live, through the Supervisor's HTTP edge: the sync engine on bilim (stub target) reaches
    /// Incremental by itself, answers /v1/sync, pauses and resumes, refuses an unconfirmed or
    /// impossible rebuild, and carries a test document's write to the stub.
    /// </summary>
    [Fact]
    public void TheEngineRunsInTheSupervisorAndIsControlledThroughTheEdge()
    {
        if (!f.Available || f.File is null) return;
        RunWriting("sync-edge", "AIBA_REWRITE_S13_");
    }
}
