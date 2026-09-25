using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Diagnostic only — the parity oracle for <see cref="CatalogReadService"/>. A literal port of
/// the old adapter's JSON catalog page (main.os:5983–6446): names selected as-is, every value
/// read by column index and rendered one COM call at a time (<see cref="LegacyValue.Slow"/>),
/// tabular sections through <c>ВЫБРАТЬ *</c>. It shares no query text or value logic with the
/// fast path, which is the point. Slow by design; never on a request path.
/// </summary>
internal static class LegacyCatalogOracle
{
    public static List<Dictionary<string, object?>> Page(SessionContext ctx, CatalogSchema s, string after, int limit)
    {
        using var scope = new ComScope();
        var fields = new List<string> { "Ссылка", "ПометкаУдаления" };
        if (s.HasCode) fields.Add("Код");
        if (s.HasName) fields.Add("Наименование");
        if (s.Hierarchical) fields.Add("Родитель");
        if (s.HasFolders) fields.Add("ЭтоГруппа");
        if (s.HasOwner) fields.Add("ПРЕДСТАВЛЕНИЕ(Владелец) КАК Владелец");
        var attrs = s.Attributes.Select(a => a.Name).ToList();
        fields.AddRange(attrs);

        var q = NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ {limit} {string.Join(", ", fields)} ИЗ Справочник.{s.Name} ГДЕ Ссылка > &ПослеСсылка УПОРЯДОЧИТЬ ПО Ссылка");
        var managers = scope.Track(Dispatch.Get(ctx.Connection, "Справочники", ctx.Error), "Справочники");
        var manager = scope.Track(Dispatch.Get(managers, s.Name, ctx.Error), s.Name);
        var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", after), "UUID");
        var afterRef = scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");
        Set(ctx, q, "ПослеСсылка", afterRef);

        var result = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
        var col = Columns(ctx, scope, result);
        var sel = scope.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка");
        var pageRefs = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Массив"), "Массив");

        var rows = new List<Dictionary<string, object?>>();
        while (Dispatch.CallBool(sel, "Следующий", ctx.Error))
        {
            using var rs = new ComScope();
            object? At(string name) => col.TryGetValue(name, out int i) ? Track(rs, Dispatch.Call(sel, "Получить", ctx.Error, i)) : null;

            var r = At("Ссылка")!;
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = UuidOf(ctx, r) };
            if (col.ContainsKey("Код")) row["code"] = LegacyValue.Scalar(At("Код"));
            if (col.ContainsKey("Наименование")) row["name"] = LegacyValue.Scalar(At("Наименование"));
            row["deletionMark"] = At("ПометкаУдаления");
            if (col.ContainsKey("Родитель")) row["parent"] = LegacyValue.Slow(At("Родитель"), ctx);
            if (col.ContainsKey("ЭтоГруппа")) row["isFolder"] = At("ЭтоГруппа");
            row["Владелец"] = col.ContainsKey("Владелец") ? LegacyValue.Slow(At("Владелец"), ctx) : null;
            foreach (var a in attrs) row[a] = LegacyValue.Slow(At(a), ctx);
            if (s.OrgAttribute is { } org && UuidOf(ctx, At(org)) is { Length: > 0 } g) row["orgRef"] = g;

            Dispatch.Call(pageRefs, "Добавить", ctx.Error, r);
            rows.Add(row);
        }

