using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace OneC.Sync;

public sealed record UploadResult(int Total, int Inserted, int Updated, int Skipped);

/// <summary>Where synced rows go: backend/1c's contract, mapped from its source (D39).</summary>
public interface IUploadTarget
{
    /// <summary>POST /api/v2/entity/upload — upsert by (oneCId, table, __rowKey).</summary>
    Task<UploadResult> UploadAsync(string oneCId, IReadOnlyDictionary<string, List<JsonObject>> tables, CancellationToken ct);

    /// <summary>POST /api/v1/onec/{id}/prune-missing — rows whose key is gone from 1C. Returns rows deleted.</summary>
    Task<int> PruneAsync(string oneCId, string table, IReadOnlyList<string> missingKeys, int liveCount, CancellationToken ct);

    /// <summary>POST /api/v1/onec/{id}/reconcile-recorder — drop a recorder's rows not in liveKeys. Returns rows deleted.</summary>
    Task<int> ReconcileRecorderAsync(string oneCId, string table, string recorderRef, IReadOnlyList<string> liveKeys, CancellationToken ct);

    /// <summary>GET /api/v1/onec/{id}/counts — rows stored per table.</summary>
    Task<IReadOnlyDictionary<string, long>> CountsAsync(string oneCId, CancellationToken ct);
}

public sealed class UploadException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The HTTP client of that contract, as the connector drives it (connector
/// <c>chunk-uploader.ts</c>): multipart <c>oneCId</c> + <c>file</c> (with a filename — without one
/// Starlette rejects parts over 1 MB), plain JSON (backend/1c has no gzip decoding), chunks of at
/// most <see cref="MaxChunkBytes"/>, a 413 splits the chunk in half, network and 5xx failures
/// retry five times with backoff. Chunks go one at a time: backend/1c serialises nothing between
/// two in-flight chunks touching the same row keys.
/// </summary>
public sealed class HttpUploadTarget : IUploadTarget
{
    public const int MaxChunkBytes = 34 * 1024 * 1024;
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8) };

    private readonly HttpClient _http;
    private readonly Action<HttpRequestMessage> _auth;

    /// <param name="http">BaseAddress = the service root (e.g. https://…/), without /api.</param>
    /// <param name="auth">Adds the credential: <c>Authorization: Bearer …</c> for uploads (a service secret is refused there).</param>
    public HttpUploadTarget(HttpClient http, Action<HttpRequestMessage> auth)
    {
        _http = http;
        _auth = auth;
    }

    public int ChunkBytes { get; init; } = MaxChunkBytes;
    public TimeSpan[] RetryDelays { get; init; } = Backoff;

    public async Task<UploadResult> UploadAsync(string oneCId, IReadOnlyDictionary<string, List<JsonObject>> tables, CancellationToken ct)
    {
        var total = new UploadResult(0, 0, 0, 0);
        foreach (var (table, rows) in tables)
            foreach (var chunk in Chunks(rows))
                total = Add(total, await UploadChunkAsync(oneCId, table, chunk, depth: 0, ct));
        return total;
    }

    /// <summary>Rows in order, cut where the serialised chunk would pass the size limit.</summary>
    private IEnumerable<List<JsonObject>> Chunks(List<JsonObject> rows)
    {
        var chunk = new List<JsonObject>();
        long bytes = 0;
        foreach (var row in rows)
        {
            long size = Encoding.UTF8.GetByteCount(JsJson.Serialize(row)) + 1;
            if (chunk.Count > 0 && bytes + size > ChunkBytes) { yield return chunk; chunk = new(); bytes = 0; }
            chunk.Add(row);
            bytes += size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    private async Task<UploadResult> UploadChunkAsync(string oneCId, string table, List<JsonObject> rows, int depth, CancellationToken ct)
    {
        var body = new StringBuilder();
        body.Append('{');
        body.Append(JsJson.Serialize(JsonValue.Create(table))).Append(":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) body.Append(',');
            JsJson.Write(body, rows[i], sortKeys: false);
        }
        body.Append("]}");
        byte[] file = Encoding.UTF8.GetBytes(body.ToString());

        var response = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(oneCId), "oneCId" }
            };
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(part, "file", "chunk.json");
            return new HttpRequestMessage(HttpMethod.Post, "api/v2/entity/upload") { Content = form };
        }, ct);

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && rows.Count > 1 && depth < 10)
        {
            int half = rows.Count / 2;
            return Add(await UploadChunkAsync(oneCId, table, rows.GetRange(0, half), depth + 1, ct),
                       await UploadChunkAsync(oneCId, table, rows.GetRange(half, rows.Count - half), depth + 1, ct));
        }
        var json = await Read(response, ct);
        return new UploadResult(Int(json["totalItems"]), Int(json["totalInserted"]),
                                json["results"] is JsonArray r ? r.Sum(x => Int(x?["updated"])) : 0, Int(json["totalSkipped"]));
    }

    public async Task<int> PruneAsync(string oneCId, string table, IReadOnlyList<string> missingKeys, int liveCount, CancellationToken ct)
    {
        var body = new JsonObject { ["tableName"] = table, ["missingKeys"] = new JsonArray(missingKeys.Select(k => (JsonNode)k).ToArray()), ["liveCount"] = liveCount };
        var json = await Read(await SendAsync(() => Json(HttpMethod.Post, $"api/v1/onec/{Uri.EscapeDataString(oneCId)}/prune-missing?scope=connection", body), ct), ct);
        return Int(json["deleted"]);
    }

    public async Task<int> ReconcileRecorderAsync(string oneCId, string table, string recorderRef, IReadOnlyList<string> liveKeys, CancellationToken ct)
    {
        var body = new JsonObject { ["tableName"] = table, ["recorderRef"] = recorderRef, ["liveKeys"] = new JsonArray(liveKeys.Select(k => (JsonNode)k).ToArray()) };
        var json = await Read(await SendAsync(() => Json(HttpMethod.Post, $"api/v1/onec/{Uri.EscapeDataString(oneCId)}/reconcile-recorder?scope=connection", body), ct), ct);
        return Int(json["deleted"]);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountsAsync(string oneCId, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/v1/onec/{Uri.EscapeDataString(oneCId)}/counts?scope=connection"), ct);
        if (!response.IsSuccessStatusCode) throw new UploadException($"counts: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}", (int)response.StatusCode);
        var array = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? new JsonArray();
        return array.OfType<JsonObject>().ToDictionary(o => o["tableName"]!.GetValue<string>(), o => (long)Int(o["quantity"]), StringComparer.Ordinal);
    }

    /// <summary>Retries network failures and 5xx; everything else is the caller's.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = make();
            _auth(request);
            try
            {
                var response = await _http.SendAsync(request, ct);
                if ((int)response.StatusCode < 500 || attempt >= RetryDelays.Length) return response;
            }
            catch (HttpRequestException) when (attempt < RetryDelays.Length) { }
            await Task.Delay(RetryDelays[attempt], ct);
        }
    }

    private static HttpRequestMessage Json(HttpMethod method, string url, JsonNode body) =>
        new(method, url) { Content = new StringContent(JsJson.Serialize(body), Encoding.UTF8, "application/json") };

    private static async Task<JsonObject> Read(HttpResponseMessage response, CancellationToken ct)
    {
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new UploadException($"{response.RequestMessage?.RequestUri?.AbsolutePath}: {(int)response.StatusCode} {text}", (int)response.StatusCode);
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    private static UploadResult Add(UploadResult a, UploadResult b) =>
        new(a.Total + b.Total, a.Inserted + b.Inserted, a.Updated + b.Updated, a.Skipped + b.Skipped);
}
