using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OneC.EventLog;
using OneC.Host;
using OneC.Sessions;
using OneC.Supervisor;
using OneC.Sync.Engine;
using OneC.Sync.Incremental;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Stub;
using OneC.Sync.Upload;
using OneC.SyncState;

/// <summary>
/// SyncLiveTests' two scenarios that post, unpost and delete a test document through a
/// SessionManager while OneC.Host processes read the same base: the suite's own process died of
/// 1C's heap corruption (0xC0000374) inside each of them (2026-10-02 gate runs 3 and 4). The steps
/// and checks are the tests' as they were; a failed check throws <see cref="CheckFailed"/>.
/// Test documents carry <c>AIBA_REWRITE_S8_&lt;tag&gt;</c> / <c>AIBA_REWRITE_S13_&lt;tag&gt;</c>, the tag
/// chosen by the calling test, so its fallback cleanup (<see cref="CleanupOwned"/>) finds exactly them.
/// </summary>
internal static class SyncScenarios
{
    private const string DocType = "ПоступлениеТоваровУслуг";
    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe");

    public sealed class CheckFailed(string message) : Exception(message);

    private static void Check(bool ok, string? what)
    {
        if (!ok) throw new CheckFailed(what ?? "check failed");
    }

    /// <summary>The suite fixture's pool settings: the scenarios ran on it before they moved here.</summary>
    public static SessionManager Manager(string comcntr, IEnumerable<OneCBase> bases)
    {
        var m = new SessionManager(comcntr, new PoolOptions
        {
            GlobalMaxSessions = 4, PerBaseMaxSessions = 2, IdleTimeout = TimeSpan.FromSeconds(3), SweepInterval = TimeSpan.FromSeconds(1)
        });
        foreach (var b in bases) m.Register(b);
        return m;
    }

    // ---------------- S7/S8: a test document's whole life reaches the stub exactly ----------------

    public static async Task<string?> Lifecycle(SessionManager m, IEnumerable<OneCBase> bases, string tag)
    {
        foreach (var b in bases) await LifecycleOn(m, b, "AIBA_REWRITE_S8_" + tag);
        return null;
    }

