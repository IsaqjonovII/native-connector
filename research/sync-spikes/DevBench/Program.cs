using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;

// DevBench lifecycle <step> --bases f.json --base bilim --state state.json
//   steps: create | post | unpost | change | delete | cleanup | show
// The S12 dev test's 1C side: ONE test-owned ПоступлениеТоваровУслуг on the local bilim copy,
// marked AIBA_REWRITE_S12_<stamp> (WriteService refuses anything without the AIBA_REWRITE_ prefix
// and anything it did not create). After each step it prints what 1C now holds for the document:
// posted or not, and its Хозрасчетный movements (lines, sum) — what the dev backend must show.
const string Doc = "ПоступлениеТоваровУслуг";
const string Prefix = "AIBA_REWRITE_S12_";

string Arg(string n) => args.SkipWhile(a => a != "--" + n).Skip(1).FirstOrDefault() ?? throw new ArgumentException("--" + n);

// DevBench stress --bases f.json --rounds N [--gc]: the MultiBaseTests pattern (managers created,
// read through, disposed; sessions evicted) in a loop, read-only, to reproduce the testhost's
// 0xC0000374 heap corruption outside the test runner. --gc forces collections and finalizers
// between rounds: an RCW nobody released explicitly is then released on the finalizer thread.
// DevBench counts --bases f.json --base N: read-only — rows per catalog, and Хозрасчетный lines per
// month, to pick the S12 first-run table set (small, deterministic).
if (args.Length > 0 && args[0] == "counts")
{
    var cb = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(Arg("bases")))!.First(x => x.Name == Arg("base"));
    var pc = PlatformCatalog.Select(PlatformCatalog.Discover(), cb.PlatformVersion, cb.IsFile)!;
    using var cm = new SessionManager(pc.ComcntrPath, new PoolOptions());
    cm.Register(cb);
    var tables = new TableCatalog(cm).List(cb.Name);
    long Count(string text) => cm.Use(cb.Name, ctx =>
    {
        using var s = new ComScope();
        var q = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", text, ctx.Error);
        var sel = s.Track(Dispatch.Call(s.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res"), "Выбрать", ctx.Error), "sel");
        Dispatch.CallBool(sel, "Следующий", ctx.Error);
        return Convert.ToInt64(Dispatch.Get(sel, "n", ctx.Error));
    });
    var sizes = new List<(string Table, long N)>();
    foreach (var t in tables.Where(t => t.Family is "catalog" or "chart" or "document"))
    {
        string meta = t.Family switch { "catalog" => "Справочник", "chart" => "ПланСчетов", _ => "Документ" } + "." + t.Name;
        try { sizes.Add((t.Table, Count($"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК n ИЗ {meta}"))); } catch (OneCException) { }
    }
    foreach (var (t, n) in sizes.Where(x => x.N > 0).OrderBy(x => x.Table.Split('_')[0]).ThenByDescending(x => x.N))
        Console.WriteLine($"{t}\t{n}");
    Console.WriteLine("Хозрасчетный lines per month (last 24 with any):");
    var months = cm.Use(cb.Name, ctx =>
    {
        using var s = new ComScope();
        var q = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", "ВЫБРАТЬ НАЧАЛОПЕРИОДА(Т.Период, МЕСЯЦ) КАК m, КОЛИЧЕСТВО(*) КАК n ИЗ РегистрБухгалтерии.Хозрасчетный КАК Т СГРУППИРОВАТЬ ПО НАЧАЛОПЕРИОДА(Т.Период, МЕСЯЦ) УПОРЯДОЧИТЬ ПО m УБЫВ", ctx.Error);
        var sel = s.Track(Dispatch.Call(s.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res"), "Выбрать", ctx.Error), "sel");
        var list = new List<string>();
        while (Dispatch.CallBool(sel, "Следующий", ctx.Error) && list.Count < 24)
            list.Add($"{Convert.ToDateTime(Dispatch.Get(sel, "m", ctx.Error)):yyyy-MM}\t{Dispatch.Get(sel, "n", ctx.Error)}");
        return list;
    });
    foreach (var l in months) Console.WriteLine("  " + l);
    return 0;
}

// DevBench leftovers --bases f.json: read-only — test-owned (AIBA_REWRITE_) documents still in each base.
if (args.Length > 0 && args[0] == "leftovers")
{
    var lb = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(Arg("bases")))!;
    var pl = PlatformCatalog.Select(PlatformCatalog.Discover(), lb[0].PlatformVersion, lb[0].IsFile)!;
    using var lm = new SessionManager(pl.ComcntrPath, new PoolOptions());
    foreach (var x in lb) lm.Register(x);
    var lw = new WriteService(lm, "AIBA_REWRITE_");
    int total = 0;
    foreach (var x in lb)
        foreach (var dt in new[] { "ПоступлениеТоваровУслуг", "РеализацияТоваровУслуг" })
        {
            var left = lw.FindOwned(x.Name, dt, "AIBA_REWRITE_");
            if (args.Contains("--delete"))
            {
                // Test-owned only (WriteService refuses anything without the AIBA_REWRITE_ comment).
                foreach (var r in left) { lw.Delete(x.Name, dt, r); Console.WriteLine($"deleted {x.Name} {dt} {r}"); }
                left = lw.FindOwned(x.Name, dt, "AIBA_REWRITE_");
            }
            total += left.Count;
            Console.WriteLine($"{x.Name} {dt}: {left.Count} AIBA_REWRITE_ document(s){(left.Count > 0 ? ": " + string.Join(", ", left) : "")}");
        }
    Console.WriteLine($"total {total}");
    return total == 0 ? 0 : 2;
}

if (args.Length > 0 && args[0] == "stress")
{
    var sb = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(Arg("bases")))!;
    int rounds = int.Parse(Arg("rounds"));
    bool gc = args.Contains("--gc");
    var q = new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 20 };
    var p0 = PlatformCatalog.Select(PlatformCatalog.Discover(), sb[0].PlatformVersion, sb[0].IsFile)!;
    if (args.Contains("--two-managers"))
    {
        // The test fixture's long-lived manager keeps serving both bases (its sweeper evicting
        // idle sessions), while short-lived second managers come and go — the MultiBaseTests shape.
        using var fixture = new SessionManager(p0.ComcntrPath, new PoolOptions { GlobalMaxSessions = 4, PerBaseMaxSessions = 2, IdleTimeout = TimeSpan.FromSeconds(3), SweepInterval = TimeSpan.FromSeconds(1) });
        foreach (var x in sb) fixture.Register(x);
        var frs = new ReadService(fixture);
        var until = DateTime.UtcNow.AddSeconds(rounds);
        int fr = 0, fe = 0, made = 0;
        var bg = new Thread(() =>
        {
            int k = 0;
            while (DateTime.UtcNow < until)
            {
                try { frs.Read(sb[k++ % sb.Count].Name, q); Interlocked.Increment(ref fr); } catch { Interlocked.Increment(ref fe); }
                Thread.Sleep(k % 7 == 0 ? 3500 : 50);             // sometimes idle long enough to be swept
            }
        });
        bg.Start();
        while (DateTime.UtcNow < until)
        {
            using (var second = new SessionManager(p0.ComcntrPath, new PoolOptions { IdleTimeout = TimeSpan.FromMilliseconds(500), SweepInterval = TimeSpan.FromMilliseconds(250) }))
            {
                foreach (var x in sb) second.Register(x);
                var srs = new ReadService(second);
                foreach (var x in sb) try { srs.Read(x.Name, q); } catch { Interlocked.Increment(ref fe); }
                Thread.Sleep(800);
            }
            made++;
        }
        bg.Join();
        Console.WriteLine($"{rounds} s: {made} second managers, fixture reads {fr}, errors {fe}");
        return 0;
    }
    if (args.Contains("--concurrent"))
    {
        // One base kept busy on its own thread while the other base's sessions are opened and
        // evicted over and over on another — the overlap the two crashing tests have.
        using var sm = new SessionManager(p0.ComcntrPath, new PoolOptions { GlobalMaxSessions = 4, PerBaseMaxSessions = 2, IdleTimeout = TimeSpan.FromMilliseconds(150), SweepInterval = TimeSpan.FromMilliseconds(100) });
        foreach (var x in sb) sm.Register(x);
        var rs = new ReadService(sm);
        bool churnFile = args.Contains("--churn-file");
        var busyBase = sb.First(x => x.IsFile != churnFile).Name;
        var churnBase = sb.First(x => x.IsFile == churnFile).Name;
        int reads = 0, errors = 0;
        var stop = DateTime.UtcNow.AddSeconds(rounds);
        var busy = new Thread(() => { while (DateTime.UtcNow < stop) try { rs.Read(busyBase, q); Interlocked.Increment(ref reads); } catch { Interlocked.Increment(ref errors); } });
        busy.Start();
        int churns = 0;
        while (DateTime.UtcNow < stop)
        {
            try { rs.Read(churnBase, q); } catch { Interlocked.Increment(ref errors); }
            Thread.Sleep(400);                                     // idle past the timeout: the sweeper evicts it
            churns++;
        }
        busy.Join();
        var st = sm.Stats();
        Console.WriteLine($"{rounds} s: {reads} busy reads, {churns} churn reads, errors {errors}; " +
                          string.Join("; ", st.Select(s => $"{s.BaseName} created {s.Created} evicted {s.Evicted}")) + $"; wrong-thread releases {ComRef.WrongThreadReleases}");
        return 0;
    }
    for (int i = 1; i <= rounds; i++)
    {
        using (var sm = new SessionManager(p0.ComcntrPath, new PoolOptions { GlobalMaxSessions = 4, PerBaseMaxSessions = 2, IdleTimeout = TimeSpan.FromMilliseconds(300), SweepInterval = TimeSpan.FromMilliseconds(200) }))
        {
            foreach (var x in sb) sm.Register(x);
            var rs = new ReadService(sm);
            foreach (var x in sb) rs.Read(x.Name, q);
            Thread.Sleep(700);                                     // let the sweeper evict idle sessions
            foreach (var x in sb) rs.Read(x.Name, q);
        }
        if (gc) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Console.WriteLine($"round {i}: live {ComRef.Live}, wrong-thread releases {ComRef.WrongThreadReleases}, release failures {ComRef.ReleaseFailures}");
    }
    return 0;
}

