using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OneC.Sync;

/// <summary>
/// A local stand-in for backend/1c's sync endpoints (D39), in memory, loopback only. It keeps the
/// contract as backend/1c's source has it (mapped 2026-09-25, `development` = `production`):
/// <list type="bullet">
/// <item><c>POST /api/v2/entity/upload</c> — multipart <c>oneCId</c> + <c>file</c> (the part needs a filename;
/// a gzip body fails like the real one); body <c>{table: [rows]}</c> or nested under the oneCId;
/// <c>__rowKey</c>/<c>__rowHash</c> popped; with a key (or a row GUID) → upsert by (oneCId, table, key),
/// else content-hash dedup; 201 with per-table inserted/updated/skipped. Needs a Bearer token —
/// the real endpoint refuses a service secret here (403).</item>
/// <item><c>GET /api/v1/onec/{id}/counts</c> — <c>[{tableName, quantity}]</c>.</item>
/// <item><c>POST …/prune-missing</c> — deletes rows by key; 409 when nothing is stored or the keys
/// exceed <see cref="PruneMaxFraction"/> of what is stored (the backend never trusts liveCount).</item>
/// <item><c>POST …/reconcile-recorder</c> — deletes a recorder's <c>ref#line</c> rows not in liveKeys.</item>
/// </list>
/// </summary>
public sealed class StubBackend : IAsyncDisposable
{
    public const double PruneMaxFraction = 0.5;

    private sealed class Table
    {
        public readonly Dictionary<string, (JsonObject Row, string Hash)> Keyed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, JsonObject> Plain = new(StringComparer.Ordinal);
        public int Count => Keyed.Count + Plain.Count;
    }

    private readonly Dictionary<(string OneCId, string Table), Table> _tables = new();
    private readonly object _gate = new();
    private WebApplication? _app;

    public int Port { get; private set; }
    public string Token { get; } = "stub-" + Guid.NewGuid().ToString("N");
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    /// <summary>Every upload call, newest last — what the test asserts on besides the stored rows.</summary>
    public List<(string OneCId, string Table, int Rows, int Bytes)> Uploads { get; } = new();

