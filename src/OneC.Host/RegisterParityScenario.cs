using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host regparity</c>: every register of every kind, newest page and a page after a
/// cursor, through the fast path and <see cref="LegacyRegisterOracle"/>. Accounting pages must
/// match row by row (both order by Период, Регистратор, НомерСтроки). The old information and
/// accumulation reads ordered by Период alone, so rows sharing a Период had no defined order:
/// those pages are compared as sets, without the last Период when the page was full (its rows
/// may be cut at different places).
/// </summary>
internal static class RegisterParityScenario
{
    public static int Run(SessionManager m, IEnumerable<string> baseNames, int limit, string? only)
    {
        var svc = new RegisterReadService(m);
        int mismatched = 0, fastOnly = 0;
        foreach (var b in baseNames)
        {
            foreach (var kind in new[] { RegisterKind.Accounting, RegisterKind.Accumulation, RegisterKind.Information })
            {
                var names = m.Use(b, ctx => Names(ctx, kind));
                if (only is not null) names = names.Where(n => n == only).ToList();
                int compared = 0, rows = 0, bothFail = 0;
                long fastMs = 0, oldMs = 0;
                foreach (var name in names)
                {
                    if (Environment.GetEnvironmentVariable("REGPARITY_TRACE") == "1") Console.WriteLine($"  … {kind} {name}");
                    var queries = new[]
                    {
                        new RegisterQuery { Kind = kind, Register = name, Limit = limit, SkipTotal = true },
                        new RegisterQuery { Kind = kind, Register = name, Limit = limit, SkipTotal = true, Order = "asc", CursorDate = new DateTime(2020, 1, 1), Offset = 1 }
                    };
                    string? fastErr = null, oldErr = null, diff = null;
                    foreach (var q in queries)
                    {
                        RegisterPage? f = null; LegacyRegisterOracle.Page? o = null;
                        var sw = Stopwatch.StartNew();
                        bool trace = Environment.GetEnvironmentVariable("REGPARITY_TRACE") == "1";
                        try { f = svc.List(b, q); } catch (OneCException e) { fastErr ??= e.Message; }
                        if (trace) Console.WriteLine($"    fast {q.Order} {sw.ElapsedMilliseconds} ms, {f?.Rows.Count} rows");
                        fastMs += sw.ElapsedMilliseconds; sw.Restart();
                        try { o = m.Use(b, ctx => LegacyRegisterOracle.Read(ctx, q)); } catch (OneCException e) { oldErr ??= e.Message; }
                        if (trace) Console.WriteLine($"    old  {q.Order} {sw.ElapsedMilliseconds} ms, {o?.Rows.Count} rows");
                        oldMs += sw.ElapsedMilliseconds;
                        if (f is null || o is null) continue;
                        rows += f.Rows.Count;
                        diff ??= Compare(kind, q, o, f);
                    }
                    if (fastErr is not null && oldErr is not null) { bothFail++; Console.WriteLine($"  both fail  {kind} {name}: {One(fastErr)}"); }
                    else if (fastErr is not null) { fastOnly++; Console.WriteLine($"  FAST FAIL  {kind} {name}: {One(fastErr)}"); }
                    else if (oldErr is not null) Console.WriteLine($"  old fails  {kind} {name}: {One(oldErr)}");
                    else if (diff is not null) { mismatched++; Console.WriteLine($"  MISMATCH   {kind} {name}: {diff}"); }
                    else compared++;
                }
                Console.WriteLine($"{b} {kind}: {names.Count} registers, {compared} identical, {bothFail} unreadable by both, {rows} rows; fast {fastMs} ms, old algorithm {oldMs} ms");
            }
        }
        Console.WriteLine($"mismatched {mismatched}, fast-only errors {fastOnly}");
        return mismatched + fastOnly == 0 ? 0 : 1;
    }

    internal static string? Compare(RegisterKind kind, RegisterQuery q, LegacyRegisterOracle.Page old, RegisterPage fast)
    {
        if (kind == RegisterKind.Accounting)
        {
            if (LegacyCatalogOracle.Diff(old.Rows, fast.Rows) is { } d) return d;
            if ((old.NextDate ?? "") != (fast.NextCursorDate ?? "") || old.NextSkip != fast.NextCursorSkip || old.HasMore != fast.HasMore)
                return $"cursor: old {old.NextDate}/{old.NextSkip}/{old.HasMore} fast {fast.NextCursorDate}/{fast.NextCursorSkip}/{fast.HasMore}";
            return null;
        }
        var o = Canon(old.Rows, q.Limit, q.Offset);
        var f = Canon(fast.Rows, q.Limit, q.Offset);
        if (o is null || f is null) return old.Rows.Count == fast.Rows.Count ? null : $"{old.Rows.Count} old rows vs {fast.Rows.Count} fast rows";
        if (o.SequenceEqual(f)) return null;
        var missing = o.Except(f).FirstOrDefault();
        var extra = f.Except(o).FirstOrDefault();
        return $"sets differ ({o.Count} vs {f.Count}); old-only: {Trunc(missing)}; fast-only: {Trunc(extra)}";
    }

