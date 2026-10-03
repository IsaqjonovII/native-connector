using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using OneC.Cloud;
using OneC.Sync.Targets.Python;

namespace OneC.Supervisor;

/// <summary>
/// S12 on the shared DEVELOPMENT backend/1c (D-2: dev/staging only, never production). The token is
/// the signed-in desktop user's: the session the app keeps in its data folder for the Development
/// environment, refreshed through the same <see cref="CloudClient"/> on a 401. Nothing here writes a
/// token anywhere; the session file is the app's own (DPAPI-protected).
/// </summary>
internal static class DevBackend
{
    /// <summary>The only shared host sync may reach. Production (<c>*.aiba.group</c>) is refused by construction.</summary>
    public static readonly Uri Root = new(CloudEnvironment.Dev.OneCBase.GetLeftPart(UriPartial.Authority) + "/");

    public static (PythonMongoSyncTarget Target, HttpClient Http, CloudClient Cloud) Connect(string sessionDir, string connectionId, HttpFaults faults)
    {
        if (Root.Host.EndsWith(".aiba.group", StringComparison.OrdinalIgnoreCase) || Root.Host != "aiba-1c-dev.aiba.uz")
            throw new InvalidOperationException($"sync dev target resolved to {Root.Host}: refused (D-2)");
        var cloud = new CloudClient(CloudEnvironment.Dev, new SessionStore(sessionDir), DeviceId.Get());
        if (!cloud.SignedIn) throw new InvalidOperationException($"no Development session in {sessionDir}: sign in to AIBA (Development) in the app first");
        faults.InnerHandler = new HttpClientHandler();
        var http = new HttpClient(faults) { BaseAddress = Root, Timeout = TimeSpan.FromMinutes(5) };
        return (new PythonMongoSyncTarget(http, new CloudTokens(cloud), connectionId), http, cloud);
    }

    /// <summary>
    /// A record S12 may write to carries this prefix in its name AND its odataName. The odataName is
    /// the old Connector's only key to a local base (it syncs records whose odataName names one of its
    /// 1C connections), so a test record named this way is never picked up by an old Connector.
    /// </summary>
    public const string TestPrefix = "SYNC-TEST";

    /// <summary>
    /// Refuses to start on anything but a test-owned record nobody else serves: the record exists for
    /// this user, its name and odataName start with <see cref="TestPrefix"/>, it is not being deleted,
    /// and no connector socket is live for it (D-1: an old Connector on the same oneCId would sync it
    /// too). Read-only. Throws with the reason.
    /// </summary>
    public static async Task<JsonNode> GuardAsync(HttpClient http, CloudClient cloud, string connectionId, CancellationToken ct = default)
    {
        var rec = await GetJsonAsync(http, cloud, $"api/v2/onec/{Uri.EscapeDataString(connectionId)}", ct);
        string name = (string?)rec["name"] ?? "", odata = (string?)rec["odataName"] ?? "", state = (string?)rec["connection_state"] ?? "?";
        if (!name.StartsWith(TestPrefix, StringComparison.Ordinal) || !odata.StartsWith(TestPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"dev record {connectionId} is not test-owned (name \"{name}\", odataName \"{odata}\"): " +
                                                $"S12 writes only to records whose name and odataName start with {TestPrefix}");
        if (state != "offline")
            throw new InvalidOperationException($"dev record {connectionId}: connection_state={state} — a connector is live for it (D-1); stop it first");
        return rec;
    }

    /// <summary>GET with the session token; one refresh on 401. The token never leaves the request header.</summary>
    public static async Task<JsonNode> GetJsonAsync(HttpClient http, CloudClient cloud, string url, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new("Bearer", cloud.Session?.AccessToken ?? throw new InvalidOperationException("signed out of the Development cloud"));
            using var resp = await http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await cloud.RefreshAccessAsync(ct)) continue;
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GET {url.Split('?')[0]}: {(int)resp.StatusCode} {text[..Math.Min(300, text.Length)]}");
            return JsonNode.Parse(text)!;
        }
    }

    private sealed class CloudTokens(CloudClient cloud) : ITokenSource
    {
        public Task<string> GetAsync(CancellationToken ct) =>
            Task.FromResult(cloud.Session?.AccessToken ?? throw new InvalidOperationException("signed out of the Development cloud"));

        public Task<bool> RefreshAsync(CancellationToken ct) => cloud.RefreshAccessAsync(ct);
    }
}

