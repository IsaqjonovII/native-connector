using System.Net;
using System.Text.Json.Nodes;
using OneC.EventLog;
using OneC.Sync;
using Xunit;

namespace OneC.Tests;

public class SyncIdentityTests
{
    [Theory]
    [InlineData("Catalog_Контрагенты", TableKind.Catalog, "Контрагенты")]
    [InlineData("Document_ПоступлениеТоваровУслуг", TableKind.Document, "ПоступлениеТоваровУслуг")]
    [InlineData("AccountingRegister_Хозрасчетный_RecordType", TableKind.AccountingRegister, "Хозрасчетный")]
    [InlineData("AccumulationRegister_РозничнаяВыручка", TableKind.AccumulationRegister, "РозничнаяВыручка")]
    [InlineData("InformationRegister_ДокументыФизическихЛиц", TableKind.InformationRegister, "ДокументыФизическихЛиц")]
    [InlineData("ChartOfAccounts_Хозрасчетный", TableKind.ChartOfAccounts, "Хозрасчетный")]
    public void BackendTableNamesParse(string name, TableKind kind, string oneC)
    {
        var t = SyncTable.Parse(name);
        Assert.Equal((kind, oneC), (t.Kind, t.OneCName));
    }

    [Fact]
    public void RowKeysFollowTheConnector()
    {
        Assert.Equal("a1", RowIdentity.RowKey(SyncTable.Parse("Document_X"), new JsonObject { ["id"] = "a1" }));
        Assert.Equal("60.10", RowIdentity.RowKey(SyncTable.Parse("ChartOfAccounts_Х"), new JsonObject { ["code"] = "60.10" }));
        var line = JsonNode.Parse("""{ "recorderRef": "r9", "lineNo": 3 }""")!.AsObject();
        Assert.Equal("r9#3", RowIdentity.RowKey(SyncTable.Parse("AccountingRegister_Х_RecordType"), line));
        Assert.Null(RowIdentity.RowKey(SyncTable.Parse("InformationRegister_Х"), line));
    }

    [Fact]
    public void JsonIsWrittenTheWayJavaScriptWritesIt()
    {
        var row = JsonNode.Parse("""{ "b": 3790.00, "a": 12.50, "z": -0.0, "s": "Мир \"x\"\n\u0001" }""")!.AsObject();
        Assert.Equal("""{"b":3790,"a":12.5,"z":0,"s":"Мир \"x\"\n\u0001"}""", JsJson.Serialize(row));
        // The hash ignores key order and number spelling.
        var other = JsonNode.Parse("""{ "s": "Мир \"x\"\n\u0001", "z": 0, "a": 12.5, "b": 3790 }""")!.AsObject();
        Assert.Equal(RowIdentity.Hash(row), RowIdentity.Hash(other));
    }
}

public class SyncUploadTests
{
    private static JsonObject Row(string id, int v) => new() { ["id"] = id, ["v"] = v };

