// COM threading probe for V83.ComConnector.
//
// Answers, by measurement rather than by reading docs:
//   a - what apartment does a plain .NET console process actually run in
//   b - ONE connector object, N threads calling Connect concurrently
//   c - N connector objects, one per thread
//   d - scaling curve: N connections, each connecting AND querying
//   e - pure query concurrency: connections opened up front, connect cost excluded
//
// Read-only throughout: connects, reads metadata, runs SELECT, releases.
// Never writes to 1C.
//
// Usage: com-probe <mode> [configPath] [n] [rounds]
//   config is the same shape csharp-worker uses: { baseName, connectionString }

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

class Probe
{
    [DllImport("ole32.dll")]
    static extern int CoGetApartmentType(out int pAptType, out int pAptQualifier);

    static string ApartmentOfCurrentThread()
    {
        var managed = Thread.CurrentThread.GetApartmentState().ToString();
        int hr = CoGetApartmentType(out int t, out _);
        string native = hr == 0
            ? t switch { 0 => "STA", 1 => "MTA", 2 => "NEUTRAL", 3 => "MAIN_STA", _ => "APTTYPE_" + t }
            : $"hr=0x{hr:X8}";
        return $"managed={managed} native={native}";
    }

    static object NewConnector()
    {
        var t = Type.GetTypeFromProgID("V83.COMConnector")
                ?? throw new InvalidOperationException("V83.COMConnector not registered");
        return Activator.CreateInstance(t)!;
    }