    /// <summary>
    /// Sorted row texts without the Период groups a page may cut arbitrarily: the last one of a
    /// full page, and the first one when an offset skipped into it. Null = not comparable
    /// (such a cut, but no Период to cut by).
    /// </summary>
    private static List<string>? Canon(List<Dictionary<string, object?>> rows, int limit, int offset)
    {
        var keep = rows;
        bool cutEnd = rows.Count >= limit, cutStart = offset > 0;
        if ((cutEnd || cutStart) && rows.Count > 0)
        {
            if (!rows[0].ContainsKey("Период")) return null;
            object? first = rows[0]["Период"], last = rows[^1]["Период"];
            keep = rows.Where(r => !(cutEnd && Equals(r["Период"], last)) && !(cutStart && Equals(r["Период"], first))).ToList();
        }
        return keep.Select(r => System.Text.Json.JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <c>OneC.Host regwalk</c>: the connector's cursor walk (asc from <paramref name="from"/>,
    /// next cursor + skip while hasMore) with resource samples every 20 pages, then the first
    /// page through the old algorithm for the per-row comparison.
    /// </summary>
    public static int Walk(SessionManager m, string b, RegisterKind kind, string name, int pageSize, DateTime from, DateTime? to, int maxPages)
    {
        var svc = new RegisterReadService(m);
        Console.WriteLine(ResourceSampler.Take("before", m.BudgetUsed).Line());
        var sw = Stopwatch.StartNew();
        DateTime cursor = from; int skip = 0, pages = 0, rows = 0; long slowest = 0;
        while (pages < maxPages)
        {
            var t = Stopwatch.StartNew();
            var p = svc.List(b, new RegisterQuery { Kind = kind, Register = name, Limit = pageSize, Order = "asc", CursorDate = cursor, Offset = skip, To = to, SkipTotal = true });
            slowest = Math.Max(slowest, t.ElapsedMilliseconds);
            rows += p.Rows.Count;
            if (++pages % 20 == 0) Console.WriteLine(ResourceSampler.Take($"page {pages} ({rows} rows)", m.BudgetUsed).Line());
            if (!p.HasMore || p.NextCursorDate is null) break;
            cursor = DateTime.Parse(p.NextCursorDate); skip = p.NextCursorSkip;
        }
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine(ResourceSampler.Take("after walk", m.BudgetUsed).Line());
        Console.WriteLine($"{kind} {name}: {rows} rows in {pages} pages of {pageSize}, {ms} ms, {(ms == 0 ? 0 : rows * 1000L / ms)} rows/s, slowest page {slowest} ms");

        var first = new RegisterQuery { Kind = kind, Register = name, Limit = pageSize, Order = "asc", CursorDate = from, To = to, SkipTotal = true };
        var f = Stopwatch.StartNew();
        int n = svc.List(b, first).Rows.Count;
        long fastMs = f.ElapsedMilliseconds;
        f.Restart();
        m.Use(b, ctx => LegacyRegisterOracle.Read(ctx, first));
        Console.WriteLine($"first page ({n} rows): fast {fastMs} ms, old algorithm {f.ElapsedMilliseconds} ms");
        return 0;
    }

    private static string Trunc(string? s) => s is null ? "-" : s.Length <= 300 ? s : s[..300] + "…";
    private static string One(string? s) => (s ?? "").ReplaceLineEndings(" ");

    internal static List<string> Names(SessionContext ctx, RegisterKind kind)
    {
        string coll = kind switch
        {
            RegisterKind.Information => "РегистрыСведений",
            RegisterKind.Accumulation => "РегистрыНакопления",
            _ => "РегистрыБухгалтерии"
        };
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var c = scope.Track(Dispatch.Get(md, coll, ctx.Error), coll);
        int n = Dispatch.CallInt(c, "Количество", ctx.Error);
        var names = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            names.Add(Dispatch.GetString(s.Track(Dispatch.Call(c, "Получить", ctx.Error, i), "Регистр"), "Имя", ctx.Error)!);
        }
        return names;
    }
}
