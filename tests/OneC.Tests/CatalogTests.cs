using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OneC.Host;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>Milestone 5.2 — catalog reads, without 1C.</summary>
public class CatalogUnitTests
{
    private static readonly CatalogSchema Shop = new(
        "Номенклатура", HasCode: true, HasName: true, Hierarchical: true, HasFolders: true, HasOwner: false,
        OrgAttribute: null,
        Attributes: new[]
        {
            AttributeShape.Primitive("Артикул"),
            new AttributeShape("ЕдиницаИзмерения", true, true, false, false, false),
            new AttributeShape("Комментарий", false, false, false, true, false)
        },
        Tabular: Array.Empty<TabularShape>());

    [Fact]
    public void KeysetPageQueryAlwaysHasTheSeekCondition()
    {
        var sql = CatalogReadService.BuildSelect(Shop, Shop.Attributes, 250, "Ссылка > &after", "Ссылка");
        Assert.StartsWith("ВЫБРАТЬ ПЕРВЫЕ 250 Т.Ссылка КАК id, Т.ПометкаУдаления КАК dm, Т.Код КАК code, Т.Наименование КАК name, " +
                          "Т.Родитель КАК p, ВЫБОР КОГДА Т.Родитель = ЗНАЧЕНИЕ(Справочник.Номенклатура.ПустаяСсылка) ТОГДА ЛОЖЬ " +
                          "ИНАЧЕ Т.Родитель.Наименование КОНЕЦ КАК pn, Т.Родитель.Код КАК pc, Т.ЭтоГруппа КАК g, ", sql);
        Assert.Contains("Т.ЕдиницаИзмерения КАК a1, Т.ЕдиницаИзмерения.Наименование КАК a1n, Т.ЕдиницаИзмерения.Код КАК a1c", sql);
        Assert.EndsWith(" ИЗ Справочник.Номенклатура КАК Т ГДЕ Ссылка > &after УПОРЯДОЧИТЬ ПО Ссылка", sql);
        Assert.DoesNotContain("Владелец", sql);
    }

    [Fact]
    public void ProjectionKeepsKnownAttributesInOrderAndReportsTheRest()
    {
        var org = Shop with { OrgAttribute = "Организация", Attributes = Shop.Attributes.Append(AttributeShape.Primitive("Организация")).ToArray() };
        var (attrs, ignored) = CatalogReadService.Resolve(org, new[] { "Комментарий", " Артикул", "Код", "Комментарий", "Нет" });
        Assert.Equal(new[] { "Комментарий", "Артикул", "Организация" }, attrs.Select(a => a.Name));   // org always read
        Assert.Equal(new[] { "Код", "Нет" }, ignored);

        var (none, _) = CatalogReadService.Resolve(Shop, Array.Empty<string>());
        Assert.Empty(none);
    }

    [Fact]
    public void FiltersBecomeParametersAndOwnerInnIsServerSide()
    {
        var terms = CatalogReadService.FilterTerms(new Dictionary<string, object?> { ["ИНН"] = "123", ["_ownerInn"] = "456" });
        Assert.Equal(new[] { "ИНН = &f0", "Владелец.ИНН = &f1" }, terms.Select(t => t.Term));
        Assert.Throws<ArgumentException>(() => CatalogReadService.FilterTerms(new Dictionary<string, object?> { ["x; ВЫБРАТЬ"] = 1 }));
    }

    [Theory]
    [InlineData("ГоловнаяОрганизация,Организация", "Организация")]
    [InlineData("УдалитьОрганизация,ГоловнаяОрганизация", "ГоловнаяОрганизация")]
    [InlineData("УдалитьОрганизация", "УдалитьОрганизация")]
    [InlineData("Контрагент", null)]
    public void OrganisationAttributeFollowsTheOldPreference(string names, string? expected)
        => Assert.Equal(expected, MetadataShapes.OrgAttribute(names.Split(',')));

