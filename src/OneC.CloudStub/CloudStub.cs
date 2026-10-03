using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OneC.CloudStub;

/// <summary>
/// The cloud routes the desktop app uses, on loopback, with backend rules copied from the spec
/// (cited there): main API login / refresh / me / companies (<c>/api/v1</c>) and backend/1c
/// connection records (<c>/api/v2/onec</c>). Tokens are HS256 JWTs signed with a per-run key, so
/// expiry can be forced (<see cref="ExpireAccessTokens"/>). Everything lives in memory.
/// </summary>
public sealed class CloudStub : IAsyncDisposable
{
    public sealed record User(string Id, string Phone, string Password, string FirstName, string LastName);
    public sealed record Company(string Id, string Name, string Inn, bool IsDefault, string OwnerId);
    public sealed class Record
    {
        public required string Id, UserId, CompanyId, Name, OdataName, Provider, Version;
        public string Status = "inactive";
        public DateTime? StatusUpdatedUtc;
        public int Patches;
        public long TotalCount;
        public double Percentage;
        public string? LastError;
    }

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, User> _usersByPhone = new();
    private readonly List<Company> _companies = new();
    private readonly ConcurrentDictionary<string, Record> _records = new();
    private readonly ConcurrentDictionary<string, string> _refresh = new();      // refresh token → user id
    private readonly ConcurrentQueue<DateTime> _logins = new();
    private WebApplication? _app;

    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromDays(1);
    public int LoginRateLimitPerMinute { get; set; } = 5;
    public int RefreshCalls;
    public Uri? BaseAddress { get; private set; }
    public IReadOnlyCollection<Record> Records => _records.Values.ToList();

    /// <summary>Every access token issued so far is refused (401), as if it had expired.</summary>
    private int _generation;
    public void ExpireAccessTokens() => Interlocked.Increment(ref _generation);
    public void RevokeRefreshTokens() => _refresh.Clear();

    public void AddUser(User u) => _usersByPhone[u.Phone] = u;
    public void AddCompany(Company c) { lock (_companies) _companies.Add(c); }
    public void MarkDeleting(string recordId) => _records[recordId].Status = "deleting";

