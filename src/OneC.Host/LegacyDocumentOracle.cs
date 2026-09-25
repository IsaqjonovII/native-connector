using OneC.Interop;
using OneC.Sessions;
using static OneC.Host.LegacyCatalogOracle;

namespace OneC.Host;

/// <summary>
/// Diagnostic only — the parity oracle for <see cref="DocumentReadService"/>: literal ports of
/// the old JSON list page (ПолучитьДокументы, main.os:7421) and the batch route
/// (ПолучитьДокументыПоИдентификаторам, :8317). Names selected as-is, values read by column
/// index and rendered per value, tabular sections through <c>ВЫБРАТЬ *</c>, the old seek rule
/// (no «Ссылка &gt;» for the zero GUID) and the old organisation ladder of the list. Filters
/// are parsed by <see cref="DocumentFilters"/> (its rules are unit-tested against the old
/// helpers); everything that renders a row is independent of the fast path.
/// </summary>
internal static class LegacyDocumentOracle
{
    public sealed record Page(List<Dictionary<string, object?>> Rows, long Total, string? NextDate, int NextSkip);

    public static Page List(SessionContext ctx, DocumentSchema s, DocumentQuery q)
    {
        using var scope = new ComScope();
        string name = s.Name;
        var filters = DocumentFilters.Parse(q.Filters, s);

        var window = new List<string>();
        if (q.From is not null) window.Add("Дата >= &FromBound");
        if (q.To is not null) window.Add("Дата < &ToBound");

        long total = -1;
        if (q.Limit <= 0 || !q.SkipTotal)
        {
            var cq = NewQuery(ctx, scope, "ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК ВсегоДокументов ИЗ Документ." + name +
                                          (window.Count > 0 ? " ГДЕ " + string.Join(" И ", window) : ""));
            if (q.From is { } f0) Set(ctx, cq, "FromBound", f0);
            if (q.To is { } t0) Set(ctx, cq, "ToBound", t0);
            var cr = scope.Track(Dispatch.Call(cq, "Выполнить", ctx.Error), "РезультатЗапроса");
            var cs = scope.Track(Dispatch.Call(cr, "Выбрать", ctx.Error), "Выборка");
            total = Dispatch.CallBool(cs, "Следующий", ctx.Error) ? Convert.ToInt64(Dispatch.Get(cs, "ВсегоДокументов", ctx.Error)) : 0;
        }
        if (q.Limit <= 0) return new Page(new(), total, null, 0);

        // The list's own organisation ladder (main.os:7538): no УдалитьТекущаяОрганизация.
        var names = s.Attributes.Select(a => a.Name).ToHashSet();
        string? org = names.Contains("Организация") ? "Организация"
                    : names.Contains("ГоловнаяОрганизация") ? "ГоловнаяОрганизация"
                    : names.Contains("УдалитьОрганизация") ? "УдалитьОрганизация" : null;
        // Literal: an EMPTY field list selects every attribute (main.os:7545) — the fast path
        // treats it as "none", the stated intent (D33); parity runs use null or a real list.
        var attrs = q.Fields is null || q.Fields.Count == 0
            ? s.Attributes.Select(a => a.Name).ToList()
            : q.Fields.Select(f => f.Trim()).Where(f => names.Contains(f)).Distinct().ToList();
        if (org is not null && !attrs.Contains(org)) attrs.Add(org);

        var fields = new List<string> { "Ссылка", "Дата", "Проведен", "ПометкаУдаления" };
        if (s.HasNumber) fields.Add("Номер");
        fields.AddRange(attrs);

        bool asc = q.Order.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase);
        var parts = new List<string>();
        if (filters.Where is { } fw) parts.Add(fw);
        if (q.CursorDate is not null) parts.Add(asc ? "Дата >= &CursorDate" : "Дата <= &CursorDate");
        parts.AddRange(window);
        string where = parts.Count > 0 ? " ГДЕ " + string.Join(" И ", parts) : "";

