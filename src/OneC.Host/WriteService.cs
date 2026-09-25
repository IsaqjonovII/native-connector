using System.Diagnostics;
using System.Runtime.InteropServices;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record WriteResult(
    string BaseName, string DocumentType, string Ref, string? Number,
    WriteKind Kind, long ElapsedMs, int SessionId);

/// <summary>
/// The write vertical slice: create, update, post, mark-for-deletion and delete for 1C
/// documents, with no <c>dynamic</c> and every COM object scoped to the session thread.
///
/// Ownership guard: the host only ever modifies documents it created. Every document it
/// writes gets a Комментарий starting with <see cref="CommentPrefix"/> (AIBA's canonical
/// <c>AIBA_&lt;KIND&gt;_…</c> marker), and update / post / delete refuse anything whose
/// comment does not start with it. That is a production rule, not only a test safety net —
/// the connector must never rewrite an accountant's own documents.
/// </summary>
public sealed class WriteService
{
    private readonly SessionManager _sessions;
    private readonly WriteGates _gates = new();

    public string CommentPrefix { get; }
    public TimeSpan GateTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public WriteService(SessionManager sessions, string commentPrefix = "AIBA_")
    {
        if (string.IsNullOrWhiteSpace(commentPrefix) || !commentPrefix.StartsWith("AIBA_", StringComparison.Ordinal))
            throw new ArgumentException("comment prefix must start with AIBA_", nameof(commentPrefix));
        _sessions = sessions;
        CommentPrefix = commentPrefix;
    }

    // ---------------- operations ----------------

    /// <summary>
    /// Creates a new document by cloning the most recent posted one of that type, then
    /// stamping date, comment and any extra scalar fields. Cloning is how research created
    /// valid documents without re-implementing every mandatory attribute of a config.
    /// </summary>
    public WriteResult CreateByClone(string baseName, string docType, string comment,
                                     IReadOnlyDictionary<string, object?>? fields = null,
                                     DateTime? date = null, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        RequireOwnedComment(comment);
        ValidateFields(fields);

        return Gated(baseName, WriteKind.Create, ct, ctx =>
        {
            using var scope = new ComScope();
            var source = FindNewestPosted(ctx, scope, docType)
                ?? throw OneCException.Host($"no posted {docType} to clone from", ctx.Error, "CreateByClone", docType);

            var original = scope.Track(Dispatch.Call(source, "ПолучитьОбъект", ctx.Error), "source object");
            var obj = scope.Track(Dispatch.Call(original, "Скопировать", ctx.Error), "new object");

            Dispatch.Set(obj, "Дата", date ?? DateTime.Now, ctx.Error);
            Dispatch.Set(obj, "Комментарий", comment, ctx.Error);
            ApplyFields(ctx, obj, fields);
            Dispatch.Call(obj, "Записать", ctx.Error);

            return Result(ctx, scope, docType, obj, WriteKind.Create);
        });
    }