    [Fact]
    public async Task UpsertsByRowKeyAndDedupsKeylessRows()
    {
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        var target = new HttpUploadTarget(http, stub.Authorize);
        var t = SyncTable.Parse("Catalog_X");
        List<JsonObject> Stamped(params JsonObject[] rows) { foreach (var r in rows) SyncEngine.Stamp(t, r); return rows.ToList(); }

        var first = await target.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["Catalog_X"] = Stamped(Row("a", 1), Row("b", 2)) }, default);
        Assert.Equal(2, first.Inserted);
        var again = await target.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["Catalog_X"] = Stamped(Row("a", 1), Row("b", 3)) }, default);
        Assert.Equal((0, 1, 1), (again.Inserted, again.Updated, again.Skipped));
        Assert.Equal(2, stub.Count("c1", "Catalog_X"));
        Assert.Equal(3, stub.Row("c1", "Catalog_X", "b")!["v"]!.GetValue<int>());
        Assert.Null(stub.Row("c1", "Catalog_X", "b")!["__rowKey"]);                    // popped, as the backend does

        var info = SyncTable.Parse("InformationRegister_Y");
        var plain = new JsonObject { ["Период"] = "2026-01-01T00:00:00", ["x"] = 1 };
        SyncEngine.Stamp(info, plain);
        await target.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["InformationRegister_Y"] = new() { plain } }, default);
        var dup = await target.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["InformationRegister_Y"] = new() { (JsonObject)plain.DeepClone() } }, default);
        Assert.Equal(1, dup.Skipped);
        Assert.Equal(1, stub.Count("c1", "InformationRegister_Y"));
    }

    [Fact]
    public async Task ChunksAreCutBySizeAndA413SplitsAChunk()
    {
        await using var stub = new StubBackend();
        await stub.StartAsync();
        var handler = new TooLargeAbove(maxRows: 3) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(handler) { BaseAddress = stub.BaseAddress };
        var target = new HttpUploadTarget(http, stub.Authorize) { ChunkBytes = 400, RetryDelays = Array.Empty<TimeSpan>() };
        var rows = Enumerable.Range(0, 20).Select(i => Row("id" + i, i)).ToList();
        var r = await target.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["Catalog_X"] = rows }, default);
        Assert.Equal(20, r.Inserted);
        Assert.Equal(20, stub.Count("c1", "Catalog_X"));
        Assert.True(handler.Refused > 0, "no chunk was large enough to be refused");
        Assert.All(stub.Uploads, u => Assert.True(u.Rows <= 3));
    }

    [Fact]
    public async Task UploadsNeedAUserTokenAndPruneHasTheBackendsGuard()
    {
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        var secret = new HttpUploadTarget(http, r => r.Headers.Add("X-Service-Secret", "s")) { RetryDelays = Array.Empty<TimeSpan>() };
        var e = await Assert.ThrowsAsync<UploadException>(() =>
            secret.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["Catalog_X"] = new() { Row("a", 1) } }, default));
        Assert.Equal(403, e.Status);

        var user = new HttpUploadTarget(http, stub.Authorize);
        var rows = Enumerable.Range(0, 4).Select(i => Row("k" + i, i)).ToList();
        foreach (var r in rows) SyncEngine.Stamp(SyncTable.Parse("Catalog_X"), r);
        await user.UploadAsync("c1", new Dictionary<string, List<JsonObject>> { ["Catalog_X"] = rows }, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<UploadException>(() => user.PruneAsync("c1", "Catalog_X", new[] { "k0", "k1", "k2" }, 1, default))).Status);
        Assert.Equal(1, await user.PruneAsync("c1", "Catalog_X", new[] { "k0" }, 3, default));
        Assert.Equal(3, stub.Count("c1", "Catalog_X"));
    }

    /// <summary>Answers 413 for a chunk with more than <c>maxRows</c> rows, like a proxy body limit.</summary>
    private sealed class TooLargeAbove(int maxRows) : DelegatingHandler
    {
        public int Refused;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is MultipartFormDataContent form)
            {
                var file = form.First(c => c.Headers.ContentDisposition?.Name?.Trim('"') == "file");
                var body = JsonNode.Parse(await file.ReadAsStringAsync(ct))!.AsObject();
                if (body.First().Value!.AsArray().Count > maxRows)
                {
                    Interlocked.Increment(ref Refused);
                    return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge);
                }
            }
            return await base.SendAsync(request, ct);
        }
    }
}

/// <summary>
/// The whole path live on the local KAN copy (server base): hosts → sync engine → local stub
/// backend (D39, nothing leaves the PC). A document written and posted through the writer
/// reaches the stub with its accounting movements through the change feed; deleting it removes
/// both. Test-owned document, AIBA_REWRITE_SYNC marker, deleted at the end.
/// </summary>
[Collection("onec-live")]
public class SyncLiveTests
{
    private const string Doc = "ПоступлениеТоваровУслуг";
    private readonly LiveFixture _f;
    public SyncLiveTests(LiveFixture f) => _f = f;

    [Fact]
    public async Task AWrittenPostedThenDeletedDocumentFlowsThroughTheFeed()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var today = DateTime.Today;
        var docs = SyncTable.Parse("Document_" + Doc, today);
        var reg = SyncTable.Parse("AccountingRegister_Хозрасчетный_RecordType", today);

