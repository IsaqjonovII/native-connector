using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OneC.Host;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>Milestone 5.4 — register reads, without 1C.</summary>
public class RegisterUnitTests
{
    [Fact]
    public void AccountingSelectAddsTheAccountCodes()
    {
        var s = new RegisterSchema(RegisterKind.Accounting, "Хозрасчетный", "РегистрБухгалтерии.Хозрасчетный",
            "РегистрБухгалтерии.Хозрасчетный.ДвиженияССубконто", true,
            new[] { AttributeShape.Primitive("Период"), new AttributeShape("СчетДт", true, true, false, false, false) });
        string sql = RegisterReadService.Select(s, 250, s.Source + "(&windowEdge, &windowStart)", null, "Период УБЫВ");
        Assert.Equal("ВЫБРАТЬ ПЕРВЫЕ 250 Т.Период КАК c0, Т.СчетДт КАК c1, Т.СчетДт.Наименование КАК c1n, Т.СчетДт.Код КАК c1c, Т.СчетДт.Код КАК c1k " +
                     "ИЗ РегистрБухгалтерии.Хозрасчетный.ДвиженияССубконто(&windowEdge, &windowStart) КАК Т УПОРЯДОЧИТЬ ПО Период УБЫВ", sql);
    }

    [Fact]
    public void EdgeQueryStringMapsLikeTheOldRegisterRoute()
    {
        var a = EdgeServer.RegisterArgs("info", "КурсыВалют", new QueryCollection(new Dictionary<string, StringValues>
        {
            ["limit"] = "250", ["cursorDate"] = "2024-01-01T00:00:00", ["syncOrder"] = "asc", ["skipTotal"] = "1", ["to"] = "2024-02-01"
        }));
        Assert.Equal("info", a["kind"]!.GetValue<string>());
        Assert.Equal("asc", a["order"]!.GetValue<string>());
        Assert.True(a["skipTotal"]!.GetValue<bool>());
        Assert.Equal("2024-02-01", a["to"]!.GetValue<string>());
    }
}

[Collection("onec-live")]
public class RegisterLiveTests
{
    private readonly LiveFixture _f;
    public RegisterLiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private RegisterReadService Svc => new(M);

    private bool Exists(string b, RegisterKind kind, string name)
    {
        try { M.Use(b, ctx => RegisterSchemas.Get(ctx, kind, name)); return true; }
        catch (OneCException e) when (e.Message.Contains("not found")) { return false; }
    }

