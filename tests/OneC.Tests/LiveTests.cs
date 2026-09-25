using System.Text.Json;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Live 1C tests. Skipped unless ONEC_TEST_BASES points at a bases json, because they need
/// real infobases and credentials that never live in the repo. One fixture for the whole
/// collection: a process can bind exactly one comcntr, so these cannot run in parallel.
/// </summary>
public sealed class LiveFixture : IDisposable
{
    public bool Available { get; }
    public string Skip { get; } = "";
    public SessionManager? Manager { get; }
    public List<OneCBase> Bases { get; } = new();
    public OneCBase? Server { get; }
    public OneCBase? File { get; }

    public LiveFixture()
    {
        string? path = Environment.GetEnvironmentVariable("ONEC_TEST_BASES");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            Skip = "set ONEC_TEST_BASES to a bases json to run live 1C tests";
            return;
        }

        Bases = JsonSerializer.Deserialize<List<OneCBase>>(System.IO.File.ReadAllText(path))!;
        Server = Bases.FirstOrDefault(b => !b.IsFile);
        File = Bases.FirstOrDefault(b => b.IsFile);

        var installs = PlatformCatalog.Discover();
        var pick = PlatformCatalog.Select(installs, Bases[0].PlatformVersion, Bases[0].IsFile);
        if (pick is null) { Skip = "no matching 1C install"; return; }

        Manager = new SessionManager(pick.ComcntrPath, new PoolOptions
        {
            GlobalMaxSessions = 4,
            PerBaseMaxSessions = 2,
            IdleTimeout = TimeSpan.FromSeconds(3),
            SweepInterval = TimeSpan.FromSeconds(1)
        });
        foreach (var b in Bases) Manager.Register(b);
        Available = true;
    }

    public void Dispose() => Manager?.Dispose();
}

[CollectionDefinition("onec-live", DisableParallelization = true)]
public class LiveCollection : ICollectionFixture<LiveFixture> { }

[Collection("onec-live")]
public class LiveTests
{
    private readonly LiveFixture _f;
    public LiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private string AnyBase => (_f.Server ?? _f.File!)!.Name;

    private bool Skip(out string why)
    {
        why = _f.Skip;
        return !_f.Available;
    }