        using var sup = new OneC.Supervisor.Supervisor(new OneC.Supervisor.SupervisorOptions
        {
            HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), RecycleIdleAboveMb = 0, MonitorInterval = TimeSpan.FromHours(1)
        });
        sup.Start(new[] { _f.Server });
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        var engine = new SyncEngine(new OneC.Supervisor.SupervisorSource(sup), new HttpUploadTarget(http, stub.Authorize));
        string path = Path.Combine(Path.GetTempPath(), $"sync-live-{Guid.NewGuid():N}.json");
        var writes = new OneC.Host.WriteService(_f.Manager!, "AIBA_REWRITE_");
        string marker = "AIBA_REWRITE_SYNC_" + DateTime.Now.ToString("MMddHHmmss");
        string? id = null;
        try
        {
            await engine.RunOnceAsync(b, "o", new[] { docs, reg }, BaseState.Load(path), path);   // cold read: today's rows

            var body = _f.Manager!.Use(b, ctx =>
            {
                var src = OneC.Host.WriteParityScenario.Sources(ctx, Doc, 1, posted: true);
                return OneC.Host.WritePayload.FromRef(ctx, Doc, src[0]);
            });
            body["Date"] = today.AddHours(12).ToString("yyyy-MM-ddTHH:mm:ss");
            body["Комментарий"] = marker + ": sync";
            var created = new OneC.Host.DocumentWriter(writes).Create(b, Doc, body, post: true);
            id = created.Id;
            Assert.True(created.Posted);
            int movements = (await new OneC.Supervisor.SupervisorSource(sup).RegisterByRecorderAsync(b, reg, Doc, id, default)).Count;
            Assert.True(movements > 0, "the posted copy has no accounting movements");

            await PassesUntil(engine, b, docs, reg, path, () => stub.Row("o", docs.Name, id) is not null &&
                                                                 RecorderRows(stub, reg, id) == movements);

            writes.Delete(b, Doc, id);
            string deletedId = id;
            id = null;
            await PassesUntil(engine, b, docs, reg, path, () => stub.Row("o", docs.Name, deletedId) is null &&
                                                                 RecorderRows(stub, reg, deletedId) == 0);
        }
        finally
        {
            if (id is not null) writes.Delete(b, Doc, id);
            File.Delete(path);
        }
    }

    private static int RecorderRows(StubBackend stub, SyncTable reg, string id) =>
        stub.Rows("o", reg.Name).Count(r => r["recorderRef"]?.GetValue<string>() == id);

    /// <summary>Sync passes until the stub shows the change; the server writes its event log with a delay.</summary>
    private static async Task PassesUntil(SyncEngine engine, string b, SyncTable docs, SyncTable reg, string path, Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            await engine.RunOnceAsync(b, "o", new[] { docs, reg }, BaseState.Load(path), path);
            if (done()) return;
            await Task.Delay(3000);
        }
        Assert.Fail("the change did not reach the stub within 3 minutes");
    }
}

