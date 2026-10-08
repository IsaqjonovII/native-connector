using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using OneC.Sessions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Routing;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Supervisor;

/// <summary>
/// <c>OneC.Supervisor sync-verify --bases f.json --sync-config cfg.json [--report out.json]</c> — S12:
/// does the DEVELOPMENT backend hold exactly what 1C holds? Never trusts a count the backend reports.
///  - 1C side: every configured table read afresh through the engine's own reader, mapper and the
///    connection's real org routing (from dev's bindings) into an in-memory target — the canonical
///    rows, per partition; plus 1C's own COUNT as an independent check;
///  - backend side: every stored row of every partition read back (<c>GET /api/v2/entity</c>, all pages),
///    keyed from its rawData by the same key rule;
///  - per table and partition: missing keys, extra (stale) keys, duplicate keys, rows in the wrong
///    partition, and every field of every expected row compared with the stored one (numbers by value).
/// Read-only on dev and in 1C. Run it while the engine is idle (nothing pending), or the moving
/// target shows up as differences.
/// </summary>
internal static class SyncVerify
{
    public static async Task<int> Run(List<OneCBase> bases, SupervisorOptions opt, string configPath, string? reportPath)
    {
        var cfg = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        string targetSpec = (string?)cfg["target"] ?? "";
        bool rust = targetSpec.StartsWith(RustBackend.Scheme, StringComparison.Ordinal);
        bool local = !rust && targetSpec != "dev" && Uri.TryCreate(targetSpec, UriKind.Absolute, out var lu) && lu.IsLoopback;   // isolated backend/1c (R10)
        if (targetSpec != "dev" && !rust && !local) { Console.Error.WriteLine("sync-verify reads the dev backend, the local Rust module or the isolated local backend/1c: target must be \"dev\", \"rust:<loopback URL>\" or a loopback URL"); return 1; }
        var b = cfg["bases"]!.AsArray().OfType<JsonObject>().Single();
        string baseName = (string)b["name"]!, conn = (string)b["connectionId"]!;
        var plans = SyncMode.Tables(b);                                       // the engine's own plan, same names and routing
        var ob = bases.First(x => x.Name == baseName);

        OneC.Sync.Targets.Rust.RustSyncTarget? rustTarget = null;
        HttpClient http;
        OneC.Cloud.CloudClient? cloud = null;
        OneC.Sync.Abstractions.SyncConfig config;
        string? localToken = null;
        if (rust)
        {
            (rustTarget, http) = RustBackend.Connect(targetSpec, (string)cfg["secrets"]!, conn);
            config = await rustTarget.GetSyncConfigAsync(conn, CancellationToken.None);
        }
        else if (local)
        {
            (http, localToken) = LocalBackend.Reader(targetSpec, (string)cfg["secrets"]!);
            var (lt, _) = await LocalBackend.ConnectAsync(targetSpec, (string)cfg["secrets"]!, baseName, conn);
            config = await lt.GetSyncConfigAsync(conn, CancellationToken.None);
        }
        else
        {
            OneC.Sync.Targets.Python.PythonMongoSyncTarget dev;
            (dev, http, cloud) = DevBackend.Connect((string)cfg["session"]!, conn, new HttpFaults());
            var record = await DevBackend.GetJsonAsync(http, cloud, $"api/v2/onec/{Uri.EscapeDataString(conn)}");
            if (!((string?)record["name"] ?? "").StartsWith(DevBackend.TestPrefix, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"sync-verify reads test records only ({DevBackend.TestPrefix}…): {conn} is \"{record["name"]}\"");
                return 1;
            }
            config = await dev.GetSyncConfigAsync(conn, CancellationToken.None);
        }
        var router = new OrgRouter(config);
        var partitions = config.Partitions.Select(p => p.PartitionId).Append(config.SharedPartitionId).Distinct().ToList();
        Console.WriteLine($"{baseName} → {(rust ? "Rust" : local ? "local backend/1c" : "dev")} connection {conn}: {partitions.Count} partition(s), " +
                          $"{config.Partitions.Count(p => p.OrgRef is not null)} org binding(s)");

        // ---- 1C: the canonical rows, as a fresh snapshot would send them ----
        using var sup = new Supervisor(opt);
        sup.Start(new[] { ob });
        var hostProc = sup.HostFor(baseName);
        if (!hostProc.WaitReady(TimeSpan.FromMinutes(3))) { Console.Error.WriteLine("host not ready: " + hostProc.LastError); return 1; }
        string dbPath = Path.Combine(Path.GetTempPath(), $"aiba-sync-verify-{Guid.NewGuid():N}.db");
        var expected = new StubSyncTarget(StubMode.V2) { KeepJson = true };
        var counts = new Dictionary<string, long>();
        List<(string OrgRef, string Table, long Rows)> unmapped;
        var reader = new SupervisorReader(sup);
        var sw = Stopwatch.StartNew();
        try
        {
            using var db = SyncDb.Open(dbPath);
            var budgets = new SyncBudgets();
            var gate = new UploadGate(budgets);
            var runner = new SnapshotRunner(db, reader, new Uploader(expected, gate), gate, expected.Capabilities, router, new SnapshotOptions());
            var activation = new BaseActivation(baseName, ob.IsFile, SyncPriority.Snapshot, false, new SyncLeases(budgets, () => DateTimeOffset.UtcNow),
                                                budgets, CancellationToken.None);
            foreach (var t in plans)
            {
                counts[t.Table] = await reader.CountAsync(baseName, t, CancellationToken.None);
                var r = await runner.RunTableAsync(activation, t, 1);
                if (r.Outcome != SnapshotOutcome.Complete) { Console.Error.WriteLine($"{t.Table}: 1C read {r.Outcome}"); return 1; }
            }
            unmapped = db.Read(tx => tx.UnmappedOrgs(baseName));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*")) File.Delete(f);
        }
        Console.WriteLine($"1C read in {sw.Elapsed.TotalSeconds:F1} s");

        // Rows sent per (partition, table) by the fresh read: more sent than distinct = duplicate canonical keys in 1C's own answer.
        var sent = expected.Calls.Where(c => c.StartsWith("upload ", StringComparison.Ordinal))
            .Select(c => { var p = c[7..].Split(' '); return (Key: p[0], N: long.Parse(p[1], CultureInfo.InvariantCulture)); })
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.N));

        // ---- backend: every stored row, every partition ----
        sw.Restart();
        var byPlan = plans.ToDictionary(p => p.Table, StringComparer.Ordinal);
        var stored = new Dictionary<(string Partition, string Table), Dictionary<string, JsonObject>>();
        var dupStored = new List<string>();
        var otherTables = new Dictionary<string, int>(StringComparer.Ordinal);
        long storedRows = 0;
        if (rustTarget is not null)
            foreach (var p in partitions)
                foreach (var plan in plans)
                    await foreach (var row in rustTarget.ReadRowsAsync(p, plan.Table))
                    {
                        storedRows++;
                        var raw = row.Data ?? new JsonObject();
                        string key;
                        try { key = CanonicalMapper.Key(plan, raw); }
                        catch (RowMappingException e) { dupStored.Add($"{p}/{plan.Table}: unkeyable row {row.Key}: {e.Message}"); continue; }
                        // The stored key must be the one the row's own fields give (the key rule, §7).
                        if (key != row.Key) { dupStored.Add($"{p}/{plan.Table}: unkeyable row {row.Key}: its fields give key {key}"); continue; }
                        if (!stored.TryGetValue((p, plan.Table), out var d)) stored[(p, plan.Table)] = d = new(StringComparer.Ordinal);
                        if (!d.TryAdd(key, raw)) dupStored.Add($"{p}/{plan.Table} {key}");
                    }
        else
        foreach (var p in partitions)
            for (int page = 1; ; page++)
            {
                string pageUrl = $"api/v2/entity?oneCId={p}&pageSize=1000&pageNumber={page}";
                JsonNode json;
                if (localToken is not null)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", localToken);
                    using var resp = await http.SendAsync(req);
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{pageUrl}: {(int)resp.StatusCode} {text}");
                    json = JsonNode.Parse(text)!;
                }
                else json = await DevBackend.GetJsonAsync(http, cloud!, pageUrl);
                var items = json["results"]!.AsArray().OfType<JsonObject>().ToList();
                foreach (var i in items)
                {
                    storedRows++;
                    string table = (string)i["tableName"]!;
                    if (!byPlan.TryGetValue(table, out var plan)) { otherTables[table] = otherTables.GetValueOrDefault(table) + 1; continue; }
                    var raw = (JsonObject)i["rawData"]!;
                    string key;
                    try { key = CanonicalMapper.Key(plan, raw); }
                    catch (RowMappingException e) { dupStored.Add($"{p}/{table}: unkeyable row {i["id"]}: {e.Message}"); continue; }
                    if (!stored.TryGetValue((p, table), out var d)) stored[(p, table)] = d = new(StringComparer.Ordinal);
                    if (!d.TryAdd(key, raw)) dupStored.Add($"{p}/{table} {key}");
                }
                if (items.Count < 1000) break;
            }
        Console.WriteLine($"backend read: {storedRows} stored rows in {sw.Elapsed.TotalSeconds:F1} s");

        // ---- compare ----
        int bad = 0;
        var report = new JsonArray();
        foreach (var t in plans)
        {
            var wantByPart = partitions.ToDictionary(p => p, p => expected.Rows(p, t.Table));
            long want = wantByPart.Values.Sum(d => d.Count);
            long have = partitions.Sum(p => stored.GetValueOrDefault((p, t.Table))?.Count ?? 0);
            long sentRows = partitions.Sum(p => sent.GetValueOrDefault($"{p}/{t.Table}"));
            int missing = 0, extra = 0, wrongPart = 0, wrongContent = 0;
            var examples = new List<string>();
            var homeOf = wantByPart.SelectMany(kv => kv.Value.Keys.Select(k => (k, kv.Key))).ToDictionary(x => x.k, x => x.Key, StringComparer.Ordinal);
            foreach (var p in partitions)
            {
                var exp = wantByPart[p];
                var got = stored.GetValueOrDefault((p, t.Table)) ?? new();
                foreach (var (k, jsonText) in exp)
                {
                    if (!got.TryGetValue(k, out var raw)) { missing++; if (examples.Count < 4) examples.Add($"missing {p}/{k}"); continue; }
                    if (Diff(JsonNode.Parse(jsonText)!.AsObject(), raw) is { } field)
                    {
                        wrongContent++;
                        if (examples.Count < 4) examples.Add($"content {p}/{k}: {field}");
                    }
                }
                foreach (var k in got.Keys.Where(k => !exp.ContainsKey(k)))
                {
                    if (homeOf.TryGetValue(k, out var home)) { wrongPart++; if (examples.Count < 4) examples.Add($"in {p}, belongs to {home}: {k}"); }
                    else { extra++; if (examples.Count < 4) examples.Add($"stale/extra {p}/{k}"); }
                }
            }
            int dups = dupStored.Count(d => d.Contains("/" + t.Table + " ", StringComparison.Ordinal));
            // A From date windows documents too (SnapshotRunner reads documents from it); 1C's COUNT is
            // of the whole table, so it only proves anything for an unwindowed one.
            bool windowed = t.From is not null && (Families.IsRegister(t.Family) || t.Family == Families.Document);
            bool countOk = windowed || counts[t.Table] == want;
            int tableBad = missing + extra + wrongPart + wrongContent + dups + (countOk ? 0 : 1) + (sentRows > want ? 1 : 0);
            bad += tableBad;
            string parts = string.Join(", ", partitions.Select(p => $"{(p == config.SharedPartitionId ? "shared" : p.Length > 6 ? p[^6..] : p)} {stored.GetValueOrDefault((p, t.Table))?.Count ?? 0}"));
            Console.WriteLine($"{(tableBad == 0 ? "OK  " : "BAD ")} {t.Table}: 1C count {counts[t.Table]}{(windowed ? $" (whole register; window from {t.From:yyyy-MM-dd})" : "")}, " +
                              $"canonical {want} (sent {sentRows}), backend {have} [{parts}]; missing {missing}, stale/extra {extra}, " +
                              $"wrong partition {wrongPart}, wrong content {wrongContent}, duplicate keys {dups}");
            foreach (var e in examples) Console.WriteLine("       e.g. " + e);
            report.Add(new JsonObject
            {
                ["table"] = t.Table, ["oneCCount"] = counts[t.Table], ["canonical"] = want, ["sent"] = sentRows, ["backend"] = have,
                ["missing"] = missing, ["extra"] = extra, ["wrongPartition"] = wrongPart, ["wrongContent"] = wrongContent, ["duplicateKeys"] = dups,
                ["examples"] = new JsonArray(examples.Select(e => (JsonNode)e).ToArray())
            });
        }
        foreach (var d in dupStored.Where(d => d.Contains("unkeyable", StringComparison.Ordinal))) { Console.WriteLine("BAD  " + d); bad++; }
        foreach (var (table, n) in otherTables) Console.WriteLine($"note: {n} stored row(s) of {table}, a table not in this run's plan");
        foreach (var (org, table, rows) in unmapped) Console.WriteLine($"unmapped organisation {org}: {rows} row(s) of {table} wait for a binding");
        Console.WriteLine(bad == 0 ? $"PASS: the {(rust ? "Rust" : local ? "local backend/1c" : "dev")} backend holds exactly the canonical 1C rows" : $"FAIL: {bad} difference(s)");
        if (reportPath is not null)
            await File.WriteAllTextAsync(reportPath, new JsonObject
            {
                ["at"] = DateTime.UtcNow.ToString("o"), ["base"] = baseName, ["connection"] = conn, ["pass"] = bad == 0, ["tables"] = report,
                ["unmapped"] = new JsonArray(unmapped.Select(u => (JsonNode)new JsonObject { ["org"] = u.OrgRef, ["table"] = u.Table, ["rows"] = u.Rows }).ToArray())
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return bad == 0 ? 0 : 2;
    }

    /// <summary>The first field of the expected row the stored row does not hold the same way; null when equal.</summary>
    private static string? Diff(JsonObject want, JsonObject have)
    {
        foreach (var (name, w) in want)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;                 // transport fields the backend drops
            have.TryGetPropertyValue(name, out var h);
            if (Norm(w) != Norm(h)) return $"{name}: 1C {Short(w)} vs backend {Short(h)}";
        }
        return null;
    }

    private static string Short(JsonNode? n) { string s = n?.ToJsonString() ?? "∅"; return s.Length > 80 ? s[..80] + "…" : s; }

    /// <summary>Numbers by value (1532.20 = 1532.2), nested objects/arrays by their normalised parts, null = absent.</summary>
    private static string Norm(JsonNode? n) => n switch
    {
        null => "",
        JsonObject o => "{" + string.Join(",", o.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                               .Select(kv => kv.Key + ":" + Norm(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Norm)) + "]",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                         decimal.TryParse(v.ToJsonString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Null => "",
        _ => n.ToJsonString()
    };
}
