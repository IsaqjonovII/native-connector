using System.Runtime.InteropServices;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

public class WriteValidationTests
{
    [Fact]
    public void ScalarFieldsAreAccepted()
        => WriteService.ValidateFields(new Dictionary<string, object?>
        {
            ["Комментарий"] = "x", ["СуммаДокумента"] = 10.5m, ["Проведен"] = false,
            ["Дата"] = DateTime.Now, ["Пусто"] = null
        });

    [Fact]
    public void NonScalarFieldsAreRejected()
        => Assert.Throws<ArgumentException>(() => WriteService.ValidateFields(
            new Dictionary<string, object?> { ["Контрагент"] = new object() }));

    [Fact]
    public void DottedFieldsAreRejected()
        => Assert.Throws<ArgumentException>(() => WriteService.ValidateFields(
            new Dictionary<string, object?> { ["Контрагент.Наименование"] = "x" }));

    [Fact]
    public void IllegalFieldNamesAreRejected()
        => Assert.Throws<ArgumentException>(() => WriteService.ValidateFields(
            new Dictionary<string, object?> { ["Комментарий\" = 1; "] = "x" }));

    [Theory]
    [InlineData(WriteKind.Post, true, 1)]
    [InlineData(WriteKind.Post, false, 1)]
    [InlineData(WriteKind.Delete, false, 1)]
    [InlineData(WriteKind.Create, false, 4)]
    [InlineData(WriteKind.Create, true, 2)]
    [InlineData(WriteKind.Update, false, 2)]
    public void GateLimitsFollowTheMeasuredCeilings(WriteKind k, bool file, int expected)
    {
        var b = new OneCBase
        {
            Name = "b",
            ConnectionString = file ? "File=\"D:\\b\";" : "Srvr=\"s\";Ref=\"r\";"
        };
        Assert.Equal(expected, WriteGates.Limit(b, k));
    }
}

/// <summary>
/// Live write tests. They create real documents on the test bases, tagged
/// <c>AIBA_REWRITE_TEST_&lt;run&gt;</c>, and delete everything they create. Server-base tests
/// use ПоступлениеТоваровУслуг (writable on KAN); РеализацияТоваровУслуг is the known KAN
/// save failure and doubles as the runtime-error case.
/// </summary>
[Collection("onec-live")]
public class WriteLiveTests
{
    private const string Doc = "ПоступлениеТоваровУслуг";
    private static readonly string Marker = "AIBA_REWRITE_TEST_" + DateTime.Now.ToString("MMddHHmmss");

    private readonly LiveFixture _f;
    public WriteLiveTests(LiveFixture f) => _f = f;

    private SessionManager M => _f.Manager!;
    private WriteService W => new(M, "AIBA_REWRITE_");

    private bool NoServer => !_f.Available || _f.Server is null;

    [Fact]
    public void ServerLifecycleCreatesUpdatesMarksAndDeletes()
    {
        if (NoServer) return;
        var w = W;
        string b = _f.Server!.Name;
        var created = w.CreateByClone(b, Doc, Marker + " lifecycle");
        try
        {
            Assert.True(Guid.TryParse(created.Ref, out _));
            Assert.False(string.IsNullOrWhiteSpace(created.Number));
            // FindOwned must actually find it — otherwise "0 left" after cleanup proves nothing.
            Assert.Contains(created.Ref, w.FindOwned(b, Doc, Marker));

            var upd = w.Update(b, Doc, created.Ref,
                new Dictionary<string, object?> { ["Комментарий"] = Marker + " lifecycle updated" });
            Assert.Equal(created.Ref, upd.Ref);

            w.MarkForDeletion(b, Doc, created.Ref);
            w.MarkForDeletion(b, Doc, created.Ref, mark: false);
        }
        finally
        {
            w.Delete(b, Doc, created.Ref);
        }
        Assert.Empty(w.FindOwned(b, Doc, Marker));
    }

    [Fact]
    public void RefusesToTouchADocumentAibaDidNotCreate()
    {
        if (NoServer) return;
        string b = _f.Server!.Name;

        // Newest real document whose comment does NOT carry our test prefix.
        string foreign = M.Use(b, ctx =>
        {
            using var scope = new ComScope();
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Dispatch.Set(q, "Текст",
                $"ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка ИЗ Документ.{Doc} " +
                "ГДЕ НЕ Комментарий ПОДОБНО \"AIBA~_REWRITE%\" СПЕЦСИМВОЛ \"~\" УПОРЯДОЧИТЬ ПО Дата УБЫВ", ctx.Error);
            var res = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res");
            var cur = scope.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "cur");
            Assert.True(Dispatch.CallBool(cur, "Следующий", ctx.Error));
            var r = scope.Track(Dispatch.Get(cur, "Ссылка", ctx.Error), "ref");
            return OneCValue.RefGuid(r, ctx)!;
        });