    [Fact]
    public void ScalarsAreRenderedLikeTheOldAdapter()
    {
        Assert.Equal("", LegacyValue.Scalar(DateTime.MinValue));                                  // oscript: Формат(Дата(1,1,1)) = ""
        Assert.Equal("2020-03-04T05:06:07", LegacyValue.Scalar(new DateTime(2020, 3, 4, 5, 6, 7)));
        // Both seen against the live old adapter on KAN, 2026-09-25: the empty date arrives over
        // COM as 0100-01-01 and was sent as ""; 1C NULL was sent as "" (XMLСтрока(Null)).
        Assert.Equal("", LegacyValue.Scalar(new DateTime(100, 1, 1)));
        Assert.False(LegacyValue.Filled(new DateTime(100, 1, 1)));
        Assert.Equal("", LegacyValue.Scalar(DBNull.Value));
        Assert.Null(LegacyValue.Scalar(null));                                                   // Неопределено
        Assert.Equal(12.5m, LegacyValue.Scalar(12.5m));
        Assert.False(LegacyValue.Filled("   "));                                                 // oscript: ЗначениеЗаполнено("   ") = False
        Assert.False(LegacyValue.Filled(0m));
        Assert.True(LegacyValue.Filled("0"));
        Assert.Equal("26004/0025308", LegacyValue.CodeText("26004/0025308             "));
        Assert.Equal("1234567", LegacyValue.CodeText(1234567m));
    }

    /// <summary>The parity comparator bites: values, key order and row counts all count.</summary>
    [Fact]
    public void ParityDiffCatchesValueOrderAndCountDifferences()
    {
        static List<Dictionary<string, object?>> Rows(params (string K, object? V)[][] rows) =>
            rows.Select(r => r.ToDictionary(p => p.K, p => p.V)).ToList();

        var a = Rows(new[] { ("id", (object?)"1"), ("name", "X") });
        Assert.Null(LegacyCatalogOracle.Diff(a, Rows(new[] { ("id", (object?)"1"), ("name", "X") })));
        Assert.Contains("name", LegacyCatalogOracle.Diff(a, Rows(new[] { ("id", (object?)"1"), ("name", "Y") })));
        Assert.Contains("key order", LegacyCatalogOracle.Diff(a, Rows(new[] { ("name", (object?)"X"), ("id", "1") })));
        Assert.Contains("rows", LegacyCatalogOracle.Diff(a, new List<Dictionary<string, object?>>()));

        // Old tabular lines arrive in storage order; they are compared by line number.
        Dictionary<string, object?> Line(decimal n) => new() { ["lineNumber"] = n };
        var old = new List<Dictionary<string, object?>> { new() { ["id"] = "1", ["tabularSections"] =
            new Dictionary<string, object?> { ["T"] = new List<Dictionary<string, object?>> { Line(2), Line(1) } } } };
        var fast = new List<Dictionary<string, object?>> { new() { ["id"] = "1", ["tabularSections"] =
            new Dictionary<string, object?> { ["T"] = new List<Dictionary<string, object?>> { Line(1), Line(2) } } } };
        Assert.Null(LegacyCatalogOracle.Diff(old, fast));
    }

    [Fact]
    public void EdgeQueryStringMapsLikeTheOldAdapter()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["limit"] = "250", ["after"] = LegacyValue.EmptyGuid, ["skipTotal"] = "да", ["fields"] = "ИНН, КПП",
            ["syncOrder"] = "asc", ["tabular"] = "false", ["ИНН"] = "301234567", ["st"] = "x"
        });
        var a = EdgeServer.CatalogArgs("Контрагенты", q);
        Assert.Equal(250, a["limit"]!.GetValue<int>());
        Assert.True(a["skipTotal"]!.GetValue<bool>());
        Assert.Equal(new[] { "ИНН", "КПП" }, a["fields"]!.AsArray().Select(n => n!.GetValue<string>()));
        var filters = a["filters"]!.AsObject();
        Assert.Equal(new[] { "ИНН", "st" }, filters.Select(p => p.Key));      // control words excluded exactly

        var dash = EdgeServer.CatalogArgs("X", new QueryCollection(new Dictionary<string, StringValues> { ["fields"] = "-" }));
        Assert.Empty(dash["fields"]!.AsArray());
    }
}