if (args.Length < 2 || args[0] != "lifecycle") { Console.Error.WriteLine("DevBench lifecycle <create|post|unpost|change|delete|cleanup|show> --bases f.json --base N --state s.json"); return 1; }
string step = args[1], baseName = Arg("base"), statePath = Arg("state");
var bases = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(Arg("bases")))!;
var b = bases.First(x => x.Name == baseName);
if (!b.IsFile || !b.ConnectionString.Contains(@"D:\1C\bilim", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("S12 lifecycle runs on the local bilim copy only");
    return 1;
}
var pick = PlatformCatalog.Select(PlatformCatalog.Discover(), b.PlatformVersion, b.IsFile) ?? throw new InvalidOperationException("no 1C install");
using var m = new SessionManager(pick.ComcntrPath, new PoolOptions { GlobalMaxSessions = 1, PerBaseMaxSessions = 1 });
m.Register(b);
var w = new WriteService(m, "AIBA_REWRITE_");
var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
string? id = (string?)state["ref"];

switch (step)
{
    case "create":
        if (id is not null) throw new InvalidOperationException($"state already has a document {id}: run delete first");
        string marker = Prefix + DateTime.Now.ToString("MMddHHmmss");
        var c = w.CreateByClone(baseName, Doc, marker + " sync dev lifecycle", date: DateTime.Now);
        state["ref"] = id = c.Ref;
        state["marker"] = marker;
        Console.WriteLine($"created {Doc} {c.Number} ref {c.Ref} ({marker})");
        break;
    case "post":
        w.Post(baseName, Doc, Need(id));
        Console.WriteLine("posted");
        break;
    case "unpost":
        w.Unpost(baseName, Doc, Need(id));
        Console.WriteLine("unposted");
        break;
    case "change":
        // Changed values on the same document: line 1 quantity and amount doubled (Записать, not posted).
        Console.WriteLine(m.Use(baseName, ctx =>
        {
            using var s = new ComScope();
            var obj = s.Track(Dispatch.Call(RefOf(ctx, s, Need(id)), "ПолучитьОбъект", ctx.Error), "obj");
            string comment = Dispatch.GetString(obj, "Комментарий", ctx.Error) ?? "";
            if (!comment.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("not the S12 test document: " + comment);
            var lines = s.Track(Dispatch.Get(obj, "Товары", ctx.Error), "Товары");
            if (Dispatch.CallInt(lines, "Количество", ctx.Error) == 0) throw new InvalidOperationException("the cloned document has no Товары line to change");
            var line = s.Track(Dispatch.Call(lines, "Получить", ctx.Error, 0), "line");
            var parts = new List<string>();
            foreach (var f in new[] { "Количество", "Сумма", "СуммаНДС" })
                if (Dispatch.HasMember(line, f))
                {
                    decimal v = Convert.ToDecimal(Dispatch.Get(line, f, ctx.Error));
                    Dispatch.Set(line, f, v * 2, ctx.Error);
                    parts.Add($"{f} {v} → {v * 2}");
                }
            Dispatch.Call(obj, "Записать", ctx.Error);
            return "changed line 1: " + string.Join(", ", parts);
        }));
        break;
    case "delete":
        w.Delete(baseName, Doc, Need(id));
        Console.WriteLine($"deleted {id}");
        state.Remove("ref");
        File.WriteAllText(statePath, state.ToJsonString());
        return 0;
    case "cleanup":
        foreach (var r in w.FindOwned(baseName, Doc, Prefix)) { w.Delete(baseName, Doc, r); Console.WriteLine("deleted leftover " + r); }
        Console.WriteLine($"{Prefix} documents left: {w.FindOwned(baseName, Doc, Prefix).Count}");
        state.Remove("ref");
        File.WriteAllText(statePath, state.ToJsonString());
        return 0;
    case "show":
        break;
    default:
        throw new ArgumentException("unknown step " + step);
}
File.WriteAllText(statePath, state.ToJsonString());

// What 1C holds now for the document.
Console.WriteLine(m.Use(baseName, ctx =>
{
    using var s = new ComScope();
    var r = RefOf(ctx, s, Need(id));
    bool posted = Dispatch.GetBool(r, "Проведен", ctx.Error);
    var q = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
    Dispatch.Set(q, "Текст", "ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК n, ЕСТЬNULL(СУММА(Т.Сумма), 0) КАК sum ИЗ РегистрБухгалтерии.Хозрасчетный КАК Т ГДЕ Т.Регистратор = &r", ctx.Error);
    Dispatch.Call(q, "УстановитьПараметр", ctx.Error, "r", r);
    var res = s.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res");
    var sel = s.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "sel");
    Dispatch.CallBool(sel, "Следующий", ctx.Error);
    return $"1C now: {Doc} {id} posted={posted}; Хозрасчетный lines {Dispatch.Get(sel, "n", ctx.Error)}, sum {Dispatch.Get(sel, "sum", ctx.Error)}";
}));
return 0;

static object RefOf(SessionContext ctx, ComScope s, string guid)
{
    var docs = s.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы");
    var mgr = s.Track(Dispatch.Get(docs, Doc, ctx.Error), Doc);
    var uuid = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", guid), "UUID");
    return s.Track(Dispatch.Call(mgr, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");
}

static string Need(string? id) => id ?? throw new InvalidOperationException("no document in the state file: run create first");