        var w = W;
        var ex = Assert.Throws<OneCException>(() => w.Update(b, Doc, foreign,
            new Dictionary<string, object?> { ["Номер"] = "SHOULD-NEVER-BE-WRITTEN" }));
        Assert.Equal(OneCLayer.Host, ex.Layer);
        Assert.Contains("not created by AIBA", ex.Message);

        // Post / unpost of a customer-created document by exact ref is ALLOWED since D53 (not exercised
        // here: this is a server base). The untyped field update, delete and deletion mark stay owned-only.
        Assert.Throws<OneCException>(() => w.Delete(b, Doc, foreign));
        Assert.Throws<OneCException>(() => w.MarkForDeletion(b, Doc, foreign));
    }

    [Fact]
    public void CommentWithoutThePrefixIsRejectedBeforeTouching1C()
    {
        if (NoServer) return;
        long before = ComRef.Created;
        Assert.Throws<ArgumentException>(() => W.CreateByClone(_f.Server!.Name, Doc, "no marker here"));
        Assert.Equal(before, ComRef.Created);
    }

    [Fact]
    public void UnknownGuidIsANotFoundHostError()
    {
        if (NoServer) return;
        var ex = Assert.Throws<OneCException>(() =>
            W.Update(_f.Server!.Name, Doc, Guid.NewGuid().ToString(),
                     new Dictionary<string, object?> { ["Комментарий"] = Marker }));
        Assert.Equal(OneCLayer.Host, ex.Layer);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void KnownKanSaveFailureIsAStructuredRuntimeError()
    {
        if (NoServer) return;
        var ex = Assert.Throws<OneCException>(() =>
            W.CreateByClone(_f.Server!.Name, "РеализацияТоваровУслуг", Marker + " expected failure"));
        Assert.Equal(OneCLayer.Runtime, ex.Layer);
        Assert.Equal(1001, ex.OneCCode);
        Assert.Contains("Реализация товаров и услуг", ex.Message);
        Assert.Empty(W.FindOwned(_f.Server!.Name, "РеализацияТоваровУслуг", Marker));
    }

    [Fact]
    public void WritesDoNotLeakComObjects()
    {
        if (NoServer) return;
        var w = W;
        string b = _f.Server!.Name;
        int liveBefore = ComRef.Live;
        int wrongBefore = ComRef.WrongThreadReleases;

        var refs = new List<string>();
        for (int i = 0; i < 3; i++) refs.Add(w.CreateByClone(b, Doc, $"{Marker} leak #{i}").Ref);
        foreach (var r in refs) w.Delete(b, Doc, r);

        // Sessions may be created by these writes (+1 ComRef each for the connection) and
        // may be swept; per-call objects must never accumulate beyond that.
        Assert.True(ComRef.Live <= liveBefore + M.Options.GlobalMaxSessions,
                    $"live refs {liveBefore} -> {ComRef.Live}");
        Assert.Equal(wrongBefore, ComRef.WrongThreadReleases);
        Assert.Empty(w.FindOwned(b, Doc, Marker + " leak"));
    }

    /// <summary>Posting works on the file base (it is blocked on KAN by the config).</summary>
    [Fact]
    public void FileBaseDocumentCanBePostedAndCleanedUp()
    {
        if (!_f.Available || _f.File is null) return;
        var w = W;
        string b = _f.File.Name;
        var created = w.CreateByClone(b, Doc, Marker + " post");
        try
        {
            var posted = w.Post(b, Doc, created.Ref);
            Assert.Equal(created.Ref, posted.Ref);

            bool isPosted = M.Use(b, ctx =>
            {
                using var scope = new ComScope();
                var docs = scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "docs");
                var mgr = scope.Track(Dispatch.Get(docs, Doc, ctx.Error), "mgr");
                var id = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", created.Ref), "id");
                var r = scope.Track(Dispatch.Call(mgr, "ПолучитьСсылку", ctx.Error, id), "ref");
                return Dispatch.GetBool(r, "Проведен", ctx.Error);
            });
            Assert.True(isPosted);
        }
        finally
        {
            w.Delete(b, Doc, created.Ref);
        }
        Assert.Empty(w.FindOwned(b, Doc, Marker + " post"));
    }
}