/// <summary>
/// Every HTTP call of the sync target passes here: per-call timing and sizes (S12 performance:
/// p50/p95, bytes, retry rate), and — for the S12 failure tests only, armed through
/// <c>POST /v1/sync/faults</c> — a few injected faults that never reach the backend: a 503, a
/// dropped connection, an invalid token (the backend's own 401 then drives the real refresh), and a
/// 400 on the next upload. Nothing is injected unless armed.
/// </summary>
public sealed class HttpFaults : DelegatingHandler
{
    public sealed record Call(DateTime At, string Method, string Path, int Status, double Ms, long RequestBytes, long ResponseBytes);

    private readonly List<Call> _calls = new();
    private int _fail503, _failNetwork, _badToken, _reject400;

    public bool Armable { get; init; }

    public void Arm(int fail503, int failNetwork, int badToken, int reject400)
    {
        if (!Armable) throw new InvalidOperationException("faults are not enabled for this target");
        Interlocked.Exchange(ref _fail503, fail503);
        Interlocked.Exchange(ref _failNetwork, failNetwork);
        Interlocked.Exchange(ref _badToken, badToken);
        Interlocked.Exchange(ref _reject400, reject400);
    }

    public object Armed => new { fail503 = _fail503, failNetwork = _failNetwork, badToken = _badToken, reject400 = _reject400 };

    public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

    public void ClearCalls() { lock (_calls) _calls.Clear(); }

    private static bool Take(ref int counter)
    {
        while (true)
        {
            int n = Volatile.Read(ref counter);
            if (n <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, n - 1, n) == n) return true;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync(ct);    // a multipart body has no length until buffered
        var sw = Stopwatch.StartNew();
        long reqBytes = request.Content?.Headers.ContentLength ?? 0;
        string path = request.RequestUri?.AbsolutePath ?? "";
        bool upload = request.Method == HttpMethod.Post && path.EndsWith("/entity/upload", StringComparison.Ordinal);
        HttpResponseMessage resp;
        if (Take(ref _failNetwork))
        {
            Record(request, path, 0, sw, reqBytes, 0);
            throw new HttpRequestException("injected: connection dropped (S12 failure test)");
        }
        if (Take(ref _fail503)) resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("injected 503 (S12 failure test)"), RequestMessage = request };
        else if (upload && Take(ref _reject400))
            resp = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"detail\":\"injected validation failure (S12 failure test)\"}"), RequestMessage = request };
        else
        {
            if (request.Headers.Authorization is not null && Take(ref _badToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-token-s12-failure-test");
            resp = await base.SendAsync(request, ct);
        }
        Record(request, path, (int)resp.StatusCode, sw, reqBytes, resp.Content.Headers.ContentLength ?? 0);
        return resp;
    }

    private void Record(HttpRequestMessage r, string path, int status, Stopwatch sw, long reqBytes, long respBytes)
    {
        // The path without ids or query: "/api/v2/entity/upload", "/api/v2/onec/{id}/org-bindings".
        string shape = string.Join('/', path.Split('/').Select(s => s.Length == 24 && s.All(Uri.IsHexDigit) ? "{id}" : s));
        lock (_calls)
        {
            _calls.Add(new Call(DateTime.UtcNow, r.Method.Method, shape, status, sw.Elapsed.TotalMilliseconds, reqBytes, respBytes));
            if (_calls.Count > 200_000) _calls.RemoveRange(0, 50_000);
        }
    }
}
