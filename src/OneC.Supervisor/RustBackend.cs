using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OneC.Sync.Targets.Rust;

namespace OneC.Supervisor;

/// <summary>
/// R3 (PYTHON_TO_RUST_MIGRATION_PLAN): the Rust onec module started locally for the sync tests —
/// loopback only, its own Postgres cluster, test secrets made for it (a JSON file {url, service}).
/// Not a production path: a deployed Rust module is reached with the signed-in user's token (R7).
/// <code>
///   sync-rust create --secrets F --base NAME --company N [--bind ORG_GUID=COMPANY,…]   a test connection (+ bindings)
///   sync-rust counts --secrets F --connection ID                                        exact rows per partition and table
/// </code>
/// </summary>
internal static class RustBackend
{
    public const string Scheme = "rust:";

    /// <summary>"rust:http://127.0.0.1:18112" → a target for one connection; refuses anything but a loopback address (D-2).</summary>
    public static (RustSyncTarget Target, HttpClient Http) Connect(string spec, string secretsFile, string connectionId)
    {
        var (http, service) = Client(spec, secretsFile);
        if (!long.TryParse(connectionId, out _)) throw new ArgumentException($"rust target: connectionId must be the Rust connection id (a number), not \"{connectionId}\"");
        return (new RustSyncTarget(http, new ServiceSecretCredential(service), connectionId), http);
    }

