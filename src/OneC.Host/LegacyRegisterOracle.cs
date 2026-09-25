using OneC.Interop;
using OneC.Sessions;
using static OneC.Host.LegacyCatalogOracle;

namespace OneC.Host;

/// <summary>
/// Diagnostic only — the parity oracle for <see cref="RegisterReadService"/>: literal ports of
/// ПолучитьДанныеРегистраСведений / …Накопления / …Бухгалтерии (main.os:17911, :18078, :18286),
/// JSON paths. <c>ВЫБРАТЬ *</c>, values by column index rendered per value, the old order
/// (information and accumulation registers by Период only), the old count rule (always, except
/// accounting with skipTotal).
/// </summary>
internal static class LegacyRegisterOracle
{
    public sealed record Page(List<Dictionary<string, object?>> Rows, long Total, string? NextDate, int NextSkip, bool? HasMore);

    public static Page Read(SessionContext ctx, RegisterQuery q)
    {
        using var scope = new ComScope();
        string table = $"{RegisterSchemas.Prefix(q.Kind)}.{q.Register}";
        bool accounting = q.Kind == RegisterKind.Accounting;

        long total = -1;
        if (!accounting || q.Limit <= 0 || !q.SkipTotal)
        {
            var cq = NewQuery(ctx, scope, $"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК ВсегоЗаписей ИЗ {table}");
            var cr = scope.Track(Dispatch.Call(cq, "Выполнить", ctx.Error), "РезультатЗапроса");
            var cs = scope.Track(Dispatch.Call(cr, "Выбрать", ctx.Error), "Выборка");
            total = Dispatch.CallBool(cs, "Следующий", ctx.Error) ? Convert.ToInt64(Dispatch.Call(cs, "Получить", ctx.Error, 0)) : 0;
        }
        if (q.Limit <= 0) return new Page(new(), total, null, 0, null);

        bool asc = q.Order.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase);
        string dir = asc ? "ВОЗР" : "УБЫВ";
        string where = "";
        if (q.CursorDate is not null) where = asc ? " ГДЕ Период >= &CursorDate" : " ГДЕ Период <= &CursorDate";
        if (q.To is not null) where = (where == "" ? " ГДЕ " : where + " И ") + "Период < &ToBound";

