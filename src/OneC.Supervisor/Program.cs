using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Supervisor;

/// <summary>
///   run    --bases f.json | --bases-stdin  [--port N|0] [--token T]
///          hosts + HTTP edge until stdin closes. With --bases-stdin the first stdin line is
///          the bases JSON (credentials never touch disk) — how the desktop app launches it.
///          Port 0 = any free loopback port; the ready line reports the real one.
///   verify --bases f.json [--file-version V] [--doc T]  the whole path, through HTTP, measured
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = new UTF8Encoding(false);
        string mode = argv.Length > 0 && !argv[0].StartsWith("--") ? argv[0] : "help";
        string? basesFile = Arg(argv, "bases");
        bool basesStdin = argv.Contains("--bases-stdin");
        if (mode == "sync-snapshot" && basesFile is not null)
        {
            var sb = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(basesFile))!;
            return await SyncBench.Run(sb, new SupervisorOptions { HostExe = Arg(argv, "host") ?? DefaultHostExe() },
                                       Arg(argv, "base") ?? sb[0].Name, Arg(argv, "table") ?? "catalog:Банки",
                                       int.TryParse(Arg(argv, "k"), out int k) ? k : 1, SyncBench.Date(Arg(argv, "from")),
                                       int.TryParse(Arg(argv, "page"), out int pg) ? pg : 500);
        }
        if (mode == "sync-dev") return await SyncDev.Run(argv, n => Arg(argv, n));
        if (mode == "sync-rust") return await RustBackend.Run(argv, n => Arg(argv, n));
        // R10: one record on the ISOLATED local backend/1c for a base (loopback only), kept across runs.
        if (mode == "sync-local" && argv.Length > 1 && argv[1] == "create")
        {
            string url = Arg(argv, "url") ?? "http://127.0.0.1:18041";
            if (!new Uri(url).IsLoopback) { Console.Error.WriteLine("sync-local: loopback only"); return 1; }
            var (_, id) = await LocalBackend.ConnectAsync(url, Arg(argv, "secrets") ?? throw new ArgumentException("--secrets F"), Arg(argv, "base") ?? "sync");
            Console.WriteLine($"created local backend/1c record {id}");
            return 0;
        }
        if (mode == "sync-verify" && basesFile is not null && Arg(argv, "sync-config") is { } verifyConfig)
            return await SyncVerify.Run(JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(basesFile))!,
                                        new SupervisorOptions { HostExe = Arg(argv, "host") ?? DefaultHostExe() }, verifyConfig, Arg(argv, "report"));
        if ((basesFile is null && !(basesStdin && mode == "run")) || mode is not ("run" or "verify"))
        {
            Console.WriteLine("OneC.Supervisor run|verify --bases <list.json> | --bases-stdin [--port N] [--token T] [--sync-config sync.json] " +
                              "[--file-version 8.3.18.1289] [--doc ПоступлениеТоваровУслуг] [--host OneC.Host.exe]\n" +
                              "OneC.Supervisor sync-snapshot|sync-verify|sync-dev|sync-rust …  sync measurement, S12 and local Rust helpers");
            return 1;
        }

        string basesJson = basesStdin ? Console.In.ReadLine() ?? "[]" : File.ReadAllText(basesFile!);
        var bases = JsonSerializer.Deserialize<List<OneCBase>>(basesJson)!;
        // Lets the two-version path be exercised with the bases we have: a file base opens on
        // any newer platform, so requiring 8.3.18 moves it to its own 8.3.18 host.
        if (Arg(argv, "file-version") is string fv)
            bases = bases.Select(b => b.IsFile ? b with { PlatformVersion = fv } : b).ToList();

        int port = int.TryParse(Arg(argv, "port"), out int p) ? p : 55990;
        var opt = new SupervisorOptions { HostExe = Arg(argv, "host") ?? DefaultHostExe() };
        return mode == "run"
            ? await Run(bases, opt, port, Arg(argv, "token"), Arg(argv, "sync-config"))
            : await Verify(bases, opt, port, Arg(argv, "doc") ?? "ПоступлениеТоваровУслуг");
    }

    // ---------------- run ----------------

    private static async Task<int> Run(List<OneCBase> bases, SupervisorOptions opt, int port, string? token, string? syncConfig)
    {
        using var sup = new Supervisor(opt);
        sup.Start(bases);
        await using var edge = new EdgeServer(sup, port, token);
        // Sync (D42): stub, a loopback backend or the dev target only, until the developer's go (D-2).
        (OneC.Sync.Engine.SyncEngineHost Host, IDisposable Db)? sync = syncConfig is null ? null : await SyncMode.StartAsync(sup, bases, syncConfig);
        edge.Sync = sync?.Host;
        await edge.StartAsync();
        // The launching process (the WinUI app) reads the port and token from this line.
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", port = edge.Port, token = edge.Token,
                                                         pid = Environment.ProcessId,
                                                         hosts = sup.Hosts.Select(h => h.Key),
                                                         unplaceable = sup.Unplaceable.Select(u => new { name = u.Base.Name, reason = u.Reason }) }));
        Console.Out.Flush();
        await Task.Run(() => { while (Console.In.ReadLine() is not null) { } });
        if (SyncMode.Commands is { } commands) await commands.DisposeAsync();
        if (sync is { } s)
        {
            await s.Host.StopAsync();
            s.Host.Dispose();
            s.Db.Dispose();
        }
        return 0;
    }

    // ---------------- verify ----------------

    private sealed class Check
    {
        public int Passed, Failed;
        public void That(bool ok, string what)
        {
            if (ok) Passed++; else Failed++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        }
    }

    private static async Task<int> Verify(List<OneCBase> bases, SupervisorOptions opt, int port, string doc)
    {
        var check = new Check();
        var sw = Stopwatch.StartNew();
        using var sup = new Supervisor(opt with { MonitorInterval = TimeSpan.FromSeconds(1), RecycleIdleAboveMb = 0 });
        sup.Start(bases);
        await using var edge = new EdgeServer(sup, port);
        await edge.StartAsync();
        Console.WriteLine($"supervisor + {sup.Hosts.Count} host(s) + edge :{port} up in {sw.ElapsedMilliseconds} ms");
        foreach (var e in sup.Events) Console.WriteLine($"  [{e.Utc:HH:mm:ss.fff}] {e.Key,-18} {e.What}");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        Console.WriteLine("\nedge basics:");
        check.That((await anon.GetAsync("/v1/health")).IsSuccessStatusCode, "GET /v1/health without token → 200");
        check.That((int)(await anon.GetAsync("/v1/hosts")).StatusCode == 401, "GET /v1/hosts without token → 401");
        var hosts = await http.GetFromJsonAsync<JsonArray>("/v1/hosts");
        check.That(hosts?.Count == sup.Hosts.Count, $"GET /v1/hosts → {hosts?.Count} host(s)");
        var bl = await http.GetFromJsonAsync<JsonArray>("/v1/bases");
        check.That(bl?.Count == bases.Count, $"GET /v1/bases → {bl?.Count} base(s)");

        Console.WriteLine("\nper base, through HTTP → pipe → 1C:");
        var readBody = new JsonObject { ["entity"] = "Справочник.Номенклатура", ["fields"] = new JsonArray("Наименование", "Код"), ["limit"] = 5 };
        foreach (var b in bases)
        {
            var v = await http.GetFromJsonAsync<JsonObject>($"/v1/bases/{b.Name}/version");
            string reported = v?["platformVersion"]?.GetValue<string>() ?? "?";
            check.That(reported == sup.HostFor(b.Name).Assignment.Install.Version,
                       $"{b.Name}: 1C reports {reported} from host {sup.HostFor(b.Name).Key}");
            var r = await http.PostAsJsonAsync($"/v1/bases/{b.Name}/read", readBody);
            var rows = (await r.Content.ReadFromJsonAsync<JsonObject>())?["rows"]?.AsArray();
            check.That(r.IsSuccessStatusCode && rows?.Count > 0, $"{b.Name}: read → {(int)r.StatusCode}, {rows?.Count} rows");
        }

        Console.WriteLine("\nerror mapping:");
        await Expect(http, check, HttpMethod.Get, "/v1/bases/no-such-base/version", null, 404, "unknown base");
        await Expect(http, check, HttpMethod.Post, $"/v1/bases/{bases[0].Name}/read", "{not json", 400, "invalid JSON body");
        await Expect(http, check, HttpMethod.Post, $"/v1/bases/{bases[0].Name}/read",
            new JsonObject { ["entity"] = "Справочник.Номенклатура\"; x", ["fields"] = new JsonArray("Ссылка") }.ToJsonString(), 400, "illegal identifier");
        await Expect(http, check, HttpMethod.Post, $"/v1/bases/{bases[0].Name}/read",
            new JsonObject { ["entity"] = "Документ.НетТакогоДокумента", ["fields"] = new JsonArray("Ссылка") }.ToJsonString(), 422, "1C runtime error (unknown table)");

        var writeBase = bases.FirstOrDefault(b => !b.IsFile) ?? bases[0];
        string marker = "AIBA_REWRITE_EDGE_" + DateTime.Now.ToString("MMddHHmmss");
        Console.WriteLine($"\nwrite lifecycle on {writeBase.Name} ({doc}), marker {marker}:");
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        var created = await http.PostAsJsonAsync($"/v1/bases/{writeBase.Name}/documents/{doc}?post=false",
                                                 new JsonObject { ["Date"] = today, ["Комментарий"] = marker + ": edge" });
        var cj = await created.Content.ReadFromJsonAsync<JsonObject>();
        string? id = cj?["id"]?.GetValue<string>();
        check.That(created.IsSuccessStatusCode && id is not null, $"create → {(int)created.StatusCode} ref={id} №{cj?["number"]}");
        if (id is not null)
        {
            var upd = await http.PatchAsJsonAsync($"/v1/bases/{writeBase.Name}/documents/{doc}/{id}",
                new JsonObject { ["fields"] = new JsonObject { ["Комментарий"] = marker + " edge updated" } });
            check.That(upd.IsSuccessStatusCode, $"update → {(int)upd.StatusCode}");
            var mk = await http.PostAsJsonAsync($"/v1/bases/{writeBase.Name}/documents/{doc}/{id}/mark-deleted", new JsonObject { ["mark"] = true });
            check.That(mk.IsSuccessStatusCode, $"mark-deleted → {(int)mk.StatusCode}");
            var del = await http.DeleteAsync($"/v1/bases/{writeBase.Name}/documents/{doc}/{id}?hard=true");
            check.That(del.IsSuccessStatusCode, $"delete → {(int)del.StatusCode}");
            await Expect(http, check, HttpMethod.Delete, $"/v1/bases/{writeBase.Name}/documents/{doc}/{id}?hard=true", null, 404, "delete again → not found");
        }
        await Expect(http, check, HttpMethod.Post, $"/v1/bases/{writeBase.Name}/documents/{doc}",
            new JsonObject { ["Date"] = today, ["Комментарий"] = "no marker" }.ToJsonString(), 400, "create without AIBA_ comment");
        var owned = sup.Send(writeBase.Name, "findOwned", new JsonObject { ["docType"] = doc, ["prefix"] = marker });
        check.That(owned.Ok && owned.Result?["refs"]?.AsArray().Count == 0, "no marker documents left");

        Console.WriteLine("\nlatency, warm, sequential (HTTP edge → pipe → host → 1C → back):");
        foreach (var b in bases)
        {
            var small = new JsonObject { ["entity"] = "Справочник.Номенклатура", ["fields"] = new JsonArray("Наименование"), ["limit"] = 1 };
            var lat = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                (await http.PostAsJsonAsync($"/v1/bases/{b.Name}/read", small)).EnsureSuccessStatusCode();
                lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            var direct = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                if (!sup.Send(b.Name, "read", (JsonObject)small.DeepClone()).Ok) throw new Exception("pipe read failed");
                direct.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            lat.Sort(); direct.Sort();
            Console.WriteLine($"  {b.Name,-10} via edge p50={lat[100]:F2} ms p95={lat[190]:F2} ms   " +
                              $"pipe only p50={direct[100]:F2} ms p95={direct[190]:F2} ms");
        }

        Console.WriteLine("\nresilience:");
        var victim = sup.HostFor(bases[0].Name);
        int oldPid = victim.Pid ?? -1;
        victim.Kill();
        var kt = Stopwatch.StartNew();
        HttpResponseMessage? back = null;
        while (kt.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(250);
            back = await http.PostAsJsonAsync($"/v1/bases/{bases[0].Name}/read", readBody);
            if (back.IsSuccessStatusCode && sup.HostFor(bases[0].Name).Pid != oldPid) break;
        }
        check.That(back?.IsSuccessStatusCode == true, $"killed host {victim.Key}: reads served again after {kt.ElapsedMilliseconds} ms " +
                                                      $"(restarts={sup.Restarts(victim.Key)})");

        Console.WriteLine("\nmemory:");
        foreach (var h in sup.Hosts)
        {
            var (ws, priv) = h.Memory();
            Console.WriteLine($"  host {h.Key,-18} pid={h.Pid,-6} ws={ws,5} MB priv={priv,5} MB");
        }
        using (var me = Process.GetCurrentProcess())
        {
            me.Refresh();
            Console.WriteLine($"  supervisor + edge pid={me.Id} ws={me.WorkingSet64 / 1024 / 1024} MB " +
                              $"priv={me.PrivateMemorySize64 / 1024 / 1024} MB threads={me.Threads.Count}");
        }
        check.That(ComActivator.LoadedComcntrModules().Count == 0, "supervisor has no comcntr mapped");

        Console.WriteLine("\nevents:");
        foreach (var e in sup.Events) Console.WriteLine($"  [{e.Utc:HH:mm:ss.fff}] {e.Key,-18} {e.What}");

        await edge.DisposeAsync();
        sup.Dispose();
        await Task.Delay(1000);
        int orphans = Process.GetProcessesByName("OneC.Host").Length;
        check.That(orphans == 0, $"OneC.Host processes after shutdown: {orphans}");

        Console.WriteLine($"\n{check.Passed} passed, {check.Failed} failed");
        return check.Failed == 0 ? 0 : 1;
    }

    private static async Task Expect(HttpClient http, Check check, HttpMethod m, string path, string? body, int status, string what)
    {
        var req = new HttpRequestMessage(m, path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var r = await http.SendAsync(req);
        string text = await r.Content.ReadAsStringAsync();
        string msg = "";
        try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? ""; } catch { }
        if (msg.Length > 90) msg = msg[..90] + "…";
        check.That((int)r.StatusCode == status, $"{what} → {(int)r.StatusCode} (want {status}) {msg}");
    }

    private static string DefaultHostExe()
    {
        // src/OneC.Supervisor/bin/<cfg>/net9.0 -> src/OneC.Host/bin/<cfg>/net9.0
        var here = AppContext.BaseDirectory;
        var cfg = new DirectoryInfo(here).Parent!.Name;
        return Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "OneC.Host", "bin", cfg, "net9.0", "OneC.Host.exe"));
    }

    private static string? Arg(string[] a, string k)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == "--" + k) return a[i + 1];
        return null;
    }
}