    [Fact]
    public void ReadsRealRows()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var r = svc.Read(AnyBase, new ReadQuery
        {
            Entity = "Справочник.Номенклатура",
            Fields = new[] { "Наименование", "Код" },
            Limit = 5
        });
        Assert.NotEmpty(r.Rows);
        Assert.True(r.Rows.Count <= 5);
        Assert.All(r.Rows, row => Assert.True(row.ContainsKey("Наименование")));
    }

    [Fact]
    public void UnknownTableGivesARuntimeErrorWithText()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var ex = Assert.Throws<OneCException>(() => svc.Read(AnyBase, new ReadQuery
        { Entity = "Документ.НетТакогоДокумента", Fields = new[] { "Ссылка" }, Limit = 1 }));

        Assert.Equal(OneCLayer.Runtime, ex.Layer);
        Assert.Equal(1001, ex.OneCCode);                 // 1C runtime errors carry wCode 1001
        Assert.Equal(0, ex.SCode);                       // and scode 0 — what breaks C# dynamic
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("empty EXCEPINFO", ex.Message);
        Assert.False(ex.IsRetryable);
        Assert.False(ex.IsSessionFatal);
    }

    [Fact]
    public void UnknownBaseIsAHostError()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var ex = Assert.Throws<OneCException>(() => svc.Read("definitely-not-registered",
            new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Ссылка" }, Limit = 1 }));
        Assert.Equal(OneCLayer.Host, ex.Layer);
    }

    [Fact]
    public void BadCredentialsAreAConnectorErrorWithFullText()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var b = _f.Bases[0];
        var bad = b with
        {
            Name = "live-bad-creds",
            ConnectionString = System.Text.RegularExpressions.Regex.Replace(
                b.ConnectionString, "Pwd=[^;]*", "Pwd=definitely-wrong")
        };
        M.Register(bad);
        var svc = new ReadService(M);
        var ex = Assert.Throws<OneCException>(() => svc.Read("live-bad-creds",
            new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Ссылка" }, Limit = 1 }));

        Assert.Equal(OneCLayer.Connector, ex.Layer);
        Assert.Equal(Hr.E_FAIL, ex.SCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        // The wrapper keeps the second sentence that C# dynamic drops.
        Assert.True(ex.Message.Length > 20, ex.Message);
    }

    [Fact]
    public void NothingIsCreatedUntilABaseIsUsed()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        using var lazy = new SessionManager(M.ComcntrPath, new PoolOptions { GlobalMaxSessions = 4 });
        foreach (var b in _f.Bases) lazy.Register(b);
        for (int i = 0; i < 20; i++) lazy.Register(_f.Bases[0] with { Name = $"ghost-{i}" });

        Assert.Empty(lazy.Stats());
        Assert.Equal(0, lazy.BudgetUsed);
    }

    [Fact]
    public void PoolStopsAtThePerBaseCeiling()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var q = new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 10 };

        // Exceptions on raw threads kill the whole test process ("Test Run Aborted", seen
        // 2026-09-23); capture them and assert on the test thread instead.
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
        {
            try { svc.Read(AnyBase, q); } catch (Exception e) { errors.Add(e); }
        })).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Assert.Empty(errors);

        var stats = M.Stats().Single(s => s.BaseName == AnyBase);
        Assert.True(stats.Live <= 2, $"live={stats.Live} exceeded the per-base cap of 2");
        Assert.True(M.BudgetUsed <= 4);
    }

    [Fact]
    public void IdleSessionsAreRetiredAndTheBudgetComesBack()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        svc.Read(AnyBase, new ReadQuery
        { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 1 });

        Assert.True(M.BudgetUsed >= 1);

        // Assert the end state, not who did the retiring: the background sweeper runs on
        // its own timer and may well get there first.
        Thread.Sleep(4000);
        M.SweepIdle();
        Assert.Equal(0, M.BudgetUsed);
        Assert.All(M.Stats(), s => Assert.Equal(0, s.Live));
    }

    [Fact]
    public void RepeatedReadsDoNotLeakComObjects()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var q = new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование", "Код" }, Limit = 25 };

        svc.Read(AnyBase, q);
        int liveAfterFirst = ComRef.Live;
        long createdAfterFirst = ComRef.Created;
        // Deltas, not absolutes: the counters are process-wide and a unit test elsewhere
        // deliberately provokes a wrong-thread release.
        int wrongThreadBefore = ComRef.WrongThreadReleases;
        int failuresBefore = ComRef.ReleaseFailures;

        for (int i = 0; i < 20; i++) svc.Read(AnyBase, q);
        long createdPerRead = (ComRef.Created - createdAfterFirst) / 20;

        // Live refs may only fall (the idle sweeper can retire a session mid-test); they
        // must never accumulate, and each read must cost a fixed, small number of refs.
        Assert.True(ComRef.Live <= liveAfterFirst,
                    $"live grew from {liveAfterFirst} to {ComRef.Live} over 20 reads");
        Assert.InRange(createdPerRead, 1, 100);
        Assert.Equal(wrongThreadBefore, ComRef.WrongThreadReleases);
        Assert.Equal(failuresBefore, ComRef.ReleaseFailures);
    }

    [Fact]
    public void SessionsReportTheRightPlatformVersion()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        string? reported = M.Use(AnyBase, ctx => ctx.Error.PlatformVersion);
        Assert.False(string.IsNullOrWhiteSpace(reported));
        Assert.StartsWith(_f.Bases[0].PlatformVersion.Split('.')[0], reported);
        Assert.Single(ComActivator.LoadedComcntrModules());
    }

    [Fact]
    public void OneCObjectsCarryNoTypeInformation()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        M.Use(AnyBase, ctx =>
        {
            using var scope = new ComScope();
            Assert.Equal(0, Dispatch.TypeInfoCount(ctx.Connection));
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Assert.Equal(0, Dispatch.TypeInfoCount(q));
            return 0;
        });
    }

    /// <summary>
    /// Parity with the old behaviour: the same query driven by C# <c>dynamic</c> — the way
    /// the research harness and the oscript adapter both reach 1C — must return exactly the
    /// same rows as the new IDispatch read path. This is the only place dynamic is allowed.
    /// </summary>
    [Fact]
    public void DispatchPathReturnsTheSameRowsAsDynamic()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        const string Sql = "ВЫБРАТЬ ПЕРВЫЕ 25 Наименование, Код ИЗ Справочник.Номенклатура " +
                           "УПОРЯДОЧИТЬ ПО Код";

        var viaDispatch = new ReadService(M).Read(AnyBase, new ReadQuery
        {
            Entity = "Справочник.Номенклатура",
            Fields = new[] { "Наименование", "Код" },
            Limit = 25,
            OrderBy = "Код"
        }).Rows.Select(r => $"{r["Код"]}|{r["Наименование"]}").ToList();

        var viaDynamic = M.Use(AnyBase, ctx =>
        {
            var outRows = new List<string>();
            dynamic conn = ctx.Connection;
            dynamic q = conn.NewObject("Запрос");
            q.Текст = Sql;
            dynamic res = q.Выполнить();
            dynamic sel = res.Выбрать();
            while ((bool)sel.Следующий())
                outRows.Add($"{sel.Код}|{sel.Наименование}");
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sel);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(res);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(q);
            return outRows;
        });

        Assert.Equal(viaDynamic, viaDispatch);
        Assert.NotEmpty(viaDispatch);
    }

    /// <summary>
    /// Reference columns (milestone 4.0 regression): the old path rendered a document
    /// reference as its bare number because Строка() does not exist on the COM connection.
    /// Text must be 1C's full presentation, guid must be a GUID, both must carry both.
    /// </summary>
    [Fact]
    public void ReferenceColumnsComeBackAsPresentationGuidOrBoth()
    {
        if (!_f.Available || _f.Server is null) return;
        var svc = new ReadService(M);
        ReadQuery Q(RefMode m) => new()
        {
            Entity = "РегистрБухгалтерии.Хозрасчетный", Fields = new[] { "Регистратор", "Сумма" },
            Limit = 20, OrderBy = "Период", Refs = m
        };

        var text = svc.Read(_f.Server.Name, Q(RefMode.Text)).Rows;
        var guid = svc.Read(_f.Server.Name, Q(RefMode.Guid)).Rows;
        var both = svc.Read(_f.Server.Name, Q(RefMode.Both)).Rows;

        Assert.Equal(text.Count, guid.Count);
        for (int i = 0; i < text.Count; i++)
        {
            var t = Assert.IsType<string>(text[i]["Регистратор"]);
            Assert.Contains(' ', t);                                  // "Поступление … 0000 dated …", not "00000000106"
            Assert.True(Guid.TryParse(Assert.IsType<string>(guid[i]["Регистратор"]), out _));
            var b = Assert.IsType<Dictionary<string, string?>>(both[i]["Регистратор"]);
            Assert.Equal(t, b["text"]);
            Assert.Equal(guid[i]["Регистратор"], b["ref"]);
            // Scalars are untouched by the mode. 1C picks the VARIANT type per value: whole
            // amounts arrive as int, others as decimal/double — all numbers in JSON.
            Assert.True(text[i]["Сумма"] is int or long or decimal or double,
                        $"Сумма came back as {text[i]["Сумма"]?.GetType().Name}");
        }
    }

    [Fact]
    public void HostSurvivesABrokenBaseAndKeepsServingOthers()
    {
        if (Skip(out var why)) { Assert.True(true, why); return; }
        var svc = new ReadService(M);
        var broken = _f.Bases[0] with
        {
            Name = "live-broken",
            ConnectionString = "File=\"D:\\1C\\definitely-not-here\";"
        };
        M.Register(broken);

        Assert.Throws<OneCException>(() => svc.Read("live-broken",
            new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Ссылка" }, Limit = 1 }));

        var ok = svc.Read(AnyBase, new ReadQuery
        { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 3 });
        Assert.NotEmpty(ok.Rows);
    }
}