        object query;
        int skip = q.Offset;
        if (accounting)
        {
            // The row-count window, main.os:18393 — written out again, not shared with the fast path.
            DateTime? edge = null, start = null; int inner = 0;
            using (var ws = new ComScope())
            {
                var bq = NewQuery(ctx, ws, $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset + 1} Период ИЗ {table}{where} УПОРЯДОЧИТЬ ПО Период {dir}");
                if (q.CursorDate is { } c0) Set(ctx, bq, "CursorDate", c0);
                if (q.To is { } t0) Set(ctx, bq, "ToBound", t0);
                var br = ws.Track(Dispatch.Call(bq, "Выполнить", ctx.Error), "r");
                var bs = ws.Track(Dispatch.Call(br, "Выбрать", ctx.Error), "s");
                int n = 0, run = 0; DateTime? prev = null;
                while (Dispatch.CallBool(bs, "Следующий", ctx.Error))
                {
                    var p = (DateTime)Dispatch.Get(bs, "Период", ctx.Error)!;
                    if (prev is not null && p == prev) run++; else { prev = p; run = 0; }
                    if (n == q.Offset) { start = p; inner = run; }
                    edge = p; n++;
                }
                if (n <= q.Limit + q.Offset) edge = null;
                if (n <= q.Offset) { start = null; inner = 0; }
            }
            if (q.To is { } tb && edge is null) edge = tb;
            skip = start is not null ? inner : q.Offset;
            string l = "", r = "";
            if (asc) { l = start is not null ? "&WindowStart" : q.CursorDate is not null ? "&CursorDate" : ""; if (edge is not null) r = "&WindowEdge"; }
            else { if (edge is not null) l = "&WindowEdge"; r = start is not null ? "&WindowStart" : q.CursorDate is not null ? "&CursorDate" : ""; }

            query = NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + skip} * ИЗ {table}.ДвиженияССубконто({l}, {r}) " +
                                         $"УПОРЯДОЧИТЬ ПО Период {dir}, Регистратор {dir}, НомерСтроки {dir}");
            if (q.CursorDate is { } c1 && (l + r).Contains("&CursorDate")) Set(ctx, query, "CursorDate", c1);
            if (edge is { } e) Set(ctx, query, "WindowEdge", e);
            if (start is { } s) Set(ctx, query, "WindowStart", s);
        }
        else
        {
            query = NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset} * ИЗ {table}{where} УПОРЯДОЧИТЬ ПО Период {dir}");
            if (q.CursorDate is { } c2) Set(ctx, query, "CursorDate", c2);
            if (q.To is { } t2) Set(ctx, query, "ToBound", t2);
        }

        object result;
        try { result = scope.Track(Dispatch.Call(query, "Выполнить", ctx.Error), "РезультатЗапроса"); }
        catch (OneCException) when (q.Kind == RegisterKind.Information)
        {
            // Non-periodic information register: no cursor, no order (main.os:18012).
            var fq = NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset} * ИЗ {table}");
            result = scope.Track(Dispatch.Call(fq, "Выполнить", ctx.Error), "РезультатЗапроса");
        }
        var col = Columns(ctx, scope, result);
        var names = col.OrderBy(c => c.Value).Select(c => c.Key).ToList();
        var sel = scope.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка");

        var rows = new List<Dictionary<string, object?>>();
        DateTime? last = null; int onLast = 0, seen = 0;
        while (Dispatch.CallBool(sel, "Следующий", ctx.Error))
        {
            if (seen++ < skip) continue;
            using var rs = new ComScope();
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                var v = Track(rs, Dispatch.Call(sel, "Получить", ctx.Error, i));
                string name = names[i];
                if (q.Kind == RegisterKind.Information && q.Register == "ДокументыФизическихЛиц" && name == "Физлицо")
                {
                    row[name] = Person(ctx, v);
                    continue;
                }
                row[name] = LegacyValue.Slow(v, ctx);
                if (!accounting) continue;
                if (name is "СчетДт" or "СчетКт" && OneCValue.IsCom(v) && Dispatch.HasMember(v!, "Код"))
                {
                    object? code = null;
                    try { code = Dispatch.Get(v!, "Код", ctx.Error); } catch (OneCException) { }
                    if (LegacyValue.Filled(code)) row[name + "Код"] = LegacyValue.Slow(code, ctx);
                }
                if (name is "Регистратор" or "Организация" && UuidOf(ctx, v) is { Length: > 0 } g)
                    row[name == "Регистратор" ? "recorderRef" : "orgRef"] = g;
                if (name == "НомерСтроки") row["lineNo"] = LegacyValue.Slow(v, ctx);
            }
            if (col.TryGetValue("Период", out int pi) && Track(rs, Dispatch.Call(sel, "Получить", ctx.Error, pi)) is DateTime period)
            {
                if (last == period) onLast++; else { last = period; onLast = 1; }
            }
            rows.Add(row);
        }
        if (!accounting) return new Page(rows, total, null, 0, null);

        string? nextDate = last is { } lp ? (string)LegacyValue.Scalar(lp)! : "";
        int nextSkip = last is null ? 0 : onLast + (q.CursorDate == last ? q.Offset : 0);
        return new Page(rows, total, nextDate, nextSkip, rows.Count >= q.Limit);
    }

    /// <summary>ПолучитьПолныйЭлементСправочникаПоСсылке (main.os:3041) for ДокументыФизическихЛиц.</summary>
    private static object? Person(SessionContext ctx, object? v)
    {
        if (!OneCValue.IsCom(v)) return LegacyValue.Scalar(v);
        string? id = UuidOf(ctx, v);
        if (string.IsNullOrEmpty(id)) return LegacyValue.RefObject(v, ctx);
        return CatalogReadService.ReadOne(ctx, "ФизическиеЛица", id, withTabular: false, CancellationToken.None)
               ?? LegacyValue.RefObject(v, ctx);
    }
}