public class SyncSchedulerTests
{
    /// <summary>Passes repeat on the interval, never overlap, report per base, and a failing target backs off and says why.</summary>
    [Fact]
    public async Task PassesRepeatAndFailuresAreReported()
    {
        var oneC = new SyncEngineTests.FakeOneC();
        oneC.Catalog["a"] = new JsonObject { ["id"] = "a" };
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        string dir = Path.Combine(Path.GetTempPath(), $"sync-sched-{Guid.NewGuid():N}");
        var cfg = new SyncConfig
        {
            StateDir = dir, IntervalSeconds = 0,
            Bases = new() { new SyncBaseConfig("good", "o1", new[] { "Catalog_X" }), new SyncBaseConfig("bad", "o2", new[] { "Catalog_X" }) }
        };
        var good = new HttpUploadTarget(http, stub.Authorize);
        var bad = new HttpUploadTarget(http, _ => { }) { RetryDelays = Array.Empty<TimeSpan>() };  // no token: 401
        try
        {
            await using (var s1 = new SyncScheduler(new SyncEngine(oneC, good), cfg with { Bases = new() { cfg.Bases[0] } }))
            await using (var s2 = new SyncScheduler(new SyncEngine(oneC, bad), cfg with { Bases = new() { cfg.Bases[1] } }))
            {
                s1.Start();
                s2.Start();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10) &&
                       ((s1.Status().FirstOrDefault()?.Passes ?? 0) < 3 || (s2.Status().FirstOrDefault()?.ConsecutiveFailures ?? 0) < 1))
                    await Task.Delay(50);
                var ok = Assert.Single(s1.Status());
                Assert.True(ok.LastOk && ok.Passes >= 3, $"passes {ok.Passes}, error {ok.LastError}");
                var failed = Assert.Single(s2.Status());
                Assert.False(failed.LastOk);
                Assert.Contains("401", failed.LastError);
            }
            Assert.Equal(1, stub.Count("o1", "Catalog_X"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

/// <summary>The engine against an in-memory 1C: cold read, then the change feed.</summary>
public class SyncEngineTests
{
    internal sealed class FakeOneC : IOneCSource
    {
        public readonly SortedDictionary<string, JsonObject> Catalog = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, JsonObject> Docs = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<JsonObject>> Movements = new(StringComparer.Ordinal);
        public readonly List<ChangeEvent> Pending = new();
        public int Position;

        public Task<long> CountAsync(string b, SyncTable t, CancellationToken ct) =>
            Task.FromResult((long)(t.Kind == TableKind.Catalog ? Catalog.Count : t.Kind == TableKind.Document ? Docs.Count : Movements.Sum(m => m.Value.Count)));

        public Task<List<JsonObject>> CatalogPageAsync(string b, string c, string after, int limit, CancellationToken ct) =>
            Task.FromResult(Catalog.Where(p => string.CompareOrdinal(p.Key, after) > 0).Take(limit).Select(p => (JsonObject)p.Value.DeepClone()).ToList());

        public Task<JsonObject?> CatalogByIdAsync(string b, string c, string id, CancellationToken ct) =>
            Task.FromResult(Catalog.TryGetValue(id, out var r) ? (JsonObject?)r.DeepClone() : null);

        public Task<(List<JsonObject>, string?, int)> DocumentPageAsync(string b, string d, string? cursor, int skip, int limit, DateTime? from, CancellationToken ct)
        {
            var all = Docs.Values.OrderBy(r => r["date"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(r => r["id"]!.GetValue<string>()).ToList();
            var rest = all.Where(r => cursor is null || string.CompareOrdinal(r["date"]!.GetValue<string>(), cursor) >= 0).Skip(skip).Take(limit).ToList();
            string? next = rest.Count > 0 ? rest[^1]["date"]!.GetValue<string>() : null;
            int nextSkip = rest.Count(r => r["date"]!.GetValue<string>() == next) + (next == cursor ? skip : 0);
            return Task.FromResult((rest.Select(r => (JsonObject)r.DeepClone()).ToList(), next, nextSkip));
        }

        public Task<List<JsonObject>> DocumentsByIdsAsync(string b, string d, IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult(ids.Where(Docs.ContainsKey).Select(i => (JsonObject)Docs[i].DeepClone()).ToList());

        public Task<(List<JsonObject>, string?, int, bool)> RegisterPageAsync(string b, SyncTable t, string? cursor, int skip, int limit, CancellationToken ct)
        {
            // One period for every row: the cursor stays on it and the skip grows, as the host's does.
            var all = Movements.Values.SelectMany(m => m).Skip(skip).Take(limit).Select(r => (JsonObject)r.DeepClone()).ToList();
            return Task.FromResult((all, (string?)"2026-09-01T00:00:00", skip + all.Count, all.Count >= limit));
        }

        public Task<List<JsonObject>> RegisterByRecorderAsync(string b, SyncTable t, string doc, string id, CancellationToken ct) =>
            Task.FromResult(Movements.TryGetValue(id, out var m) ? m.Select(r => (JsonObject)r.DeepClone()).ToList() : new List<JsonObject>());

        public Task<ChangeBatch> ChangesAsync(string b, LogCursor? cursor, CancellationToken ct)
        {
            var events = Pending.ToList();
            Pending.Clear();
            Position += events.Count;
            return Task.FromResult(new ChangeBatch(events, LogCursor.Parse($"{Guid.Empty}|1Cv8.lgp|{Position}"), false, null, false, events.Count));
        }

        public void Doc(string id, string date, params int[] lines)
        {
            Docs[id] = new JsonObject { ["id"] = id, ["date"] = date, ["n"] = lines.Length };
            Movements[id] = lines.Select(l => new JsonObject { ["recorderRef"] = id, ["lineNo"] = l, ["Сумма"] = l * 10 }).ToList();
        }
    }

    private static readonly SyncTable Cat = SyncTable.Parse("Catalog_Контрагенты");
    private static readonly SyncTable Doc = SyncTable.Parse("Document_ПоступлениеТоваровУслуг");
    private static readonly SyncTable Reg = SyncTable.Parse("AccountingRegister_Хозрасчетный_RecordType");

    [Fact]
    public async Task ColdReadThenFeedKeepsTheBackendEqualTo1C()
    {
        var oneC = new FakeOneC();
        for (int i = 0; i < 7; i++) oneC.Catalog[$"c{i:00}"] = new JsonObject { ["id"] = $"c{i:00}", ["name"] = "n" + i };
        oneC.Doc("d1", "2026-09-01T10:00:00", 1, 2);
        oneC.Doc("d2", "2026-09-02T10:00:00", 1);

        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        var engine = new SyncEngine(oneC, new HttpUploadTarget(http, stub.Authorize)) { PageSize = 3 };
        string path = Path.Combine(Path.GetTempPath(), $"sync-{Guid.NewGuid():N}.json");
        var state = BaseState.Load(path);
        var tables = new[] { Cat, Doc, Reg };
        try
        {
            await engine.RunOnceAsync("b", "o", tables, state, path);
            Assert.Equal((7, 2, 3), (stub.Count("o", Cat.Name), stub.Count("o", Doc.Name), stub.Count("o", Reg.Name)));
            Assert.All(tables, t => Assert.True(state.Table(t.Name).ColdDone));

            // d1 reposted with one line fewer, d2 deleted, c03 renamed, c04 deleted, d3 new.
            oneC.Doc("d1", "2026-09-01T10:00:00", 1);
            oneC.Docs.Remove("d2"); oneC.Movements.Remove("d2");
            oneC.Catalog["c03"]!["name"] = "renamed";
            oneC.Catalog.Remove("c04");
            oneC.Doc("d3", "2026-09-03T10:00:00", 1, 2, 3);
            oneC.Pending.AddRange(new[]
            {
                new ChangeEvent("t", "Post", Doc.Metadata, "d1"), new ChangeEvent("t", "Delete", Doc.Metadata, "d2"),
                new ChangeEvent("t", "Update", Cat.Metadata, "c03"), new ChangeEvent("t", "Delete", Cat.Metadata, "c04"),
                new ChangeEvent("t", "New", Doc.Metadata, "d3"), new ChangeEvent("t", "Post", Doc.Metadata, "d3")
            });
            var report = await engine.RunOnceAsync("b", "o", tables, BaseState.Load(path), path);

            Assert.Equal(6, report.FeedEvents);
            Assert.Equal(6, stub.Count("o", Cat.Name));
            Assert.Equal("renamed", stub.Row("o", Cat.Name, "c03")!["name"]!.GetValue<string>());
            Assert.Equal(new[] { "d1", "d3" }, stub.Rows("o", Doc.Name).Select(r => r["id"]!.GetValue<string>()).Order());
            Assert.Equal(new[] { "d1#1", "d3#1", "d3#2", "d3#3" },
                         stub.Rows("o", Reg.Name).Select(r => $"{r["recorderRef"]}#{r["lineNo"]}").Order());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AResumedColdReadStartsWhereTheLastAcceptedPageEnded()
    {
        var oneC = new FakeOneC();
        for (int i = 0; i < 10; i++) oneC.Catalog[$"c{i:00}"] = new JsonObject { ["id"] = $"c{i:00}" };
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress };
        string path = Path.Combine(Path.GetTempPath(), $"sync-{Guid.NewGuid():N}.json");
        try
        {
            var state = BaseState.Load(path);
            state.FeedCursor = $"{Guid.Empty}|1Cv8.lgp|0";
            state.Table(Cat.Name).After = "c05";                          // a previous run got this far
            state.Save(path);
            await new SyncEngine(oneC, new HttpUploadTarget(http, stub.Authorize)) { PageSize = 4 }
                .RunOnceAsync("b", "o", new[] { Cat }, BaseState.Load(path), path);
            Assert.Equal(new[] { "c06", "c07", "c08", "c09" }, stub.Rows("o", Cat.Name).Select(r => r["id"]!.GetValue<string>()).Order());
        }
        finally { File.Delete(path); }
    }
}
