using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneC.Cloud;

/// <summary>
/// The AIBA cloud as the old Connector uses it (specs/2026-09-30-auth-and-cloud-link.md):
///   - login: form POST <c>{api}/auth/login</c> (phone, password) → access (1 day) + refresh (30 days);
///   - every call: <c>Authorization: Bearer</c> + <c>X-Device-Id</c>, nothing else (no company header);
///   - 401 → one refresh (<c>POST {api}/auth/refresh</c>, same refresh token back), one retry;
///     never for <c>/auth/*</c>; a failed refresh signs out;
///   - companies: <c>{api}/company/</c>, paged; 1C links: <c>{1c}/onec</c> on backend/1c.
/// Never logs request bodies, tokens or the /user/me body (it carries third-party OAuth tokens).
/// </summary>
public sealed class CloudClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SessionStore _store;
    private readonly string _deviceId;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public CloudEnvironment Env { get; }
    public CloudSession? Session { get; private set; }
    public bool SignedIn => Session is not null;

    /// <summary>The session ended (refresh refused, or sign-out). Raised on the calling thread.</summary>
    public event EventHandler? SignedOut;

    public CloudClient(CloudEnvironment env, SessionStore store, string deviceId, HttpMessageHandler? handler = null)
    {
        Env = env;
        _store = store;
        _deviceId = deviceId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        Session = store.Load(env.Key);
    }

    // ---------------- auth ----------------

    /// <summary>+998 numbers are the norm; a bare 9-digit local number gets +998. The backend
    /// parses with default region US, so a number without the country code is refused (spec §1).</summary>
    public static string NormalizePhone(string input)
    {
        string digits = new(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 9) return "+998" + digits;
        return digits.Length == 0 ? "" : "+" + digits;
    }

    public async Task<CloudSession> LoginAsync(string phone, string password, CancellationToken ct = default)
    {
        string e164 = NormalizePhone(phone);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Env.ApiBase, "auth/login"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["phone"] = e164, ["password"] = password, ["is_socket_device"] = "false"
            })
        };
        req.Headers.Add("X-Device-Id", _deviceId);
        using var resp = await _http.SendAsync(req, ct);
        var body = await ReadJson(resp, ct);
        if (!resp.IsSuccessStatusCode) throw Error(resp.StatusCode, body);

        string access = body?["access_token"]?.GetValue<string>() ?? throw new CloudException(0, "bad_response", "The login answer had no token.");
        string refresh = body["refresh_token"]?.GetValue<string>() ?? "";

        // Names from /user/me; the user id from the token itself — backend/1c compares userId
        // with the JWT's sub (spec §5.1), so that is the id that must be sent.
        string first = "", last = "";
        using (var me = new HttpRequestMessage(HttpMethod.Get, new Uri(Env.ApiBase, "user/me")))
        {
            me.Headers.Authorization = new("Bearer", access);
            me.Headers.Add("X-Device-Id", _deviceId);
            using var mr = await _http.SendAsync(me, ct);
            var m = await ReadJson(mr, ct);
            if (!mr.IsSuccessStatusCode) throw Error(mr.StatusCode, m);
            first = m?["first_name"]?.GetValueKind() == JsonValueKind.String ? m["first_name"]!.GetValue<string>() : "";
            last = m?["last_name"]?.GetValueKind() == JsonValueKind.String ? m["last_name"]!.GetValue<string>() : "";
        }
        string userId = JwtSubject(access) ?? throw new CloudException(0, "bad_response", "The login token has no user id.");

        Session = new CloudSession(Env.Key, userId, e164, first, last, access, refresh);
        _store.Save(Session);
        return Session;
    }

    /// <summary>
    /// Local sign-out, as the old app does (it never calls the server logout, which needs an
    /// fsm_token — spec open question 6). backend/1c checks only signature + expiry, so the
    /// token is deleted here and forgotten.
    /// </summary>
    public void SignOut()
    {
        Session = null;
        _store.Clear();
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// After a 401 from another service (backend/1c called by the sync engine): one refresh of the
    /// current access token. False when there is no session or the refresh was refused.
    /// </summary>
    public Task<bool> RefreshAccessAsync(CancellationToken ct = default) =>
        Session is { } s ? RefreshAsync(s.AccessToken, ct) : Task.FromResult(false);

    private async Task<bool> RefreshAsync(string staleAccess, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var s = Session;
            if (s is null) return false;
            if (s.AccessToken != staleAccess) return true;              // another call already refreshed
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Env.ApiBase, "auth/refresh"))
            {
                Content = JsonContent.Create(new { refresh_token = s.RefreshToken })
            };
            req.Headers.Add("X-Device-Id", _deviceId);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await ReadJson(resp, ct);
            if (body?["access_token"]?.GetValue<string>() is not { Length: > 0 } access) return false;
            string refresh = body["refresh_token"]?.GetValue<string>() is { Length: > 0 } r ? r : s.RefreshToken;
            Session = s with { AccessToken = access, RefreshToken = refresh };
            _store.Save(Session);
            return true;
        }
        catch (HttpRequestException) { return false; }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Sends with the current token; on 401 refreshes once and retries once.</summary>
    private async Task<JsonNode?> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var s = Session ?? throw new CloudException(401, "not_signed_in", "Sign in to the AIBA cloud first.");
            using var req = make();
            req.Headers.Authorization = new("Bearer", s.AccessToken);
            req.Headers.Add("X-Device-Id", _deviceId);
            using var resp = await _http.SendAsync(req, ct);
            var body = await ReadJson(resp, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                if (await RefreshAsync(s.AccessToken, ct)) continue;
                SignOut();
                throw new CloudException(401, "auth.session_ended", CloudException.Friendly(401, "auth.invalid_refresh_token"));
            }
            if (!resp.IsSuccessStatusCode) throw Error(resp.StatusCode, body);
            return body;
        }
    }

    // ---------------- companies and 1C links ----------------

    /// <summary>All pages of <c>GET /company/</c> (both paging spellings, as the old app sends).</summary>
    public async Task<List<CloudCompany>> CompaniesAsync(CancellationToken ct = default)
    {
        var list = new List<CloudCompany>();
        for (int page = 1, pages = 1; page <= pages && page <= 100; page++)
        {
            int p = page;
            var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                new Uri(Env.ApiBase, $"company/?pageNumber={p}&pageSize=50&page={p}&size=50")), ct);
            pages = body?["pages"]?.GetValue<int>() ?? 1;
            foreach (var c in body?["results"]?.AsArray() ?? new JsonArray())
                list.Add(new CloudCompany(OneCRecord.Str(c!["id"]) ?? "", OneCRecord.Str(c["name"]) ?? "",
                                          OneCRecord.Str(c["inn"]) ?? "", OneCRecord.Str(c["form"]) ?? "",
                                          c["is_default"]?.GetValueKind() == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>The company's 1C connections, without records being deleted (old app: info-base.ts:217).</summary>
    public async Task<List<OneCRecord>> OneCListAsync(string companyId, CancellationToken ct = default)
    {
        var list = new List<OneCRecord>();
        string cid = Uri.EscapeDataString(companyId);
        for (int page = 1, pages = 1; page <= pages && page <= 100; page++)
        {
            int p = page;
            var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                new Uri(Env.OneCBase, $"onec?page={p}&size=50&companyId={cid}")), ct);
            pages = body?["pages"]?.GetValue<int>() ?? 1;
            foreach (var n in body?["items"]?.AsArray() ?? new JsonArray())
            {
                var r = OneCRecord.From(n!);
                if (r.Status != "deleting") list.Add(r);
            }
        }
        return list;
    }

    /// <summary><c>POST /onec</c>. 409 = a record with this company + odataName + name exists.
    /// <paramref name="version"/> may be null: backend/1c's OneCCreate.version is optional.</summary>
    public async Task<OneCRecord> OneCCreateAsync(string companyId, string name, string odataName, string provider,
                                                  string? version, CancellationToken ct = default)
    {
        if (provider is not ("unisoft" or "venkon")) throw new ArgumentException("provider must be unisoft or venkon", nameof(provider));
        string userId = Session?.UserId ?? throw new CloudException(401, "not_signed_in", "Sign in to the AIBA cloud first.");
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(Env.OneCBase, "onec"))
        {
            Content = JsonContent.Create(new { userId, companyId, name, odataName, provider, version })
        }, ct);
        var node = body is JsonArray a && a.Count > 0 ? a[0]! : body!;
        return OneCRecord.From(node);
    }

    /// <summary>
    /// <c>DELETE /onec/connection/{id}</c>: the record turns "deleting" and backend/1c purges it
    /// with all its synced data in the background (v2 onec.py:171-201). Cannot be undone. 404 / 410
    /// mean it is already gone, as the old app counts them (accounting/page.tsx:148-165).
    /// </summary>
    public async Task OneCDeleteAsync(string oneCId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
                new Uri(Env.OneCBase, $"onec/connection/{Uri.EscapeDataString(oneCId)}")), ct);
        }
        catch (CloudException ex) when (ex.Status is 404 or 410) { }
    }

    /// <summary><c>PATCH /onec/connection/{id} {status}</c> — active | inactive | running.</summary>
    public Task PatchStatusAsync(string oneCId, string status, CancellationToken ct = default) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, new Uri(Env.OneCBase, $"onec/connection/{Uri.EscapeDataString(oneCId)}"))
        {
            Content = JsonContent.Create(new { status })
        }, ct);

    // ---------------- helpers ----------------

    private static async Task<JsonNode?> ReadJson(HttpResponseMessage resp, CancellationToken ct)
    {
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (text.Length == 0) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    /// <summary>The backend answers <c>{errors:[{message}]}</c> or <c>{detail: key}</c> (spec §1).</summary>
    private static CloudException Error(HttpStatusCode status, JsonNode? body)
    {
        string code = "";
        if (body?["errors"] is JsonArray errs && errs.Count > 0) code = OneCRecord.Str(errs[0]?["message"]) ?? "";
        else if (body?["detail"] is { } d) code = d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : "";
        return new CloudException((int)status, code, CloudException.Friendly((int)status, code));
    }

    /// <summary>The <c>sub</c> claim of a JWT, without verifying it (the server does that).</summary>
    public static string? JwtSubject(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        string p = parts[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        try
        {
            var n = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            return OneCRecord.Str(n?["sub"]);
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }
}
