using System.Text.Json.Nodes;
using OneC.Host;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>The create body parser (D38): the old keys, refused early when the old code would build a broken document.</summary>
public class DocumentWriteBodyTests
{
    private const string Prefix = "AIBA_";

    private static DocumentWriteBody Parse(string json) => DocumentWriteBody.Parse(JsonNode.Parse(json)!.AsObject(), Prefix);

    [Fact]
    public void OldKeysAreRead()
    {
        var b = Parse("""
            { "Date": "2026-09-24T10:30:00.000Z", "Номер": "A-1", "Комментарий": "AIBA_ODOO_7_ab12cd34: Bill 7",
              "_idempotencyMarker": "AIBA_ODOO_7_ab12cd34",
              "Контрагент": { "type": "CatalogRef.Контрагенты", "ИНН": "301234567" },
              "СуммаДокумента": 100.5, "Пусто": null,
              "tabularSections": { "Товары": [ { "LineNumber": 1, "Количество": 2 } ] },
              "Услуги": [ { "Сумма": 5 } ] }
            """);
        Assert.Equal(new DateTime(2026, 9, 24, 10, 30, 0), b.Date);
        Assert.Equal("A-1", b.Number);
        Assert.Equal("AIBA_ODOO_7_ab12cd34", b.IdempotencyMarker);
        Assert.Equal(new[] { "Контрагент", "СуммаДокумента" }, b.Header.Select(h => h.Key));   // null = not given
        Assert.Equal(new[] { "Товары", "Услуги" }, b.Tabular.Select(t => t.Key));
    }

    [Theory]
    [InlineData("2026-09-24", 2026, 9, 24, 0, 0)]
    [InlineData("2026-09-24T07:05:09", 2026, 9, 24, 7, 5)]
    [InlineData("2026-09-24 07:05:09", 2026, 9, 24, 7, 5)]
    [InlineData("2026-09-24T07:05:09.123+05:00", 2026, 9, 24, 7, 5)]
    public void DatesByPosition(string s, int y, int mo, int d, int h, int mi)
        => Assert.Equal(new DateTime(y, mo, d, h, mi, s.Length >= 19 ? 9 : 0), DocumentWriteBody.ParseDate(s));

    [Theory]
    [InlineData("24.09.2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-09")]
    [InlineData("")]
    public void BadDatesAreNotDates(string s) => Assert.Null(DocumentWriteBody.ParseDate(s));

    [Theory]
    [InlineData("""{ "Комментарий": "AIBA_X_1: x" }""", "'Date' is required")]
    [InlineData("""{ "Date": "24.09.2026", "Комментарий": "AIBA_X_1: x" }""", "not a date")]
    [InlineData("""{ "Date": "2026-09-24" }""", "must start with an AIBA marker")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "accountant's own" }""", "must start with an AIBA marker")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "_idempotencyMarker": "AIBA_X_2" }""", "must contain 'AIBA_X_2:'")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X 1: x", "_idempotencyMarker": "AIBA_X 1" }""", "no spaces, no colon")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "_monthlyUpsert": {} }""", "not supported")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "tabularSections": [] }""", "must be an object")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "Товары": [ 1 ] }""", "every row must be an object")]
    [InlineData("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "Товары": [], "tabularSections": { "Товары": [] } }""", "given twice")]
    public void BrokenBodiesAreRefusedBeforeAnyCall(string json, string message)
    {
        var e = Assert.Throws<WriteRejected>(() => Parse(json));
        Assert.False(e.Unprocessable);                 // 400, not 422
        Assert.Contains(message, e.Message);
    }

    [Theory]
    [InlineData("Error running processor - 'ОбработкаПроведения' Attempt to transfer from client to server a mutable value of 1-th parameter of method ПроведениеРеализацииПакетДокументов()", true)]
    [InlineData("Ошибка при вызове процедуры: Попытка передачи с клиента на сервер изменяемого значения", true)]
    [InlineData("Не заполнено поле Счет учета", false)]
    public void TheKnownConfigurationBlockerIsRecognised(string text, bool known)
        => Assert.Equal(known, DocumentWriter.KnownBlocker(text)?.Code == "posting_blocked_by_configuration");

    [Fact]
    public void UnknownDirectivesAreReportedNotApplied()
    {
        var b = Parse("""{ "Date": "2026-09-24", "Комментарий": "AIBA_X_1: x", "_autofill": "off", "_whatever": 1 }""");
        Assert.Empty(b.Header);
        var d = Assert.Single(b.Diagnostics);
        Assert.Equal(("unknown_directive", "_whatever"), (d.Code, d.Field));
    }
}