    public async Task StartAsync(int port = 0)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port, l => l.Protocols = HttpProtocols.Http1);
            k.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
        });
        var app = b.Build();
        Map(app);
        await app.StartAsync();
        _app = app;
        var addr = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                      .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(addr).Port;
    }

    public void Authorize(HttpRequestMessage r) => r.Headers.Authorization = new("Bearer", Token);

    public IReadOnlyList<JsonObject> Rows(string oneCId, string table)
    {
        lock (_gate)
            return _tables.TryGetValue((oneCId, table), out var t)
                ? t.Keyed.Values.Select(v => v.Row).Concat(t.Plain.Values).ToList()
                : new List<JsonObject>();
    }

    public JsonObject? Row(string oneCId, string table, string key)
    {
        lock (_gate) return _tables.TryGetValue((oneCId, table), out var t) && t.Keyed.TryGetValue(key, out var v) ? v.Row : null;
    }

    public int Count(string oneCId, string table)
    {
        lock (_gate) return _tables.TryGetValue((oneCId, table), out var t) ? t.Count : 0;
    }

    private void Map(IEndpointRouteBuilder r)
    {
        r.MapPost("/api/v2/entity/upload", async (HttpContext c) =>
        {
            if (Auth(c, uploads: true) is { } denied) return denied;
            if (!c.Request.HasFormContentType) return Results.Json(new { detail = "multipart expected" }, statusCode: 422);
            var form = await c.Request.ReadFormAsync();
            string? oneCId = form["oneCId"];
            if (string.IsNullOrEmpty(oneCId)) return Results.Json(new { detail = "oneCId required" }, statusCode: 422);
            var file = form.Files.GetFile("file");
            // Without a filename Starlette sees a plain field, not an UploadFile.
            if (file is null) return Results.Json(new { detail = "file required (the part needs a filename)" }, statusCode: 422);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            byte[] bytes = ms.ToArray();
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Results.Json(new { detail = "file is not UTF-8 JSON (gzip is not decoded)" }, statusCode: 400); }
            if (JsonNode.Parse(text) is not JsonObject root) return Results.Json(new { detail = "JSON object expected" }, statusCode: 400);
            var tables = root[oneCId] is JsonObject nested && nested.Any(p => p.Value is JsonArray { Count: > 0 }) ? nested : root;

            var results = new JsonArray();
            int items = 0, inserted = 0, skipped = 0;
            foreach (var (table, value) in tables)
            {
                if (value is not JsonArray { Count: > 0 } rows) continue;
                var (i, u, s) = Store(oneCId, table, rows);
                lock (_gate) Uploads.Add((oneCId, table, rows.Count, bytes.Length));
                results.Add(new JsonObject
                {
                    ["tableName"] = table, ["entityType"] = table, ["total"] = rows.Count,
                    ["inserted"] = i, ["updated"] = u, ["collapsed"] = 0, ["skipped"] = s
                });
                items += rows.Count; inserted += i; skipped += s;
            }
            if (results.Count == 0) return Results.Json(new { detail = "No valid entities found" }, statusCode: 400);
            return Results.Json(new JsonObject
            {
                ["message"] = "ok", ["oneCId"] = oneCId, ["results"] = results, ["totalTables"] = results.Count,
                ["totalItems"] = items, ["totalInserted"] = inserted, ["totalSkipped"] = skipped,
                ["processedAt"] = DateTime.UtcNow.ToString("O")
            }, statusCode: 201);
        });

        r.MapGet("/api/v1/onec/{id}/counts", (string id, HttpContext c) =>
        {
            if (Auth(c, uploads: false) is { } denied) return denied;
            lock (_gate)
                return Results.Json(_tables.Where(t => t.Key.OneCId == id)
                                           .Select(t => new { tableName = t.Key.Table, quantity = t.Value.Count }).ToList());
        });

        r.MapPost("/api/v1/onec/{id}/prune-missing", async (string id, HttpContext c) =>
        {
            if (Auth(c, uploads: false) is { } denied) return denied;
            var body = (await JsonNode.ParseAsync(c.Request.Body)) as JsonObject ?? new JsonObject();
            string table = body["tableName"]!.GetValue<string>();
            var keys = body["missingKeys"]!.AsArray().Select(k => k!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            lock (_gate)
            {
                if (!_tables.TryGetValue((id, table), out var t) || t.Count == 0)
                    return Results.Json(new { detail = "nothing stored for this table" }, statusCode: 409);
                if (keys.Count > PruneMaxFraction * t.Count)
                    return Results.Json(new { detail = $"{keys.Count} keys exceed {PruneMaxFraction:P0} of {t.Count} stored" }, statusCode: 409);
                int stored = t.Count, deleted = 0;
                foreach (var k in keys) if (t.Keyed.Remove(k)) deleted++;
                return Results.Json(new { deleted, candidates = keys.Count, storedCount = stored, liveCount = body["liveCount"]?.GetValue<int>() ?? 0, tableName = table });
            }
        });

        r.MapPost("/api/v1/onec/{id}/reconcile-recorder", async (string id, HttpContext c) =>
        {
            if (Auth(c, uploads: false) is { } denied) return denied;
            var body = (await JsonNode.ParseAsync(c.Request.Body)) as JsonObject ?? new JsonObject();
            string table = body["tableName"]!.GetValue<string>(), recorder = body["recorderRef"]!.GetValue<string>();
            var live = body["liveKeys"]!.AsArray().Select(k => k!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            int deleted = 0;
            lock (_gate)
                if (_tables.TryGetValue((id, table), out var t))
                    foreach (var k in t.Keyed.Keys.Where(k => k.StartsWith(recorder + "#", StringComparison.Ordinal) && !live.Contains(k)).ToList())
                        if (t.Keyed.Remove(k)) deleted++;
            return Results.Json(new { deleted, tableName = table, recorderRef = recorder, liveKeys = live.ToList() });
        });
    }

    private (int Inserted, int Updated, int Skipped) Store(string oneCId, string table, JsonArray rows)
    {
        int inserted = 0, updated = 0, skipped = 0;
        lock (_gate)
        {
            if (!_tables.TryGetValue((oneCId, table), out var t)) _tables[(oneCId, table)] = t = new Table();
            foreach (var node in rows)
            {
                if (node is not JsonObject row) continue;
                var data = (JsonObject)row.DeepClone();
                string? key = data["__rowKey"] is JsonValue kv && kv.TryGetValue(out string? ks) && ks.Length > 0 ? ks
                            : data["id"] is JsonValue iv && iv.TryGetValue(out string? ids) && Guid.TryParse(ids, out _) ? ids
                            : null;
                data.Remove("__rowKey");
                data.Remove("__rowHash");
                string hash = RowIdentity.Hash(data);
                if (key is null)
                {
                    if (t.Plain.TryAdd(hash, data)) inserted++; else skipped++;
                    continue;
                }
                if (t.Keyed.TryGetValue(key, out var old))
                {
                    if (old.Hash == hash) { skipped++; continue; }
                    updated++;
                }
                else inserted++;
                t.Keyed[key] = (data, hash);
            }
        }
        return (inserted, updated, skipped);
    }

    /// <summary>The upload needs a user token; the other routes also take a service secret, as the real ones do.</summary>
    private IResult? Auth(HttpContext c, bool uploads)
    {
        string bearer = c.Request.Headers.Authorization.ToString();
        if (bearer == "Bearer " + Token) return null;
        if (c.Request.Headers.ContainsKey("X-Service-Secret"))
            return uploads ? Results.Json(new { detail = "Access denied" }, statusCode: 403) : null;
        return Results.Json(new { detail = "Not authenticated" }, statusCode: 401);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