[Collection("onec-live")]
public class CatalogLiveTests
{
    /// <summary>Catalogs worth covering; each base gets the ones it has.</summary>
    private static readonly string[] Probe =
    {
        "Номенклатура", "Контрагенты", "ДоговорыКонтрагентов", "Организации", "ПодразделенияОрганизаций",
        "Валюты", "БанковскиеСчета", "Склады", "НомераГТД", "ФизическиеЛица", "Банки", "СтатьиДвиженияДенежныхСредств",
        // Elements with tabular rows on KAN (found by `OneC.Host catparity`).
        "ГрафикиРаботы", "СпецификацииНоменклатуры", "ГруппыПользователей"
    };

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly LiveFixture _f;
    public CatalogLiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private CatalogReadService Svc => new(M);
    private IEnumerable<OneCBase> Bases => new[] { _f.Server, _f.File }.OfType<OneCBase>();

    private CatalogSchema? SchemaOf(string baseName, string catalog)
    {
        try { return M.Use(baseName, ctx => CatalogSchemas.Get(ctx, catalog)); }
        catch (OneCException e) when (e.Message.Contains("not found")) { return null; }
    }

    /// <summary>
    /// The parity proof: the fast path (query-side dereference, one call per leftover value)
    /// returns byte-identical JSON to the literal old algorithm (one call per step per value,
    /// ВЫБРАТЬ * for tabular sections) — first page and second page, on both bases.
    /// </summary>
    [Fact]
    public void RowsMatchTheOldAdapterAlgorithm()
    {
        if (!_f.Available) return;
        foreach (var b in Bases)
        {
            int compared = 0, rows = 0, lines = 0;
            foreach (var cat in Probe)
            {
                var schema = SchemaOf(b.Name, cat);
                if (schema is null) continue;

                string after = LegacyValue.EmptyGuid;
                for (int page = 0; page < 2; page++)
                {
                    var fast = Svc.List(b.Name, new CatalogQuery { Catalog = cat, Limit = 40, After = after, SkipTotal = true });
                    var old = M.Use(b.Name, ctx => LegacyCatalogOracle.Page(ctx, schema, after, 40));
                    AssertSameRows($"{b.Name}/{cat} page {page}", old, fast.Rows);
                    rows += fast.Rows.Count;
                    lines += fast.Rows.Count(r => r.ContainsKey("tabularSections"));
                    if (fast.Next is null) break;
                    after = fast.Next;
                }
                compared++;
            }
            // The comparison must have had something to compare.
            Assert.True(compared >= 3, $"{b.Name}: only {compared} probe catalogs exist");
            Assert.True(rows >= 100, $"{b.Name}: only {rows} rows compared");
            Assert.True(lines > 0, $"{b.Name}: no element with tabular sections was compared");
        }
    }