    public WriteResult Update(string baseName, string docType, string refGuid,
                              IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        ValidateFields(fields);
        if (fields.TryGetValue("Комментарий", out var c)) RequireOwnedComment(c as string);

        return Gated(baseName, WriteKind.Update, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, docType, refGuid, "Update");
            ApplyFields(ctx, obj, fields);
            Dispatch.Call(obj, "Записать", ctx.Error);
            return Result(ctx, scope, docType, obj, WriteKind.Update);
        });
    }

    /// <summary>
    /// Posts (проводит) an owned document; an already posted one is posted again, as before
    /// (main.os:17431) — without the old path's fill steps and draft-write retry (D38).
    /// K=1 per base, see DECISIONS D12.
    /// </summary>
    public WriteResult Post(string baseName, string docType, string refGuid, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        return Gated(baseName, WriteKind.Post, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, docType, refGuid, "Post");
            DocumentWriter.WriteObject(ctx, obj, docType, "Проведение", new List<WriteDiagnostic>());
            return Result(ctx, scope, docType, obj, WriteKind.Post);
        });
    }

    /// <summary>Clears the posting of an owned document (main.os:17563).</summary>
    public WriteResult Unpost(string baseName, string docType, string refGuid, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        return Gated(baseName, WriteKind.Post, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, docType, refGuid, "Unpost");
            DocumentWriter.WriteObject(ctx, obj, docType, "ОтменаПроведения", new List<WriteDiagnostic>());
            return Result(ctx, scope, docType, obj, WriteKind.Post);
        });
    }

    public WriteResult MarkForDeletion(string baseName, string docType, string refGuid,
                                       bool mark = true, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        return Gated(baseName, WriteKind.MarkDeleted, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, docType, refGuid, "MarkForDeletion");
            Dispatch.Call(obj, "УстановитьПометкуУдаления", ctx.Error, mark);
            return Result(ctx, scope, docType, obj, WriteKind.MarkDeleted);
        });
    }

    /// <summary>
    /// Direct, irreversible delete of an owned document. K=1 per base and slow (research:
    /// 0.7–2.5 op/s, 9 s tail) — callers should run it off the request path.
    /// </summary>
    public WriteResult Delete(string baseName, string docType, string refGuid, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        return Gated(baseName, WriteKind.Delete, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, docType, refGuid, "Delete");
            string? number = Dispatch.GetString(obj, "Номер", ctx.Error);
            Dispatch.Call(obj, "Удалить", ctx.Error);
            return new WriteResult(ctx.Base.Name, docType, refGuid, number, WriteKind.Delete, 0, ctx.SessionId);
        });
    }

    /// <summary>
    /// Every owned document of a type whose comment starts with <paramref name="prefix"/>.
    /// Used for cleanup; the prefix must itself be an owned (AIBA_) prefix.
    /// </summary>
    public IReadOnlyList<string> FindOwned(string baseName, string docType, string prefix,
                                           CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        RequireOwnedComment(prefix);
        if (prefix.Contains('"')) throw new ArgumentException("prefix contains a quote", nameof(prefix));

        return _sessions.Use(baseName, ctx =>
        {
            using var scope = new ComScope();
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Dispatch.Set(q, "Текст",
                $"ВЫБРАТЬ Ссылка, Комментарий ИЗ Документ.{docType} " +
                "ГДЕ Комментарий ПОДОБНО &Prefix СПЕЦСИМВОЛ \"~\"", ctx.Error);
            using (var p = new ComScope())
            {
                var r = Dispatch.Call(q, "УстановитьПараметр", ctx.Error, "Prefix", EscapeLike(prefix) + "%");
                if (r is not null && Marshal.IsComObject(r)) p.Add(r, "УстановитьПараметр");
            }
            var res = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
            var cur = scope.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "Выборка");

            var refs = new List<string>();
            while (Dispatch.CallBool(cur, "Следующий", ctx.Error))
            {
                using var row = new ComScope();
                string comment = Dispatch.GetString(cur, "Комментарий", ctx.Error) ?? "";
                // Belt and braces: ПОДОБНО is case-insensitive and treats _ as a wildcard.
                if (!comment.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var r = row.Track(Dispatch.Get(cur, "Ссылка", ctx.Error), "Ссылка");
                string? guid = OneCValue.RefGuid(r, ctx);
                if (guid is not null) refs.Add(guid);
            }
            return (IReadOnlyList<string>)refs;
        }, ct);
    }

    // ---------------- plumbing ----------------

    private WriteResult Gated(string baseName, WriteKind kind, CancellationToken ct,
                              Func<SessionContext, WriteResult> work)
    {
        var sw = Stopwatch.StartNew();
        var r = RunGated(baseName, kind, ct, work);
        return r with { ElapsedMs = sw.ElapsedMilliseconds };
    }

    /// <summary>
    /// <paramref name="work"/> on a session of the base, inside this service's write gate for
    /// <paramref name="kind"/> (D12) — the one gate set every writer of the host shares.
    /// </summary>
    internal T RunGated<T>(string baseName, WriteKind kind, CancellationToken ct, Func<SessionContext, T> work) =>
        _gates.Run(_sessions.GetBase(baseName), kind, GateTimeout, () => _sessions.Use(baseName, work, ct), ct);

    private static object? FindNewestPosted(SessionContext ctx, ComScope scope, string docType)
    {
        var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст",
            $"ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка ИЗ Документ.{docType} ГДЕ Проведен УПОРЯДОЧИТЬ ПО Дата УБЫВ", ctx.Error);
        var res = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
        var cur = scope.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "Выборка");
        if (!Dispatch.CallBool(cur, "Следующий", ctx.Error)) return null;
        return scope.Track(Dispatch.Get(cur, "Ссылка", ctx.Error), "Ссылка");
    }

    /// <summary>Loads a document object by GUID and refuses it unless AIBA created it.</summary>
    internal object LoadOwned(SessionContext ctx, ComScope scope, string docType, string refGuid, string op)
    {
        if (!Guid.TryParse(refGuid, out _))
            throw new ArgumentException($"'{refGuid}' is not a GUID", nameof(refGuid));

        var docs = scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы");
        var manager = scope.Track(Dispatch.Get(docs, docType, ctx.Error), docType);
        var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", refGuid), "UUID");
        var r = scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");

        // ПолучитьСсылку returns a "broken" ref for a GUID that does not exist; ПолучитьОбъект
        // on it gives Неопределено rather than an error.
        var obj = Dispatch.Call(r, "ПолучитьОбъект", ctx.Error);
        if (obj is null || !Marshal.IsComObject(obj))
            throw OneCException.Host($"{docType} {refGuid} not found", ctx.Error, op, docType);
        scope.Add(obj, "object");

        string comment = Dispatch.GetString(obj, "Комментарий", ctx.Error) ?? "";
        if (!comment.StartsWith(CommentPrefix, StringComparison.Ordinal))
            throw OneCException.Host(
                $"refused: {docType} {refGuid} was not created by AIBA (comment does not start with '{CommentPrefix}')",
                ctx.Error, op, docType);
        return obj;
    }

    private static WriteResult Result(SessionContext ctx, ComScope scope, string docType, object obj, WriteKind kind)
    {
        var r = scope.Track(Dispatch.Get(obj, "Ссылка", ctx.Error), "Ссылка");
        string guid = OneCValue.RefGuid(r, ctx)
            ?? throw OneCException.Host("written document has no readable reference", ctx.Error, kind.ToString(), docType);
        return new WriteResult(ctx.Base.Name, docType, guid, Dispatch.GetString(obj, "Номер", ctx.Error),
                               kind, 0, ctx.SessionId);
    }

    private void RequireOwnedComment(string? comment)
    {
        if (comment is null || !comment.StartsWith(CommentPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"comment must start with '{CommentPrefix}'", nameof(comment));
    }

    /// <summary>
    /// Only CLR scalars are written for now. References (Контрагент, Склад…) need a lookup
    /// layer that is part of subsystem migration, not of this slice.
    /// </summary>
    internal static void ValidateFields(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null) return;
        foreach (var (k, v) in fields)
        {
            ReadService.ValidateIdentifier(k, "field");
            if (k.Contains('.')) throw new ArgumentException($"field '{k}': dotted paths cannot be written", nameof(fields));
            if (v is not (null or string or bool or int or long or decimal or double or DateTime))
                throw new ArgumentException($"field '{k}': only scalar values can be written, got {v.GetType().Name}", nameof(fields));
        }
    }

    private static void ApplyFields(SessionContext ctx, object obj, IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null) return;
        foreach (var (k, v) in fields) Dispatch.Set(obj, k, v, ctx.Error);
    }

    internal static string EscapeLike(string s) =>
        s.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[").Replace("]", "~]");
}