    private static async Task LifecycleOn(SessionManager m, OneCBase b, string comment)
    {
        var (logDir, err) = new LogLocator().Resolve(b.ConnectionString);
        Check(logDir is not null, err);
        using var sup = new OneC.Supervisor.Supervisor(new SupervisorOptions { HostExe = HostExe, MonitorInterval = TimeSpan.FromHours(1) });
        sup.Start(new[] { b });
        var reader = new SupervisorReader(sup);
        string dbDir = Directory.CreateTempSubdirectory("aiba-synclive-").FullName;
        try
        {
            using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
            var doc = new TablePlan("Document_" + DocType, DocType, Families.Document, true);
            var acc = new TablePlan("AccountingRegister_Хозрасчетный", "Хозрасчетный", Families.AccountingRegister, true);
            var tables = new Dictionary<string, TablePlan> { [doc.Table] = doc, [acc.Table] = acc };
            var target = new StubSyncTarget(StubMode.V2);
            for (int i = 0; i < 100; i++) target.Seed("p", doc.Table, Guid.NewGuid().ToString(), "{}");   // a table the 5 % cap lets delete from
            var up = new Uploader(target, new UploadGate(new SyncBudgets()));
            var parts = new SinglePartition("p");
            var deletes = new DeleteObjectHandler(db, target, tables, parts);
            var rec = new SyncRecorderHandler(db, reader, target, up, target.Capabilities, tables, parts, deletes);
            deletes.ClearMovements = rec.ClearAsync;
            var exec = new WorkExecutor(db, new Dictionary<string, IWorkHandler>
            {
                [WorkKinds.SyncObject] = new SyncObjectHandler(db, reader, up, tables, parts, deletes),
                [WorkKinds.SyncRecorder] = rec, [WorkKinds.DeleteObject] = deletes
            });
            var feed = new FeedService(db, new EventLogReader(), new SyncBudgets());
            var map = new TableMap(tables.Values);
            feed.Handshake(b.Name, logDir!);
            var act = new BaseActivation(b.Name, b.IsFile, SyncPriority.Incremental, true, new SyncLeases(new SyncBudgets(), () => DateTimeOffset.UtcNow),
                                         new SyncBudgets(), CancellationToken.None);

            var w = new WriteService(m, "AIBA_REWRITE_");
            string id = w.CreateByClone(b.Name, DocType, comment).Ref;
            var latencies = new List<double>();
            int movementsSeen = 0;
            try
            {
                async Task Step(string what, Action write, bool expectDoc)
                {
                    var sw = Stopwatch.StartNew();
                    write();
                    // Poll like the scheduler would (feed stat → drain → execute) until the stub matches 1C.
                    string? diff = null;
                    for (int i = 0; i < 60; i++)
                    {
                        feed.Drain(b.Name, logDir!, map);
                        await exec.RunAsync(act);
                        diff = await Compare(reader, target, b.Name, acc, id, expectDoc, doc);
                        if (diff is null) break;
                        await Task.Delay(1000);
                    }
                    latencies.Add(sw.Elapsed.TotalSeconds);
                    int lines = target.Rows("p", acc.Table).Keys.Count(k => k.StartsWith(id + "#", StringComparison.Ordinal));
                    movementsSeen = Math.Max(movementsSeen, lines);
                    Console.WriteLine($"{b.Name} {what}: stub = 1C after {sw.Elapsed.TotalSeconds:F1} s ({lines} Хозрасчетный lines)");
                    Check(diff is null, $"{b.Name} {what}: {diff}; dead letters: {string.Join("; ", db.Read(tx => tx.DeadLetters(b.Name)).Select(d => d.Error))}");
                }

                await Step("create + post", () => w.Post(b.Name, DocType, id), true);
                await Step("unpost", () => w.Unpost(b.Name, DocType, id), true);
                await Step("repost", () => w.Post(b.Name, DocType, id), true);
                await Step("delete", () => w.Delete(b.Name, DocType, id), false);
            }
            finally
            {
                try { w.Delete(b.Name, DocType, id); } catch { /* already deleted by the last step */ }
            }
            Check(w.FindOwned(b.Name, DocType, comment).Count == 0, $"{b.Name}: test document {comment} left");
            Check(movementsSeen > 0, $"{b.Name}: posting made no Хозрасчетный movements — the comparison proved nothing");
            Console.WriteLine($"{b.Name}: latencies {string.Join(", ", latencies.Select(l => l.ToString("F1")))} s; movement reads {rec.MovementReads}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, true); } catch (IOException) { }
        }
    }

    /// <summary>Null when the stub holds exactly 1C's state for the recorder: the document row (or none) and every movement key.</summary>
    private static async Task<string?> Compare(IOneCReader reader, StubSyncTarget target, string baseName, TablePlan acc, string id, bool expectDoc, TablePlan doc)
    {
        bool hasDoc = target.Rows("p", doc.Table).ContainsKey(id);
        if (hasDoc != expectDoc) return $"document row {(hasDoc ? "present" : "missing")}";
        var inOneC = new HashSet<string>();
        int? after = null;
        while (true)
        {
            var page = await reader.MovementsAsync(baseName, acc, DocType, id, after, 1000, CancellationToken.None);
            foreach (var r in page.Rows) inOneC.Add(id + "#" + r["lineNo"]);
            if (!page.HasMore) break;
            after = page.NextLine;
        }
        var inStub = target.Rows("p", acc.Table).Keys.Where(k => k.StartsWith(id + "#", StringComparison.Ordinal)).ToHashSet();
        return inOneC.SetEquals(inStub) ? null : $"movements 1C {inOneC.Count}, stub {inStub.Count}";
    }

    // ---------------- S13: the engine in the Supervisor, controlled through the edge ----------------

    public static async Task<string?> Edge(SessionManager m, OneCBase b, string tag)
    {
        var (logDir, _) = new LogLocator().Resolve(b.ConnectionString);
        using var sup = new OneC.Supervisor.Supervisor(new SupervisorOptions { HostExe = HostExe, MonitorInterval = TimeSpan.FromHours(1) });
        sup.Start(new[] { b });
        string dbDir = Directory.CreateTempSubdirectory("aiba-syncedge-").FullName;
        var target = new StubSyncTarget(StubMode.V2);
        try
        {
            using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
            var tables = new[]
            {
                new TablePlan("Catalog_Контрагенты", "Контрагенты", Families.Catalog, false),
                new TablePlan("Document_" + DocType, DocType, Families.Document, true),
                new TablePlan("AccountingRegister_Хозрасчетный", "Хозрасчетный", Families.AccountingRegister, true, From: DateTime.Today.AddDays(-400))
            };
            using var host = new SyncEngineHost(db, target, new SupervisorReader(sup),
                new[] { new BasePlan(b.Name, true, logDir, "conn", tables) }, new SyncBudgets { FeedPollActive = TimeSpan.FromSeconds(1) });
            await using var edge = new EdgeServer(sup, 0) { Sync = host };
            await edge.StartAsync();
            host.Start(TimeSpan.FromMilliseconds(500));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.Port}/") };
            http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
            async Task<System.Text.Json.Nodes.JsonNode> Base() => System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync("v1/sync"))![0]!;
            async Task WaitMode(string mode, int seconds = 600)
            {
                var until = DateTime.UtcNow.AddSeconds(seconds);
                while ((string)(await Base())["mode"]! != mode)
                {
                    Check(DateTime.UtcNow < until, $"mode {(await Base()).ToJsonString()}");
                    await Task.Delay(1000);
                }
            }

            var sw = Stopwatch.StartNew();
            await WaitMode(SyncModes.Incremental);
            Console.WriteLine($"{b.Name}: snapshot of {string.Join(", ", tables.Select(t => $"{t.Name} {target.Count("conn", t.Table)}"))} → incremental in {sw.Elapsed.TotalSeconds:F0} s");

            int pause = (int)(await http.PostAsync($"v1/sync/bases/{b.Name}/pause", null)).StatusCode;
            Check(pause == 200, $"pause → {pause}, want 200");
            await WaitMode(SyncModes.Paused, 10);
            int resume = (int)(await http.PostAsync($"v1/sync/bases/{b.Name}/resume", null)).StatusCode;
            Check(resume == 200, $"resume → {resume}, want 200");
            await WaitMode(SyncModes.Incremental, 10);
            int unconfirmed = (int)(await http.PostAsync($"v1/sync/bases/{b.Name}/tables/AccountingRegister_Хозрасчетный/rebuild", null)).StatusCode;
            Check(unconfirmed == 409, $"unconfirmed rebuild → {unconfirmed}, want 409");
            var rebuild = await http.PostAsync($"v1/sync/bases/{b.Name}/tables/AccountingRegister_Хозрасчетный/rebuild?confirm=true", null);
            Check((int)rebuild.StatusCode == 409, $"confirmed rebuild → {(int)rebuild.StatusCode}, want 409");    // v2 cannot purge: nothing changed
            string rebuildText = await rebuild.Content.ReadAsStringAsync();
            Check(rebuildText.Contains("purge"), $"rebuild answer without \"purge\": {rebuildText}");
            string dead = await http.GetStringAsync($"v1/sync/bases/{b.Name}/dead-letters");
            Check(dead == "[]", $"dead letters {dead}, want []");

            // A write in 1C reaches the stub through the running engine.
            var w = new WriteService(m, "AIBA_REWRITE_");
            string comment = "AIBA_REWRITE_S13_" + tag;
            string id = w.CreateByClone(b.Name, DocType, comment).Ref;
            try
            {
                w.Post(b.Name, DocType, id);
                sw.Restart();
                while (!target.Rows("conn", "Document_" + DocType).ContainsKey(id) ||
                       !target.Rows("conn", "AccountingRegister_Хозрасчетный").Keys.Any(k => k.StartsWith(id + "#", StringComparison.Ordinal)))
                {
                    if (sw.Elapsed >= TimeSpan.FromSeconds(60)) Check(false, "the write did not arrive: " + (await Base()).ToJsonString());
                    await Task.Delay(250);
                }
                Console.WriteLine($"{b.Name}: posted test document in the stub with its movements after {sw.Elapsed.TotalSeconds:F1} s");
            }
            finally { w.Delete(b.Name, DocType, id); }
            sw.Restart();
            while (target.Rows("conn", "Document_" + DocType).ContainsKey(id))
            {
                Check(sw.Elapsed < TimeSpan.FromSeconds(60), "the delete did not arrive");
                await Task.Delay(250);
            }
            Console.WriteLine($"{b.Name}: deletion in the stub after {sw.Elapsed.TotalSeconds:F1} s");
            Check(w.FindOwned(b.Name, DocType, comment).Count == 0, $"test document {comment} left");
            await host.StopAsync();
            return null;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, true); } catch (IOException) { }
        }
    }

    // ---------------- fallback cleanup after a failed or crashed scenario ----------------

    /// <summary>
    /// Deletes the documents of <paramref name="docType"/> whose comment starts with
    /// <paramref name="prefix"/> on every base — only a run-specific <c>AIBA_REWRITE_S…_&lt;tag&gt;</c>
    /// prefix is accepted (AGENT.md: never a broad prefix), and WriteService itself refuses any
    /// document whose comment is not owned.
    /// </summary>
    public static string? CleanupOwned(SessionManager m, IEnumerable<OneCBase> bases, string docType, string prefix)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^AIBA_REWRITE_S\d+_\d{10,}$"))
            return $"refused: cleanup prefix '{prefix}' is not a run-specific AIBA_REWRITE_S<n>_<tag>";
        var w = new WriteService(m, "AIBA_REWRITE_");
        var left = new List<string>();
        foreach (var b in bases)
        {
            var refs = w.FindOwned(b.Name, docType, prefix);
            foreach (var id in refs) w.Delete(b.Name, docType, id);
            Console.WriteLine($"cleanup {b.Name}/{docType} {prefix}: found {refs.Count}, deleted {refs.Count}");
            left.AddRange(w.FindOwned(b.Name, docType, prefix).Select(id => $"{b.Name}:{id}"));
        }
        return left.Count == 0 ? null : "left after cleanup: " + string.Join(", ", left);
    }
}
