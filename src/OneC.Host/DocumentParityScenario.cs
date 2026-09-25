using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host docparity</c>: every document type of every base, through the fast path and
/// through <see cref="LegacyDocumentOracle"/> — the first keyset page, the newest page by date
/// (with its next cursor), and a batch read of that page's ids — compared row by row.
/// </summary>
internal static class DocumentParityScenario
{
    public static int Run(SessionManager m, IEnumerable<string> baseNames, int limit, string? only)
    {
        var svc = new DocumentReadService(m);
        int mismatched = 0, fastOnlyErrors = 0;
        foreach (var b in baseNames)
        {
            var names = m.Use(b, ctx => DocumentNames(ctx));
            if (only is not null) names = names.Where(n => n == only).ToList();
            int compared = 0, empty = 0, rows = 0, lines = 0, bothFail = 0;
            long fastMs = 0, oldMs = 0;
            var slowest = new List<(string, long)>();

            foreach (var name in names)
            {
                if (Environment.GetEnvironmentVariable("DOCPARITY_TRACE") == "1") Console.WriteLine($"  … {name}");
                string? fastErr = null, oldErr = null, diff = null;
                var sw = new Stopwatch();
                long f0 = 0, o0 = 0;
                try
                {
                    var schema = m.Use(b, ctx => DocumentSchemas.Get(ctx, name));
                    var keyset = new DocumentQuery { Document = name, Limit = limit, After = LegacyValue.EmptyGuid, SkipTotal = true };
                    var byDate = new DocumentQuery { Document = name, Limit = limit, SkipTotal = true };

                    sw.Restart();
                    DocumentPage? fk = null, fd = null; List<Dictionary<string, object?>>? fb = null;
                    try
                    {
                        var st = Stopwatch.StartNew();
                        fk = svc.List(b, keyset);
                        long tk = st.ElapsedMilliseconds;
                        fd = svc.List(b, byDate);
                        long td = st.ElapsedMilliseconds - tk;
                        fb = svc.ByIds(b, name, fk.Rows.Select(r => (string)r["id"]!).ToList());
                        if (Environment.GetEnvironmentVariable("DOCPARITY_TRACE") == "1")
                            Console.WriteLine($"    fast keyset {tk} ms, by date {td} ms, batch {st.ElapsedMilliseconds - tk - td} ms");
                    }
                    catch (OneCException e) { fastErr = e.Message; }
                    f0 = sw.ElapsedMilliseconds;

                    sw.Restart();
                    LegacyDocumentOracle.Page? ok = null, od = null; List<Dictionary<string, object?>>? ob = null;
                    try
                    {
                        (ok, od, ob) = m.Use(b, ctx =>
                        {
                            var k = LegacyDocumentOracle.List(ctx, schema, keyset);
                            var d = LegacyDocumentOracle.List(ctx, schema, byDate);
                            return (k, d, LegacyDocumentOracle.Batch(ctx, schema, k.Rows.Select(r => (string)r["id"]!).ToList()));
                        });
                    }
                    catch (OneCException e) { oldErr = e.Message; }
                    o0 = sw.ElapsedMilliseconds;

                    if (fk is not null && ok is not null && fd is not null && od is not null && fb is not null && ob is not null)
                    {
                        diff = LegacyCatalogOracle.Diff(ok.Rows, fk.Rows) is { } d1 ? "keyset: " + d1
                             : LegacyCatalogOracle.Diff(od.Rows, fd.Rows) is { } d2 ? "by date: " + d2
                             : (od.NextDate, od.NextSkip) != (fd.NextCursorDate, fd.NextCursorSkip)
                                 ? $"next cursor: old {od.NextDate}/{od.NextSkip} fast {fd.NextCursorDate}/{fd.NextCursorSkip}"
                             : LegacyCatalogOracle.Diff(ob, fb) is { } d3 ? "batch: " + d3 : null;
                        compared++;
                        if (fk.Rows.Count == 0) empty++;
                        rows += fk.Rows.Count + fd.Rows.Count + fb.Count;
                        lines += fb.Sum(r => r.GetValueOrDefault("tabularSections") is Dictionary<string, object?> ts
                                            ? ts.Values.Sum(v => ((List<Dictionary<string, object?>>)v!).Count) : 0);
                    }
                }
                catch (OneCException e) { fastErr ??= e.Message; oldErr ??= e.Message; }

                fastMs += f0; oldMs += o0;
                slowest.Add((name, f0));
                if (fastErr is not null && oldErr is not null) { bothFail++; Console.WriteLine($"  both fail  {name}: {One(fastErr)}"); }
                else if (fastErr is not null) { fastOnlyErrors++; Console.WriteLine($"  FAST FAIL  {name}: {One(fastErr)}"); }
                else if (oldErr is not null) Console.WriteLine($"  old fails  {name}: {One(oldErr)}");
                else if (diff is not null) { mismatched++; Console.WriteLine($"  MISMATCH   {name}: {diff}"); }
            }
            Console.WriteLine($"{b}: {names.Count} document types, {compared} compared, {empty} empty, {bothFail} unreadable by both, " +
                              $"{rows} rows, {lines} tabular lines (batch); fast {fastMs} ms, old algorithm {oldMs} ms");
            Console.WriteLine("  slowest fast: " + string.Join(", ", slowest.OrderByDescending(x => x.Item2).Take(6).Select(x => $"{x.Item1} {x.Item2} ms")));
        }
        Console.WriteLine($"mismatched {mismatched}, fast-only errors {fastOnlyErrors}");
        return mismatched + fastOnlyErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>OneC.Host docwalk</c>: a whole document type (optionally a window), keyset page by
    /// page with tabular sections — the sync's cold read — plus the first page through the old
    /// algorithm for the per-row comparison.
    /// </summary>
    public static int Walk(SessionManager m, string b, string document, int pageSize, DateTime? from, DateTime? to, int maxPages)
    {
        var svc = new DocumentReadService(m);
        Console.WriteLine(ResourceSampler.Take("before", m.BudgetUsed).Line());
        var schema = m.Use(b, ctx => DocumentSchemas.Get(ctx, document));
        Console.WriteLine("per-value columns: " + string.Join(", ",
            schema.Attributes.Where(a => a.PerValue).Select(a => a.Name)
                  .Concat(schema.Tabular.SelectMany(t => t.Attributes.Where(a => a.PerValue).Select(a => t.Name + "." + a.Name)))));
        bool tabular = Environment.GetEnvironmentVariable("DOCWALK_TABULAR") != "0";
        var q = new DocumentQuery { Document = document, Limit = pageSize, SkipTotal = true, From = from, To = to, Tabular = tabular };
        var sw = Stopwatch.StartNew();
        string? after = LegacyValue.EmptyGuid;
        int pages = 0, rows = 0, lines = 0; long slowest = 0;
        while (after is not null && pages < maxPages)
        {
            var t = Stopwatch.StartNew();
            var p = svc.List(b, q with { After = after });
            slowest = Math.Max(slowest, t.ElapsedMilliseconds);
            rows += p.Rows.Count;
            lines += p.Rows.Sum(r => r.GetValueOrDefault("tabularSections") is Dictionary<string, object?> ts
                                     ? ts.Values.Sum(v => ((List<Dictionary<string, object?>>)v!).Count) : 0);
            if (pages++ == 0) Console.WriteLine(ResourceSampler.Take("after page 1", m.BudgetUsed, collect: false).Line());
            if (pages % 20 == 0) Console.WriteLine(ResourceSampler.Take($"page {pages} ({rows} docs, {lines} lines)", m.BudgetUsed).Line());
            after = p.Next;
        }
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine(ResourceSampler.Take("after walk", m.BudgetUsed).Line());
        Console.WriteLine($"{document}: {rows} documents + {lines} tabular lines in {pages} pages of {pageSize}, {ms} ms, " +
                          $"{(ms == 0 ? 0 : rows * 1000L / ms)} docs/s, slowest page {slowest} ms");

        var first = q with { After = LegacyValue.EmptyGuid };
        var f = Stopwatch.StartNew();
        int n = svc.List(b, first).Rows.Count;
        long fastMs = f.ElapsedMilliseconds;
        f.Restart();
        m.Use(b, ctx => LegacyDocumentOracle.List(ctx, DocumentSchemas.Get(ctx, document), first));
        Console.WriteLine($"first page ({n} documents): fast {fastMs} ms, old algorithm {f.ElapsedMilliseconds} ms");
        return 0;
    }

    private static string One(string? s) => (s ?? "").ReplaceLineEndings(" ");

    internal static List<string> DocumentNames(SessionContext ctx)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var docs = scope.Track(Dispatch.Get(md, "Документы", ctx.Error), "Документы");
        int n = Dispatch.CallInt(docs, "Количество", ctx.Error);
        var names = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            names.Add(Dispatch.GetString(s.Track(Dispatch.Call(docs, "Получить", ctx.Error, i), "Документ"), "Имя", ctx.Error)!);
        }
        return names;
    }
}
