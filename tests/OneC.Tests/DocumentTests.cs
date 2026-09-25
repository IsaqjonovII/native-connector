using System.Text.Encodings.Web;
using System.Text.Json;
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

/// <summary>Milestone 5.3 — document reads, without 1C. Each case mirrors an old helper.</summary>
public class DocumentUnitTests
{
    private static DocumentSchema Doc(string name, params string[] attrs) =>
        new(name, true, null, attrs.Select(AttributeShape.Primitive).ToArray(), Array.Empty<TabularShape>());

    [Theory]
    [InlineData("  № 12   34 ", "12 34")]
    [InlineData("Договор №5", "Договор 5")]
    [InlineData(null, "")]
    public void ContractNumbersNormaliseLikeTheOldAdapter(string? raw, string expected)
        => Assert.Equal(expected, DocumentFilters.NormalizeContract(raw));

    [Theory]
    [InlineData("2024-03-05T00:00:00", "2024-03-05", true)]                    // ISO prefix
    [InlineData("Договор 7 от 05.03.2024", "2024-03-05", true)]                // local form anywhere
    [InlineData("Договор 7 от 05.03.2024", "05.03.2024", true)]
    [InlineData("2024-03-05T10:00:00", "05.03.2024", true)]                    // local filter → ISO prefix
    [InlineData("2024-03-06T00:00:00", "2024-03-05", false)]
    [InlineData("", "2024-03-05", false)]
    [InlineData("anything", "", true)]                                          // empty filter matches
    public void ContractDatesMatchLikeTheOldAdapter(string value, string filter, bool expected)
        => Assert.Equal(expected, DocumentFilters.ContractDateMatches(value, filter));

    [Fact]
    public void FiltersBecomeTermsAliasesCastsAndPostFilters()
    {
        var f = DocumentFilters.Parse(new Dictionary<string, object?>
        {
            ["posted"] = "да",
            ["number"] = "0001",
            ["Контрагент.ИНН"] = "301234567",
            ["ДатаВходящегоДокумента"] = "2024-03-05T10:11:12",
            ["date"] = "2024-03-05",
            ["contract_number"] = " № 5 ",
            ["ДоговорКонтрагента"] = "ignored, contract_number came first"
        }, Doc("РеализацияТоваровУслуг", "Контрагент"));

        Assert.Equal(new[] { "Проведен = &f0", "Номер = &f1", "Контрагент.ИНН = &f2", "ДатаВходящегоДокумента = &f3",
                             "(Дата >= &dayFrom И Дата <= &dayTo)", "" }, f.Terms.Select(t => t.Term));
        Assert.Equal(true, f.Terms[0].Value);                                     // ЗначениеФильтраВБулево
        Assert.Equal("0001", f.Terms[1].Value);                                   // not a date
        Assert.Equal(new DateTime(2024, 3, 5, 10, 11, 12), f.Terms[3].Value);     // ISO auto-cast
        Assert.Equal(new DateTime(2024, 3, 5), f.Terms[4].Value);
        Assert.Equal(new DateTime(2024, 3, 5, 23, 59, 59), f.Terms[5].Value);
        Assert.Equal("5", f.ContractNumber);
        Assert.Equal("Проведен = &f0 И Номер = &f1 И Контрагент.ИНН = &f2 И ДатаВходящегоДокумента = &f3 И (Дата >= &dayFrom И Дата <= &dayTo)", f.Where);

        Assert.True(f.Matches(new Dictionary<string, object?> { ["ДоговорКонтрагента"] = "Договор №5 от 01.01.2024" }));
        Assert.True(f.Matches(new Dictionary<string, object?> { ["НомерВходящегоДокумента"] = "A-5" }));
        Assert.False(f.Matches(new Dictionary<string, object?> { ["ДоговорКонтрагента"] = "Договор 7" }));
    }

