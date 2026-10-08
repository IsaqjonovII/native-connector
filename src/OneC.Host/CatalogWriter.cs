using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record CatalogWritten(string Id, string? Code, string Name, bool Created, bool Idempotent, IReadOnlyList<WriteDiagnostic> Diagnostics, int SessionId);

/// <summary>
/// Catalog writes for R9 (PYTHON_TO_RUST_MIGRATION_PLAN): create an item, and compare-and-set update.
/// The same rules as documents (D18, D38): only catalogs that have a Комментарий can carry the
/// ownership marker, only items AIBA created (Комментарий starts with the host's prefix) can be
/// changed, values are converted by the document writer's own rules (exact references, no item
/// auto-created, a value 1C changes is an error), and a create is idempotent by its marker.
/// <para>Compare-and-set: every field in <c>expected</c> must still hold that value in 1C, else
/// nothing is written and the answer names each field with what 1C holds now (<c>conflict</c>) —
/// a cloud that read the item earlier never overwrites a change it has not seen.</para>
/// </summary>
public sealed class CatalogWriter(WriteService writes)
{
    public const string CommentField = "Комментарий";

    /// <summary>A catalog as the write path needs it: its attributes plus Наименование / Код.</summary>
    private static WriteSchema Schema(SessionContext ctx, string catalog) => SchemaCache.Get(ctx, "catalog-write", catalog, (c, name) =>
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
        var meta = MetadataShapes.Find(c, scope, md, "Справочники", name, "catalog");
        var attrs = WriteSchemas.Fields(c, scope, meta);
        int nameLen = MetadataShapes.Length(c, meta, "ДлинаНаименования");
        if (nameLen > 0) attrs["Наименование"] = new FieldType(Array.Empty<string>(), true, nameLen, false, false, false, false);
        int codeLen = MetadataShapes.Length(c, meta, "ДлинаКода");
        if (codeLen > 0)
        {
            bool numeric = Dispatch.HasMember(meta, "ТипКода") &&
                           (Dispatch.Get(meta, "ТипКода", c.Error)?.ToString() ?? "").Contains("Число", StringComparison.Ordinal);
            attrs["Код"] = numeric ? new FieldType(Array.Empty<string>(), false, 0, true, false, false, false)
                                   : new FieldType(Array.Empty<string>(), true, codeLen, false, false, false, false);
        }
        return new WriteSchema(name, codeLen > 0, attrs, new Dictionary<string, WriteTabular>());
    });

    public CatalogWritten Create(string baseName, string catalog, JsonObject body, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(catalog, nameof(catalog));
        string comment = (body[CommentField] as JsonValue)?.TryGetValue(out string? cs) == true ? cs! : "";
        if (!comment.StartsWith(writes.CommentPrefix, StringComparison.Ordinal))
            throw WriteRejected.Bad($"'{CommentField}' must start with the AIBA marker ('{writes.CommentPrefix}…')");
        string? marker = (body["_idempotencyMarker"] as JsonValue)?.TryGetValue(out string? ms) == true ? ms : null;
        if (marker is not null && !comment.StartsWith(marker + ":", StringComparison.Ordinal))
            throw WriteRejected.Bad($"'{CommentField}' must start with '{marker}:' (the idempotency marker)");
        if (body.Select(kv => kv.Key).FirstOrDefault(k => k.StartsWith('_') && k != "_idempotencyMarker") is { } directive)
            throw WriteRejected.Bad($"directive '{directive}' is not accepted");

        var stripe = marker is null ? null
            : DocumentWriter.MarkerStripes[(uint)StringComparer.Ordinal.GetHashCode(baseName.ToLowerInvariant() + "|" + marker) % (uint)DocumentWriter.MarkerStripes.Length];
        if (stripe is not null && !stripe.Wait(writes.GateTimeout, ct))
            throw WriteRejected.Bad($"another write with marker '{marker}' is still running");
        try
        {
            return writes.RunGated(baseName, WriteKind.Create, ct, ctx =>
            {
                using var scope = new ComScope();
                var schema = Schema(ctx, catalog);
                if (schema.Attribute(CommentField) is null)
                    throw new WriteRejected($"Справочник.{catalog} has no {CommentField}: AIBA cannot mark an item there as its own", unprocessable: true,
                                            new[] { new WriteDiagnostic("catalog_not_ownable", WriteDiagnostic.Error, CommentField, "no Комментарий attribute") });
                if (marker is not null && FindByMarker(ctx, scope, catalog, marker) is { } found)
                    return Result(ctx, scope, found, created: false, idempotent: true, Array.Empty<WriteDiagnostic>());

                var diagnostics = new List<WriteDiagnostic>();
                var values = Values(ctx, scope, catalog, schema, body, diagnostics);
                DocumentWriter.Reject(diagnostics, "could not be used");
                var catalogs = scope.Track(Dispatch.Get(ctx.Connection, "Справочники", ctx.Error), "Справочники");
                var manager = scope.Track(Dispatch.Get(catalogs, catalog, ctx.Error), catalog);
                var obj = scope.Track(Dispatch.Call(manager, "СоздатьЭлемент", ctx.Error), "item");
                foreach (var (field, value) in values) DocumentWriter.Set(ctx, obj, field, value, field, diagnostics);
                DocumentWriter.Reject(diagnostics, "were changed by 1C on the way in");
                Dispatch.Call(obj, "Записать", ctx.Error);
                return Result(ctx, scope, obj, created: true, idempotent: false, diagnostics);
            });
        }
        finally { stripe?.Release(); }
    }

    public CatalogWritten CompareAndSet(string baseName, string catalog, string id, JsonObject expected, JsonObject set, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(catalog, nameof(catalog));
        if (!Guid.TryParse(id, out _)) throw WriteRejected.Bad($"'{id}' is not a GUID");
        if (set.Count == 0) throw WriteRejected.Bad("'set' names no field to change");
        if (set.Select(kv => kv.Key).FirstOrDefault(k => k.StartsWith('_')) is { } directive)
            throw WriteRejected.Bad($"directive '{directive}' is not accepted");
        if (set[CommentField] is JsonValue cv && (!cv.TryGetValue(out string? c) || !c.StartsWith(writes.CommentPrefix, StringComparison.Ordinal)))
            throw WriteRejected.Bad($"'{CommentField}' must keep its AIBA marker ('{writes.CommentPrefix}…')");

        return writes.RunGated(baseName, WriteKind.Update, ct, ctx =>
        {
            using var scope = new ComScope();
            var schema = Schema(ctx, catalog);
            var obj = LoadOwned(ctx, scope, catalog, id);

            // Compare first: a field that moved since the caller read it stops the whole write.
            var conflicts = new List<WriteDiagnostic>();
            foreach (var (field, want) in expected)
            {
                if (schema.Attribute(field) is null)
                {
                    conflicts.Add(new WriteDiagnostic("unknown_attribute", WriteDiagnostic.Error, field, $"Справочник.{catalog} has no '{field}'"));
                    continue;
                }
                string now = Normal(ctx, Dispatch.Get(obj, field, ctx.Error));
                string was = Normal(want);
                if (now != was) conflicts.Add(new WriteDiagnostic("conflict", WriteDiagnostic.Error, field, $"expected {Show(was)}, 1C holds {Show(now)}"));
            }
            if (conflicts.Count > 0)
                throw new WriteRejected($"compare-and-set refused: {conflicts.Count} field(s) differ from what the caller expected; nothing written",
                                        unprocessable: true, conflicts);

            var diagnostics = new List<WriteDiagnostic>();
            var values = Values(ctx, scope, catalog, schema, set, diagnostics);
            DocumentWriter.Reject(diagnostics, "could not be used");
            foreach (var (field, value) in values) DocumentWriter.Set(ctx, obj, field, value, field, diagnostics);
            DocumentWriter.Reject(diagnostics, "were changed by 1C on the way in");
            Dispatch.Call(obj, "Записать", ctx.Error);
            return Result(ctx, scope, obj, created: false, idempotent: false, diagnostics);
        });
    }

    /// <summary>Deletes an item AIBA owns — test cleanup through the local edge only; no cloud command does this.</summary>
    public string DeleteOwned(string baseName, string catalog, string id, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(catalog, nameof(catalog));
        if (!Guid.TryParse(id, out _)) throw WriteRejected.Bad($"'{id}' is not a GUID");
        return writes.RunGated(baseName, WriteKind.Delete, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = LoadOwned(ctx, scope, catalog, id);
            Dispatch.Call(obj, "Удалить", ctx.Error);
            return id;
        });
    }

    private static Dictionary<string, object?> Values(SessionContext ctx, ComScope scope, string catalog, WriteSchema schema, JsonObject fields,
                                                      List<WriteDiagnostic> diagnostics)
    {
        var resolver = new RefResolver(ctx, scope, diagnostics);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in fields)
        {
            if (field.StartsWith('_') || value is null) continue;
            if (schema.Attribute(field) is not { } type)
            {
                diagnostics.Add(new WriteDiagnostic("unknown_attribute", WriteDiagnostic.Error, field, $"Справочник.{catalog} has no '{field}'"));
                continue;
            }
            values[field] = DocumentWriter.Convert(field, type, value, null, resolver, diagnostics);
        }
        return values;
    }

    private object LoadOwned(SessionContext ctx, ComScope scope, string catalog, string id)
    {
        var catalogs = scope.Track(Dispatch.Get(ctx.Connection, "Справочники", ctx.Error), "Справочники");
        var manager = scope.Track(Dispatch.Get(catalogs, catalog, ctx.Error), catalog);
        var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", id), "UUID");
        var r = scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");
        var obj = Dispatch.Call(r, "ПолучитьОбъект", ctx.Error);
        if (obj is null || !Marshal.IsComObject(obj))
            throw OneCException.Host($"Справочник.{catalog} {id} not found", ctx.Error, "CatalogUpdate", catalog);
        scope.Add(obj, "item");
        string comment = Dispatch.HasMember(obj, CommentField) ? Dispatch.GetString(obj, CommentField, ctx.Error) ?? "" : "";
        if (!comment.StartsWith(writes.CommentPrefix, StringComparison.Ordinal))
            throw OneCException.Host($"refused: Справочник.{catalog} {id} was not created by AIBA (comment does not start with '{writes.CommentPrefix}')",
                                     ctx.Error, "CatalogUpdate", catalog);
        return obj;
    }

    private static object? FindByMarker(SessionContext ctx, ComScope scope, string catalog, string marker)
    {
        var q = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ 5 Т.Ссылка КАК r, Т.{CommentField} КАК c ИЗ Справочник.{catalog} КАК Т " +
            $"ГДЕ НЕ Т.ПометкаУдаления И Т.{CommentField} ПОДОБНО &m СПЕЦСИМВОЛ \"~\"");
        QueryKit.SetParameter(ctx, q, "m", WriteService.EscapeLike(marker + ":") + "%");
        var cur = QueryKit.Execute(ctx, scope, q);
        while (cur.CallBool("Следующий", ctx.Error))
        {
            // ПОДОБНО ignores case: the comment must start with the marker exactly.
            if (!(cur.Get("c", ctx.Error) as string ?? "").StartsWith(marker + ":", StringComparison.Ordinal)) continue;
            var r = scope.Track(cur.Get("r", ctx.Error), "Ссылка");
            return scope.Track(Dispatch.Call(r, "ПолучитьОбъект", ctx.Error), "item");
        }
        return null;
    }

    private static CatalogWritten Result(SessionContext ctx, ComScope scope, object obj, bool created, bool idempotent, IReadOnlyList<WriteDiagnostic> diagnostics)
    {
        var r = scope.Track(Dispatch.Get(obj, "Ссылка", ctx.Error), "Ссылка");
        string id = OneCValue.RefGuid(r, ctx) ?? throw OneCException.Host("written item has no readable reference", ctx.Error, "Catalog", "");
        string? code = Dispatch.HasMember(obj, "Код") ? Dispatch.Get(obj, "Код", ctx.Error)?.ToString()?.Trim() : null;
        string name = Dispatch.HasMember(obj, "Наименование") ? Dispatch.GetString(obj, "Наименование", ctx.Error) ?? "" : "";
        return new CatalogWritten(id, code, name, created, idempotent, diagnostics, ctx.SessionId);
    }

    /// <summary>A 1C value in the comparable form: text trimmed, numbers by value, dates ISO, a reference by GUID, empty = "".</summary>
    private static string Normal(SessionContext ctx, object? v) => v switch
    {
        null => "",
        string s => s.TrimEnd(),
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        decimal or double or float or int or long or short => System.Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("0.############", CultureInfo.InvariantCulture),
        _ when OneCValue.IsCom(v) => OneCValue.RefGuid(v, ctx) is { } g && g != Guid.Empty.ToString() ? g : "",
        _ => v.ToString() ?? ""
    };

    /// <summary>The caller's expected value in the same form: a string, number, boolean, null, or {"id": guid}.</summary>
    private static string Normal(JsonNode? n) => n switch
    {
        null => "",
        JsonObject o => (o["id"] as JsonValue)?.TryGetValue(out string? g) == true ? g!.ToLowerInvariant() : o.ToJsonString(),
        JsonValue v when v.TryGetValue(out bool b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue(out decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue(out string? s) => DocumentWriteBody.ParseDate(s!) is { } dt && s!.Length >= 10 && s[4] == '-'
            ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : s!.TrimEnd(),
        _ => n.ToJsonString()
    };

    private static string Show(string s) => s.Length == 0 ? "(empty)" : $"'{(s.Length > 80 ? s[..80] + "…" : s)}'";
}
