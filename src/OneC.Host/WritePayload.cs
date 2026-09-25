using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// An existing document as an old-style create body: every filled attribute and tabular value,
/// references as <c>{type, id}</c>, enums as <c>{type, value}</c>, dates as ISO text. The write
/// sweep (<see cref="WriteParityScenario"/>) writes it back as a new document and compares the
/// two bodies — the write path's round-trip oracle. Session thread only.
/// </summary>
internal static class WritePayload
{
    /// <summary>
    /// Attributes that differ by nature between a document and its copy. The configuration
    /// recomputes some on every write, whatever was sent: ДатаСоздания (stamped on a new object,
    /// KAN ОтчетОРозничныхПродажах), КраткийСоставДокумента (a summary of the rows, bilim
    /// НачислениеЗарплаты — same rows, other order than when the original was written).
    /// </summary>
    public static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        DocumentWriteBody.CommentField, "Номер", "Проведен", "ПометкаУдаления", "Ссылка", "ВерсияДанных", "ДатаСоздания",
        "КраткийСоставДокумента"
    };

    public static JsonObject FromObject(SessionContext ctx, WriteSchema schema, object obj)
    {
        var body = new JsonObject();
        if (Dispatch.Get(obj, "Дата", ctx.Error) is DateTime d) body["Date"] = Iso(d);
        foreach (var name in schema.Attributes.Keys.Where(n => !Ignored.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
            if (Value(ctx, Dispatch.Get(obj, name, ctx.Error)) is { } v) body[name] = v;

        var sections = new JsonObject();
        foreach (var (section, ts) in schema.Tabular.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            using var s = new ComScope();
            var table = s.Track(Dispatch.Get(obj, section, ctx.Error), section);
            int n = Dispatch.CallInt(table, "Количество", ctx.Error);
            if (n == 0) continue;
            var rows = new JsonArray();
            for (int i = 0; i < n; i++)
            {
                using var rs = new ComScope();
                var line = rs.Track(Dispatch.Call(table, "Получить", ctx.Error, i), "row");
                var row = new JsonObject();
                foreach (var column in ts.Columns.Keys.OrderBy(c => c, StringComparer.Ordinal))
                    if (Value(ctx, Dispatch.Get(line, column, ctx.Error)) is { } v) row[column] = v;
                rows.Add(row);
            }
            sections[section] = rows;
        }
        if (sections.Count > 0) body["tabularSections"] = sections;
        return body;
    }

    /// <summary>The body of document <paramref name="guid"/>.</summary>
    public static JsonObject FromRef(SessionContext ctx, string document, string guid)
    {
        using var scope = new ComScope();
        var r = QueryKit.RefByGuid(ctx, scope, "Документы", document, guid);
        var obj = scope.Track(Dispatch.Call(r, "ПолучитьОбъект", ctx.Error), document);
        return FromObject(ctx, WriteSchemas.Get(ctx, document), obj);
    }

    /// <summary>
    /// One value as the payload carries it; null for "not filled". The value is released here:
    /// only its text leaves.
    /// </summary>
    private static JsonNode? Value(SessionContext ctx, object? v)
    {
        switch (v)
        {
            case null or DBNull: return null;
            case string s: return s.Length == 0 ? null : s;
            case bool b: return b;
            case DateTime d: return d == DateTime.MinValue || d.Year <= 1 ? null : Iso(d);
            case decimal or double or float or int or long or short or byte:
                // Divide by 1.000…: same value without trailing zeros, so 1.50 and 1.5 compare equal.
                return JsonValue.Create(Convert.ToDecimal(v, CultureInfo.InvariantCulture) / 1.000000000000000000000000000000000m);
        }
        if (!OneCValue.IsCom(v)) return null;
        using var s2 = new ComScope();
        s2.Add(v, "value");
        var x = Dispatch.Call(ctx.Connection, "XMLТипЗнч", ctx.Error, v);
        if (!OneCValue.IsCom(x)) return null;                     // ХранилищеЗначения and the like
        s2.Add(x, "ТипДанныхXML");
        string? type = Dispatch.GetString(x!, "ИмяТипа", ctx.Error);
        if (type is null || RefTable.Parse(type) is null) return null;
        string? text = Dispatch.Call(ctx.Connection, "XMLСтрока", ctx.Error, v) as string;
        if (string.IsNullOrEmpty(text) || text == LegacyValue.EmptyGuid) return null;
        return type.StartsWith("EnumRef.", StringComparison.Ordinal)
            ? new JsonObject { ["type"] = type, ["value"] = text }
            : new JsonObject { ["type"] = type, ["id"] = text };
    }

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Field paths where the two bodies differ (Комментарий and numbering excluded).</summary>
    public static List<string> Diff(JsonObject expected, JsonObject actual)
    {
        var diffs = new List<string>();
        foreach (var key in expected.Select(p => p.Key).Union(actual.Select(p => p.Key)).Distinct())
        {
            if (key == "tabularSections") continue;
            string a = expected[key]?.ToJsonString() ?? "∅", b = actual[key]?.ToJsonString() ?? "∅";
            if (a != b) diffs.Add($"{key}: {a} → {b}");
        }
        var es = expected["tabularSections"] as JsonObject ?? new JsonObject();
        var acts = actual["tabularSections"] as JsonObject ?? new JsonObject();
        foreach (var section in es.Select(p => p.Key).Union(acts.Select(p => p.Key)).Distinct())
        {
            var ea = es[section] as JsonArray ?? new JsonArray();
            var aa = acts[section] as JsonArray ?? new JsonArray();
            if (ea.Count != aa.Count) { diffs.Add($"{section}: {ea.Count} rows → {aa.Count}"); continue; }
            for (int i = 0; i < ea.Count; i++)
                foreach (var d in Diff((JsonObject)ea[i]!, (JsonObject)aa[i]!))
                    diffs.Add($"{section}[{i}].{d}");
        }
        return diffs;
    }
}
