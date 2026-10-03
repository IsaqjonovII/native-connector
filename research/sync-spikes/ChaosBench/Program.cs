using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChaosBench;
using OneC.Sync.Targets.Python;
using OneC.SyncState;
using OneC.Sync.Engine;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;

// ChaosBench run <workDir> <backendUrl> <secrets.json> [kills=50] [seed=1]
// ChaosBench engine <workDir>   (the child the parent kills)
const string UserId = "5f0c2a8e-1c1e-4d7b-9a55-5ab1c0de0001";
var tables = new[]
{
    new TablePlan("Catalog_ChaosCat", "Cat", Families.Catalog, false),
    new TablePlan("Document_ChaosDoc", "Doc", Families.Document, true),
    new TablePlan("AccountingRegister_ChaosAcc", "Acc", Families.AccountingRegister, true)
};

if (args[0] == "engine") return await Engine(args[1]);
if (args[0] == "verify") return await Verify(args[1], World.Load(Path.Combine(args[1], "world.json")));
return await Run(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 50, args.Length > 5 ? int.Parse(args[5]) : 1);

async Task<int> Run(string work, string url, string secretsFile, int kills, int seed)
{
    if (!new Uri(url).IsLoopback) { Console.Error.WriteLine("local backend only"); return 1; }
    if (Directory.Exists(work)) Directory.Delete(work, true);
    Directory.CreateDirectory(work);
    var r = new Random(seed);
    string worldPath = Path.Combine(work, "world.json"), log = Path.Combine(work, "1Cv8Log");
    var world = World.Seed(2000, 800, r);
    world.Save(worldPath);
    LogWriter.Create(log);
    var s = JsonNode.Parse(File.ReadAllText(secretsFile))!;
    string conn = await CreateConnection(url, (string)s["service"]!);
    File.WriteAllText(Path.Combine(work, "target.json"), new JsonObject { ["url"] = url, ["conn"] = conn, ["token"] = Mint((string)s["jwt"]!) }.ToJsonString());
    Console.WriteLine($"world: {world.Cats.Count} items, {world.Docs.Count} documents, {world.Docs.Values.Sum(d => d.Lines.Count)} movements; connection {conn}");

    var sw = Stopwatch.StartNew();
    long peakPrivate = 0;
    for (int k = 1; k <= kills; k++)
    {
        using var child = StartChild(work);
        var until = DateTime.UtcNow.AddMilliseconds(r.Next(1500, 15000));
        while (DateTime.UtcNow < until && !child.HasExited)
        {
            // 1C keeps changing while the engine runs (and while it is dead, below).
            for (int i = r.Next(1, 4); i > 0; i--) { var ev = world.Mutate(r); world.Save(worldPath); LogWriter.Append(log, ev); }
            try { child.Refresh(); peakPrivate = Math.Max(peakPrivate, child.PrivateMemorySize64); } catch (InvalidOperationException) { }
            await Task.Delay(r.Next(50, 400));
        }
        if (child.HasExited) Console.WriteLine($"kill {k}: the engine exited by itself, code {child.ExitCode}: {File.ReadAllText(Path.Combine(work, "engine.err")).Trim()}");
        else { child.Kill(entireProcessTree: true); child.WaitForExit(); }
        for (int i = r.Next(0, 5); i > 0; i--) { var ev = world.Mutate(r); world.Save(worldPath); LogWriter.Append(log, ev); }
        if (k % 10 == 0) Console.WriteLine($"kill {k}: {world.Mutations} mutations so far, {Status(work)}");
    }

    // 1C stops changing; the engine runs until it has nothing left.
    Console.WriteLine($"{kills} kills in {sw.Elapsed.TotalSeconds:F0} s, {world.Mutations} mutations; now converging");
    using (var last = StartChild(work))
    {
        var conv = Stopwatch.StartNew();
        string st = "";
        while (conv.Elapsed < TimeSpan.FromMinutes(15))
        {
            await Task.Delay(1000);
            try { last.Refresh(); peakPrivate = Math.Max(peakPrivate, last.PrivateMemorySize64); } catch (InvalidOperationException) { }
            st = Status(work);
            // Converged: nothing pending AND the cursor at the end of the log (not just "idle").
            long logEnd = new FileInfo(Path.Combine(log, "20260930000000.lgp")).Length;
            if (st.StartsWith("incremental pending 0 dead ", StringComparison.Ordinal) && st.Contains($"|{logEnd} ", StringComparison.Ordinal)) break;
        }
        Console.WriteLine($"converged in {conv.Elapsed.TotalSeconds:F0} s: {st}; engine peak private {peakPrivate / 1048576} MB");
        last.Kill(entireProcessTree: true);
        last.WaitForExit();
    }
    return await Verify(work, World.Load(worldPath));
}

System.Diagnostics.Process StartChild(string work)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardError = false, CreateNoWindow = true };
    psi.ArgumentList.Add("engine");
    psi.ArgumentList.Add(work);
    return System.Diagnostics.Process.Start(psi)!;
}

string Status(string work)
{
    try { return File.ReadAllText(Path.Combine(work, "status.txt")); } catch (IOException) { return "?"; }
}

