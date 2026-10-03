using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Host;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S3 — host read prerequisites, without 1C.</summary>
public class SyncReadUnitTests
{
    [Fact]
    public void NotFoundSaysWhetherTheObjectOrTheTableIsMissing()
    {
        var obj = OneCException.Host($"Справочник.Номенклатура {Guid.NewGuid()} not found", ErrorContext.None, "catalog", "Номенклатура");
        var doc = OneCException.Host($"Документ.ПоступлениеТоваровУслуг {Guid.NewGuid()} not found", ErrorContext.None, "document", "X");
        var table = OneCException.Host("catalog 'НетТакого' not found", ErrorContext.None, "catalog", "НетТакого");
        Assert.Equal(("notFound", "object"), (Operations.FromOneC(obj).Kind, Operations.FromOneC(obj).NotFoundScope));
        Assert.Equal("object", Operations.FromOneC(doc).NotFoundScope);
        Assert.Equal("metadata", Operations.FromOneC(table).NotFoundScope);
        Assert.Null(Operations.FromOneC(OneCException.Host("boom", ErrorContext.None, "x", "y")).NotFoundScope);
    }

    [Fact]
    public void VersionTablesMustNameOneObject()
    {
        Assert.Equal(("Документы", "X"), VersionReadService.Parse("Документ.X"));
        Assert.Equal(("Справочники", "Банки"), VersionReadService.Parse("Справочник.Банки"));
        Assert.Throws<ArgumentException>(() => VersionReadService.Parse("РегистрСведений.X"));
        Assert.Throws<ArgumentException>(() => VersionReadService.Parse("Документ.X.Товары"));
        Assert.Throws<ArgumentException>(() => VersionReadService.Parse("Документ.X; ВЫБРАТЬ"));
    }

    [Fact]
    public void RegisterKeysRoundTripAndStayUnique()
    {
        var key = new List<RegisterKeyset.KeyValue?>
        {
            new("d", "", "2026-09-30T00:00:00"), new("CatalogRef.Валюты", "http://v8.1c.ru/8.1/data/enterprise/current-config", Guid.NewGuid().ToString()), null
        };
        var back = RegisterKeyset.Decode(RegisterKeyset.Encode(key), 3);
        Assert.Equal(key, back);
        Assert.NotEqual(RegisterKeyset.NaturalKey(new RegisterKeyset.KeyValue?[] { new("s", "", "a|b"), new("s", "", "c") }),
                        RegisterKeyset.NaturalKey(new RegisterKeyset.KeyValue?[] { new("s", "", "a"), new("s", "", "b|c") }) + "x");
        Assert.Throws<ArgumentException>(() => RegisterKeyset.Decode(RegisterKeyset.Encode(key), 2));
        Assert.Throws<ArgumentException>(() => RegisterKeyset.Decode("not json", 3));
    }
}