    [Fact]
    public void KeysetWalkVisitsEveryElementOnceInRefOrder()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        foreach (var cat in Probe)
        {
            if (SchemaOf(b.Name, cat) is null) continue;
            var all = Svc.List(b.Name, new CatalogQuery { Catalog = cat, Limit = 5000, Fields = Array.Empty<string>(), After = LegacyValue.EmptyGuid });
            if (all.Rows.Count is < 7 or >= 5000) continue;

            var walked = new List<string>();
            string? after = LegacyValue.EmptyGuid;
            int pages = 0;
            while (after is not null)
            {
                var p = Svc.List(b.Name, new CatalogQuery { Catalog = cat, Limit = 3, Fields = Array.Empty<string>(), After = after, SkipTotal = true });
                walked.AddRange(p.Rows.Select(r => (string)r["id"]!));
                after = p.Next;
                Assert.True(++pages < 5000, "walk did not end");
            }
            Assert.Equal(all.Rows.Select(r => (string)r["id"]!), walked);
            Assert.Equal(walked.Count, walked.Distinct().Count());
            Assert.Equal(all.TotalCount, walked.Count);
            return;
        }
        Assert.Fail("no probe catalog with 7..5000 elements");
    }

    [Fact]
    public void CountFollowsTheOldRules()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var full = Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 1 });
        var countOnly = Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 0 });
        Assert.True(full.TotalCount > 0);
        Assert.Equal(full.TotalCount, countOnly.TotalCount);
        Assert.Empty(countOnly.Rows);

        Assert.Equal(-1, Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 1, SkipTotal = true }).TotalCount);
        var filtered = Svc.List(b.Name, new CatalogQuery
        {
            Catalog = "Номенклатура", Limit = 1, Filters = new Dictionary<string, object?> { ["ПометкаУдаления"] = false }
        });
        Assert.Equal(-1, filtered.TotalCount);
    }

    [Fact]
    public void OffsetPagesAreSlicesOfTheNameOrder()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var ten = Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 10, Fields = Array.Empty<string>() });
        var second = Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 5, Offset = 5, Fields = Array.Empty<string>() });
        Assert.Equal(ten.Rows.Skip(5).Select(r => r["id"]), second.Rows.Select(r => r["id"]));
        Assert.Null(second.Next);
    }

    [Fact]
    public void FilterByAttributeAndByOwnerInn()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var some = Svc.List(b.Name, new CatalogQuery { Catalog = "Контрагенты", Limit = 300, Fields = new[] { "ИНН" }, After = LegacyValue.EmptyGuid });
        var probe = some.Rows.First(r => r["ИНН"] is string s && s.Trim().Length > 0);
        var hit = Svc.List(b.Name, new CatalogQuery
        {
            Catalog = "Контрагенты", Limit = 50, Fields = new[] { "ИНН" },
            Filters = new Dictionary<string, object?> { ["ИНН"] = probe["ИНН"] }
        });
        Assert.Contains(hit.Rows, r => (string?)r["id"] == (string?)probe["id"]);
        Assert.All(hit.Rows, r => Assert.Equal(probe["ИНН"], r["ИНН"]));

        if (SchemaOf(b.Name, "ДоговорыКонтрагентов") is null) return;
        var contracts = new ReadService(M).Read(b.Name, new ReadQuery
        {
            Entity = "Справочник.ДоговорыКонтрагентов", Fields = new[] { "Ссылка", "Владелец.ИНН" }, Limit = 200, Refs = RefMode.Guid
        });
        var withInn = contracts.Rows.First(r => r["Владелец.ИНН"] is string s && s.Trim().Length > 0);
        var owned = Svc.List(b.Name, new CatalogQuery
        {
            Catalog = "ДоговорыКонтрагентов", Limit = 500, Fields = Array.Empty<string>(),
            Filters = new Dictionary<string, object?> { ["_ownerInn"] = withInn["Владелец.ИНН"] }
        });
        Assert.Contains(owned.Rows, r => (string?)r["id"] == (string?)withInn["Ссылка"]);
        Assert.All(owned.Rows, r => Assert.IsType<string>(r["Владелец"]));
    }

    [Fact]
    public void ProjectionDropsTabularSectionsAndOtherAttributes()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var schema = SchemaOf(b.Name, "Контрагенты")!;
        var p = Svc.List(b.Name, new CatalogQuery { Catalog = "Контрагенты", Limit = 20, Fields = new[] { "ИНН", "НетТакогоРеквизита" } });
        Assert.Equal(new[] { "НетТакогоРеквизита" }, p.IgnoredFields);
        var allowed = new HashSet<string> { "id", "code", "name", "deletionMark", "parent", "isFolder", "Владелец", "ИНН", "orgRef" };
        if (schema.OrgAttribute is { } org) allowed.Add(org);
        Assert.All(p.Rows, r => Assert.Subset(allowed, r.Keys.ToHashSet()));
    }

    [Fact]
    public void ByIdIsTheSameRowTheListReturns()
    {
        if (!_f.Available) return;
        foreach (var b in Bases)
        {
            var list = Svc.List(b.Name, new CatalogQuery { Catalog = "Контрагенты", Limit = 30, After = LegacyValue.EmptyGuid });
            var pick = list.Rows.FirstOrDefault(r => r.ContainsKey("tabularSections")) ?? list.Rows[0];
            var one = Svc.ById(b.Name, "Контрагенты", (string)pick["id"]!);
            Assert.Equal(JsonSerializer.Serialize(pick, Json), JsonSerializer.Serialize(one, Json));
        }
    }

    [Fact]
    public void ErrorsAreErrorsNotEmptyPages()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var nf = Assert.Throws<OneCException>(() => Svc.List(b.Name, new CatalogQuery { Catalog = "НетТакогоСправочника" }));
        Assert.Equal(ErrorKinds.NotFound, Operations.FromOneC(nf).Kind);

        var bad = Assert.Throws<OneCException>(() => Svc.List(b.Name, new CatalogQuery
        {
            Catalog = "Номенклатура", Filters = new Dictionary<string, object?> { ["НетТакогоПоля"] = "x" }
        }));
        Assert.Equal(OneCLayer.Runtime, bad.Layer);
        Assert.False(CatalogSchemas.IsCached(b.Name, "Номенклатура"));      // a 1C error drops the cached schema

        Assert.Throws<ArgumentException>(() => Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", After = "not-a-guid" }));
        Assert.Throws<ArgumentException>(() => Svc.List(b.Name, new CatalogQuery { Catalog = "Справочник.Номенклатура" }));
        var missing = Assert.Throws<OneCException>(() => Svc.ById(b.Name, "Номенклатура", Guid.NewGuid().ToString()));
        Assert.Equal(ErrorKinds.NotFound, Operations.FromOneC(missing).Kind);

        Assert.NotEmpty(Svc.List(b.Name, new CatalogQuery { Catalog = "Номенклатура", Limit = 1 }).Rows);   // session still fine
    }

    [Fact]
    public void RepeatedCatalogReadsDoNotLeakComObjects()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var q = new CatalogQuery { Catalog = "Контрагенты", Limit = 50, After = LegacyValue.EmptyGuid };
        Svc.List(b.Name, q);
        int live = ComRef.Live, wrong = ComRef.WrongThreadReleases;
        for (int i = 0; i < 10; i++) Svc.List(b.Name, q);
        Assert.True(ComRef.Live <= live, $"live refs {live} -> {ComRef.Live}");
        Assert.Equal(wrong, ComRef.WrongThreadReleases);
    }

    /// <summary>The same reads through the supervisor, the host process and the HTTP edge.</summary>
    [Fact]
    public async Task CatalogRoutesWorkThroughTheEdge()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = new OneC.Supervisor.Supervisor(new SupervisorOptions
        {
            HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1)
        });
        s.Start(new[] { b });
        await using var edge = new EdgeServer(s, 0);                // OS-picked free port
        await edge.StartAsync();
        int port = edge.Port;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        string root = $"/v1/bases/{Uri.EscapeDataString(b.Name)}/catalogs/";

        var page = await http.GetAsync(root + $"{Uri.EscapeDataString("Контрагенты")}?limit=5&after={LegacyValue.EmptyGuid}&skipTotal=1");
        var body = System.Text.Json.Nodes.JsonNode.Parse(await page.Content.ReadAsStringAsync())!;
        Assert.True(page.IsSuccessStatusCode, body.ToJsonString());
        var rows = body["rows"]!.AsArray();
        Assert.Equal(5, rows.Count);
        Assert.Equal(-1, body["totalCount"]!.GetValue<long>());
        Assert.Equal(rows[4]!["id"]!.GetValue<string>(), body["next"]!.GetValue<string>());

        string id = rows[0]!["id"]!.GetValue<string>();
        var one = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(root + $"{Uri.EscapeDataString("Контрагенты")}/{id}"))!;
        Assert.Equal(id, one["row"]!["id"]!.GetValue<string>());

        Assert.Equal(404, (int)(await http.GetAsync(root + Uri.EscapeDataString("НетТакого"))).StatusCode);
        Assert.Equal(400, (int)(await http.GetAsync(root + $"{Uri.EscapeDataString("Контрагенты")}?after=zzz")).StatusCode);
        Assert.Equal(400, (int)(await http.GetAsync(root + $"{Uri.EscapeDataString("Контрагенты")}?limit=abc")).StatusCode);
        Assert.Equal(422, (int)(await http.GetAsync(root + $"{Uri.EscapeDataString("Контрагенты")}?{Uri.EscapeDataString("НетПоля")}=1")).StatusCode);
    }

    private static void AssertSameRows(string what, List<Dictionary<string, object?>> old, List<Dictionary<string, object?>> fast)
    {
        if (LegacyCatalogOracle.Diff(old, fast) is { } diff) Assert.Fail($"{what}: {diff}");
    }
}