    /// <summary>A record made elsewhere (the old Connector, the web app), for list checks.</summary>
    public Record AddRecord(string userId, string companyId, string name, string odataName, string provider = "unisoft",
                            long totalCount = 0, double percentage = 0, string? lastError = null)
    {
        var rec = new Record
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), UserId = userId,
            CompanyId = companyId, Name = name, OdataName = odataName, Provider = provider, Version = "3.0",
            TotalCount = totalCount, Percentage = percentage, LastError = lastError
        };
        _records[rec.Id] = rec;
        return rec;
    }

    public async Task StartAsync(int port = 0)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        _app = b.Build();
        Map(_app);
        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseAddress = new Uri(addr.EndsWith('/') ? addr : addr + "/");
    }

    public Uri ApiBase => new(BaseAddress!, "api/v1/");
    public Uri OneCBase => new(BaseAddress!, "api/v2/");

    private static IResult Detail(int status, string detail) => Results.Json(new { detail }, statusCode: status);

    private void Map(WebApplication app)
    {
        // ---- main API (backend/backend app/api/v1/auth.py:565-674, user.py:94-105, company.py:589-702)
        app.MapPost("/api/v1/auth/login", async (HttpContext c) =>
        {
            var now = DateTime.UtcNow;
            _logins.Enqueue(now);
            while (_logins.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(1)) _logins.TryDequeue(out _);
            if (_logins.Count > LoginRateLimitPerMinute) return Results.Json(new { error = "Rate limit exceeded: 5 per 1 minute" }, statusCode: 429);

            var form = await c.Request.ReadFormAsync();
            string phone = form["phone"].ToString(), password = form["password"].ToString();
            if (!phone.StartsWith('+') || phone.Length < 8 || !phone[1..].All(char.IsDigit)) return Detail(400, "auth.invalid_phone_number");
            if (!_usersByPhone.TryGetValue(phone, out var u)) return Detail(400, "auth.user_not_found");
            if (u.Password != password) return Detail(401, "auth.password_incorrect");
            string refresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _refresh[refresh] = u.Id;
            return Results.Json(new { access_token = Issue(u.Id), refresh_token = refresh, token_type = "bearer" });
        });

        app.MapPost("/api/v1/auth/refresh", async (HttpContext c) =>
        {
            Interlocked.Increment(ref RefreshCalls);
            var body = await JsonNode.ParseAsync(c.Request.Body);
            string token = body?["refresh_token"]?.GetValue<string>() ?? "";
            if (!_refresh.TryGetValue(token, out var uid)) return Detail(401, "auth.invalid_refresh_token");
            return Results.Json(new { access_token = Issue(uid), refresh_token = token });   // not rotated
        });

        app.MapGet("/api/v1/user/me", (HttpContext c) =>
        {
            if (Subject(c) is not { } uid) return Detail(401, "Could not validate credentials");
            var u = _usersByPhone.Values.First(x => x.Id == uid);
            // The real /me carries third-party OAuth tokens; the client must never store or log them.
            return Results.Json(new
            {
                id = int.Parse(u.Id), phone = u.Phone, first_name = u.FirstName, last_name = u.LastName,
                socials = new[] { new { provider = "google", access_token = "SOCIAL-SECRET-DO-NOT-STORE", refresh_token = "SOCIAL-REFRESH" } }
            });
        });

        app.MapGet("/api/v1/company/", (HttpContext c) =>
        {
            if (Subject(c) is not { } uid) return Detail(401, "Could not validate credentials");
            int page = Q(c, "pageNumber") ?? Q(c, "page") ?? 1, size = Q(c, "pageSize") ?? Q(c, "size") ?? 10;
            List<Company> mine;
            lock (_companies) mine = _companies.Where(x => x.OwnerId == uid).ToList();
            int pages = Math.Max(1, (mine.Count + size - 1) / size);
            var results = mine.Skip((page - 1) * size).Take(size)
                              .Select(x => new { id = x.Id, name = x.Name, inn = x.Inn, form = "MChJ", is_default = x.IsDefault });
            return Results.Json(new { results, count = mine.Count, page, size, pages });
        });

        // ---- stub only: what the app did, for end-to-end checks (loopback, no real data)
        app.MapGet("/__stub/records", () => Results.Json(_records.Values.Select(r => new
        {
            r.Id, r.CompanyId, r.Name, r.OdataName, r.Provider, r.Version, r.Status, r.Patches, r.StatusUpdatedUtc
        })));

        // ---- backend/1c (app/api/v2/routes/onec.py:77-169, app/utils/onec.py:336-351)
        app.MapGet("/api/v2/onec", (HttpContext c) =>
        {
            if (Subject(c) is null) return Detail(401, "Could not validate credentials");
            int page = Q(c, "page") ?? 1, size = Q(c, "size") ?? 50;
            string? cid = c.Request.Query["companyId"];
            var rows = _records.Values.Where(r => cid is null || r.CompanyId == cid).OrderBy(r => r.Id).ToList();
            int pages = Math.Max(1, (rows.Count + size - 1) / size);
            return Results.Json(new { items = rows.Skip((page - 1) * size).Take(size).Select(View), total = rows.Count, page, size, pages });
        });

        app.MapPost("/api/v2/onec", async (HttpContext c) =>
        {
            if (Subject(c) is not { } uid) return Detail(401, "Could not validate credentials");
            var b = await JsonNode.ParseAsync(c.Request.Body);
            string S(string k) => b?[k]?.GetValue<string>() ?? "";
            if (S("userId") != uid) return Detail(403, "You can only create connections for yourself");
            if (S("provider") is not ("unisoft" or "venkon"))
                return Results.Json(new { detail = new[] { new { loc = new[] { "body", "provider" }, msg = "String should match pattern '^(unisoft|venkon)$'" } } }, statusCode: 422);
            if (_records.Values.Any(r => r.CompanyId == S("companyId") && r.OdataName == S("odataName") && r.Name == S("name")))
                return Detail(409, "1C connection already exists");
            var rec = new Record
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), UserId = uid,
                CompanyId = S("companyId"), Name = S("name"), OdataName = S("odataName"), Provider = S("provider"), Version = S("version")
            };
            _records[rec.Id] = rec;
            return Results.Json(View(rec), statusCode: 201);
        });

        app.MapMethods("/api/v2/onec/connection/{id}", new[] { "PATCH" }, async (string id, HttpContext c) =>
        {
            if (Subject(c) is not { } uid) return Detail(401, "Could not validate credentials");
            if (!_records.TryGetValue(id, out var rec)) return Detail(404, "1C connection not found");
            if (rec.UserId != uid) return Detail(403, "Not your connection");
            if (rec.Status == "deleting") return Detail(409, "This 1C connection is being deleted");
            var b = await JsonNode.ParseAsync(c.Request.Body);
            if (b?["status"]?.GetValue<string>() is { } st)
            {
                if (st is not ("active" or "inactive" or "running" or "deleting")) return Detail(422, "bad status");
                rec.Status = st;
                rec.StatusUpdatedUtc = DateTime.UtcNow;
                rec.Patches++;
            }
            return Results.Json(View(rec));
        });

        // v2 onec.py:171-201: flips to "deleting", a worker purges it; the list stops showing it.
        app.MapDelete("/api/v2/onec/connection/{id}", (string id, HttpContext c) =>
        {
            if (Subject(c) is not { } uid) return Detail(401, "Could not validate credentials");
            if (!_records.TryGetValue(id, out var rec)) return Detail(404, "1C connection not found");
            if (rec.UserId != uid) return Detail(403, "Not your connection");
            rec.Status = "deleting";
            return Results.Json(new { message = "Deletion started", id, status = "deleting" }, statusCode: 202);
        });
    }

    private static object View(Record r) => new
    {
        id = r.Id, userId = r.UserId, provider = r.Provider, odataName = r.OdataName, companyId = r.CompanyId,
        name = r.Name, version = r.Version, status = r.Status, totalCount = r.TotalCount, percentage = r.Percentage,
        lastError = r.LastError, connectionId = (string?)null, isShared = false, statusUpdatedAt = r.StatusUpdatedUtc
    };

    private static int? Q(HttpContext c, string k) => int.TryParse(c.Request.Query[k], out int v) ? v : null;

    // ---- tokens: HS256 JWT, sub + iat + exp

    private string Issue(string userId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string h = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        string p = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = userId, iat = now, exp = now + (long)AccessLifetime.TotalSeconds,
            jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), gen = Volatile.Read(ref _generation)
        }));
        return $"{h}.{p}.{B64(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{h}.{p}")))}";
    }

    private string? Subject(HttpContext c)
    {
        string auth = c.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ")) return null;
        var parts = auth[7..].Split('.');
        if (parts.Length != 3) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(B64(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}")))),
                                                     Encoding.ASCII.GetBytes(parts[2]))) return null;
        var n = JsonNode.Parse(Encoding.UTF8.GetString(Unb64(parts[1])))!;
        var exp = DateTimeOffset.FromUnixTimeSeconds(n["exp"]!.GetValue<long>()).UtcDateTime;
        if (exp <= DateTime.UtcNow || n["gen"]!.GetValue<int>() != Volatile.Read(ref _generation)) return null;
        return n["sub"]!.GetValue<string>();
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Unb64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