/// <summary>
/// The corrected write path (D38) against the local copies only — never a client's base (user
/// rule 2026-09-24). Every document is <c>AIBA_REWRITE_DW_&lt;run&gt;</c> and deleted afterwards.
/// Drafts on the server base (posting is blocked on KAN by its configuration), posting on the file base.
/// </summary>
[Collection("onec-live")]
public class DocumentWriteLiveTests
{
    private const string Doc = "ПоступлениеТоваровУслуг";
    private static readonly string Run = "AIBA_REWRITE_DW_" + DateTime.Now.ToString("MMddHHmmss");

    private readonly LiveFixture _f;
    public DocumentWriteLiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private WriteService W => new(M, "AIBA_REWRITE_");
    private DocumentWriter Writer => new(W);

    /// <summary>An existing document of <paramref name="doc"/> as an old-style body, AIBA-marked.</summary>
    private JsonObject BodyFrom(string baseName, string doc, string tag, bool posted = false)
    {
        var body = M.Use(baseName, ctx =>
        {
            var src = WriteParityScenario.Sources(ctx, doc, 1, posted);
            Assert.NotEmpty(src);
            return WritePayload.FromRef(ctx, doc, src[0]);
        });
        body[DocumentWriteBody.CommentField] = $"{Run}_{tag}: test";
        return body;
    }

    private void Cleanup(string baseName, string doc)
    {
        var w = W;
        foreach (var id in w.FindOwned(baseName, doc, Run)) w.Delete(baseName, doc, id);
        Assert.Empty(w.FindOwned(baseName, doc, Run));
    }