    [Fact]
    public void TimesheetsMatchADayByTheirPeriod()
    {
        var both = DocumentFilters.Parse(new Dictionary<string, object?> { ["Дата"] = "05.03.2024" },
            Doc("ТабельУчетаРабочегоВремениОрганизации", "ДатаНачалаПериода", "ДатаОкончанияПериода"));
        Assert.Equal("(ДатаНачалаПериода <= &dayTo И ДатаОкончанияПериода >= &dayFrom)", both.Where);

        var month = DocumentFilters.Parse(new Dictionary<string, object?> { ["date"] = "2024-03-05" },
            Doc("ТабельУчетаРабочегоВремени", "ПериодРегистрации"));
        Assert.Equal("НАЧАЛОПЕРИОДА(ПериодРегистрации, МЕСЯЦ) = НАЧАЛОПЕРИОДА(&dayFrom, МЕСЯЦ)", month.Where);
        Assert.DoesNotContain(month.Terms, t => t.Param == "dayTo");               // unused parameters break 1C queries

        var period = DocumentFilters.Parse(new Dictionary<string, object?> { ["period_start"] = "2024-01-01", ["period_end"] = "2024-01-31" },
            Doc("НачислениеЗарплаты", "ПериодРегистрации"));
        Assert.Equal("ПериодРегистрации >= &periodStart И ПериодРегистрации <= &periodEnd", period.Where);
        Assert.Null(DocumentFilters.Parse(new Dictionary<string, object?> { ["period_start"] = "2024-01-01" }, Doc("X")).Where);
    }

    [Fact]
    public void BadInputIsRejectedNotGuessed()
    {
        // The old code filtered on TODAY when a date did not parse.
        Assert.Throws<ArgumentException>(() => DocumentFilters.Parse(new Dictionary<string, object?> { ["date"] = "yesterday" }, Doc("X")));
        Assert.Throws<ArgumentException>(() => DocumentFilters.Parse(new Dictionary<string, object?> { ["x; ВЫБРАТЬ"] = "1" }, Doc("X")));
        Assert.Null(DocumentFilters.TryParseIsoDate("2024-13-40"));
    }

    [Fact]
    public void NextDateCursorCountsTheRowsOnTheBoundaryDate()
    {
        static List<Dictionary<string, object?>> Rows(params string[] dates) =>
            dates.Select(d => new Dictionary<string, object?> { ["date"] = d }).ToList();

        Assert.Equal(("2024-03-01T00:00:00", 2), DocumentReadService.NextDateCursor(Rows("2024-03-02T00:00:00", "2024-03-01T00:00:00", "2024-03-01T00:00:00"), null, 0));
        // Still on the incoming cursor date: the skip accumulates.
        Assert.Equal(("2024-03-01T00:00:00", 5), DocumentReadService.NextDateCursor(Rows("2024-03-01T00:00:00", "2024-03-01T00:00:00"), new DateTime(2024, 3, 1), 3));
        Assert.Equal((null, 0), DocumentReadService.NextDateCursor(Rows(), null, 0));
    }

    [Fact]
    public void EdgeQueryStringMapsLikeTheOldDocumentRoute()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["limit"] = "250", ["offset"] = "3", ["cursorDate"] = "2024-03-01T00:00:00", ["syncOrder"] = "ASC",
            ["tabular"] = "false", ["from"] = "2024-01-01", ["skipTotal"] = "true", ["posted"] = "true", ["post"] = "1"
        });
        var a = EdgeServer.DocumentArgs("РеализацияТоваровУслуг", q);
        Assert.Equal("asc", a["order"]!.GetValue<string>());
        Assert.False(a["tabular"]!.GetValue<bool>());
        Assert.True(a["skipTotal"]!.GetValue<bool>());
        Assert.Equal("2024-03-01T00:00:00", a["cursorDate"]!.GetValue<string>());
        Assert.Equal(new[] { "posted", "post" }, a["filters"]!.AsObject().Select(p => p.Key));   // "post" is a filter here, as before
    }
}

[Collection("onec-live")]
public class DocumentLiveTests
{
    private static readonly string[] Probe =
    {
        "РеализацияТоваровУслуг", "ПоступлениеТоваровУслуг", "ПоступлениеНаРасчетныйСчет", "СписаниеСРасчетногоСчета",
        "ПриходныйКассовыйОрдер", "РасходныйКассовыйОрдер", "ОтчетОРозничныхПродажах", "СчетНаОплатуПокупателю"
    };

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly LiveFixture _f;
    public DocumentLiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private DocumentReadService Svc => new(M);

