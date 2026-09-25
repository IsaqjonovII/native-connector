using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host catparity</c>: every catalog of every base, first page through the fast path
/// and through <see cref="LegacyCatalogOracle"/>, compared row by row. The live tests cover a
/// handful of catalogs; this sweep is the evidence that the value rules hold across the whole
/// configuration (composite types, enums, value storage, catalogs without names or codes).
/// </summary>
internal static class CatalogParityScenario
{
    public static int Run(SessionManager m, IEnumerable<string> baseNames, int limit, string? only)
    {
        var svc = new CatalogReadService(m);
        int mismatched = 0, fastOnlyErrors = 0;
        foreach (var b in baseNames)
        {
            var names = m.Use(b, ctx => CatalogNames(ctx));
            if (only is not null) names = names.Where(n => n == only).ToList();

            int compared = 0, identical = 0, empty = 0, bothFail = 0, rows = 0, lines = 0;
            long schemaMs = 0, fastMs = 0, oldMs = 0;
            var slowest = new List<(string Name, long Ms)>();
            var withLines = new List<string>();
            foreach (var name in names)
            {
                if (Environment.GetEnvironmentVariable("CATPARITY_TRACE") == "1") Console.WriteLine($"  … {name}");
                List<Dictionary<string, object?>>? fast = null, old = null;
                string? fastErr = null, oldErr = null;
                var sw = Stopwatch.StartNew();
                try { m.Use(b, ctx => CatalogSchemas.Get(ctx, name)); } catch (OneCException) { }
                schemaMs += sw.ElapsedMilliseconds;

                sw.Restart();
                try { fast = svc.List(b, new CatalogQuery { Catalog = name, Limit = limit, After = LegacyValue.EmptyGuid, SkipTotal = true }).Rows; }
                catch (OneCException e) { fastErr = e.Message; }
                fastMs += sw.ElapsedMilliseconds;
                slowest.Add((name, sw.ElapsedMilliseconds));

                sw.Restart();
                try { old = m.Use(b, ctx => LegacyCatalogOracle.Page(ctx, CatalogSchemas.Get(ctx, name), LegacyValue.EmptyGuid, limit)); }
                catch (OneCException e) { oldErr = e.Message; }
                oldMs += sw.ElapsedMilliseconds;

                if (fast is null || old is null)
                {
                    if (fast is null && old is null) { bothFail++; Console.WriteLine($"  both fail  {name}: {One(fastErr)}"); }
                    else if (fast is null) { fastOnlyErrors++; Console.WriteLine($"  FAST FAIL  {name}: {One(fastErr)}"); }
                    else Console.WriteLine($"  old fails  {name}: {One(oldErr)}");
                    continue;
                }

                compared++;
                if (fast.Count == 0) empty++;
                rows += fast.Count;
                int here = fast.Sum(r => r.GetValueOrDefault("tabularSections") is Dictionary<string, object?> ts
                                         ? ts.Values.Sum(v => ((List<Dictionary<string, object?>>)v!).Count) : 0);
                lines += here;
                if (here > 0) withLines.Add($"{name} {here}");
                if (LegacyCatalogOracle.Diff(old, fast) is { } diff) { mismatched++; Console.WriteLine($"  MISMATCH   {name}: {diff}"); }
                else identical++;
            }
            Console.WriteLine($"{b}: {names.Count} catalogs, {compared} compared, {identical} identical, {empty} empty, " +
                              $"{bothFail} unreadable by both, {rows} rows, {lines} tabular lines; schema load {schemaMs} ms, " +
                              $"fast pages {fastMs} ms, old algorithm pages {oldMs} ms");
            Console.WriteLine("  with tabular lines: " + string.Join(", ", withLines));
            Console.WriteLine("  slowest fast pages: " + string.Join(", ", slowest.OrderByDescending(x => x.Ms).Take(8).Select(x => $"{x.Name} {x.Ms} ms")));
        }
        Console.WriteLine($"mismatched {mismatched}, fast-only errors {fastOnlyErrors}");
        return mismatched + fastOnlyErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>OneC.Host catbench</c>: per catalog, cold schema load, then a full page through the
    /// fast path (median of <paramref name="reps"/>, schema warm) and through the old
    /// algorithm (once) — the per-row cost the sync pays.
    /// </summary>
    public static int Bench(SessionManager m, string b, IEnumerable<string> catalogs, int rows, int reps)
    {
        var svc = new CatalogReadService(m);
        Console.WriteLine($"{"catalog",-28} {"rows",6} {"schema ms",10} {"fast ms",8} {"µs/row",7} {"old ms",8} {"µs/row",7} {"x",5}");
        foreach (var cat in catalogs)
        {
            CatalogSchemas.Forget(b);
            var sw = Stopwatch.StartNew();
            var schema = m.Use(b, ctx => CatalogSchemas.Get(ctx, cat));
            long schemaMs = sw.ElapsedMilliseconds;

            var q = new CatalogQuery { Catalog = cat, Limit = rows, After = LegacyValue.EmptyGuid, SkipTotal = true };
            svc.List(b, q);                                                      // warm the page in 1C's caches
            var times = new List<long>();
            int n = 0;
            for (int i = 0; i < reps; i++)
            {
                sw.Restart();
                n = svc.List(b, q).Rows.Count;
                times.Add(sw.ElapsedMilliseconds);
            }
            long fast = times.Order().ElementAt(times.Count / 2);

            sw.Restart();
            m.Use(b, ctx => LegacyCatalogOracle.Page(ctx, schema, LegacyValue.EmptyGuid, rows));
            long old = sw.ElapsedMilliseconds;

            double per(long ms) => n == 0 ? 0 : ms * 1000.0 / n;
            Console.WriteLine($"{cat,-28} {n,6} {schemaMs,10} {fast,8} {per(fast),7:F0} {old,8} {per(old),7:F0} {(fast == 0 ? 0 : (double)old / fast),5:F1}");
            if (Environment.GetEnvironmentVariable("CATBENCH_SPLIT") == "1") Split(m, b, schema, rows);
        }
        return 0;
    }

    /// <summary>
    /// <c>OneC.Host catwalk</c>: a whole catalog, keyset page by page, the way the sync reads
    /// it. Resources before, at the peak page and after, so growth per page shows.
    /// </summary>
    public static int Walk(SessionManager m, string b, string catalog, int pageSize, bool tabular)
    {
        var svc = new CatalogReadService(m);
        Console.WriteLine(ResourceSampler.Take("before", m.BudgetUsed).Line());
        var q = new CatalogQuery
        {
            Catalog = catalog, Limit = pageSize, SkipTotal = true,
            Fields = tabular ? null : Array.Empty<string>()
        };
        var sw = Stopwatch.StartNew();
        string? after = LegacyValue.EmptyGuid;
        int pages = 0, rows = 0; long slowest = 0;
        long bytes = 0;
        while (after is not null)
        {
            var t = Stopwatch.StartNew();
            var p = svc.List(b, q with { After = after });
            slowest = Math.Max(slowest, t.ElapsedMilliseconds);
            rows += p.Rows.Count;
            if (pages++ == 0) Console.WriteLine(ResourceSampler.Take("after page 1", m.BudgetUsed, collect: false).Line());
            if (pages % 20 == 0) bytes = Math.Max(bytes, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(p.Rows).Length);
            after = p.Next;
        }
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine(ResourceSampler.Take("after walk", m.BudgetUsed).Line());
        Console.WriteLine($"{catalog}: {rows} rows in {pages} pages of {pageSize}, {ms} ms, " +
                          $"{(ms == 0 ? 0 : rows * 1000L / ms)} rows/s, slowest page {slowest} ms, " +
                          $"largest sampled page {bytes / 1024} KB JSON");
        return 0;
    }

    /// <summary>Where a fast page's time goes: query execution, bare row walk, per-attribute reads.</summary>
    private static void Split(SessionManager m, string b, CatalogSchema schema, int rows)
    {
        var attrs = schema.Attributes.ToList();
        string sql = CatalogReadService.BuildSelect(schema, attrs, rows, null, "Ссылка");
        m.Use(b, ctx =>
        {
            using var scope = new ComScope();
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Dispatch.Set(q, "Текст", sql, ctx.Error);
            var sw = Stopwatch.StartNew();
            var r = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "r");
            long exec = sw.ElapsedMilliseconds;
            var c = new DispatchMemo(scope.Track(Dispatch.Call(r, "Выбрать", ctx.Error), "c"));
            var perAttr = new long[attrs.Count];
            sw.Restart();
            int n = 0;
            while (c.CallBool("Следующий", ctx.Error))
            {
                n++;
                for (int i = 0; i < attrs.Count; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    LegacyValue.Read(c, "a" + i, attrs[i], ctx);
                    perAttr[i] += Stopwatch.GetTimestamp() - t0;
                }
            }
            Console.WriteLine($"    execute {exec} ms, walk+attrs {sw.ElapsedMilliseconds} ms over {n} rows");
            foreach (var (a, t) in attrs.Zip(perAttr).OrderByDescending(x => x.Second).Take(8))
                Console.WriteLine($"    {a.Name,-36} {t * 1000.0 / Stopwatch.Frequency,8:F0} ms  " +
                                  $"n={a.DerefName} c={a.DerefCode} r={a.DerefNumber} prim={a.HasPrimitive} uuid={a.MayBeUuid} enum={a.RefWithoutDeref}");
            return 0;
        });
    }

    private static string One(string? s) => (s ?? "").ReplaceLineEndings(" ");

    private static List<string> CatalogNames(SessionContext ctx)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var cats = scope.Track(Dispatch.Get(md, "Справочники", ctx.Error), "Справочники");
        int n = Dispatch.CallInt(cats, "Количество", ctx.Error);
        var names = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            names.Add(Dispatch.GetString(s.Track(Dispatch.Call(cats, "Получить", ctx.Error, i), "Справочник"), "Имя", ctx.Error)!);
        }
        return names;
    }
}