    [Fact]
    public void AnOldStyleBodyRoundTripsIntoAnIdenticalDraft()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        try
        {
            var body = BodyFrom(b, Doc, "RT");
            var c = Writer.Create(b, Doc, body, post: false);
            Assert.True(c.Created);
            Assert.False(c.Posted);
            var written = M.Use(b, ctx => WritePayload.FromRef(ctx, Doc, c.Id));
            var expected = (JsonObject)body.DeepClone();
            expected.Remove(DocumentWriteBody.CommentField);             // the copy's own marker
            Assert.Empty(WritePayload.Diff(expected, written));
        }
        finally { Cleanup(b, Doc); }
    }

    /// <summary>
    /// A retry with the same marker returns the first document; once that one is marked for
    /// deletion it no longer counts and a new one is written (the old lookup reused it, Q5).
    /// </summary>
    [Fact]
    public void RetriesAreIdempotentAndDeletionMarkedDocumentsDoNotCount()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        try
        {
            var body = BodyFrom(b, Doc, "IDEM");
            string marker = Run + "_IDEM";
            body["_idempotencyMarker"] = marker;

            var first = Writer.Create(b, Doc, (JsonObject)body.DeepClone(), post: false);
            var retry = Writer.Create(b, Doc, (JsonObject)body.DeepClone(), post: false);
            Assert.True(first.Created);
            Assert.True(retry.Idempotent);
            Assert.False(retry.Created);
            Assert.Equal(first.Id, retry.Id);

            W.MarkForDeletion(b, Doc, first.Id);
            var third = Writer.Create(b, Doc, (JsonObject)body.DeepClone(), post: false);
            Assert.True(third.Created);
            Assert.NotEqual(first.Id, third.Id);
        }
        finally { Cleanup(b, Doc); }
    }

    /// <summary>Nothing is written when a reference does not resolve; every failure is listed.</summary>
    [Fact]
    public void UnresolvedReferencesRefuseTheDocumentAndWriteNothing()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var body = new JsonObject
        {
            ["Date"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["Комментарий"] = $"{Run}_UNRES: test",
            ["Контрагент"] = new JsonObject { ["type"] = "CatalogRef.Контрагенты", ["ИНН"] = "000000001" },
            ["Склад"] = new JsonObject { ["type"] = "CatalogRef.Номенклатура", ["Наименование"] = "x" }
        };
        var e = Assert.Throws<WriteRejected>(() => Writer.Create(b, Doc, body, post: false));
        Assert.True(e.Unprocessable);
        Assert.Contains(e.Diagnostics, d => d.Code == "unresolved_reference" && d.Field == "Контрагент");
        Assert.Contains(e.Diagnostics, d => d.Code == "reference_type_not_accepted" && d.Field == "Склад");
        Assert.Empty(W.FindOwned(b, Doc, Run + "_UNRES"));
    }

    [Fact]
    public void ATooLongStringIsRefusedNotCut()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var schema = M.Use(b, ctx => WriteSchemas.Get(ctx, Doc));
        var limited = schema.Attributes.First(a => a.Value.String && a.Value.StringLength is > 0 and < 200 && !a.Value.HasRefs);
        var body = new JsonObject
        {
            ["Date"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["Комментарий"] = $"{Run}_LONG: test",
            [limited.Key] = new string('x', limited.Value.StringLength + 1)
        };
        var e = Assert.Throws<WriteRejected>(() => Writer.Create(b, Doc, body, post: false));
        Assert.Contains(e.Diagnostics, d => d.Code == "value_too_long" && d.Field == limited.Key);
        Assert.Empty(W.FindOwned(b, Doc, Run + "_LONG"));
    }

    /// <summary>
    /// Update: references by the same rules, AIBA documents only, no tabular sections; a posted
    /// document changes only with autoUnpost.
    /// </summary>
    [Fact]
    public void UpdateChangesAnOwnedDraftAndRefusesWhatItMustNot()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        try
        {
            var c = Writer.Create(b, Doc, BodyFrom(b, Doc, "UPD"), post: false);
            var schema = M.Use(b, ctx => WriteSchemas.Get(ctx, Doc));
            var text = schema.Attributes.Where(a => a.Value.String && !a.Value.HasRefs && a.Key != "Комментарий" && a.Value.StringLength is 0 or >= 10)
                             .OrderBy(a => a.Key == "НомерВходящегоДокумента" ? 0 : 1).First();
            var u = Writer.Update(b, Doc, c.Id, new JsonObject { [text.Key] = "AIBA-UPD" }, post: null, autoUnpost: false);
            Assert.False(u.Posted);
            var after = M.Use(b, ctx => WritePayload.FromRef(ctx, Doc, c.Id));
            Assert.Equal("AIBA-UPD", after[text.Key]?.GetValue<string>());

            Assert.Throws<WriteRejected>(() => Writer.Update(b, Doc, c.Id,
                new JsonObject { ["tabularSections"] = new JsonObject() }, null, false));
            Assert.Throws<WriteRejected>(() => Writer.Update(b, Doc, c.Id,
                new JsonObject { ["Комментарий"] = "lost the marker" }, null, false));
        }
        finally { Cleanup(b, Doc); }
    }

    /// <summary>On the file base (where posting works): create posted, change it with autoUnpost, unpost, post.</summary>
    [Fact]
    public void FileBaseCreatePostedThenUpdateUnpostAndPost()
    {
        if (!_f.Available || _f.File is null) return;
        string b = _f.File.Name;
        try
        {
            var c = Writer.Create(b, Doc, BodyFrom(b, Doc, "POST", posted: true), post: true);
            Assert.True(c.Posted);

            var e = Assert.Throws<WriteRejected>(() => Writer.Update(b, Doc, c.Id, new JsonObject(), null, autoUnpost: false));
            Assert.Contains(e.Diagnostics, d => d.Code == "requires_unpost");
            var u = Writer.Update(b, Doc, c.Id, new JsonObject(), post: null, autoUnpost: true);
            Assert.True(u.Posted);                              // changed and posted again in one write

            W.Unpost(b, Doc, c.Id);
            Assert.False(M.Use(b, ctx => Posted(ctx, c.Id)));
            W.Post(b, Doc, c.Id);
            Assert.True(M.Use(b, ctx => Posted(ctx, c.Id)));
        }
        finally { Cleanup(b, Doc); }
    }

    [Fact]
    public void ExchangeModeIsRefusedThroughTheOperation()
    {
        if (!_f.Available || _f.Server is null) return;
        var ops = new Operations(M, "AIBA_REWRITE_");
        var r = ops.Execute(new IpcRequest
        {
            Id = 1, Op = Ops.Create, Base = _f.Server.Name,
            Args = new JsonObject
            {
                ["docType"] = Doc, ["exchange"] = true,
                ["body"] = new JsonObject { ["Date"] = "2026-09-24", ["Комментарий"] = $"{Run}_EXCH: x" }
            }
        }, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(Layers.Validation, r.Error!.Layer);
        Assert.Empty(W.FindOwned(_f.Server.Name, Doc, Run + "_EXCH"));
    }

    /// <summary>The write routes through supervisor → pipe → host, with the old flags and the new error shape.</summary>
    [Fact]
    public async Task WriteRoutesSpeakTheOldContractThroughTheEdge()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var body = BodyFrom(b, Doc, "EDGE");
        body["_idempotencyMarker"] = Run + "_EDGE";

        using var s = new OneC.Supervisor.Supervisor(new OneC.Supervisor.SupervisorOptions
        {
            HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), RecycleIdleAboveMb = 0,
            MonitorInterval = TimeSpan.FromHours(1)
        });
        s.Start(new[] { _f.Server });
        await using var edge = new OneC.Supervisor.EdgeServer(s, 0);
        await edge.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edge.Port}") };
        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        string root = $"/v1/bases/{Uri.EscapeDataString(b)}/documents/{Uri.EscapeDataString(Doc)}";
        try
        {
            var created = await http.PostAsync(root + "?post=false", Json(body));
            Assert.Equal(201, (int)created.StatusCode);
            var c = await Read(created);
            string id = c["id"]!.GetValue<string>();
            Assert.True(c["created"]!.GetValue<bool>());

            var again = await Read(await http.PostAsync(root + "?post=false", Json(body)));
            Assert.True(again["idempotent"]!.GetValue<bool>());
            Assert.Equal(id, again["id"]!.GetValue<string>());

            var put = await http.PutAsync($"{root}/{id}", Json(new JsonObject()));
            Assert.Equal(200, (int)put.StatusCode);
            Assert.True((await Read(put))["updated"]!.GetValue<bool>());

            var mark = await Read(await http.DeleteAsync($"{root}/{id}"));
            Assert.True(mark["marked"]!.GetValue<bool>());
            var del = await http.DeleteAsync($"{root}/{id}?hard=true");
            Assert.True((await Read(del))["deleted"]!.GetValue<bool>());
            Assert.Equal(404, (int)(await http.DeleteAsync($"{root}/{id}?hard=true")).StatusCode);

            var bad = (JsonObject)body.DeepClone();
            bad.Remove("_idempotencyMarker");
            bad["Контрагент"] = new JsonObject { ["type"] = "CatalogRef.Контрагенты", ["ИНН"] = "000000001" };
            var refused = await http.PostAsync(root + "?post=false", Json(bad));
            Assert.Equal(422, (int)refused.StatusCode);
            var err = (await Read(refused))["error"]!;
            Assert.Equal("unprocessable", err["kind"]!.GetValue<string>());
            Assert.Contains(err["data"]!["fillDiagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "unresolved_reference");

            Assert.Equal(400, (int)(await http.PostAsync(root + "?exchange=true", Json(body))).StatusCode);
        }
        finally { Cleanup(b, Doc); }
    }

    private static StringContent Json(JsonNode n) => new(n.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonObject> Read(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    private static bool Posted(SessionContext ctx, string id)
    {
        using var scope = new ComScope();
        var r = QueryKitRef(ctx, scope, id);
        return Dispatch.GetBool(r, "Проведен", ctx.Error);
    }

    private static object QueryKitRef(SessionContext ctx, ComScope scope, string id)
    {
        var docs = scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы");
        var mgr = scope.Track(Dispatch.Get(docs, Doc, ctx.Error), Doc);
        var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", id), "UUID");
        return scope.Track(Dispatch.Call(mgr, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");
    }
}
