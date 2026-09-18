// .NET <-> 1C concurrency research harness.
//
// Answers, by measurement: can one .NET process host many concurrent 1C sessions for
// READS and for WRITES, on SERVER and on FILE bases, and what breaks when it goes wrong.
//
// Safety rules baked in:
//   - writes only ever CLONE an existing posted document into a NEW draft
//   - every created document carries an unmistakable marker (AIBA_RESEARCH_<run>)
//   - existing accounting documents are never mutated
//   - posting happens only in the explicit `post` mode
//   - `cleanup` marks every marked draft for deletion
//   - every COM object is released on its creating thread
//
// Usage: onec-concurrency <mode> --cfg=<path> [options]

using System.Diagnostics;
using OneC.Research;

class Program
{
    static string RunId = DateTime.Now.ToString("MMdd-HHmmss");
    static string Marker => "AIBA_RESEARCH_" + RunId;
    const string MarkerPrefix = "AIBA_RESEARCH_";

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string mode = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "help";
        string cfgPath = Arg(args, "cfg") ?? @"D:\aiba\research\1c-adapter\csharp-worker\config.local.json";
        var ks = (Arg(args, "k") ?? "1,2,4,8").Split(',').Select(int.Parse).ToArray();
        int rounds = int.Parse(Arg(args, "rounds") ?? "10");

        if (mode == "help") { Help(); return; }

        var cfg = BaseCfg.Load(cfgPath);
        Console.WriteLine($"=== {mode.ToUpper()} | base={cfg.Name} [{cfg.Kind}] | run={RunId} ===");