    /// <summary>Parity with the literal old reads — accounting row by row, the others as sets (see RegisterParityScenario).</summary>
    [Fact]
    public void RowsMatchTheOldAdapterAlgorithm()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var probe = new (RegisterKind Kind, string Name)[]
        {
            (RegisterKind.Accounting, "Хозрасчетный"),
            (RegisterKind.Accumulation, "ТоварыОрганизаций"), (RegisterKind.Accumulation, "Продажи"),
            (RegisterKind.Accumulation, "РозничнаяВыручка"), (RegisterKind.Accumulation, "ВзаиморасчетыСКонтрагентами"),
            (RegisterKind.Information, "КурсыВалют"), (RegisterKind.Information, "ДокументыФизическихЛиц"),
            (RegisterKind.Information, "ЦеныНоменклатуры")
        };
        int compared = 0, rows = 0;
        foreach (var (kind, name) in probe)
        {
            if (!Exists(b, kind, name)) continue;
            foreach (var q in new[]
            {
                new RegisterQuery { Kind = kind, Register = name, Limit = 60, SkipTotal = true },
                new RegisterQuery { Kind = kind, Register = name, Limit = 60, SkipTotal = true, Order = "asc", CursorDate = new DateTime(2021, 1, 1), Offset = 3 }
            })
            {
                var fast = Svc.List(b, q);
                var old = M.Use(b, ctx => LegacyRegisterOracle.Read(ctx, q));
                if (RegisterParityScenario.Compare(kind, q, old, fast) is { } d) Assert.Fail($"{kind} {name} {q.Order}: {d}");
                rows += fast.Rows.Count;
            }
            compared++;
        }
        Assert.True(compared >= 3, $"only {compared} probe registers exist");
        Assert.True(rows >= 150, $"only {rows} rows compared");
    }

    /// <summary>
    /// The cursor walk (next_cursor_date + next_cursor_skip) over a slice of Хозрасчетный returns
    /// exactly the rows of one unpaged read of that slice, in order, none twice — with a page
    /// small enough that pages end inside a document's movements.
    /// </summary>
    [Fact]
    public void AccountingCursorWalkIsLossless()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        if (!Exists(b, RegisterKind.Accounting, "Хозрасчетный")) return;
        var (from, to) = Slice(b, RegisterKind.Accounting, "Хозрасчетный");

        var whole = Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 100_000, Order = "asc", CursorDate = from, To = to, SkipTotal = true });
        var walked = Walk(b, RegisterKind.Accounting, "Хозрасчетный", from, to, 37);
        Assert.True(whole.Rows.Count >= 100, $"slice too small ({whole.Rows.Count})");
        Assert.Equal(whole.Rows.Select(Key), walked.Select(Key));
    }

    /// <summary>Accumulation registers now page stably too: Период alone gave no order inside a second.</summary>
    [Fact]
    public void AccumulationCursorWalkIsLossless()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        // The first accumulation register with recorders and a few thousand movements.
        string? name = M.Use(b, ctx => RegisterParityScenario.Names(ctx, RegisterKind.Accumulation))
            .FirstOrDefault(n => M.Use(b, ctx => RegisterSchemas.Get(ctx, RegisterKind.Accumulation, n)).Recorded &&
                                 Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accumulation, Register = n, Limit = 0 }).TotalCount >= 2500);
        Assert.NotNull(name);
        var (from, to) = Slice(b, RegisterKind.Accumulation, name);

        var whole = Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accumulation, Register = name, Limit = 100_000, Order = "asc", CursorDate = from, To = to, SkipTotal = true });
        var walked = Walk(b, RegisterKind.Accumulation, name, from, to, 23);
        Assert.True(whole.Rows.Count >= 50, $"slice too small ({whole.Rows.Count})");
        Assert.Equal(whole.Rows.Select(r => System.Text.Json.JsonSerializer.Serialize(r)), walked.Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
    }

    [Fact]
    public void CountRulesAndErrors()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var count = Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 0 });
        Assert.True(count.TotalCount > 0);
        Assert.Empty(count.Rows);
        Assert.Equal(-1, Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 1, SkipTotal = true }).TotalCount);

        var nf = Assert.Throws<OneCException>(() => Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accumulation, Register = "НетТакогоРегистра" }));
        Assert.Equal(ErrorKinds.NotFound, Operations.FromOneC(nf).Kind);
        Assert.Throws<ArgumentException>(() => Svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Order = "up" }));
    }

    [Fact]
    public void RepeatedRegisterReadsDoNotLeak()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var q = new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 200, SkipTotal = true };
        Svc.List(b, q);
        int live = ComRef.Live, wrong = ComRef.WrongThreadReleases;
        for (int i = 0; i < 5; i++) Svc.List(b, q);
        Assert.True(ComRef.Live <= live, $"live refs {live} -> {ComRef.Live}");
        Assert.Equal(wrong, ComRef.WrongThreadReleases);
    }

    [Fact]
    public async Task RegisterRoutesWorkThroughTheEdge()
    {
        if (!_f.Available || _f.Server is null) return;
        using var s = new OneC.Supervisor.Supervisor(new SupervisorOptions
        {
            HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1)
        });
        s.Start(new[] { _f.Server });
        await using var edge = new EdgeServer(s, 0);                // OS-picked free port
        await edge.StartAsync();
        int port = edge.Port;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        string root = $"/v1/bases/{Uri.EscapeDataString(_f.Server.Name)}/registers/";

        var page = JsonNode.Parse(await http.GetStringAsync(root + Uri.EscapeDataString("Хозрасчетный") + "?limit=5&skipTotal=1"))!;
        Assert.Equal(5, page["rows"]!.AsArray().Count);
        Assert.True(page["hasMore"]!.GetValue<bool>());
        Assert.NotNull(page["rows"]![0]!["recorderRef"]);

        Assert.Equal(400, (int)(await http.GetAsync(root + "sideways/" + Uri.EscapeDataString("Хозрасчетный"))).StatusCode);
        Assert.Equal(404, (int)(await http.GetAsync(root + "accumulation/NoSuchRegister")).StatusCode);
        Assert.Equal(400, (int)(await http.GetAsync(root + Uri.EscapeDataString("Хозрасчетный") + "?cursorDate=later")).StatusCode);
    }

    private (DateTime From, DateTime? To) Slice(string b, RegisterKind kind, string name)
    {
        // From the 800th newest movement to the end: ~800 rows however the dates cluster
        // (a fixed number of days before it was empty for a register booked on a few dates).
        var anchor = Svc.List(b, new RegisterQuery { Kind = kind, Register = name, Limit = 1, Offset = 800, SkipTotal = true });
        Assert.NotEmpty(anchor.Rows);
        return (DateTime.Parse((string)anchor.Rows[0]["Период"]!), null);
    }

    private List<Dictionary<string, object?>> Walk(string b, RegisterKind kind, string name, DateTime from, DateTime? to, int page)
    {
        var all = new List<Dictionary<string, object?>>();
        DateTime cursor = from; int skip = 0;
        for (int i = 0; i < 100_000; i++)
        {
            var p = Svc.List(b, new RegisterQuery { Kind = kind, Register = name, Limit = page, Order = "asc", CursorDate = cursor, Offset = skip, To = to, SkipTotal = true });
            all.AddRange(p.Rows);
            if (!p.HasMore || p.NextCursorDate is null) break;
            cursor = DateTime.Parse(p.NextCursorDate);
            skip = p.NextCursorSkip;
        }
        return all;
    }

    private static string Key(Dictionary<string, object?> r) => $"{r["recorderRef"]}#{r["lineNo"]}";
}