async Task<int> Engine(string work)
{
    try
    {
        var t = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "target.json")))!;
        var http = new HttpClient { BaseAddress = new Uri((string)t["url"]! + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var target = new PythonMongoSyncTarget(http, new StaticToken((string)t["token"]!), (string)t["conn"]!);
        using var db = SyncDb.Open(Path.Combine(work, "sync.db"));
        using var host = new SyncEngineHost(db, target, new FileOneC(Path.Combine(work, "world.json")),
            new[] { new BasePlan("chaos", false, Path.Combine(work, "1Cv8Log"), (string)t["conn"]!, tables) },
            new SyncBudgets { FeedPollActive = TimeSpan.FromMilliseconds(500) }, new EngineOptions { Snapshot = new() { PageSize = 250 } });
        host.Start(TimeSpan.FromMilliseconds(200));
        while (true)
        {
            var b = host.Status()[0];
            File.WriteAllText(Path.Combine(work, "status.tmp"), $"{b.Mode} pending {b.PendingWork} dead {b.DeadLetters} cursor {b.Cursor} err {b.LastError}");
            File.Move(Path.Combine(work, "status.tmp"), Path.Combine(work, "status.txt"), true);
            await Task.Delay(500);
        }
    }
    catch (Exception e) { File.WriteAllText(Path.Combine(work, "engine.err"), e.ToString()); return 3; }
}

/// <summary>The backend must hold exactly the world: every item and document with its latest content, every movement line, nothing else.</summary>
async Task<int> Verify(string work, World w)
{
    var t = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "target.json")))!;
    using var http = new HttpClient { BaseAddress = new Uri((string)t["url"]! + "/") };
    http.DefaultRequestHeaders.Authorization = new("Bearer", (string)t["token"]!);
    var stored = new Dictionary<string, Dictionary<string, JsonObject>>();
    for (int page = 1; ; page++)
    {
        var p = JsonNode.Parse(await http.GetStringAsync($"api/v2/entity?oneCId={(string)t["conn"]!}&pageSize=1000&pageNumber={page}"))!;
        var items = p["results"]!.AsArray().OfType<JsonObject>().ToList();
        foreach (var i in items)
        {
            string table = (string)i["tableName"]!;
            var raw = (JsonObject)i["rawData"]!;
            string key = table.EndsWith("Acc", StringComparison.Ordinal) ? $"{raw["recorderRef"]}#{raw["lineNo"]}" : (string)raw["id"]!;
            if (!stored.TryGetValue(table, out var d)) stored[table] = d = new();
            if (d.ContainsKey(key)) Console.WriteLine($"DUPLICATE {table} {key}");
            d[key] = raw;
        }
        if (items.Count < 1000) break;
    }
    var want = new Dictionary<string, Dictionary<string, string>>
    {
        ["Catalog_ChaosCat"] = w.Cats.ToDictionary(kv => kv.Key, kv => kv.Value.Name),
        ["Document_ChaosDoc"] = w.Docs.ToDictionary(kv => kv.Key, kv => kv.Value.Sum.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ["AccountingRegister_ChaosAcc"] = w.Docs.SelectMany(kv => kv.Value.Lines.Select((a, i) => ($"{kv.Key}#{i + 1}", a.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                                            .ToDictionary(x => x.Item1, x => x.Item2)
    };
    int bad = 0;
    foreach (var (table, expected) in want)
    {
        var have = stored.GetValueOrDefault(table) ?? new();
        var missing = expected.Keys.Except(have.Keys).ToList();
        var extra = have.Keys.Except(expected.Keys).ToList();
        string field = table.StartsWith("Catalog", StringComparison.Ordinal) ? "name" : "Сумма";
        var wrong = expected.Where(kv => have.TryGetValue(kv.Key, out var row) && Norm(row[field]) != Norm(kv.Value)).Select(kv => kv.Key).ToList();
        Console.WriteLine($"{table}: 1C {expected.Count}, backend {have.Count}; missing {missing.Count}, stale/extra {extra.Count}, wrong content {wrong.Count}");
        foreach (var k in missing.Concat(extra).Concat(wrong).Take(3)) Console.WriteLine("   e.g. " + k);
        bad += missing.Count + extra.Count + wrong.Count;
    }
    Console.WriteLine(bad == 0 ? "PASS: the backend equals 1C" : $"FAIL: {bad} differences");
    return bad == 0 ? 0 : 2;
}

// Numbers compare by value: 1532.20 (a decimal sum) and 1532.2 (the backend's float) are the same amount.
static string Norm(JsonNode? n) => n is null ? "" : decimal.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
    ? (d / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture) : n.ToString();

async Task<string> CreateConnection(string url, string service)
{
    using var h = new HttpClient { BaseAddress = new Uri(url + "/") };
    using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/onec")
    {
        Content = JsonContent.Create(new { userId = UserId, provider = "unisoft", odataName = "chaos-" + Guid.NewGuid().ToString("N")[..8], companyId = Guid.NewGuid().ToString(), name = "Sync chaos" })
    };
    req.Headers.Add("X-Service-Secret", service);
    var resp = await h.SendAsync(req);
    return (string)JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["id"]!;
}

static string Mint(string secret)
{
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    string head = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
    string body = B64(Encoding.UTF8.GetBytes($"{{\"sub\":\"{UserId}\",\"exp\":{DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds()}}}"));
    return $"{head}.{body}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)))}";
}

sealed class StaticToken(string t) : ITokenSource
{
    public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(t);
    public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
}