    static (string conn, string name) LoadConfig(string path)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return (root.GetProperty("connectionString").GetString()!,
                root.GetProperty("baseName").GetString()!);
    }

    static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        catch { }
    }

    static (long connectMs, long workMs, string note) ConnectAndWork(object connector, string connString, bool doWork)
    {
        var sw = Stopwatch.StartNew();
        dynamic session = ((dynamic)connector).Connect(connString);
        long connectMs = sw.ElapsedMilliseconds;

        long workMs = 0;
        string note = "";
        if (doWork)
        {
            sw.Restart();
            try
            {
                dynamic cats = session.Метаданные.Справочники;
                int n = (int)cats.Количество();
                string first = n > 0 ? (string)cats.Получить(0).Имя : null;
                if (first != null)
                {
                    dynamic q = session.NewObject("Запрос");
                    q.Текст = "ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК C ИЗ Справочник." + first;
                    dynamic sel = q.Выполнить().Выбрать();
                    sel.Следующий();
                    note = $"{first}={sel.C}";
                }
                else note = "no catalogs";
            }
            catch (Exception ex) { note = "work failed: " + ex.GetType().Name + " " + ex.Message.Split('\n')[0]; }
            workMs = sw.ElapsedMilliseconds;
        }

        Release(session);
        return (connectMs, workMs, note);
    }

    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "a";
        string cfgPath = args.Length > 1 ? args[1] : @"D:\aiba\research\1c-adapter\csharp-worker\config.local.json";
        int n = int.TryParse(args.ElementAtOrDefault(2), out var pn) ? pn : 4;
        int rounds = int.TryParse(args.ElementAtOrDefault(3), out var pr) ? pr : 20;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (mode == "a") { RunA(); return; }

        var (connString, baseName) = LoadConfig(cfgPath);
        Console.WriteLine($"[cfg] base={baseName} kind={(connString.StartsWith("File=") ? "FILE" : "SERVER")}");

        switch (mode)
        {
            case "b": RunB(connString, n); break;
            case "c": RunC(connString, n); break;
            case "d": RunD(connString); break;
            case "e": RunE(connString, n, rounds); break;
            default: Console.WriteLine("modes: a b c d e"); break;
        }
    }

    // ---------- A: apartment ----------
    static void RunA()
    {
        Console.WriteLine("=== A: apartment of a plain .NET console process ===");
        Console.WriteLine($"main thread        : {ApartmentOfCurrentThread()}");
        Console.WriteLine($"thread-pool thread : {Task.Run(() => ApartmentOfCurrentThread()).Result}");

        var t = new Thread(() => Console.WriteLine($"new Thread default : {ApartmentOfCurrentThread()}"));
        t.Start(); t.Join();

        var sta = new Thread(() => Console.WriteLine($"new Thread w/ STA  : {ApartmentOfCurrentThread()}"));
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start(); sta.Join();

        Console.WriteLine("\n-- after creating the connector --");
        object c = null;
        try
        {
            c = NewConnector();
            Console.WriteLine($"main thread        : {ApartmentOfCurrentThread()}");
            Console.WriteLine($"connector type     : {c.GetType().FullName}");
            Console.WriteLine($"is COM object      : {Marshal.IsComObject(c)}");
        }
        catch (Exception ex) { Console.WriteLine("connector create failed: " + ex.Message); }
        finally { if (c != null) Release(c); }
    }

    // ---------- B: one connector, N threads ----------
    static void RunB(string cs, int n)
    {
        Console.WriteLine($"=== B: ONE connector object, {n} threads ===");
        object connector = NewConnector();

        var sw = Stopwatch.StartNew();
        var serial = new List<long>();
        for (int i = 0; i < n; i++) serial.Add(ConnectAndWork(connector, cs, false).connectMs);
        long serialWall = sw.ElapsedMilliseconds;
        Console.WriteLine($"serial {n}x   : wall {serialWall} ms   per-connect [{string.Join(", ", serial)}]");

        sw.Restart();
        var results = RunThreads(n, idx => ConnectAndWork(connector, cs, false).connectMs);
        long parWall = sw.ElapsedMilliseconds;
        Console.WriteLine($"parallel {n}x : wall {parWall} ms   per-connect [{string.Join(", ", results.Select(r => r.ms))}]");
        ReportErrors(results);

        Console.WriteLine($"=> speedup {(double)serialWall / Math.Max(1, parWall):F2}x  " +
                          $"({(parWall < serialWall * 0.75 ? "CONCURRENT" : "SERIALIZED")})");
        Release(connector);
    }

    // ---------- C: N connectors ----------
    static void RunC(string cs, int n)
    {
        Console.WriteLine($"=== C: {n} connector objects, one per thread ===");
        var connectors = new object[n];
        try
        {
            for (int i = 0; i < n; i++) connectors[i] = NewConnector();
            Console.WriteLine($"created {n} V83.ComConnector instances OK (distinct refs: {connectors.Distinct().Count()})");
        }
        catch (Exception ex) { Console.WriteLine("creating N connectors FAILED: " + ex.Message); return; }

        var sw = Stopwatch.StartNew();
        var results = RunThreads(n, idx => ConnectAndWork(connectors[idx], cs, false).connectMs);
        Console.WriteLine($"parallel {n}x : wall {sw.ElapsedMilliseconds} ms   per-connect [{string.Join(", ", results.Select(r => r.ms))}]");
        ReportErrors(results);

        foreach (var c in connectors) if (c != null) Release(c);
    }

    // ---------- D: scaling with connect + query ----------
    static void RunD(string cs)
    {
        Console.WriteLine("=== D: scaling curve, N connections doing connect + query ===");
        Console.WriteLine("  k   wall ms   per-op ms");
        foreach (int k in new[] { 1, 2, 4, 8 })
        {
            var connectors = new object[k];
            for (int i = 0; i < k; i++) connectors[i] = NewConnector();

            var res = new (long c, long w, string note, string err)[k];
            var sw = Stopwatch.StartNew();
            var threads = new Thread[k];
            for (int i = 0; i < k; i++)
            {
                int idx = i;
                threads[i] = new Thread(() =>
                {
                    try { var r = ConnectAndWork(connectors[idx], cs, true); res[idx] = (r.connectMs, r.workMs, r.note, null); }
                    catch (Exception ex) { res[idx] = (-1, -1, null, Describe(ex)); }
                });
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            Console.WriteLine($"  {k,-3} {sw.ElapsedMilliseconds,8}   connect[{string.Join(",", res.Select(r => r.c))}] " +
                              $"query[{string.Join(",", res.Select(r => r.w))}]   {res.FirstOrDefault(r => r.err == null).note}");
            foreach (var r in res.Where(r => r.err != null)) Console.WriteLine($"        ERR {r.err}");

            foreach (var c in connectors) if (c != null) Release(c);
            Thread.Sleep(1500); // let 1C drop the sessions between rounds
        }
    }

    // ---------- E: pure query concurrency, connections held open ----------
    //
    // D showed connect dominates and query time is flat, but the query there was a
    // COUNT(*) that happened to hit an empty catalog. E opens the sessions ONCE, picks a
    // catalog that actually has rows, then hammers real row-fetching queries so the
    // measurement is data transfer over COM rather than round-trip latency.
    static void RunE(string cs, int k, int rounds)
    {
        Console.WriteLine($"=== E: {k} held-open sessions x {rounds} rounds, connect cost excluded ===");

        object connector = NewConnector();

        // Open sessions up front, sequentially — this cost is deliberately NOT measured.
        var sessions = new dynamic[k];
        var swOpen = Stopwatch.StartNew();
        for (int i = 0; i < k; i++) sessions[i] = ((dynamic)connector).Connect(cs);
        Console.WriteLine($"opened {k} sessions in {swOpen.ElapsedMilliseconds} ms (excluded from results)");

        // Find a catalog with rows so the query moves real data.
        string catalog = null;
        long catalogRows = 0;
        // NOTE: every COM object below is released on this thread. Leaving them to the GC
        // finalizer is what makes the process die later, mid-measurement.
        dynamic meta = null, cats = null;
        try
        {
            meta = sessions[0].Метаданные;
            cats = meta.Справочники;
            int total = (int)cats.Количество();
            for (int i = 0; i < total && i < 60; i++)
            {
                dynamic item = null, q = null, result = null, sel = null;
                try
                {
                    item = cats.Получить(i);
                    string name = (string)item.Имя;
                    q = sessions[0].NewObject("Запрос");
                    q.Текст = "ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК C ИЗ Справочник." + name;
                    result = q.Выполнить();
                    sel = result.Выбрать();
                    sel.Следующий();
                    long c = Convert.ToInt64(sel.C);
                    if (c > catalogRows) { catalogRows = c; catalog = name; }
                }
                catch { /* some catalogs refuse a plain count; skip */ }
                finally
                {
                    if (sel != null) Release(sel);
                    if (result != null) Release(result);
                    if (q != null) Release(q);
                    if (item != null) Release(item);
                }
                if (catalogRows >= 1000) break;   // big enough, stop scanning
            }
        }
        catch (Exception ex) { Console.WriteLine("catalog discovery failed: " + Describe(ex)); }
        finally
        {
            if (cats != null) Release(cats);
            if (meta != null) Release(meta);
        }

        if (catalog == null) { Console.WriteLine("no queryable catalog found — aborting E"); Cleanup(); return; }
        Console.WriteLine($"query target : Справочник.{catalog} ({catalogRows:N0} rows)");

        string sql = $"ВЫБРАТЬ ПЕРВЫЕ 500 Ссылка, Код, Наименование ИЗ Справочник.{catalog}";

        // One query = execute + walk every row, so COM actually marshals the data.
        //
        // Every intermediate 1C COM object is released HERE, on the thread that created it.
        // Left to the GC, they are freed later on the finalizer thread while other threads
        // are still calling into 1C, and the process dies with an AccessViolation after a
        // few dozen queries — the same failure as oscript's COMWrapperContext.Finalize
        // crash. Set COM_PROBE_LEAK=1 to reproduce that on purpose.
        bool leak = Environment.GetEnvironmentVariable("COM_PROBE_LEAK") == "1";

        long OneQuery(dynamic session)
        {
            var sw = Stopwatch.StartNew();
            dynamic q = null, result = null, sel = null;
            try
            {
                q = session.NewObject("Запрос");
                q.Текст = sql;
                result = q.Выполнить();
                sel = result.Выбрать();
                while (sel.Следующий()) { var _ = sel.Наименование; }
            }
            finally
            {
                if (!leak)
                {
                    if (sel != null) Release(sel);
                    if (result != null) Release(result);
                    if (q != null) Release(q);
                }
            }
            return sw.ElapsedMilliseconds;
        }

        // Warm up: first query on a session compiles and caches.
        for (int i = 0; i < k; i++) OneQuery(sessions[i]);

        // Serial baseline: ONE session does k*rounds queries.
        var swSerial = Stopwatch.StartNew();
        var serialTimes = new List<long>();
        for (int r = 0; r < rounds * k; r++) serialTimes.Add(OneQuery(sessions[0]));
        long serialWall = swSerial.ElapsedMilliseconds;

        // Parallel: k sessions, each doing `rounds` queries, all at once.
        var perThread = new long[k];
        var errs = new string[k];
        var swPar = Stopwatch.StartNew();
        var threads = new Thread[k];
        for (int i = 0; i < k; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try { for (int r = 0; r < rounds; r++) perThread[idx] += OneQuery(sessions[idx]); }
                catch (Exception ex) { errs[idx] = Describe(ex); }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        long parWall = swPar.ElapsedMilliseconds;

        int totalQueries = rounds * k;
        Console.WriteLine();
        Console.WriteLine($"serial   : {totalQueries} queries on 1 session  wall {serialWall,6} ms   " +
                          $"avg {serialTimes.Average():F1} ms/query");
        Console.WriteLine($"parallel : {totalQueries} queries on {k} sessions wall {parWall,6} ms   " +
                          $"avg {perThread.Sum() / (double)totalQueries:F1} ms/query");
        Console.WriteLine($"per-thread totals: [{string.Join(", ", perThread)}] ms");
        foreach (var e in errs.Where(e => e != null)) Console.WriteLine("   ERR " + e);
        Console.WriteLine($"=> speedup {(double)serialWall / Math.Max(1, parWall):F2}x on {k} threads " +
                          $"(ideal {k}x, efficiency {(double)serialWall / Math.Max(1, parWall) / k * 100:F0}%)");

        Cleanup();

        void Cleanup()
        {
            foreach (var s in sessions) if (s != null) Release(s);
            Release(connector);
        }
    }

    // ---------- helpers ----------
    static (long ms, string err)[] RunThreads(int n, Func<int, long> body)
    {
        var results = new (long ms, string err)[n];
        var threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try { results[idx] = (body(idx), null); }
                catch (Exception ex) { results[idx] = (-1, Describe(ex)); }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        return results;
    }

    static void ReportErrors((long ms, string err)[] r)
    {
        foreach (var e in r.Where(x => x.err != null)) Console.WriteLine("   ERR " + e.err);
    }

    static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
}