    private static (HttpClient Http, string Service) Client(string spec, string secretsFile)
    {
        var s = JsonNode.Parse(File.ReadAllText(secretsFile))!;
        string url = spec.StartsWith(Scheme, StringComparison.Ordinal) ? spec[Scheme.Length..] : (string)s["url"]!;
        if (!new Uri(url).IsLoopback) throw new ArgumentException("rust target must be a loopback URL (D-2: local instance only, never a shared or production server)");
        return (new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) }, (string)s["service"]!);
    }

    /// <summary>
    /// R8: throughput of the Rust module with synthetic rows shaped like real ones (no 1C): a register
    /// snapshot in 2000-row batches, the same batches again (all unchanged), then recorder units
    /// (document + 6 movement lines each, then a repost with 4). Every number is the client's own clock;
    /// the stored result is checked by exact counts afterwards.
    /// </summary>
    private static async Task<int> BenchAsync(HttpClient http, string service, int rows, int recorders)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "api/v1/onec")
        {
            Content = JsonContent.Create(new { companyId = 4999, provider = "unisoft", name = "AIBA_REWRITE_bench_" + DateTime.Now.ToString("HHmmss") })
        };
        create.Headers.Add("X-Service-Secret", service);
        var cr = await http.SendAsync(create);
        string conn = (string)JsonNode.Parse(await cr.Content.ReadAsStringAsync())!["id"]!;
        var t = new RustSyncTarget(http, new ServiceSecretCredential(service), conn);
        const string Reg = "AccountingRegister_Хозрасчетный_RecordType", Doc = "Document_РеализацияТоваровУслуг";
        string pad = new('x', 700);                                        // ~1.2 KB a row, like a Хозрасчетный line
        static OneC.Sync.Abstractions.SyncRow Row(string key, string json, long v) => new(key, v, System.Text.Encoding.UTF8.GetBytes(json));
        string Line(string rec, int n, decimal sum) =>
            $"{{\"recorderRef\":\"{rec}\",\"lineNo\":{n},\"Период\":\"2026-10-01T00:00:00\",\"СчетДт\":\"60.01\",\"СчетКт\":\"51\",\"Сумма\":{sum},\"Содержание\":\"{pad}\"}}";
        var recs = Enumerable.Range(0, rows / 10).Select(_ => Guid.NewGuid().ToString("D")).ToList();
        var all = recs.SelectMany(r => Enumerable.Range(1, 10).Select(n => Row($"{r}#{n}", Line(r, n, n * 100m), 1))).ToList();
        long bytes = all.Sum(r => (long)r.Json.Length);

        async Task<double> Pass(string label)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lat = new List<double>();
            foreach (var chunk in all.Chunk(2000))
            {
                var s = System.Diagnostics.Stopwatch.StartNew();
                var r = await t.UploadRowsAsync(new OneC.Sync.Abstractions.UploadBatch(Guid.NewGuid().ToString("N"), conn, Reg, chunk), CancellationToken.None);
                lat.Add(s.Elapsed.TotalMilliseconds);
                if (!r.Ok || r.Applied != chunk.Length) throw new InvalidOperationException($"{label}: {r.Outcome} applied {r.Applied}/{chunk.Length} {r.Message}");
            }
            lat.Sort();
            Console.WriteLine($"{label}: {all.Count:N0} rows, {bytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1} s = {all.Count / sw.Elapsed.TotalSeconds:N0} rows/s; " +
                              $"batch p50 {lat[lat.Count / 2]:F0} ms p95 {lat[(int)(lat.Count * 0.95)]:F0} ms");
            return sw.Elapsed.TotalSeconds;
        }
        await Pass("snapshot (insert)");
        await Pass("re-send (unchanged)");

        var units = new List<double>();
        var swu = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < recorders; i++)
        {
            string d = Guid.NewGuid().ToString("D");
            foreach (var (v, lines) in new[] { (1L, 6), (2L, 4) })                    // post with 6 lines, repost with 4
            {
                var s = System.Diagnostics.Stopwatch.StartNew();
                var r = await t.SyncRecorderAtomicAsync(new OneC.Sync.Abstractions.RecorderSync(Guid.NewGuid().ToString("N"), d, v, Doc, conn,
                    Row(d, $"{{\"id\":\"{d}\",\"posted\":true,\"v\":{v}}}", v), new[] { conn }, new[] { Reg },
                    new[] { new OneC.Sync.Abstractions.PartitionMovements(conn, Reg, Enumerable.Range(1, lines).Select(n => Row($"{d}#{n}", Line(d, n, v * n), v)).ToList()) }),
                    CancellationToken.None);
                units.Add(s.Elapsed.TotalMilliseconds);
                if (!r.Ok) throw new InvalidOperationException($"recorder: {r.Outcome} {r.Message}");
            }
        }
        units.Sort();
        Console.WriteLine($"recorder units: {units.Count:N0} (post 6 lines + repost 4) in {swu.Elapsed.TotalSeconds:F1} s = {units.Count / swu.Elapsed.TotalSeconds:N0} units/s; " +
                          $"p50 {units[units.Count / 2]:F1} ms p95 {units[(int)(units.Count * 0.95)]:F1} ms");
        var counts = await t.CountsAsync(conn, CancellationToken.None, Reg, Doc);
        long wantReg = all.Count + recorders * 4L;
        Console.WriteLine($"stored (exact count): {Reg} {counts[Reg]:N0} (want {wantReg:N0}), {Doc} {counts[Doc]:N0} (want {recorders:N0}) — " +
                          (counts[Reg] == wantReg && counts[Doc] == recorders ? "OK" : "MISMATCH"));
        Console.WriteLine($"bench connection {conn}");
        return counts[Reg] == wantReg && counts[Doc] == recorders ? 0 : 2;
    }

    public static async Task<int> Run(string[] argv, Func<string, string?> arg)
    {
        string sub = argv.Length > 1 ? argv[1] : "";
        string secrets = arg("secrets") ?? throw new ArgumentException("--secrets F (the local Rust instance's {url, service} file)");
        var (http, service) = Client(arg("url") is { } u ? Scheme + u : "", secrets);
        switch (sub)
        {
            case "create":
            {
                string baseName = arg("base") ?? throw new ArgumentException("--base NAME");
                int company = int.Parse(arg("company") ?? throw new ArgumentException("--company N (a test company number)"));
                using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/onec")
                {
                    Content = JsonContent.Create(new { companyId = company, provider = arg("provider") ?? "unisoft",
                                                       name = $"AIBA_REWRITE_{baseName}_{DateTime.Now:yyyyMMdd-HHmm}", odataName = $"aiba-rewrite-{baseName}" })
                };
                req.Headers.Add("X-Service-Secret", service);
                var resp = await http.SendAsync(req);
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { Console.Error.WriteLine($"create: {(int)resp.StatusCode} {text}"); return 1; }
                string id = (string)JsonNode.Parse(text)!["id"]!;
                Console.WriteLine($"created Rust connection {id} for {baseName}");
                if (arg("bind") is { Length: > 0 } bind)
                {
                    var bindings = bind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(b => b.Split('=')).Select(p => new { orgRef = p[0], companyId = p[1] }).ToArray();
                    using var put = new HttpRequestMessage(HttpMethod.Put, $"api/v2/onec/{id}/org-bindings") { Content = JsonContent.Create(new { bindings }) };
                    put.Headers.Add("X-Service-Secret", service);
                    var pr = await http.SendAsync(put);
                    string pt = await pr.Content.ReadAsStringAsync();
                    if (!pr.IsSuccessStatusCode) { Console.Error.WriteLine($"bindings: {(int)pr.StatusCode} {pt}"); return 1; }
                    Console.WriteLine($"bound {bindings.Length} organisation(s)");
                }
                Console.WriteLine("put this id into sync.json as the base's connectionId");
                return 0;
            }
            case "counts":
            {
                string conn = arg("connection") ?? throw new ArgumentException("--connection ID");
                var target = new RustSyncTarget(http, new ServiceSecretCredential(service), conn);
                var cfg = await target.GetSyncConfigAsync(conn, CancellationToken.None);
                foreach (var p in cfg.Partitions.Select(p => p.PartitionId).Prepend(cfg.SharedPartitionId).Distinct())
                    foreach (var (t, n) in (await target.CountsAsync(p, CancellationToken.None)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        Console.WriteLine($"{p}\t{t}\t{n}");
                return 0;
            }
            case "command":
            {
                // The cloud's side of R9 for tests: store a command, then wait for the Connector's outcome.
                string conn = arg("connection") ?? throw new ArgumentException("--connection ID");
                var t = new RustSyncTarget(http, new ServiceSecretCredential(service), conn);
                var payload = JsonNode.Parse(File.ReadAllText(arg("payload") ?? throw new ArgumentException("--payload file.json")))!.AsObject();
                var (r, id, dedup) = await t.EnqueueCommandAsync(arg("kind") ?? throw new ArgumentException("--kind"),
                    arg("key") ?? throw new ArgumentException("--key (the idempotency key)"), arg("partition") ?? conn, payload, CancellationToken.None);
                if (!r.Ok || id is null) { Console.WriteLine(new JsonObject { ["enqueued"] = false, ["outcome"] = r.Outcome.ToString(), ["message"] = r.Message }.ToJsonString()); return 2; }
                var until = DateTime.UtcNow.AddSeconds(int.Parse(arg("wait") ?? "600"));
                JsonObject? c = null;
                while (DateTime.UtcNow < until)
                {
                    c = await t.GetCommandAsync(id, CancellationToken.None);
                    if ((string?)c?["state"] is "succeeded" or "failed" or "dead") break;
                    await Task.Delay(500);
                }
                c ??= new JsonObject();
                c["deduplicated"] = dedup;
                Console.WriteLine(c.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return (string?)c["state"] == "succeeded" ? 0 : 3;
            }
            case "bench":
                return await BenchAsync(http, service, int.Parse(arg("rows") ?? "200000"), int.Parse(arg("recorders") ?? "2000"));
            default:
                Console.Error.WriteLine("sync-rust create|counts|bench --secrets F …");
                return 1;
        }
    }
}