        bool keyset = !string.IsNullOrEmpty(q.After);
        bool start = keyset && q.After == LegacyValue.EmptyGuid;
        string text = keyset
            ? $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit} {string.Join(", ", fields)} ИЗ Документ.{name}" +
              (start ? where : (where == "" ? " ГДЕ " : where + " И ") + "Ссылка > &ПослеСсылка") + " УПОРЯДОЧИТЬ ПО Ссылка"
            : $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset} {string.Join(", ", fields)} ИЗ Документ.{name}{where} " +
              $"УПОРЯДОЧИТЬ ПО Дата {(asc ? "ВОЗР" : "УБЫВ")}, Ссылка";

        var query = NewQuery(ctx, scope, text);
        if (q.CursorDate is { } cd) Set(ctx, query, "CursorDate", cd);
        if (q.From is { } f1) Set(ctx, query, "FromBound", f1);
        if (q.To is { } t1) Set(ctx, query, "ToBound", t1);
        foreach (var t in filters.Terms) Set(ctx, query, t.Param, t.Value);
        if (keyset && !start)
        {
            var managers = scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы");
            var manager = scope.Track(Dispatch.Get(managers, name, ctx.Error), name);
            var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", q.After), "UUID");
            Set(ctx, query, "ПослеСсылка", scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка"));
        }

        var result = scope.Track(Dispatch.Call(query, "Выполнить", ctx.Error), "РезультатЗапроса");
        var col = Columns(ctx, scope, result);
        var sel = scope.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка");
        var pageRefs = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Массив"), "Массив");

        var rows = new List<Dictionary<string, object?>>();
        int skipped = 0;
        while (Dispatch.CallBool(sel, "Следующий", ctx.Error))
        {
            using var rs = new ComScope();
            object? At(string n) => col.TryGetValue(n, out int i) ? Track(rs, Dispatch.Call(sel, "Получить", ctx.Error, i)) : null;
            var r = At("Ссылка")!;
            var row = Header(ctx, col, At, r);
            foreach (var a in attrs) row[a] = LegacyValue.Slow(At(a), ctx);
            if (org is not null && UuidOf(ctx, At(org)) is { Length: > 0 } g) row["orgRef"] = g;

            if (!filters.Matches(row)) continue;
            if (skipped < q.Offset) { skipped++; continue; }
            if (rows.Count >= q.Limit) break;
            Dispatch.Call(pageRefs, "Добавить", ctx.Error, r);
            rows.Add(row);
        }

        if (rows.Count > 0 && q.Tabular)
            Tabular(ctx, "Документ." + name, s.Tabular.Select(t => t.Name), pageRefs, rows.ToDictionary(r => (string)r["id"]!), orderBy: "");

        // next_cursor_date / next_cursor_skip, main.os:8093.
        string? nextDate = null; int nextSkip = 0;
        if (rows.Count > 0)
        {
            var boundary = rows[^1]["date"];
            int n = 0;
            for (int i = rows.Count - 1; i >= 0 && Equals(rows[i]["date"], boundary); i--) n++;
            string incoming = q.CursorDate is { } c ? c.ToString("yyyy-MM-dd'T'HH:mm:ss") : "";
            nextSkip = Equals(incoming, boundary) ? q.Offset + n : n;
            nextDate = (string?)boundary;
        }
        return new Page(rows, total, nextDate, nextSkip);
    }

    public static List<Dictionary<string, object?>> Batch(SessionContext ctx, DocumentSchema s, IReadOnlyList<string> ids)
    {
        var result = new List<Dictionary<string, object?>>();
        string name = s.Name;
        var attrs = s.Attributes.Select(a => a.Name).ToList();
        var fields = new List<string> { "Ссылка", "Дата", "Проведен", "ПометкаУдаления" };
        if (s.HasNumber) fields.Add("Номер");
        fields.AddRange(attrs);
        string? org = MetadataShapes.OrgAttribute(attrs);

        foreach (var chunk in ids.Chunk(200))
        {
            using var scope = new ComScope();
            var managers = scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы");
            var manager = scope.Track(Dispatch.Get(managers, name, ctx.Error), name);
            var refs = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Массив"), "Массив");
            foreach (var id in chunk)
            {
                using var one = new ComScope();
                var uuid = one.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", id), "UUID");
                Dispatch.Call(refs, "Добавить", ctx.Error, one.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка"));
            }
            var query = NewQuery(ctx, scope, $"ВЫБРАТЬ {string.Join(", ", fields)} ИЗ Документ.{name} ГДЕ Ссылка В (&Ссылки)");
            Set(ctx, query, "Ссылки", refs);
            var res = scope.Track(Dispatch.Call(query, "Выполнить", ctx.Error), "РезультатЗапроса");
            var col = Columns(ctx, scope, res);
            var sel = scope.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "Выборка");

            var byId = new Dictionary<string, Dictionary<string, object?>>();
            while (Dispatch.CallBool(sel, "Следующий", ctx.Error))
            {
                using var rs = new ComScope();
                object? At(string n) => col.TryGetValue(n, out int i) ? Track(rs, Dispatch.Call(sel, "Получить", ctx.Error, i)) : null;
                var row = Header(ctx, col, At, At("Ссылка")!);
                foreach (var a in attrs) row[a] = LegacyValue.Slow(At(a), ctx);
                if (org is not null && UuidOf(ctx, At(org)) is { Length: > 0 } g) row["orgRef"] = g;
                if (s.Tabular.Count > 0)
                    row["tabularSections"] = s.Tabular.ToDictionary(t => t.Name, _ => (object?)new List<Dictionary<string, object?>>());
                byId[(string)row["id"]!] = row;
            }
            Tabular(ctx, "Документ." + name, s.Tabular.Select(t => t.Name), refs, byId, orderBy: " УПОРЯДОЧИТЬ ПО Ссылка, НомерСтроки");
            foreach (var id in chunk)
                if (byId.TryGetValue(id, out var row)) result.Add(row);
        }
        return result;
    }

    private static Dictionary<string, object?> Header(SessionContext ctx, Dictionary<string, int> col, Func<string, object?> at, object r)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = UuidOf(ctx, r) };
        if (col.ContainsKey("Номер")) row["number"] = LegacyValue.Scalar(at("Номер"));
        row["date"] = LegacyValue.Scalar(at("Дата"));
        row["posted"] = at("Проведен");
        row["deletionMark"] = at("ПометкаУдаления");
        return row;
    }
}