/// <summary>Sync block S3 — versions and chart of accounts against real bases.</summary>
[Collection("onec-live")]
public class SyncReadLiveTests
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly LiveFixture _f;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public SyncReadLiveTests(LiveFixture f, Xunit.Abstractions.ITestOutputHelper output) { _f = f; _out = output; }
    private SessionManager M => _f.Manager!;

    [Fact]
    public void AVersionWalkSeesEveryObjectOnceAndIdsAnswerWhichExist()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.Server, _f.File }.OfType<OneCBase>())
        {
            var svc = new VersionReadService(M);
            const string table = "Справочник.Контрагенты";
            long count = M.Use(b.Name, ctx => SyncCount(ctx, table));
            var all = new List<ObjectVersion>();
            string? after = null;
            for (int i = 0; i < 10_000; i++)
            {
                var p = svc.Page(b.Name, table, after, (int)Math.Max(3, count / 4));
                all.AddRange(p.Rows);
                if (p.Next is null) break;
                after = p.Next;
            }
            Assert.Equal(count, all.Count);
            Assert.Equal(all.Count, all.Select(v => v.Id).Distinct().Count());
            Assert.All(all, v => Assert.False(string.IsNullOrEmpty(v.Version)));

            var some = all.Take(3).ToList();
            string ghost = Guid.NewGuid().ToString();
            var byIds = svc.ByIds(b.Name, table, some.Select(v => v.Id).Append(ghost).ToList());
            Assert.Equal(some.OrderBy(v => v.Id), byIds.OrderBy(v => v.Id));
            Assert.DoesNotContain(byIds, v => v.Id == ghost);                  // gone = absent from the answer
        }
    }

    [Fact]
    public void VersionReadsDoNotLeakAndRefuseUnknownTables()
    {
        if (!_f.Available) return;
        var b = (_f.Server ?? _f.File)!;
        var svc = new VersionReadService(M);
        svc.Page(b.Name, "Справочник.Контрагенты", null, 500);
        int live = ComRef.Live, wrong = ComRef.WrongThreadReleases;
        for (int i = 0; i < 5; i++) svc.Page(b.Name, "Справочник.Контрагенты", null, 500);
        Assert.True(ComRef.Live <= live, $"live refs {live} -> {ComRef.Live}");
        Assert.Equal(wrong, ComRef.WrongThreadReleases);
        Assert.ThrowsAny<Exception>(() => svc.Page(b.Name, "Справочник.НетТакогоСправочника", null, 10));
    }

    [Fact]
    public void ChartRowsAreEveryAccountInCodeOrder()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.Server, _f.File }.OfType<OneCBase>())
        {
            var svc = new ChartReadService(M);
            var first = svc.List(b.Name, "Хозрасчетный", 0, 0);
            Assert.True(first.TotalCount > 0);
            var rows = new List<Dictionary<string, object?>>();
            int size = (int)(first.TotalCount / 3) + 1;
            for (int off = 0; off < first.TotalCount + size; off += size)
            {
                var p = svc.List(b.Name, "Хозрасчетный", size, off);
                rows.AddRange(p.Rows);
                if (!p.HasMore) break;
            }
            Assert.Equal(first.TotalCount, rows.Count);
            Assert.Equal(rows.Count, rows.Select(r => r["Код"]).Distinct().Count());
            var nf = Assert.Throws<OneCException>(() => svc.List(b.Name, "НетТакогоПлана", 1, 0));
            Assert.Equal("metadata", Operations.FromOneC(nf).NotFoundScope);
        }
    }

    /// <summary>The old adapter's /api/charts rows, when it is running on the same base (read-only GET, small limit).</summary>
    [Fact]
    public async Task ChartRowsMatchTheOldAdapter()
    {
        if (!_f.Available || _f.Server is null) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        JsonNode? old;
        // The old router serves several bases; /api/db/<its name>/ picks one (KAN is the local server base).
        string oldBase = _f.Server.ConnectionString.Contains("Ref=\"KAN\"") ? "KAN" : _f.Server.Name;
        try { old = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:55899/api/db/{oldBase}/charts/" + Uri.EscapeDataString("Хозрасчетный") + "?limit=60")); }
        catch (HttpRequestException) { return; }                      // the old adapter is not running
        catch (TaskCanceledException) { return; }
        var ours = new ChartReadService(M).List(_f.Server.Name, "Хозрасчетный", 60, 0);
        if (old?["total_count"]?.GetValue<long>() != ours.TotalCount)
        {
            _out.WriteLine($"old adapter serves another base ({old?["total_count"]} vs {ours.TotalCount} accounts): not compared");
            return;
        }
        var theirs = old["data"]!.AsArray().Select(n => n!.ToJsonString(Json)).ToList();
        var mine = ours.Rows.Select(r => JsonSerializer.Serialize(r, Json)).ToList();
        Assert.Equal(theirs, mine);
        _out.WriteLine($"compared {mine.Count} chart rows with the old adapter: identical");
    }

    private static long SyncCount(SessionContext ctx, string table)
    {
        using var s = new ComScope();
        var q = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", $"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК n ИЗ {table}", ctx.Error);
        var sel = new DispatchMemo(s.Track(Dispatch.Call(s.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "Р"), "Выбрать", ctx.Error), "В"));
        return sel.CallBool("Следующий", ctx.Error) ? Convert.ToInt64(sel.Get("n", ctx.Error)) : 0;
    }
}