        Tabular(ctx, "Справочник." + s.Name, s.Tabular.Select(t => t.Name), pageRefs,
                rows.ToDictionary(r => (string)r["id"]!), orderBy: "");
        return rows;
    }

    /// <summary>
    /// The old tabular-section read: <c>ВЫБРАТЬ *</c> per section for the page's refs, every
    /// column but Ссылка / НомерСтроки rendered per value. A section key appears when it gets
    /// its first row, unless the caller created the empty ones already (document by-id shape).
    /// </summary>
    internal static void Tabular(SessionContext ctx, string table, IEnumerable<string> sectionNames, object pageRefs,
                                 Dictionary<string, Dictionary<string, object?>> byId, string orderBy)
    {
        foreach (var tsName in sectionNames)
        {
            using var ss = new ComScope();
            var tq = NewQuery(ctx, ss, $"ВЫБРАТЬ * ИЗ {table}.{tsName} ГДЕ Ссылка В (&РефыСтраницы){orderBy}");
            Set(ctx, tq, "РефыСтраницы", pageRefs);
            var tr = ss.Track(Dispatch.Call(tq, "Выполнить", ctx.Error), "РезультатЗапроса");
            var tcol = Columns(ctx, ss, tr);
            var tsel = ss.Track(Dispatch.Call(tr, "Выбрать", ctx.Error), "Выборка");
            while (Dispatch.CallBool(tsel, "Следующий", ctx.Error))
            {
                using var rs = new ComScope();
                object? At(int i) => Track(rs, Dispatch.Call(tsel, "Получить", ctx.Error, i));
                if (!byId.TryGetValue(UuidOf(ctx, At(tcol["Ссылка"]))!, out var element)) continue;
                var line = new Dictionary<string, object?>(StringComparer.Ordinal) { ["lineNumber"] = At(tcol["НомерСтроки"]) };
                foreach (var (name, i) in tcol.OrderBy(c => c.Value))
                    if (name is not "Ссылка" and not "НомерСтроки") line[name] = LegacyValue.Slow(At(i), ctx);

                if (element.GetValueOrDefault("tabularSections") is not Dictionary<string, object?> sections)
                    element["tabularSections"] = sections = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (sections.GetValueOrDefault(tsName) is not List<Dictionary<string, object?>> list)
                    sections[tsName] = list = new List<Dictionary<string, object?>>();
                list.Add(line);
            }
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The first difference between an oracle page and a fast page as text, or null when they
    /// serialise identically (key order included). The oracle's tabular lines come in storage
    /// order — the old code had no ORDER BY — so they are sorted by line number first.
    /// </summary>
    public static string? Diff(List<Dictionary<string, object?>> old, List<Dictionary<string, object?>> fast)
    {
        foreach (var row in old)
            if (row.GetValueOrDefault("tabularSections") is Dictionary<string, object?> ts)
                foreach (var k in ts.Keys.ToList())
                    ts[k] = ((List<Dictionary<string, object?>>)ts[k]!).OrderBy(l => Convert.ToDecimal(l["lineNumber"])).ToList();

        if (old.Count != fast.Count) return $"{old.Count} old rows vs {fast.Count} fast rows";
        for (int i = 0; i < old.Count; i++)
        {
            string S(object? v) => System.Text.Json.JsonSerializer.Serialize(v, Json);
            if (S(old[i]) == S(fast[i])) continue;
            var keys = old[i].Keys.Union(fast[i].Keys)
                .Where(k => S(old[i].GetValueOrDefault(k)) != S(fast[i].GetValueOrDefault(k)))
                .Select(k => $"{k}: old={Trunc(S(old[i].GetValueOrDefault(k)))} fast={Trunc(S(fast[i].GetValueOrDefault(k)))}")
                .ToList();
            if (keys.Count == 0) keys.Add($"key order: old={string.Join(",", old[i].Keys)} fast={string.Join(",", fast[i].Keys)}");
            return $"row {i} ({old[i]["id"]}): " + string.Join("; ", keys);
        }
        return null;
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>ПолучитьUUID (main.os:3073): XMLСтрока(Ссылка.УникальныйИдентификатор()), "" on failure.</summary>
    internal static string? UuidOf(SessionContext ctx, object? v)
    {
        if (!OneCValue.IsCom(v)) return "";
        try
        {
            using var s = new ComScope();
            var u = s.Track(Dispatch.Call(v!, "УникальныйИдентификатор", ctx.Error), "UUID");
            return Dispatch.Call(ctx.Connection, "XMLСтрока", ctx.Error, u) as string ?? "";
        }
        catch (OneCException) { return ""; }
    }

    internal static Dictionary<string, int> Columns(SessionContext ctx, ComScope scope, object result)
    {
        var cols = scope.Track(Dispatch.Get(result, "Колонки", ctx.Error), "Колонки");
        int n = Dispatch.CallInt(cols, "Количество", ctx.Error);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            var c = s.Track(Dispatch.Call(cols, "Получить", ctx.Error, i), "Колонка");
            map[Dispatch.GetString(c, "Имя", ctx.Error)!] = i;
        }
        return map;
    }

    internal static object? Track(ComScope s, object? v)
    {
        if (OneCValue.IsCom(v)) s.Add(v, "value");
        return v;
    }

    internal static object NewQuery(SessionContext ctx, ComScope scope, string text)
    {
        var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", text, ctx.Error);
        return q;
    }

    internal static void Set(SessionContext ctx, object q, string name, object? value)
    {
        var r = Dispatch.Call(q, "УстановитьПараметр", ctx.Error, name, value);
        if (OneCValue.IsCom(r)) { using var s = new ComScope(); s.Add(r, "УстановитьПараметр"); }
    }
}