        object connector = Com.NewConnector();
        try
        {
            switch (mode)
            {
                case "discover": Discover(connector, cfg); break;
                case "read": ReadSweep(connector, cfg, ks, rounds); break;
                case "write": WriteSweep(connector, cfg, ks, rounds, Arg(args, "types")); break;
                case "mixed": Mixed(connector, cfg, rounds); break;
                case "pool": PoolCompare(connector, cfg, rounds); break;
                case "fail": FailureIsolation(connector, cfg); break;
                case "post": PostTest(connector, cfg, Arg(args, "types")); break;
                case "cleanup": Cleanup(connector, cfg); break;
                default: Help(); break;
            }
        }
        finally { Com.Rel(connector); }
    }

    static string Arg(string[] a, string name) =>
        a.FirstOrDefault(x => x.StartsWith("--" + name + "="))?[(name.Length + 3)..];

    static void Help() => Console.WriteLine(
        "modes: discover | read | write | mixed | pool | fail | post | cleanup\n" +
        "  --cfg=<path>  --k=1,2,4,8  --rounds=N  --types=A,B");

    // ================= workload primitives =================

    /// <summary>
    /// Read: run a query and walk every row so COM actually marshals data.
    /// Columns are read BY NAME — `sel.Get(0)` / index access dispatches into a COM path
    /// that crashed the process with 0xC0000005 at K>=2.
    /// </summary>
    static OpResult ReadOp(Session s, string sql, string col = "Наименование")
    {
        var sw = Stopwatch.StartNew();
        dynamic q = null, res = null, sel = null;
        try
        {
            q = s.Handle.NewObject("Запрос");
            q.Текст = sql;
            res = q.Выполнить();
            sel = res.Выбрать();
            int rows = 0;
            while ((bool)sel.Следующий())
            {
                object cell = col switch
                {
                    "Наименование" => sel.Наименование,
                    "X" => sel.X,
                    _ => sel.Ссылка
                };
                rows++;
            }
            return new OpResult { Ms = sw.ElapsedMilliseconds, Ok = true };
        }
        catch (Exception ex)
        {
            var (k, t) = Proc.Classify(ex);
            return new OpResult { Ms = sw.ElapsedMilliseconds, Ok = false, ErrKind = k, ErrText = t };
        }
        finally { Com.Rel(sel); Com.Rel(res); Com.Rel(q); }
    }

    /// <summary>
    /// Write: clone the newest posted document of `docType` into a NEW unposted draft
    /// carrying the run marker. Never mutates the source. Number allocation happens
    /// inside Записать() — that is the contention we want to see.
    /// </summary>
    static OpResult WriteOp(Session s, string docType, bool post = false)
    {
        var sw = Stopwatch.StartNew();
        dynamic q = null, res = null, sel = null, refObj = null, src = null, copy = null, mode = null;
        try
        {
            q = s.Handle.NewObject("Запрос");
            q.Текст = $"ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка КАК Ref ИЗ Документ.{docType} " +
                      $"ГДЕ Проведен УПОРЯДОЧИТЬ ПО Дата УБЫВ";
            res = q.Выполнить();
            sel = res.Выбрать();
            if (!(bool)sel.Следующий())
                return new OpResult { Ms = sw.ElapsedMilliseconds, Ok = false, ErrKind = "other", ErrText = "no posted source doc" };

            refObj = sel.Ref;
            src = refObj.ПолучитьОбъект();
            copy = src.Скопировать();
            copy.Дата = DateTime.Now;
            copy.Комментарий = Marker + ": " + docType;

            if (post)
            {
                // РежимЗаписиДокумента over COM: the enum collection can come back as a
                // null dynamic, so fall back to the raw ordinal (Запись=0, Проведение=1).
                object modeVal = null;
                try { mode = s.Handle.РежимЗаписиДокумента; modeVal = mode?.Проведение; } catch { }
                if (modeVal != null) copy.Записать(modeVal);
                else copy.Записать(1);
            }
            else copy.Записать();

            return new OpResult { Ms = sw.ElapsedMilliseconds, Ok = true };
        }
        catch (Exception ex)
        {
            var (k, t) = Proc.Classify(ex);
            return new OpResult { Ms = sw.ElapsedMilliseconds, Ok = false, ErrKind = k, ErrText = t };
        }
        finally
        {
            Com.Rel(mode); Com.Rel(copy); Com.Rel(src); Com.Rel(refObj);
            Com.Rel(sel); Com.Rel(res); Com.Rel(q);
        }
    }

    // ================= discovery =================

    /// <summary>Document types that actually have a posted document to clone from.</summary>
    static List<string> PostedDocTypes(Session s, int want, int scanCap = 120)
    {
        var found = new List<string>();
        dynamic meta = null, docs = null;
        try
        {
            meta = s.Handle.Метаданные;
            docs = meta.Документы;
            int total = (int)docs.Количество();
            for (int i = 0; i < total && i < scanCap && found.Count < want; i++)
            {
                dynamic item = null, q = null, res = null, sel = null;
                try
                {
                    item = docs.Получить(i);
                    string name = (string)item.Имя;
                    // Комментарий is required: it carries the run marker and it is what
                    // cleanup queries on. A type without it can be written but never
                    // identified afterwards, so it is not safe to use here.
                    q = s.Handle.NewObject("Запрос");
                    // A plain select, not МАКСИМУМ(Комментарий): aggregates are invalid on
                    // unlimited-length strings in 1C query language.
                    q.Текст = $"ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка КАК Ref, Комментарий КАК K " +
                              $"ИЗ Документ.{name} ГДЕ Проведен";
                    res = q.Выполнить();
                    sel = res.Выбрать();
                    if ((bool)sel.Следующий()) found.Add(name);
                }
                catch { }
                finally { Com.Rel(sel); Com.Rel(res); Com.Rel(q); Com.Rel(item); }
            }
        }
        finally { Com.Rel(docs); Com.Rel(meta); }
        return found;
    }

    static string BiggestCatalog(Session s, int scanCap = 60)
    {
        string best = null; long bestRows = 0;
        dynamic meta = null, cats = null;
        try
        {
            meta = s.Handle.Метаданные;
            cats = meta.Справочники;
            int total = (int)cats.Количество();
            for (int i = 0; i < total && i < scanCap; i++)
            {
                dynamic item = null, q = null, res = null, sel = null;
                try
                {
                    item = cats.Получить(i);
                    string name = (string)item.Имя;
                    q = s.Handle.NewObject("Запрос");
                    q.Текст = $"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК C ИЗ Справочник.{name}";
                    res = q.Выполнить();
                    sel = res.Выбрать();
                    sel.Следующий();
                    long c = Convert.ToInt64(sel.C);
                    if (c > bestRows) { bestRows = c; best = name; }
                }
                catch { }
                finally { Com.Rel(sel); Com.Rel(res); Com.Rel(q); Com.Rel(item); }
                if (bestRows >= 2000) break;
            }
        }
        finally { Com.Rel(cats); Com.Rel(meta); }
        Console.WriteLine($"  read target : Справочник.{best} ({bestRows:N0} rows)");
        return best;
    }

    static void Discover(object connector, BaseCfg cfg)
    {
        using var s = Session.Open(connector, cfg.ConnectionString, 0);
        Console.WriteLine($"connect: {s.ConnectMs} ms");
        var cat = BiggestCatalog(s);
        var docs = PostedDocTypes(s, 8);
        Console.WriteLine($"  doc types with posted docs ({docs.Count}): {string.Join(", ", docs)}");
    }

    // ================= sweeps =================

    static Run Sweep(object connector, BaseCfg cfg, int k, int rounds, string label,
                     Func<Session, int, OpResult> op)
    {
        var run = new Run { Label = label, K = k, SessionCount = k };
        var sessions = new Session[k];
        long connectTotal = 0;

        for (int i = 0; i < k; i++)
        {
            sessions[i] = Session.Open(connector, cfg.ConnectionString, i);
            connectTotal += sessions[i].ConnectMs;
        }
        run.ConnectTotalMs = connectTotal;

        var (rss0, cpu0) = Proc.Snapshot();
        var sw = Stopwatch.StartNew();
        var threads = new Thread[k];
        for (int i = 0; i < k; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                for (int r = 0; r < rounds; r++) run.Add(op(sessions[idx], idx));
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        run.WallMs = sw.ElapsedMilliseconds;

        var (rss1, cpu1) = Proc.Snapshot();
        run.PeakRssMb = rss1;
        double secs = Math.Max(0.001, run.WallMs / 1000.0);
        run.CpuPercent = (cpu1 - cpu0).TotalSeconds / secs / Environment.ProcessorCount * 100.0;

        foreach (var s in sessions) s.Dispose();
        return run;
    }

    static void ReadSweep(object connector, BaseCfg cfg, int[] ks, int rounds)
    {
        string cat;
        using (var probe = Session.Open(connector, cfg.ConnectionString, -1))
        {
            Console.WriteLine($"connect: {probe.ConnectMs} ms");
            cat = BiggestCatalog(probe);
        }
        if (cat == null) { Console.WriteLine("no catalog found"); return; }
        string sql = $"ВЫБРАТЬ ПЕРВЫЕ 500 Ссылка, Код, Наименование ИЗ Справочник.{cat}";

        foreach (int k in ks)
        {
            var run = Sweep(connector, cfg, k, rounds, "read", (s, i) => ReadOp(s, sql));
            Console.WriteLine(run.Line());
            foreach (var e in run.SampleErrors()) Console.WriteLine("     " + e);
            Thread.Sleep(1200);
        }
    }

    static void WriteSweep(object connector, BaseCfg cfg, int[] ks, int rounds, string typesArg)
    {
        List<string> types;
        using (var probe = Session.Open(connector, cfg.ConnectionString, -1))
        {
            Console.WriteLine($"connect: {probe.ConnectMs} ms");
            types = typesArg != null
                ? typesArg.Split(',').ToList()
                : PostedDocTypes(probe, 8);
        }
        if (types.Count == 0) { Console.WriteLine("no postable doc types found"); return; }
        Console.WriteLine($"  doc types: {string.Join(", ", types)}");
        Console.WriteLine($"  marker   : {Marker}");

        Console.WriteLine("\n-- 1. SAME document type on every thread (number-allocation contention) --");
        foreach (int k in ks)
        {
            var run = Sweep(connector, cfg, k, rounds, "write-same", (s, i) => WriteOp(s, types[0]));
            Console.WriteLine(run.Line());
            foreach (var e in run.SampleErrors()) Console.WriteLine("     " + e);
            if (run.Failures > run.Total / 2) { Console.WriteLine("  >50% failures — stopping sweep"); break; }
            Thread.Sleep(1500);
        }

        if (types.Count < 2) { Console.WriteLine("\n(only one doc type available — skipping different-type test)"); return; }

        Console.WriteLine("\n-- 2. DIFFERENT document type per thread --");
        foreach (int k in ks)
        {
            var run = Sweep(connector, cfg, k, rounds, "write-diff",
                            (s, i) => WriteOp(s, types[i % types.Count]));
            Console.WriteLine(run.Line());
            foreach (var e in run.SampleErrors()) Console.WriteLine("     " + e);
            if (run.Failures > run.Total / 2) { Console.WriteLine("  >50% failures — stopping sweep"); break; }
            Thread.Sleep(1500);
        }
    }

    /// <summary>Do writes stall unrelated reads? Writers and readers run at the same time.</summary>
    static void Mixed(object connector, BaseCfg cfg, int rounds)
    {
        string cat; List<string> types;
        using (var probe = Session.Open(connector, cfg.ConnectionString, -1))
        {
            cat = BiggestCatalog(probe);
            types = PostedDocTypes(probe, 2);
        }
        if (cat == null || types.Count == 0) { Console.WriteLine("missing targets"); return; }
        string sql = $"ВЫБРАТЬ ПЕРВЫЕ 500 Ссылка, Код, Наименование ИЗ Справочник.{cat}";

        Console.WriteLine("\n-- baseline: reads alone, K=2 --");
        var baseRun = Sweep(connector, cfg, 2, rounds, "read", (s, i) => ReadOp(s, sql));
        Console.WriteLine(baseRun.Line());

        Console.WriteLine("\n-- reads (K=2) WHILE writes (K=2) run --");
        var readRun = new Run { Label = "read-under-write", K = 2, SessionCount = 4 };
        var writeRun = new Run { Label = "write-under-read", K = 2, SessionCount = 4 };

        var sessions = new Session[4];
        for (int i = 0; i < 4; i++) sessions[i] = Session.Open(connector, cfg.ConnectionString, i);

        var (rss0, cpu0) = Proc.Snapshot();
        var sw = Stopwatch.StartNew();
        var threads = new List<Thread>();
        for (int i = 0; i < 2; i++)
        {
            int idx = i;
            threads.Add(new Thread(() => { for (int r = 0; r < rounds; r++) readRun.Add(ReadOp(sessions[idx], sql)); }));
        }
        for (int i = 2; i < 4; i++)
        {
            int idx = i;
            threads.Add(new Thread(() => { for (int r = 0; r < rounds; r++) writeRun.Add(WriteOp(sessions[idx], types[0])); }));
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        long wall = sw.ElapsedMilliseconds;
        readRun.WallMs = wall; writeRun.WallMs = wall;
        var (rss1, cpu1) = Proc.Snapshot();
        readRun.PeakRssMb = writeRun.PeakRssMb = rss1;
        readRun.CpuPercent = writeRun.CpuPercent = (cpu1 - cpu0).TotalSeconds / Math.Max(0.001, wall / 1000.0) / Environment.ProcessorCount * 100.0;

        Console.WriteLine(readRun.Line());
        Console.WriteLine(writeRun.Line());
        Console.WriteLine($"  => read p50 alone {baseRun.P50} ms vs under write {readRun.P50} ms " +
                          $"({(baseRun.P50 <= 0 ? 0 : (double)readRun.P50 / baseRun.P50):F2}x)");

        foreach (var s in sessions) s.Dispose();
    }

    // ================= session pool =================

    static void PoolCompare(object connector, BaseCfg cfg, int rounds)
    {
        string cat;
        using (var probe = Session.Open(connector, cfg.ConnectionString, -1))
            cat = BiggestCatalog(probe);
        string sql = $"ВЫБРАТЬ ПЕРВЫЕ 500 Ссылка, Код, Наименование ИЗ Справочник.{cat}";

        // 1. connect -> query -> release, every single operation
        Console.WriteLine("\n-- 1. connect + query + release per operation --");
        var perOp = new Run { Label = "per-op-connect", K = 1, SessionCount = rounds };
        var sw = Stopwatch.StartNew();
        long firstMs = -1;
        for (int r = 0; r < rounds; r++)
        {
            var t = Stopwatch.StartNew();
            using var s = Session.Open(connector, cfg.ConnectionString, r);
            var op = ReadOp(s, sql);
            op.Ms = t.ElapsedMilliseconds;          // full cost: connect + query + release
            if (firstMs < 0) firstMs = op.Ms;
            perOp.Add(op);
        }
        perOp.WallMs = sw.ElapsedMilliseconds;
        var (rssA, _) = Proc.Snapshot(); perOp.PeakRssMb = rssA;
        Console.WriteLine(perOp.Line());
        Console.WriteLine($"     first op {firstMs} ms");

        // 2..N. warm pools
        foreach (int size in new[] { 1, 2, 4, 8 })
        {
            Console.WriteLine($"\n-- pool of {size} (warm, sessions reused) --");
            var wsw = Stopwatch.StartNew();
            using var pool = new SessionPool(connector, cfg.ConnectionString, size);
            long warmupMs = wsw.ElapsedMilliseconds;
            var (rssIdle, _) = Proc.Snapshot();

            var run = new Run { Label = $"pool-{size}", K = size, SessionCount = size };
            var (r0, c0) = Proc.Snapshot();
            var psw = Stopwatch.StartNew();
            long firstWarm = -1;
            var threads = new Thread[size];
            for (int i = 0; i < size; i++)
            {
                threads[i] = new Thread(() =>
                {
                    for (int r = 0; r < rounds; r++)
                    {
                        var s = pool.Rent();
                        try
                        {
                            var op = ReadOp(s, sql);
                            if (Interlocked.CompareExchange(ref firstWarm, op.Ms, -1) == -1) { }
                            run.Add(op);
                        }
                        finally { pool.Return(s); }
                    }
                });
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            run.WallMs = psw.ElapsedMilliseconds;
            var (r1, c1) = Proc.Snapshot();
            run.PeakRssMb = r1;
            run.CpuPercent = (c1 - c0).TotalSeconds / Math.Max(0.001, run.WallMs / 1000.0) / Environment.ProcessorCount * 100.0;

            Console.WriteLine(run.Line());
            Console.WriteLine($"     pool warmup {warmupMs} ms, idle rss {rssIdle} MB, recreated {pool.Recreated}");
            Thread.Sleep(1000);
        }
    }

    // ================= failure isolation =================

    static void FailureIsolation(object connector, BaseCfg cfg)
    {
        Console.WriteLine("\n-- F1. wrong credentials --");
        TryConnect(connector, MangleCreds(cfg.ConnectionString), "bad password");

        Console.WriteLine("\n-- F2. non-existent base --");
        TryConnect(connector, MangleBase(cfg.ConnectionString), "bad base");

        Console.WriteLine("\n-- F3. does the connector still work after those failures? --");
        try
        {
            using var s = Session.Open(connector, cfg.ConnectionString, 99);
            Console.WriteLine($"   OK — connector still usable, connect {s.ConnectMs} ms");
        }
        catch (Exception ex) { Console.WriteLine("   POISONED: " + Proc.Classify(ex).text); }

        Console.WriteLine("\n-- F4. COM call throws on one session; do siblings survive? --");
        var sessions = new Session[4];
        for (int i = 0; i < 4; i++) sessions[i] = Session.Open(connector, cfg.ConnectionString, i);
        try
        {
            // deliberately invalid query on session 1 only
            var bad = ReadOp(sessions[1], "ВЫБРАТЬ ЭтойТаблицыНеСуществует ИЗ Справочник.НетТакого");
            Console.WriteLine($"   session1 bad query -> ok={bad.Ok} kind={bad.ErrKind}");

            for (int i = 0; i < 4; i++)
            {
                var r = ReadOp(sessions[i], "ВЫБРАТЬ 1 КАК X", "X");
                Console.WriteLine($"   session{i} after sibling error -> ok={r.Ok} {(r.Ok ? r.Ms + "ms" : r.ErrText)}");
            }

            Console.WriteLine("\n-- F5. dispose ONE session mid-flight, keep using the others --");
            sessions[2].Dispose();
            sessions[2] = null;
            for (int i = 0; i < 4; i++)
            {
                if (sessions[i] == null) { Console.WriteLine($"   session{i} disposed"); continue; }
                var r = ReadOp(sessions[i], "ВЫБРАТЬ 1 КАК X", "X");
                Console.WriteLine($"   session{i} after sibling dispose -> ok={r.Ok} {(r.Ok ? r.Ms + "ms" : r.ErrText)}");
            }

            Console.WriteLine("\n-- F6. recreate the disposed slot from the SAME connector --");
            try
            {
                sessions[2] = Session.Open(connector, cfg.ConnectionString, 2);
                var r = ReadOp(sessions[2], "ВЫБРАТЬ 1 КАК X", "X");
                Console.WriteLine($"   recreated session2 -> connect {sessions[2].ConnectMs} ms, query ok={r.Ok}");
            }
            catch (Exception ex) { Console.WriteLine("   recreate FAILED: " + Proc.Classify(ex).text); }
        }
        finally { foreach (var s in sessions) s?.Dispose(); }

        Console.WriteLine("\n-- F7. final connector health --");
        try
        {
            using var s = Session.Open(connector, cfg.ConnectionString, 100);
            var r = ReadOp(s, "ВЫБРАТЬ 1 КАК X", "X");
            Console.WriteLine($"   connector healthy at end: connect {s.ConnectMs} ms, query ok={r.Ok}");
        }
        catch (Exception ex) { Console.WriteLine("   connector DEAD at end: " + Proc.Classify(ex).text); }
    }

    static void TryConnect(object connector, string cs, string label)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var s = Session.Open(connector, cs, -1);
            Console.WriteLine($"   {label}: UNEXPECTEDLY CONNECTED in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            var (k, t) = Proc.Classify(ex);
            Console.WriteLine($"   {label}: failed in {sw.ElapsedMilliseconds} ms [{k}] {t}");
        }
    }

    static string MangleCreds(string cs) =>
        System.Text.RegularExpressions.Regex.Replace(cs, "Pwd=\"[^\"]*\"", "Pwd=\"definitely-wrong-xyz\"");

    static string MangleBase(string cs) =>
        cs.Contains("File=")
            ? System.Text.RegularExpressions.Regex.Replace(cs, "File=\"[^\"]*\"", "File=\"D:\\\\1C\\\\__no_such_base__\"")
            : System.Text.RegularExpressions.Regex.Replace(cs, "Ref=\"[^\"]*\"", "Ref=\"__no_such_base__\"");

    // ================= posting + cleanup =================

    static void PostTest(object connector, BaseCfg cfg, string typesArg)
    {
        using var s = Session.Open(connector, cfg.ConnectionString, 0);
        var types = typesArg != null ? typesArg.Split(',').ToList() : PostedDocTypes(s, 1);
        if (types.Count == 0) { Console.WriteLine("no postable doc type"); return; }
        Console.WriteLine($"  posting a clone of {types[0]} (marker {Marker})");
        var r = WriteOp(s, types[0], post: true);
        Console.WriteLine($"  post -> ok={r.Ok} {r.Ms} ms {(r.Ok ? "" : "[" + r.ErrKind + "] " + r.ErrText)}");
    }

    static void Cleanup(object connector, BaseCfg cfg)
    {
        using var s = Session.Open(connector, cfg.ConnectionString, 0);
        var types = PostedDocTypes(s, 12);
        int total = 0;
        foreach (var t in types)
        {
            dynamic q = null, res = null, sel = null;
            try
            {
                q = s.Handle.NewObject("Запрос");
                q.Текст = $"ВЫБРАТЬ Ссылка КАК Ref ИЗ Документ.{t} " +
                          $"ГДЕ Комментарий ПОДОБНО \"{MarkerPrefix.Replace("_", "~_")}%\" СПЕЦСИМВОЛ \"~\" И НЕ ПометкаУдаления";
                res = q.Выполнить();
                sel = res.Выбрать();
                int n = 0;
                while ((bool)sel.Следующий())
                {
                    dynamic rf = null, obj = null;
                    try
                    {
                        rf = sel.Ref;
                        obj = rf.ПолучитьОбъект();
                        obj.УстановитьПометкуУдаления(true);
                        n++;
                    }
                    catch (Exception ex) { Console.WriteLine($"   {t}: mark failed — {Proc.Classify(ex).text}"); }
                    finally { Com.Rel(obj); Com.Rel(rf); }
                }
                if (n > 0) { Console.WriteLine($"   {t}: marked {n} for deletion"); total += n; }
            }
            catch (Exception ex) { Console.WriteLine($"   {t}: {Proc.Classify(ex).text}"); }
            finally { Com.Rel(sel); Com.Rel(res); Com.Rel(q); }
        }
        Console.WriteLine($"  total marked: {total}");
    }
}
