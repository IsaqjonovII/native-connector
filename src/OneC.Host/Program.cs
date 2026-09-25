using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// The host process. One per (1C version, bitness); it binds that comcntr.dll, owns one
/// connector, and serves warm sessions for the bases assigned to it.
/// Command modes exist so each block can be measured on its own — the long-lived service
/// surface (IPC) is deliberately not designed yet.
/// </summary>
public static class Program
{
    public static int Main(string[] argv)
    {
        int code = Run(argv);
        // Every mode has released its sessions by now. A normal exit would run 1C's DLL detach
        // code, which can crash (null read in rtrsrvc.dll) and then spin forever in 1C's
        // ImageMagick crash filter — see NativeProcess.
        if (ComActivator.BoundPath is not null) NativeProcess.Exit(code);
        return code;
    }

    private static int Run(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = argv.Length > 0 && !argv[0].StartsWith("--") ? argv[0] : "help";

        try
        {
            return mode switch
            {
                "info" => Info(argv),
                "plan" => Plan(argv),
                "read" => Read(argv),
                "mem" => Mem(argv),
                "bench" => Bench(argv),
                "errors" => Errors(argv),
                "probe-dispid" => ProbeDispId(argv),
                "probe-connectorpool" => ProbeConnectorPool(argv),
                "lazy" => Lazy(argv),
                "poolram" => PoolRam(argv),
                "churn" => Churn(argv),
                "grow" => Grow(argv),
                "serve" => ServeMode.Run(argv),
                "connstress" => ConnStress(argv),
                "deletions" => Deletions(argv),
                "refbench" => RefBench(argv),
                "badconnect" => BadConnect(argv),
                "write" => Write(argv),
                "multi" => MultiBaseScenario.Run(LoadBases(argv), ResolveComcntr(argv, LoadBases(argv)),
                                                 IntArg(argv, "phase", 20), IntArg(argv, "idle", 10),
                                                 Arg(argv, "doc"), IntArg(argv, "pressure", 600),
                                                 Arg(argv, "phases") ?? "ABCD"),
                "cleanup" => Cleanup(argv),
                "catparity" => CatParity(argv),
                "catbench" => CatBench(argv),
                "catwalk" => CatWalk(argv),
                "docparity" => DocParity(argv),
                "docwalk" => DocWalk(argv),
                "regparity" => RegParity(argv),
                "regwalk" => RegWalk(argv),
                "coldread" => ColdRead(argv),
                "xdtobench" => XdtoBenchMode(argv),
                "logexport" => LogExport(argv),
                "logparity" => LogParity(argv),
                "nativecrash" => NativeCrash(argv),
                "writeparity" => WriteParity(argv),
                "exittrap" => ExitTrap(argv),
                "logscan" => EventLogExport.Scan(LoadBases(argv).First(x => x.Name == (Arg(argv, "base") ?? LoadBases(argv)[0].Name)),
                                                 IntArg(argv, "mb", 8) * 1048576L),
                _ => Help()
            };
        }
        catch (OneCException oe)
        {
            Console.Error.WriteLine("ONEC " + oe.Describe());
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            OneC.Host — modes

              info                  installs on this machine, bitness, host keys
              plan    --bases f.json      group bases into host processes
              read    --base N --entity T --fields A,B --limit N
              mem     --bases f.json      RAM matrix: host only, +K1 +K2 +K4, workload, recycle
              bench   --base N --entity T --fields A,B --k K --reps R
              errors  --base N            error-path verification
              probe-dispid --base N       is the 1C DISPID space global?
              probe-connectorpool --base N  PoolCapacity / PoolTimeout / MaxConnections
              lazy    --bases f.json      prove nothing is created until first use

            common: --comcntr <path>  --limit N  --json
            """);
        return 1;
    }

    // ---------- arg helpers ----------

    private static string? Arg(string[] a, string k)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == "--" + k) return a[i + 1];
        return null;
    }
    private static bool Flag(string[] a, string k) => a.Contains("--" + k);
    private static int IntArg(string[] a, string k, int def) =>
        int.TryParse(Arg(a, k), out int v) ? v : def;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static List<OneCBase> LoadBases(string[] argv)
    {
        string? file = Arg(argv, "bases");
        if (file is not null)
            return JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(file))
                   ?? new List<OneCBase>();

        string? single = Arg(argv, "cfg");
        if (single is not null)
        {
            var r = JsonDocument.Parse(File.ReadAllText(single)).RootElement;
            return new List<OneCBase>
            {
                new()
                {
                    Name = r.GetProperty("baseName").GetString()!,
                    ConnectionString = r.GetProperty("connectionString").GetString()!,
                    PlatformVersion = Arg(argv, "ver") ?? "8.3.15.1565"
                }
            };
        }
        throw new ArgumentException("pass --bases <list.json> or --cfg <single.json>");
    }

    private static string ResolveComcntr(string[] argv, IEnumerable<OneCBase> bases)
    {
        string? explicitPath = Arg(argv, "comcntr");
        if (explicitPath is not null) return explicitPath;

        var installs = PlatformCatalog.Discover();
        var first = bases.FirstOrDefault()
            ?? throw new ArgumentException("no bases to resolve a platform version from");
        var pick = PlatformCatalog.Select(installs, first.PlatformVersion, first.IsFile)
            ?? throw new InvalidOperationException(
                $"no 1C install for {first.PlatformVersion} ({first.Kind})");
        return pick.ComcntrPath;
    }

    private static int CatParity(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var names = Arg(argv, "base") is { } one ? new[] { one } : bases.Select(b => b.Name).ToArray();
        return CatalogParityScenario.Run(m, names, IntArg(argv, "limit", 20), Arg(argv, "catalog"));
    }

    private static int CatBench(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        string cats = Arg(argv, "catalogs") ?? "Номенклатура,Контрагенты,ДоговорыКонтрагентов,Организации";
        return CatalogParityScenario.Bench(m, Arg(argv, "base") ?? bases[0].Name, cats.Split(','),
                                           IntArg(argv, "rows", 1000), IntArg(argv, "reps", 3));
    }

    private static int DocParity(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var names = Arg(argv, "base") is { } one ? new[] { one } : bases.Select(b => b.Name).ToArray();
        return DocumentParityScenario.Run(m, names, IntArg(argv, "limit", 20), Arg(argv, "document"));
    }

    private static int RegParity(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var names = Arg(argv, "base") is { } one ? new[] { one } : bases.Select(b => b.Name).ToArray();
        return RegisterParityScenario.Run(m, names, IntArg(argv, "limit", 20), Arg(argv, "register"));
    }

    private static int WriteParity(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var names = Arg(argv, "base") is { } one ? new[] { one } : bases.Select(b => b.Name).ToArray();
        return WriteParityScenario.Run(m, names, IntArg(argv, "limit", 1), Arg(argv, "document"), Flag(argv, "post"));
    }

    private static int LogParity(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var b = bases.First(x => x.Name == (Arg(argv, "base") ?? bases[0].Name));
        return EventLogExport.Parity(m, b, IntArg(argv, "wait", 60), Flag(argv, "write"), Flag(argv, "rollback"));
    }

    private static int LogExport(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        int minutes = IntArg(argv, "minutes", 10);
        var to = DateTime.Now;
        var (events, sample) = m.Use(Arg(argv, "base") ?? bases[0].Name, ctx => EventLogExport.Read(ctx, to.AddMinutes(-minutes), to));
        Console.WriteLine(sample);
        Console.WriteLine($"--- {events.Count} data events; kinds: {string.Join(", ", events.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}; " +
                          $"tx: {string.Join(", ", events.GroupBy(e => e.TxStatus).Select(g => $"{g.Key} {g.Count()}"))}; with ref: {events.Count(e => e.Ref is not null)}");
        return 0;
    }

    private static int XdtoBenchMode(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        return XdtoBench.Run(m, Arg(argv, "base") ?? bases[0].Name, Arg(argv, "register") ?? "Хозрасчетный",
                             DateTime.Parse(Arg(argv, "from") ?? "2025-06-01"), IntArg(argv, "rows", 1000), IntArg(argv, "reps", 3));
    }

    private static int ColdRead(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var kind = (Arg(argv, "kind") ?? "accounting") switch
        {
            "information" or "info" => RegisterKind.Information,
            "accumulation" => RegisterKind.Accumulation,
            _ => RegisterKind.Accounting
        };
        var workers = (Arg(argv, "workers") ?? "1,2,4").Split(',').Select(int.Parse).ToArray();
        return ColdReadScenario.Run(m, Arg(argv, "base") ?? bases[0].Name, kind, Arg(argv, "register") ?? "Хозрасчетный",
                                    DateTime.Parse(Arg(argv, "from") ?? "2025-01-01"), DateTime.Parse(Arg(argv, "to") ?? "2025-02-01"),
                                    IntArg(argv, "page", 250), workers);
    }

    private static int RegWalk(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        var kind = (Arg(argv, "kind") ?? "accounting") switch
        {
            "information" or "info" => RegisterKind.Information,
            "accumulation" => RegisterKind.Accumulation,
            _ => RegisterKind.Accounting
        };
        DateTime? D(string k) => Arg(argv, k) is { } s ? DateTime.Parse(s) : null;
        return RegisterParityScenario.Walk(m, Arg(argv, "base") ?? bases[0].Name, kind, Arg(argv, "register") ?? "Хозрасчетный",
                                           IntArg(argv, "page", 250), D("from") ?? new DateTime(2025, 1, 1), D("to"), IntArg(argv, "pages", int.MaxValue));
    }

    private static int DocWalk(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        DateTime? D(string k) => Arg(argv, k) is { } s ? DateTime.Parse(s) : null;
        return DocumentParityScenario.Walk(m, Arg(argv, "base") ?? bases[0].Name, Arg(argv, "document") ?? "РеализацияТоваровУслуг",
                                           IntArg(argv, "page", 250), D("from"), D("to"), IntArg(argv, "pages", int.MaxValue));
    }

    private static int CatWalk(string[] argv)
    {
        var bases = LoadBases(argv);
        using var m = Open(argv, bases);
        for (int i = IntArg(argv, "repeat", 1); i > 0; i--)
            CatalogParityScenario.Walk(m, Arg(argv, "base") ?? bases[0].Name, Arg(argv, "catalog") ?? "Контрагенты",
                                       IntArg(argv, "page", 1000), !Flag(argv, "no-tabular"));
        return 0;
    }

    private static SessionManager Open(string[] argv, List<OneCBase> bases, PoolOptions? opt = null)
    {
        var m = new SessionManager(ResolveComcntr(argv, bases), opt);
        foreach (var b in bases) m.Register(b);
        return m;
    }

    // ---------- info / plan ----------

    private static int Info(string[] argv)
    {
        Console.WriteLine($"process bitness: {PlatformCatalog.CurrentProcessBitness}");
        Console.WriteLine("installed 1C platforms:");
        foreach (var i in PlatformCatalog.Discover())
            Console.WriteLine($"  {i.Version,-14} {i.Bitness,-5} hostKey={i.HostKey,-18} {i.ComcntrPath}");
        return 0;
    }

    private static int Plan(string[] argv)
    {
        var bases = LoadBases(argv);
        var (hosts, bad) = HostPlan.Build(bases, PlatformCatalog.Discover());
        Console.WriteLine($"{bases.Count} bases -> {hosts.Count} host process(es)");
        foreach (var h in hosts)
            Console.WriteLine($"  {h.Key,-18} {h.Install.ComcntrPath}\n" +
                              string.Join("\n", h.Bases.Select(b => $"      {b.Kind,-6} {b.Name}")));
        foreach (var (b, why) in bad) Console.WriteLine($"  UNPLACEABLE {b.Name}: {why}");
        return bad.Count == 0 ? 0 : 1;
    }

    // ---------- the read vertical slice ----------

    private static int Read(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        var q = new ReadQuery
        {
            Entity = Arg(argv, "entity") ?? "Справочник.Номенклатура",
            Fields = (Arg(argv, "fields") ?? "Ссылка,Наименование").Split(',', StringSplitOptions.RemoveEmptyEntries),
            Limit = IntArg(argv, "limit", 20),
            OrderBy = Arg(argv, "orderby"),
            Descending = Flag(argv, "desc")
        };

        using var m = Open(argv, bases);
        var svc = new ReadService(m);
        var before = ResourceSampler.Take("before read", m.BudgetUsed);
        var r = svc.Read(baseName, q);
        var after = ResourceSampler.Take("after read", m.BudgetUsed);

        if (Flag(argv, "json")) Console.WriteLine(JsonSerializer.Serialize(r, Json));
        else
        {
            Console.WriteLine($"{r.Rows.Count} rows from {r.Entity} on {r.BaseName} " +
                              $"in {r.ElapsedMs} ms (session #{r.SessionId})");
            foreach (var row in r.Rows.Take(10))
                Console.WriteLine("  " + string.Join(" | ", q.Fields.Select(f => $"{f}={row[f]}")));
            if (r.Rows.Count > 10) Console.WriteLine($"  … {r.Rows.Count - 10} more");
        }

        Console.WriteLine(before.Line());
        Console.WriteLine(after.Line());
        Console.WriteLine($"comrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");
        return 0;
    }

    // ---------- resource matrix ----------

    private static int Mem(string[] argv)
    {
        var bases = LoadBases(argv);
        var samples = new List<ResourceSample>();
        void Snap(string label, SessionManager? m) =>
            samples.Add(ResourceSampler.Take(label, m?.BudgetUsed ?? 0));

        Snap("00 process start", null);

        var opt = new PoolOptions
        {
            GlobalMaxSessions = IntArg(argv, "budget", 8),
            PerBaseMaxSessions = 4,
            IdleTimeout = TimeSpan.FromSeconds(IntArg(argv, "idle", 5)),
            SweepInterval = TimeSpan.FromSeconds(2)
        };

        // The matrix must reach K=4 even for file bases, whose default ceiling is 2 on
        // memory grounds — measuring the cost is the point of this mode.
        var measured = bases.Select(b => b with { MaxConcurrencyOverride = 4 }).ToList();

        using (var m = Open(argv, measured, opt))
        {
            Snap("01 host + connector", m);

            foreach (var b in measured)
            {
                var svc = new ReadService(m);
                var q = new ReadQuery
                {
                    Entity = Arg(argv, "entity") ?? "Справочник.Номенклатура",
                    Fields = (Arg(argv, "fields") ?? "Наименование").Split(','),
                    Limit = IntArg(argv, "limit", 50)
                };

                foreach (int k in new[] { 1, 2, 4 })
                {
                    HoldSessions(m, b.Name, k, q, svc);
                    Snap($"02 {b.Kind}:{b.Name} warm K={k}", m);
                }

                // Active workload across the warm pool.
                var sw = Stopwatch.StartNew();
                int reps = IntArg(argv, "reps", 40);
                RunParallel(m, svc, b.Name, q, 4, reps);
                Snap($"03 {b.Kind}:{b.Name} after workload ({reps * 4} reads, {sw.ElapsedMilliseconds} ms)", m);

                Thread.Sleep((int)(opt.IdleTimeout.TotalMilliseconds + 500));
                int swept = m.SweepIdle();
                Snap($"04 {b.Kind}:{b.Name} after idle sweep (retired {swept})", m);

                m.DrainBase(b.Name);
                Snap($"05 {b.Kind}:{b.Name} after drain", m);
            }
        }

        Snap("06 after host shutdown", null);

        Console.WriteLine();
        foreach (var s in samples) Console.WriteLine(s.Line());

        Console.WriteLine("\ndeltas vs host+connector:");
        var baseline = samples.First(s => s.Label.StartsWith("01"));
        foreach (var s in samples.Skip(2)) Console.WriteLine("  " + ResourceSampler.Delta(baseline, s));

        Console.WriteLine($"\ncomrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");

        string? csv = Arg(argv, "csv");
        if (csv is not null)
        {
            File.WriteAllLines(csv, new[] { ResourceSample.CsvHeader }.Concat(samples.Select(s => s.Csv())));
            Console.WriteLine($"csv -> {csv}");
        }
        return 0;
    }

    /// <summary>Forces exactly K sessions to exist and be warm, by holding K rents at once.</summary>
    private static void HoldSessions(SessionManager m, string baseName, int k, ReadQuery q, ReadService svc)
    {
        var gate = new ManualResetEventSlim(false);
        var started = new CountdownEvent(k);
        var threads = new List<Thread>();
        for (int i = 0; i < k; i++)
        {
            var t = new Thread(() =>
            {
                m.Use(baseName, ctx =>
                {
                    started.Signal();
                    gate.Wait();
                    return 0;
                });
            }) { IsBackground = true };
            threads.Add(t); t.Start();
        }
        started.Wait();
        gate.Set();
        foreach (var t in threads) t.Join();
        gate.Dispose(); started.Dispose();
    }

    private static void RunParallel(SessionManager m, ReadService svc, string baseName,
                                    ReadQuery q, int k, int repsPerThread)
    {
        var threads = new List<Thread>();
        for (int i = 0; i < k; i++)
        {
            var t = new Thread(() =>
            {
                for (int r = 0; r < repsPerThread; r++) svc.Read(baseName, q);
            }) { IsBackground = true };
            threads.Add(t); t.Start();
        }
        foreach (var t in threads) t.Join();
    }

    // ---------- soak-style growth check ----------

    private static int Bench(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        int k = IntArg(argv, "k", 4), reps = IntArg(argv, "reps", 50), rounds = IntArg(argv, "rounds", 5);

        var q = new ReadQuery
        {
            Entity = Arg(argv, "entity") ?? "Справочник.Номенклатура",
            Fields = (Arg(argv, "fields") ?? "Наименование").Split(','),
            Limit = IntArg(argv, "limit", 50)
        };

        using var m = Open(argv, bases.Select(b => b with { MaxConcurrencyOverride = k }).ToList(),
            new PoolOptions
            {
                GlobalMaxSessions = IntArg(argv, "budget", 8),
                PerBaseMaxSessions = k,
                MaxWorkingSetMb = IntArg(argv, "maxws", 1200)
            });
        var svc = new ReadService(m);

        Console.WriteLine(ResourceSampler.Take("warmup", m.BudgetUsed).Line());
        svc.Read(baseName, q);

        var first = ResourceSampler.Take("round 0 (baseline)", m.BudgetUsed);
        Console.WriteLine(first.Line());

        for (int round = 1; round <= rounds; round++)
        {
            var sw = Stopwatch.StartNew();
            RunParallel(m, svc, baseName, q, k, reps);
            var s = ResourceSampler.Take($"round {round} ({k * reps} reads, {sw.ElapsedMilliseconds} ms, " +
                                         $"{k * reps * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} op/s)",
                                         m.BudgetUsed);
            Console.WriteLine(s.Line());
        }

        var last = ResourceSampler.Take("after all rounds", m.BudgetUsed);
        Console.WriteLine(ResourceSampler.Delta(first, last));
        foreach (var st in m.Stats()) Console.WriteLine("  " + st);
        Console.WriteLine($"comrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");
        return 0;
    }

    // ---------- error paths ----------

    private static int Errors(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        using var m = Open(argv, bases);
        var svc = new ReadService(m);

        void Case(string label, Action act)
        {
            try { act(); Console.WriteLine($"  {label,-28} NO ERROR RAISED"); }
            catch (OneCException oe) { Console.WriteLine($"  {label,-28} {oe.Describe()}"); }
            catch (Exception ex) { Console.WriteLine($"  {label,-28} {ex.GetType().Name}: {ex.Message}"); }
        }

        Console.WriteLine("error paths:");
        Case("unknown table", () => svc.Read(baseName, new ReadQuery
        { Entity = "Документ.НетТакогоДокумента", Fields = new[] { "Ссылка" }, Limit = 1 }));
        Case("unknown field", () => svc.Read(baseName, new ReadQuery
        { Entity = "Справочник.Номенклатура", Fields = new[] { "НетТакогоПоля" }, Limit = 1 }));
        Case("illegal identifier", () => svc.Read(baseName, new ReadQuery
        { Entity = "Справочник.Номенклатура\"; УДАЛИТЬ", Fields = new[] { "Ссылка" }, Limit = 1 }));
        Case("unregistered base", () => svc.Read("no-such-base", new ReadQuery
        { Entity = "Справочник.Номенклатура", Fields = new[] { "Ссылка" }, Limit = 1 }));
        Case("bad credentials", () =>
        {
            var bad = bases[0] with
            {
                Name = "bad-creds",
                ConnectionString = System.Text.RegularExpressions.Regex.Replace(
                    bases[0].ConnectionString, "Pwd=[^;]*", "Pwd=definitely-wrong")
            };
            m.Register(bad);
            svc.Read("bad-creds", new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Ссылка" }, Limit = 1 });
        });

        Console.WriteLine("\nafter the error run the host must still work:");
        var ok = svc.Read(baseName, new ReadQuery
        { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 3 });
        Console.WriteLine($"  recovered: {ok.Rows.Count} rows in {ok.ElapsedMs} ms");
        Console.WriteLine($"  comrefs live={ComRef.Live} wrongThread={ComRef.WrongThreadReleases}");
        foreach (var st in m.Stats()) Console.WriteLine("  " + st);
        return 0;
    }

    // ---------- probes ----------

    /// <summary>
    /// Is the 1C DISPID space global? Resolves the same member names against unrelated
    /// object types. Caching is only safe if every name gives one DISPID everywhere.
    /// </summary>
    private static int ProbeDispId(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        using var m = Open(argv, bases);

        m.Use(baseName, ctx =>
        {
            using var scope = new ComScope();
            var probes = new List<(string Name, object Obj)>
            {
                ("connection", ctx.Connection),
                ("Запрос", scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос")),
                ("Массив", scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Массив"), "Массив")),
                ("Структура", scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Структура"), "Структура")),
                ("ТаблицаЗначений", scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "ТаблицаЗначений"), "ТЗ")),
                ("СистемнаяИнформация", scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "СистемнаяИнформация"), "СИ")),
            };

            string[] names = { "Количество", "Выполнить", "Текст", "Очистить", "Добавить",
                               "Вставить", "Свойство", "Наименование", "ВерсияПриложения",
                               "НетТакогоЧлена" };

            Console.WriteLine($"{"member",-22}" + string.Join("", probes.Select(p => $"{p.Name,-22}")));
            var seen = new Dictionary<string, HashSet<int>>();
            foreach (var n in names)
            {
                var line = new StringBuilder($"{n,-22}");
                foreach (var (_, o) in probes)
                {
                    string cell;
                    try
                    {
                        int id = Dispatch.DispIdOf(o, n, ctx.Error);
                        cell = id.ToString();
                        if (!seen.TryGetValue(n, out var set)) seen[n] = set = new HashSet<int>();
                        set.Add(id);
                    }
                    catch (OneCException) { cell = "-"; }
                    line.Append($"{cell,-22}");
                }
                Console.WriteLine(line);
            }

            var conflicting = seen.Where(kv => kv.Value.Count > 1).ToList();
            Console.WriteLine();
            if (conflicting.Count == 0)
                Console.WriteLine("VERDICT: every resolved name has ONE DISPID across all probed types " +
                                  "-> a process-wide name->DISPID cache is safe.");
            else
                foreach (var kv in conflicting)
                    Console.WriteLine($"VERDICT: '{kv.Key}' resolves to {kv.Value.Count} different DISPIDs " +
                                      $"({string.Join(",", kv.Value)}) -> global caching is UNSAFE.");
            return 0;
        });
        return 0;
    }

    /// <summary>
    /// The connector's own pool knobs, found in its type library. Read them, do not enable
    /// them blindly: they may duplicate or fight our session pool.
    /// </summary>
    private static int ProbeConnectorPool(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        string comcntr = ResolveComcntr(argv, bases);

        ComActivator.Bind(comcntr);
        using var connector = ComRef.Own(ComActivator.CreateConnector(), "connector");
        var ctx = new ErrorContext(baseName, null);

        Console.WriteLine("connector properties as created:");
        foreach (var p in new[] { "PoolCapacity", "PoolTimeout", "MaxConnections",
                                  "RAgentPortDefault", "RMngrPortDefault" })
        {
            try { Console.WriteLine($"  {p,-20} = {Dispatch.Get(connector.Target, p, ctx)}"); }
            catch (OneCException oe) { Console.WriteLine($"  {p,-20} ! {oe.Message}"); }
        }

        Console.WriteLine("\nare they writable?");
        foreach (var (p, v) in new (string, object)[] { ("PoolCapacity", 4), ("PoolTimeout", 30), ("MaxConnections", 8) })
        {
            try
            {
                Dispatch.Set(connector.Target, p, v, ctx);
                Console.WriteLine($"  {p,-20} set to {v}, reads back {Dispatch.Get(connector.Target, p, ctx)}");
            }
            catch (OneCException oe) { Console.WriteLine($"  {p,-20} ! {oe.Message}"); }
        }

        // Does a pooled connector hand back the same session object twice?
        var b = bases.First(x => x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("\nConnect twice and compare identity:");
        var s1 = ComRef.Own(ConnectorApi.Connect(connector.Target, b.ConnectionString, ctx), "s1");
        var s2 = ComRef.Own(ConnectorApi.Connect(connector.Target, b.ConnectionString, ctx), "s2");
        bool same = ReferenceEquals(s1.Target, s2.Target);
        Console.WriteLine($"  same RCW: {same}");
        Console.WriteLine($"  IUnknown equal: {IUnknownEquals(s1.Target, s2.Target)}");
        s2.Dispose(); s1.Dispose();

        // Does the connector's own pool make reconnect cheap? If it did, connect-per-op
        // would be viable and our warm pool would be duplicated work. Measured, not assumed.
        int cycles = IntArg(argv, "cycles", 6);
        Console.WriteLine($"\nconnect+release latency, {cycles} cycles per setting:");
        foreach (int cap in new[] { 0, 4 })
        {
            using var c2 = ComRef.Own(ComActivator.CreateConnector(), $"connector-cap{cap}");
            try
            {
                Dispatch.Set(c2.Target, "PoolCapacity", cap, ctx);
                Dispatch.Set(c2.Target, "PoolTimeout", cap == 0 ? 0 : 60, ctx);
            }
            catch (OneCException oe) { Console.WriteLine($"  cap={cap} cannot set: {oe.Message}"); }

            var ms = new List<long>();
            for (int i = 0; i < cycles; i++)
            {
                var sw = Stopwatch.StartNew();
                var s = ComRef.Own(ConnectorApi.Connect(c2.Target, b.ConnectionString, ctx), "cycle");
                ms.Add(sw.ElapsedMilliseconds);
                s.Dispose();
            }
            ms.Sort();
            Console.WriteLine($"  PoolCapacity={cap,-2} first={ms.Max(),6} ms  median={ms[ms.Count / 2],6} ms  " +
                              $"min={ms.Min(),6} ms  all=[{string.Join(",", ms)}]");
        }
        return 0;
    }

    private static bool IUnknownEquals(object a, object b)
    {
        IntPtr pa = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(a);
        IntPtr pb = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(b);
        try { return pa == pb; }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(pa);
            System.Runtime.InteropServices.Marshal.Release(pb);
        }
    }

    // ---------- write slice ----------

    /// <summary>
    /// One full lifecycle (create → update → [post] → mark → delete), then an optional
    /// burst of parallel creates with reads running alongside, measured, and cleaned up.
    /// Every document carries a run-specific AIBA_REWRITE_ marker.
    /// </summary>
    private static int Write(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        string docType = Arg(argv, "doc") ?? "ПоступлениеТоваровУслуг";
        string marker = "AIBA_REWRITE_" + DateTime.Now.ToString("MMddHHmmss");
        int burst = IntArg(argv, "burst", 0), k = IntArg(argv, "k", 2);

        using var m = Open(argv, bases);
        var ws = new WriteService(m, "AIBA_REWRITE_");
        var rs = new ReadService(m);
        Console.WriteLine($"write base={baseName} doc={docType} marker={marker}");
        Console.WriteLine(ResourceSampler.Take("00 host", m.BudgetUsed).Line());

        void Step(string label, Func<WriteResult> f)
        {
            try
            {
                var r = f();
                Console.WriteLine($"  ok   {label,-18} {r.ElapsedMs,6} ms  №{r.Number} {r.Ref} session#{r.SessionId}");
            }
            catch (OneCException oe) { Console.WriteLine($"  FAIL {label,-18} {oe.Describe()}"); }
        }

        WriteResult? created = null;
        Step("create (clone)", () => created = ws.CreateByClone(baseName, docType, marker + " lifecycle"));
        if (created is not null)
        {
            Step("update", () => ws.Update(baseName, docType, created.Ref,
                new Dictionary<string, object?> { ["Комментарий"] = marker + " lifecycle updated" }));
            if (Flag(argv, "post")) Step("post", () => ws.Post(baseName, docType, created.Ref));
            Step("mark deleted", () => ws.MarkForDeletion(baseName, docType, created.Ref));
            Step("unmark", () => ws.MarkForDeletion(baseName, docType, created.Ref, mark: false));
            Step("delete", () => ws.Delete(baseName, docType, created.Ref));
        }
        Console.WriteLine(ResourceSampler.Take("01 after lifecycle", m.BudgetUsed).Line());

        if (burst > 0)
        {
            var refs = new System.Collections.Concurrent.ConcurrentBag<string>();
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            int reads = 0;
            var stop = new CancellationTokenSource();
            var reader = new Thread(() =>
            {
                var q = new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 50 };
                while (!stop.IsCancellationRequested)
                {
                    try { rs.Read(baseName, q); Interlocked.Increment(ref reads); }
                    catch (Exception e) { errors.Add("read: " + e.Message); }
                }
            }) { IsBackground = true };
            reader.Start();

            var sw = Stopwatch.StartNew();
            var threads = Enumerable.Range(0, k).Select(t => new Thread(() =>
            {
                for (int i = 0; i < burst; i++)
                {
                    try { refs.Add(ws.CreateByClone(baseName, docType, $"{marker} burst t{t} #{i}").Ref); }
                    catch (Exception e) { errors.Add("create: " + e.Message); }
                }
            }) { IsBackground = true }).ToList();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            long writeMs = sw.ElapsedMilliseconds;
            stop.Cancel(); reader.Join();

            Console.WriteLine($"  burst: {refs.Count}/{k * burst} created in {writeMs} ms " +
                              $"({refs.Count * 1000.0 / Math.Max(1, writeMs):F1} op/s), {reads} reads alongside, " +
                              $"{errors.Count} errors");
            foreach (var e in errors.Distinct().Take(3)) Console.WriteLine("    " + e);
            Console.WriteLine(ResourceSampler.Take("02 after burst", m.BudgetUsed).Line());

            sw.Restart();
            int deleted = 0;
            foreach (var r in refs)
            {
                try { ws.Delete(baseName, docType, r); deleted++; }
                catch (OneCException oe) { Console.WriteLine("    delete failed: " + oe.Message); }
            }
            Console.WriteLine($"  cleanup: deleted {deleted}/{refs.Count} in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine(ResourceSampler.Take("03 after cleanup", m.BudgetUsed).Line());
        }

        int left = ws.FindOwned(baseName, docType, marker).Count;
        Console.WriteLine($"  marker documents left: {left}");
        foreach (var st in m.Stats()) Console.WriteLine("  " + st);
        Console.WriteLine($"comrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");
        return left == 0 ? 0 : 1;
    }

    /// <summary>
    /// Crash hunt (milestone 4.4): the test process died silently inside a failing Connect to
    /// a missing file base. Repeat that failing Connect N times in one process; also mixes in
    /// good connects so a corrupted connector shows up.
    /// </summary>
    private static int BadConnect(string[] argv)
    {
        var bases = LoadBases(argv);
        int n = IntArg(argv, "n", 300);
        string bad = Arg(argv, "cs") ?? "File=\"D:\\1C\\definitely-not-here\";";
        ComActivator.Bind(ResolveComcntr(argv, bases));
        var connector = ComActivator.SharedConnector();
        var ctx = new ErrorContext("bad", null);
        int failed = 0, ok = 0, churned = 0;
        var sw = Stopwatch.StartNew();

        // --churn: other threads open, use and release GOOD sessions while this thread fails
        // to connect — the overlap the test suite has (its sweeper retires sessions every second).
        var stop = new CancellationTokenSource();
        var churners = new List<Thread>();
        if (Flag(argv, "churn"))
            for (int t = 0; t < IntArg(argv, "threads", 2); t++)
            {
                var th = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            var w = SessionWorker.Open(connector, bases[t % bases.Count], Interlocked.Increment(ref churned));
                            w.Execute(c => { using var s = new ComScope(); s.Track(Dispatch.Call(c.Connection, "NewObject", c.Error, "Запрос"), "q"); });
                            w.Dispose();
                        }
                        catch (OneCException oe) { Console.WriteLine("  churn: " + oe.Message); }
                    }
                }) { IsBackground = true };
                th.Start();
                churners.Add(th);
            }

        for (int i = 1; i <= n; i++)
        {
            try { var s = ConnectorApi.Connect(connector, bad, ctx); ComRef.Own(s, "unexpected").Dispose(); ok++; }
            catch (OneCException oe) { failed++; if (i == 1) Console.WriteLine("first error: " + oe.Describe()); }
            if (i % 50 == 0)
            {
                // A good connect in between: proves the connector still works.
                var good = ComRef.Own(ConnectorApi.Connect(connector, bases[0].ConnectionString, ctx), "good");
                good.Dispose();
                Console.WriteLine($"  {i} bad connects, {failed} failed as expected, {ok} unexpectedly succeeded, " +
                                  $"good connect OK, {churned} churned sessions, {sw.ElapsedMilliseconds} ms");
            }
        }
        stop.Cancel();
        churners.ForEach(t => t.Join());
        Console.WriteLine($"survived ({churned} churned sessions alongside)");
        return 0;
    }

    /// <summary>
    /// How to render reference columns cheaply (milestone 4.0). Same N rows, one reference
    /// field, five ways: per-row Строка() (today), query-side ПРЕДСТАВЛЕНИЕ(), per-row
    /// XMLСтрока() for the GUID, fetching the RCW and dropping it (the COM floor), and both
    /// ПРЕДСТАВЛЕНИЕ and the GUID from one query.
    /// </summary>
    private static int RefBench(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        string table = Arg(argv, "entity") ?? "РегистрБухгалтерии.Хозрасчетный";
        string field = Arg(argv, "field") ?? "Регистратор";
        int n = IntArg(argv, "rows", 5000);
        using var m = Open(argv, bases);

        (long ms, int rows, string sample) Run(string select, Func<SessionContext, object, ComScope, string?> perRow)
            => m.Use(baseName, ctx =>
            {
                using var scope = new ComScope();
                var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "q");
                Dispatch.Set(q, "Текст", $"ВЫБРАТЬ ПЕРВЫЕ {n} {select} ИЗ {table}", ctx.Error);
                var sw = Stopwatch.StartNew();
                var r = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "r");
                var c = scope.Track(Dispatch.Call(r, "Выбрать", ctx.Error), "c");
                int rows = 0; string sample = "";
                while (Dispatch.CallBool(c, "Следующий", ctx.Error))
                {
                    using var row = new ComScope();
                    var s = perRow(ctx, c, row);
                    if (rows++ == 0) sample = s ?? "null";
                }
                return (sw.ElapsedMilliseconds, rows, sample);
            });

        Run($"{field} КАК F", (ctx, c, s) => "warm-up");   // warm the session and the query plan

        var variants = new (string Name, string Select, Func<SessionContext, object, ComScope, string?> Row)[]
        {
            ("B  query ПРЕДСТАВЛЕНИЕ()    ", $"ПРЕДСТАВЛЕНИЕ({field}) КАК P",
                (ctx, c, s) => Dispatch.GetString(c, "P", ctx.Error)),
            ("C  per-row XMLСтрока() guid ", $"{field} КАК F",
                (ctx, c, s) => { var v = Dispatch.Get(c, "F", ctx.Error); if (v is null) return null; s.Add(v, "ref"); return OneCValue.RefGuid(v, ctx); }),
            ("D  fetch RCW, drop (floor)  ", $"{field} КАК F",
                (ctx, c, s) => { var v = Dispatch.Get(c, "F", ctx.Error); if (v is not null && System.Runtime.InteropServices.Marshal.IsComObject(v)) s.Add(v, "ref"); return "-"; }),
            ("E  ПРЕДСТАВЛЕНИЕ + guid     ", $"{field} КАК F, ПРЕДСТАВЛЕНИЕ({field}) КАК P",
                (ctx, c, s) => { var v = Dispatch.Get(c, "F", ctx.Error); if (v is not null) s.Add(v, "ref"); return Dispatch.GetString(c, "P", ctx.Error) + " | " + (v is null ? "" : OneCValue.RefGuid(v, ctx)); }),
            ("F  raw Строка() on conn     ", $"{field} КАК F",
                (ctx, c, s) =>
                {
                    var v = Dispatch.Get(c, "F", ctx.Error);
                    if (v is null || !System.Runtime.InteropServices.Marshal.IsComObject(v)) return v?.ToString();
                    s.Add(v, "ref");
                    try { return "OK:" + Dispatch.Call(ctx.Connection, "Строка", ctx.Error, v); }
                    catch (OneCException oe) { return $"ERR:[{oe.Layer}] {oe.Message}"; }
                }),
        };

        Console.WriteLine($"{table}.{field}, {n} rows, base {baseName}");
        foreach (var v in variants)
        {
            var (ms, rows, sample) = Run(v.Select, v.Row);
            if (sample.Length > 70) sample = sample[..70] + "…";
            Console.WriteLine($"  {v.Name} {ms,7} ms  {ms * 1000.0 / Math.Max(1, rows),7:F1} µs/row  rows={rows}  e.g. {sample}");
        }
        return 0;
    }

    /// <summary>
    /// Read-only: every data-deletion event in the 1C event log since N minutes ago, exported
    /// with ВыгрузитьЖурналРегистрации and parsed. Used to establish exactly which documents a
    /// cleanup removed.
    /// </summary>
    private static int Deletions(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        int minutes = IntArg(argv, "since", 60);
        using var m = Open(argv, bases);
        string file = Path.Combine(Path.GetTempPath(), $"onec-evlog-{baseName}-{Environment.ProcessId}.xml");

        m.Use(baseName, ctx =>
        {
            using var scope = new ComScope();
            var filter = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Структура"), "Структура");
            Dispatch.Call(filter, "Вставить", ctx.Error, "ДатаНачала", DateTime.Now.AddMinutes(-minutes));
            Dispatch.Call(filter, "Вставить", ctx.Error, "Событие", "_$Data$_.Delete");
            Dispatch.Call(ctx.Connection, "ВыгрузитьЖурналРегистрации", ctx.Error, file, filter);
            return 0;
        });

        var xml = System.Xml.Linq.XDocument.Load(file);
        var events = xml.Descendants().Where(e => e.Name.LocalName == "Event").ToList();
        Console.WriteLine($"{baseName}: {events.Count} delete events in the last {minutes} min");
        string V(System.Xml.Linq.XElement e, string n) =>
            e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value ?? "";
        foreach (var g in events.GroupBy(e => V(e, "MetadataPresentation")))
            Console.WriteLine($"  {g.Count(),4} × {g.Key}");
        foreach (var e in events)
            Console.WriteLine($"  {V(e, "Date")}  {V(e, "UserName"),-28} {V(e, "DataPresentation")}");
        File.Delete(file);
        return 0;
    }

    /// <summary>
    /// Worst-case lifecycle overlap: T threads, each repeatedly opening a session on the one
    /// shared connector, reading, and releasing it — so Connects and releases on different
    /// threads constantly overlap. Research only tested concurrent Connects, never Connect
    /// racing a release. Crash hunting for the intermittent 0xC0000005 (milestone 2.3/2.4).
    /// </summary>
    private static int ConnStress(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        var b = bases.First(x => x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        int threads = IntArg(argv, "threads", 4), iters = IntArg(argv, "iters", 25);
        bool serialize = Flag(argv, "serialize");

        ComActivator.Bind(ResolveComcntr(argv, bases));
        var connector = ComActivator.SharedConnector();
        var gate = new object();
        int ok = 0, fail = 0, id = 0;
        Console.WriteLine($"connstress base={baseName} ({b.Kind}) threads={threads} iters={iters} serialize={serialize}");

        var sw = Stopwatch.StartNew();
        var ts = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            for (int i = 0; i < iters; i++)
            {
                SessionWorker? w = null;
                try
                {
                    if (serialize) { lock (gate) w = SessionWorker.Open(connector, b, Interlocked.Increment(ref id)); }
                    else w = SessionWorker.Open(connector, b, Interlocked.Increment(ref id));
                    w.Execute(ctx =>
                    {
                        using var s = new ComScope();
                        var q = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "q");
                        Dispatch.Set(q, "Текст", "ВЫБРАТЬ ПЕРВЫЕ 5 Наименование ИЗ Справочник.Номенклатура", ctx.Error);
                        var r = s.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "r");
                        var c = s.Track(Dispatch.Call(r, "Выбрать", ctx.Error), "c");
                        while (Dispatch.CallBool(c, "Следующий", ctx.Error)) Dispatch.Get(c, "Наименование", ctx.Error);
                    });
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex) { Interlocked.Increment(ref fail); Console.WriteLine($"  t{t} #{i}: {ex.Message}"); }
                finally
                {
                    if (serialize) { lock (gate) w?.Dispose(); } else w?.Dispose();
                }
            }
        })).ToList();
        ts.ForEach(t => t.Start());
        ts.ForEach(t => t.Join());
        Console.WriteLine($"done: ok={ok} fail={fail} in {sw.ElapsedMilliseconds} ms  " +
                          $"({ok * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F2} sessions/s)");
        Console.WriteLine(ResourceSampler.Take("end").Line());
        return 0;
    }

    /// <summary>
    /// One warm session, many write operations (create + delete pairs). Does a long-lived
    /// session grow without bound? If so, sessions need recycling by operation count.
    /// </summary>
    private static int Grow(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        string docType = Arg(argv, "doc") ?? "ПоступлениеТоваровУслуг";
        int ops = IntArg(argv, "ops", 200), every = IntArg(argv, "every", 25);
        string marker = "AIBA_REWRITE_GROW_" + DateTime.Now.ToString("MMddHHmmss");

        using var m = Open(argv, bases.Select(b => b with { MaxConcurrencyOverride = 1 }).ToList(),
            new PoolOptions { PerBaseMaxSessions = 1, IdleTimeout = TimeSpan.FromHours(1) });
        var ws = new WriteService(m, "AIBA_REWRITE_");
        Console.WriteLine($"grow base={baseName} doc={docType} ops={ops} (one session, create+delete pairs)");

        ws.Delete(baseName, docType, ws.CreateByClone(baseName, docType, marker + " warm").Ref);
        var first = ResourceSampler.Take("after warmup pair", m.BudgetUsed);
        Console.WriteLine(first.Line());

        var sw = Stopwatch.StartNew();
        int errors = 0;
        for (int i = 1; i <= ops; i++)
        {
            try { ws.Delete(baseName, docType, ws.CreateByClone(baseName, docType, $"{marker} #{i}").Ref); }
            catch (OneCException oe) { errors++; if (errors <= 3) Console.WriteLine("  " + oe.Message); }
            if (i % every == 0)
                Console.WriteLine(ResourceSampler.Take($"pair {i} ({sw.ElapsedMilliseconds / i} ms/pair)", m.BudgetUsed).Line());
        }
        var last = ResourceSampler.Take("end", m.BudgetUsed);
        Console.WriteLine(ResourceSampler.Delta(first, last));
        Console.WriteLine($"errors={errors}  marker docs left: {ws.FindOwned(baseName, docType, marker).Count}");
        foreach (var st in m.Stats()) Console.WriteLine("  " + st);
        return 0;
    }

    /// <summary>Deletes every document of a type whose comment starts with the given AIBA_ prefix.</summary>
    private static int Cleanup(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        string docType = Arg(argv, "doc") ?? "ПоступлениеТоваровУслуг";
        string prefix = Arg(argv, "prefix") ?? "AIBA_REWRITE_";

        // INCIDENT 2026-09-23: this ran once with prefix "AIBA_" and deleted 64 + 14 real
        // documents — every document the production AIBA pipelines had created, because the
        // canonical marker AIBA_<KIND>_<id> also starts with "AIBA_". Bulk cleanup is only ever
        // allowed inside the rewrite's own test namespace.
        const string TestNamespace = "AIBA_REWRITE_";
        if (!prefix.StartsWith(TestNamespace, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"refused: cleanup prefix must start with '{TestNamespace}' (got '{prefix}')");
            return 2;
        }

        using var m = Open(argv, bases);
        var ws = new WriteService(m, prefix);

        // --dry-run: list what is left, touch nothing. --doc all: every document type.
        if (Flag(argv, "dry-run"))
        {
            var types = docType == "all" ? m.Use(baseName, DocumentParityScenario.DocumentNames) : new List<string> { docType };
            int total = 0;
            foreach (var t in types)
            {
                IReadOnlyList<string> found;
                try { found = ws.FindOwned(baseName, t, prefix); }
                catch (OneCException oe) { Console.WriteLine($"  {t}: not searchable ({oe.Message})"); continue; }
                total += found.Count;
                foreach (var r in found) Console.WriteLine($"  {t} {r}");
            }
            Console.WriteLine($"dry run {baseName}: {types.Count} document type(s), {total} document(s) with prefix {prefix}");
            return 0;
        }

        var refs = ws.FindOwned(baseName, docType, prefix);
        int n = 0, marked = 0;
        foreach (var r in refs)
        {
            try { ws.Delete(baseName, docType, r); n++; }
            catch (OneCException oe)
            {
                // Some configurations deny direct deletion to the COM user (KAN custom documents:
                // "Access violation!"); a deletion mark still takes the test copy out of every read.
                try { ws.MarkForDeletion(baseName, docType, r); marked++; Console.WriteLine($"  marked {r}: delete refused ({oe.Message})"); }
                catch (OneCException oe2) { Console.WriteLine($"  keep {r}: {oe.Message}; mark: {oe2.Message}"); }
            }
        }
        Console.WriteLine($"cleanup {baseName}/{docType} prefix={prefix}: found {refs.Count}, deleted {n}, marked {marked}, " +
                          $"left {ws.FindOwned(baseName, docType, prefix).Count}");
        return 0;
    }

    /// <summary>
    /// Create sessions, work, drain, repeat. Threads, handles and working set must return
    /// to the same place every cycle — anything that climbs per cycle is a leak.
    /// </summary>
    private static int Churn(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        int cycles = IntArg(argv, "cycles", 5), k = IntArg(argv, "k", 2);

        using var m = Open(argv, bases, new PoolOptions
        { GlobalMaxSessions = 8, PerBaseMaxSessions = k, IdleTimeout = TimeSpan.FromHours(1) });
        var svc = new ReadService(m);
        var q = new ReadQuery
        {
            Entity = Arg(argv, "entity") ?? "Справочник.Номенклатура",
            Fields = (Arg(argv, "fields") ?? "Наименование").Split(','),
            Limit = IntArg(argv, "limit", 50)
        };

        int every = IntArg(argv, "every", 1);
        var drained = new List<ResourceSample>();

        // --keep: hold one extra session to the same base open for the whole run, in a
        // second manager (same shared connector). Tests whether the creep and the file-base
        // crash come from the base being fully closed and reopened every cycle.
        SessionManager? keeper = null;
        if (Flag(argv, "keep"))
        {
            keeper = new SessionManager(m.ComcntrPath, new PoolOptions
            { IdleTimeout = TimeSpan.FromHours(1), SweepInterval = TimeSpan.FromHours(1) });
            keeper.Register(m.GetBase(baseName));
            new ReadService(keeper).Read(baseName, q);
            Console.WriteLine("keeper session open: " + keeper.Stats()[0]);
        }
        Console.WriteLine(ResourceSampler.Take("baseline").Line());
        for (int c = 1; c <= cycles; c++)
        {
            RunParallel(m, svc, baseName, q, k, IntArg(argv, "reps", 10));
            if (c % every == 0)
                Console.WriteLine(ResourceSampler.Take($"cycle {c} peak", m.BudgetUsed, collect: false).Line());
            m.DrainBase(baseName);
            var d = ResourceSampler.Take($"cycle {c} drained", m.BudgetUsed);
            drained.Add(d);
            if (c % every == 0) Console.WriteLine(d.Line());
        }

        // Trend: least-squares slope of drained WS over the first and second half. A creep
        // that plateaus shows a much smaller second-half slope.
        static double Slope(IReadOnlyList<ResourceSample> s, Func<ResourceSample, double> y)
        {
            int n = s.Count; if (n < 2) return 0;
            double mx = (n - 1) / 2.0, my = s.Average(y), num = 0, den = 0;
            for (int i = 0; i < n; i++) { num += (i - mx) * (y(s[i]) - my); den += (i - mx) * (i - mx); }
            return num / den;
        }
        var h1 = drained.Take(drained.Count / 2).ToList();
        var h2 = drained.Skip(drained.Count / 2).ToList();
        Console.WriteLine($"\ndrained WS: first={drained[0].WorkingSetMb} MB last={drained[^1].WorkingSetMb} MB " +
                          $"min={drained.Min(s => s.WorkingSetMb)} max={drained.Max(s => s.WorkingSetMb)}");
        Console.WriteLine($"slope WS  : first half {Slope(h1, s => s.WorkingSetMb):F2} MB/cycle, " +
                          $"second half {Slope(h2, s => s.WorkingSetMb):F2} MB/cycle");
        Console.WriteLine($"slope priv: first half {Slope(h1, s => s.PrivateBytesMb):F2} MB/cycle, " +
                          $"second half {Slope(h2, s => s.PrivateBytesMb):F2} MB/cycle");
        Console.WriteLine($"threads   : {drained[0].Threads} -> {drained[^1].Threads}   " +
                          $"handles: {drained[0].Handles} -> {drained[^1].Handles}");

        string? csv = Arg(argv, "csv");
        if (csv is not null)
            File.WriteAllLines(csv, new[] { ResourceSample.CsvHeader }.Concat(drained.Select(s => s.Csv())));
        keeper?.Dispose();
        Console.WriteLine($"comrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");
        return 0;
    }

    /// <summary>
    /// Does the connector's own pool hold memory after we release our sessions? If it does,
    /// enabling PoolCapacity would quietly defeat idle-session retirement.
    /// </summary>
    private static int PoolRam(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        var b = bases.First(x => x.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        int cap = IntArg(argv, "cap", 0), k = IntArg(argv, "k", 4);

        ComActivator.Bind(ResolveComcntr(argv, bases));
        using var connector = ComRef.Own(ComActivator.CreateConnector(), "connector");
        var ctx = new ErrorContext(baseName, null);
        if (cap > 0)
        {
            Dispatch.Set(connector.Target, "PoolCapacity", cap, ctx);
            Dispatch.Set(connector.Target, "PoolTimeout", 60, ctx);
        }

        Console.WriteLine($"base={baseName} ({b.Kind}) PoolCapacity={cap} k={k}");
        Console.WriteLine(ResourceSampler.Take("00 connector only").Line());

        var open = new List<ComRef>();
        for (int i = 0; i < k; i++)
        {
            open.Add(ComRef.Own(ConnectorApi.Connect(connector.Target, b.ConnectionString, ctx), $"s{i}"));
            Console.WriteLine(ResourceSampler.Take($"01 connected {i + 1}", i + 1).Line());
        }

        for (int i = open.Count - 1; i >= 0; i--) open[i].Dispose();
        Console.WriteLine(ResourceSampler.Take("02 all released").Line());
        Thread.Sleep(2000);
        Console.WriteLine(ResourceSampler.Take("03 two seconds later").Line());
        return 0;
    }

    /// <summary>Registering many bases must cost nothing until one is actually used.</summary>
    private static int Lazy(string[] argv)
    {
        var bases = LoadBases(argv);
        var fake = new List<OneCBase>(bases);
        for (int i = 0; i < IntArg(argv, "ghosts", 30); i++)
            fake.Add(bases[0] with { Name = $"ghost-{i}" });

        using var m = Open(argv, fake);
        Console.WriteLine(ResourceSampler.Take($"{fake.Count} bases registered", m.BudgetUsed).Line());
        Console.WriteLine($"  pools created: {m.Stats().Count}  sessions: {m.BudgetUsed}");

        var svc = new ReadService(m);
        svc.Read(bases[0].Name, new ReadQuery
        { Entity = Arg(argv, "entity") ?? "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 5 });

        Console.WriteLine(ResourceSampler.Take("after using exactly one base", m.BudgetUsed).Line());
        Console.WriteLine($"  pools created: {m.Stats().Count}  sessions: {m.BudgetUsed}");
        foreach (var st in m.Stats()) Console.WriteLine("  " + st);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr arg, uint flags, out uint id);

    /// <summary>
    /// Crash probe (NativeProcess): after a session is open, crash a native thread nobody
    /// handles exceptions on. The process must die with 0xC0000005, not spin forever in 1C's
    /// ImageMagick crash filter. Exit code 9 = it survived the crash, i.e. the guard is off.
    /// </summary>
    private static int NativeCrash(string[] argv)
    {
        var bases = LoadBases(argv);
        string baseName = Arg(argv, "base") ?? bases[0].Name;
        using var m = Open(argv, bases);
        var q = new ReadQuery { Entity = "Справочник.Валюты", Fields = new[] { "Наименование" }, Limit = 5 };
        // The loop is armed when the base is opened again after its last session closed
        // (idle timeout, then a new request): 1C initialises ImageMagick a second time.
        new ReadService(m).Read(baseName, q);
        m.DrainBase(baseName);
        new ReadService(m).Read(baseName, q);
        Console.WriteLine($"crash filter after reconnect: {NativeProcess.CrashFilterModule()}");
        Console.Out.Flush();

        // strlen((char*)8) on a fresh native thread: an access violation in native code that no
        // handler claims. (A bogus start address instead dies in Control Flow Guard's fast fail,
        // which never reaches the crash filter.)
        var strlen = System.Runtime.InteropServices.NativeLibrary.GetExport(
            System.Runtime.InteropServices.NativeLibrary.Load("ucrtbase.dll"), "strlen");
        CreateThread(IntPtr.Zero, UIntPtr.Zero, strlen, (IntPtr)8, 0, out _);
        Thread.Sleep(TimeSpan.FromSeconds(IntArg(argv, "wait", 20)));
        Console.WriteLine("survived the crash");
        return 9;
    }

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern uint FlsAlloc(IntPtr callback);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern bool FlsSetValue(uint index, IntPtr value);

    /// <summary>
    /// Exit probe: arranges an access violation inside process exit (a fiber-local-storage
    /// callback, which the loader runs during ExitProcess — as 1C's rtrsrvc.dll null read does)
    /// and returns 0.
    ///   --base N   first open N, drain, open again (arms 1C's looping crash filter); the host
    ///              then leaves through NativeProcess.Exit, the trap never fires, exit code 0
    ///   --hang     no 1C; the exit callback is Sleep(INFINITE) instead: a host stuck in its own
    ///              exit, with an exit code but never finished — what the supervisor must reap
    ///   neither    no 1C; the process dies at exit with 0xC0000005
    /// </summary>
    private static int ExitTrap(string[] argv)
    {
        if (Arg(argv, "base") is { } baseName)
        {
            var bases = LoadBases(argv);
            using var m = Open(argv, bases);
            var q = new ReadQuery { Entity = "Справочник.Валюты", Fields = new[] { "Наименование" }, Limit = 5 };
            new ReadService(m).Read(baseName, q);
            m.DrainBase(baseName);
            new ReadService(m).Read(baseName, q);
        }
        // The loader calls the callback with the slot's value on the exiting thread.
        var (dll, fn, value) = Flag(argv, "hang")
            ? ("kernel32.dll", "Sleep", new IntPtr(0xFFFFFFFFL))      // Sleep(INFINITE)
            : ("ucrtbase.dll", "strlen", (IntPtr)8);                   // strlen((char*)8)
        uint slot = FlsAlloc(System.Runtime.InteropServices.NativeLibrary.GetExport(
            System.Runtime.InteropServices.NativeLibrary.Load(dll), fn));
        FlsSetValue(slot, value);
        Console.WriteLine("trap set");
        return 0;
    }
}