    private DocumentSchema? SchemaOf(string b, string doc)
    {
        try { return M.Use(b, ctx => DocumentSchemas.Get(ctx, doc)); }
        catch (OneCException e) when (e.Message.Contains("not found")) { return null; }
    }

    /// <summary>Parity with the literal old algorithm: keyset page, newest page, oldest page after a cursor, batch.</summary>
    [Fact]
    public void RowsMatchTheOldAdapterAlgorithm()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        int compared = 0, rows = 0, lines = 0;
        foreach (var doc in Probe)
        {
            var s = SchemaOf(b, doc);
            if (s is null) continue;
            var queries = new[]
            {
                new DocumentQuery { Document = doc, Limit = 30, After = LegacyValue.EmptyGuid, SkipTotal = true },
                new DocumentQuery { Document = doc, Limit = 30, SkipTotal = true },
                new DocumentQuery { Document = doc, Limit = 30, Order = "asc", CursorDate = new DateTime(2020, 1, 1), Offset = 2 }
            };
            foreach (var q in queries)
            {
                var fast = Svc.List(b, q);
                var old = M.Use(b, ctx => LegacyDocumentOracle.List(ctx, s, q));
                if (LegacyCatalogOracle.Diff(old.Rows, fast.Rows) is { } d) Assert.Fail($"{doc} {q}: {d}");
                Assert.Equal((old.Total, old.NextDate, old.NextSkip), (fast.TotalCount, fast.NextCursorDate, fast.NextCursorSkip));
                rows += fast.Rows.Count;
            }
            var ids = Svc.List(b, queries[0]).Rows.Select(r => (string)r["id"]!).ToList();
            var fb = Svc.ByIds(b, doc, ids);
            var ob = M.Use(b, ctx => LegacyDocumentOracle.Batch(ctx, s, ids));
            if (LegacyCatalogOracle.Diff(ob, fb) is { } bd) Assert.Fail($"{doc} batch: {bd}");
            lines += fb.Sum(r => r.GetValueOrDefault("tabularSections") is Dictionary<string, object?> ts
                                 ? ts.Values.Sum(v => ((List<Dictionary<string, object?>>)v!).Count) : 0);
            compared++;
        }
        Assert.True(compared >= 3, $"only {compared} probe documents exist");
        Assert.True(rows >= 100, $"only {rows} rows compared");
        Assert.True(lines > 0, "no tabular line compared");
    }

    /// <summary>
    /// The connector's date-cursor walk (next_cursor_date + next_cursor_skip as cursorDate +
    /// offset) visits exactly the documents of a window, once each — with a page size small
    /// enough that several documents share a boundary second.
    /// </summary>
    [Fact]
    public void DateCursorWalkVisitsEveryDocumentOfTheWindowOnce()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        const string Doc = "РеализацияТоваровУслуг";
        if (SchemaOf(b, Doc) is null) return;

        // A window with a few hundred documents: the two weeks before the 300th newest one
        // (the newest dates can be a handful of mistyped future documents).
        var anchor = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 1, Offset = 300, SkipTotal = true, Tabular = false });
        Assert.NotEmpty(anchor.Rows);
        var to = DateTime.Parse((string)anchor.Rows[0]["date"]!).Date.AddDays(1);
        var from = to.AddDays(-14);

        var keyset = new List<string>();
        string? after = LegacyValue.EmptyGuid;
        while (after is not null)
        {
            var p = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 200, After = after, From = from, To = to, SkipTotal = true, Tabular = false, Fields = Array.Empty<string>() });
            keyset.AddRange(p.Rows.Select(r => (string)r["id"]!));
            after = p.Next;
        }

        var walked = new List<string>();
        DateTime? cursor = null; int skip = 0; long total = -1;
        for (int page = 0; page < 10_000; page++)
        {
            var p = Svc.List(b, new DocumentQuery
            {
                Document = Doc, Limit = 7, CursorDate = cursor, Offset = skip, From = from, To = to,
                Tabular = false, Fields = Array.Empty<string>(), SkipTotal = page > 0
            });
            if (page == 0) total = p.TotalCount;
            if (p.Rows.Count == 0) break;
            walked.AddRange(p.Rows.Select(r => (string)r["id"]!));
            cursor = DateTime.Parse(p.NextCursorDate!);
            skip = p.NextCursorSkip;
        }

        Assert.True(keyset.Count >= 50, $"window too small ({keyset.Count})");
        Assert.Equal(walked.Count, walked.Distinct().Count());
        Assert.Equal(keyset.OrderBy(x => x), walked.OrderBy(x => x));
        Assert.Equal(keyset.Count, total);                                        // the count is the window's
    }

    [Fact]
    public void FiltersNarrowTheRows()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        const string Doc = "РеализацияТоваровУслуг";
        if (SchemaOf(b, Doc) is null) return;

        var posted = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 50, SkipTotal = true, Tabular = false,
                                                     Filters = new Dictionary<string, object?> { ["posted"] = "true" } });
        Assert.NotEmpty(posted.Rows);
        Assert.All(posted.Rows, r => Assert.Equal(true, r["posted"]));

        string day = ((string)posted.Rows[0]["date"]!)[..10];
        var onDay = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 500, SkipTotal = true, Tabular = false,
                                                    Filters = new Dictionary<string, object?> { ["date"] = day } });
        Assert.NotEmpty(onDay.Rows);
        Assert.All(onDay.Rows, r => Assert.StartsWith(day, (string)r["date"]!));

        var withContract = posted.Rows.FirstOrDefault(r => r.GetValueOrDefault("ДоговорКонтрагента") is string c && c.Trim().Length > 2);
        if (withContract is not null)
        {
            string needle = ((string)withContract["ДоговорКонтрагента"]!).Trim();
            var byContract = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 20, SkipTotal = true, Tabular = false,
                                                             Filters = new Dictionary<string, object?> { ["contract_number"] = needle } });
            Assert.NotEmpty(byContract.Rows);
            Assert.All(byContract.Rows, r => Assert.True(
                DocumentFilters.ContainsIgnoreCase(DocumentFilters.NormalizeContract(r["ДоговорКонтрагента"] as string), DocumentFilters.NormalizeContract(needle)) ||
                DocumentFilters.ContainsIgnoreCase(DocumentFilters.NormalizeContract(r.GetValueOrDefault("НомерВходящегоДокумента") as string), DocumentFilters.NormalizeContract(needle))));
        }
    }

    [Fact]
    public void BankDocumentNamesOfEitherConfigurationWork()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        foreach (var (a, c) in new[] { ("ПоступлениеНаРасчетныйСчет", "ПлатежноеПоручениеВходящее"), ("СписаниеСРасчетногоСчета", "ПлатежноеПоручениеИсходящее") })
        {
            string real = SchemaOf(b, a) is not null ? a : c, alias = real == a ? c : a;
            if (SchemaOf(b, real) is null || SchemaOf(b, alias) is not null) continue;   // nothing to alias
            var q = new DocumentQuery { Document = alias, Limit = 5, SkipTotal = true };
            var viaAlias = Svc.List(b, q);
            var direct = Svc.List(b, q with { Document = real });
            Assert.Equal(real, viaAlias.Document);
            Assert.Equal(JsonSerializer.Serialize(direct.Rows, Json), JsonSerializer.Serialize(viaAlias.Rows, Json));
        }
    }

    /// <summary>By id carries every tabular section, empty ones as []; the list only non-empty ones.</summary>
    [Fact]
    public void ByIdHasEveryTabularSectionAndTheListOnlyFilledOnes()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        const string Doc = "РеализацияТоваровУслуг";
        var s = SchemaOf(b, Doc);
        if (s is null) return;
        var row = Svc.List(b, new DocumentQuery { Document = Doc, Limit = 1, SkipTotal = true }).Rows.Single();
        var one = Svc.ById(b, Doc, ((string)row["id"]!).ToUpperInvariant());          // any GUID case

        var sections = (Dictionary<string, object?>)one["tabularSections"]!;
        Assert.Equal(s.Tabular.Select(t => t.Name), sections.Keys);
        var listSections = row.GetValueOrDefault("tabularSections") as Dictionary<string, object?> ?? new();
        Assert.All(listSections.Values, v => Assert.NotEmpty((List<Dictionary<string, object?>>)v!));
        foreach (var (k, v) in listSections)
            Assert.Equal(JsonSerializer.Serialize(v, Json), JsonSerializer.Serialize(sections[k], Json));
    }

    [Fact]
    public void ErrorsAreErrors()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var nf = Assert.Throws<OneCException>(() => Svc.List(b, new DocumentQuery { Document = "НетТакогоДокумента" }));
        Assert.Equal(ErrorKinds.NotFound, Operations.FromOneC(nf).Kind);
        var bad = Assert.Throws<OneCException>(() => Svc.List(b, new DocumentQuery
        {
            Document = "РеализацияТоваровУслуг", Filters = new Dictionary<string, object?> { ["НетТакогоПоля"] = "x" }
        }));
        Assert.Equal(OneCLayer.Runtime, bad.Layer);
        Assert.Throws<ArgumentException>(() => Svc.List(b, new DocumentQuery { Document = "РеализацияТоваровУслуг", After = "x" }));
        Assert.Throws<ArgumentException>(() => Svc.List(b, new DocumentQuery { Document = "РеализацияТоваровУслуг", Order = "sideways" }));
        var missing = Assert.Throws<OneCException>(() => Svc.ById(b, "РеализацияТоваровУслуг", Guid.NewGuid().ToString()));
        Assert.Equal(ErrorKinds.NotFound, Operations.FromOneC(missing).Kind);
        Assert.Empty(Svc.ByIds(b, "РеализацияТоваровУслуг", new[] { Guid.NewGuid().ToString(), "not-a-guid" }));
    }

    [Fact]
    public void RepeatedDocumentReadsDoNotLeakComObjects()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var q = new DocumentQuery { Document = "РеализацияТоваровУслуг", Limit = 30, SkipTotal = true };
        var ids = Svc.List(b, q).Rows.Select(r => (string)r["id"]!).ToList();
        Svc.ByIds(b, q.Document, ids);
        int live = ComRef.Live, wrong = ComRef.WrongThreadReleases;
        for (int i = 0; i < 5; i++) { Svc.List(b, q); Svc.ByIds(b, q.Document, ids); }
        Assert.True(ComRef.Live <= live, $"live refs {live} -> {ComRef.Live}");
        Assert.Equal(wrong, ComRef.WrongThreadReleases);
    }

    [Fact]
    public async Task DocumentRoutesWorkThroughTheEdge()
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
        string root = $"/v1/bases/{Uri.EscapeDataString(_f.Server.Name)}/documents/{Uri.EscapeDataString("РеализацияТоваровУслуг")}";

        var page = JsonNode.Parse(await http.GetStringAsync(root + "?limit=3&skipTotal=true&tabular=false"))!;
        var rows = page["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.NotNull(page["nextCursorDate"]);

        string id = rows[0]!["id"]!.GetValue<string>();
        var one = JsonNode.Parse(await http.GetStringAsync($"{root}/{id}"))!;
        Assert.Equal(id, one["row"]!["id"]!.GetValue<string>());

        var batch = await http.PostAsync(root + "/batch", new StringContent(
            new JsonObject { ["ids"] = new JsonArray(rows.Select(r => (JsonNode)JsonValue.Create(r!["id"]!.GetValue<string>())!).ToArray()) }.ToJsonString(),
            System.Text.Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await batch.Content.ReadAsStringAsync())!;
        Assert.True(batch.IsSuccessStatusCode, body.ToJsonString());
        Assert.Equal(rows.Select(r => r!["id"]!.GetValue<string>()), body["rows"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));

        Assert.Equal(400, (int)(await http.PostAsync(root + "/batch", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(400, (int)(await http.GetAsync(root + "?cursorDate=soon")).StatusCode);
        Assert.Equal(404, (int)(await http.GetAsync($"/v1/bases/{Uri.EscapeDataString(_f.Server.Name)}/documents/NoSuchDoc")).StatusCode);
    }
}
