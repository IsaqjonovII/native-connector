using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;
using OneC.Sync.Targets.Rust;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// R7 (PYTHON_TO_RUST_MIGRATION_PLAN): who may touch what on the Rust sync API, against the real module —
/// ISOLATED LOCAL instances only (loopback, own Postgres cluster, test secrets): a single-tenant one
/// (<c>url</c>, <c>ONEC_TENANT_SLUG</c> = <c>tenantSlug</c>, the km/authz fixture of aiba-next's shape:
/// user <c>ali</c> has companies 6001 (responsible), 6004 (employee role), 6005 (authz grant); <c>vali</c>
/// none) and a multi-tenant one (<c>multiUrl</c>, tenants t1 and t2 from a fake central). Runs only with
/// AIBA_RUST_SYNC_SECRETS = {url, service, jwt, multiUrl, tenantSlug}.
/// </summary>
public sealed class SyncRustSecurityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string? SecretsFile = Environment.GetEnvironmentVariable("AIBA_RUST_SYNC_SECRETS");
    private const string Cat = "Catalog_Номенклатура";
    private const string Doc = "Document_РеализацияТоваровУслуг";
    private const string Acc = "AccountingRegister_Хозрасчетный_RecordType";

    private sealed record Env(string Url, string MultiUrl, string Service, string Jwt, string Tenant);

    private static Env? Load()
    {
        if (string.IsNullOrEmpty(SecretsFile)) return null;
        var s = JsonNode.Parse(File.ReadAllText(SecretsFile))!;
        if ((string?)s["multiUrl"] is not { } multi || (string?)s["tenantSlug"] is not { } slug) return null;
        var e = new Env((string)s["url"]!, multi, (string)s["service"]!, (string)s["jwt"]!, slug);
        if (!new Uri(e.Url).IsLoopback || !new Uri(e.MultiUrl).IsLoopback) throw new InvalidOperationException("loopback instances only");
        return e;
    }

    private static string G() => Guid.NewGuid().ToString("D");
    private static string B() => Guid.NewGuid().ToString("N");
    private static SyncRow Row(string key, string json) => new(key, null, Encoding.UTF8.GetBytes(json));

    /// <summary>HS256 like aiba-next's sign_jwt; a test token for the isolated instance only.</summary>
    private static string Token(string secret, string sub, string? tenant, string role = "user", TimeSpan? life = null, bool? isAdmin = null)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = new JsonObject { ["sub"] = sub, ["username"] = sub, ["role"] = role, ["exp"] = DateTimeOffset.UtcNow.Add(life ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds() };
        if (tenant is not null) claims["tenant"] = tenant;
        if (isAdmin is { } a) claims["is_admin"] = a;
        string head = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray()), body = B64(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        return $"{head}.{body}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)))}";
    }

    private static HttpClient Http(string url, string? tenant = null)
    {
        var h = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        if (tenant is not null) h.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return h;
    }

    private static async Task<string> CreateConnectionAsync(HttpClient http, string service, int companyId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/onec")
        {
            Content = JsonContent.Create(new { companyId, provider = "unisoft", name = "AIBA_REWRITE_r7", odataName = "r7-" + B()[..8] })
        };
        req.Headers.Add("X-Service-Secret", service);
        var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, text);
        return (string)JsonNode.Parse(text)!["id"]!;
    }

    private static async Task<IReadOnlyList<SyncPartition>> BindAsync(HttpClient http, string service, string conn, params (string Org, int Company)[] b)
    {
        using var put = new HttpRequestMessage(HttpMethod.Put, $"api/v2/onec/{conn}/org-bindings")
        {
            Content = JsonContent.Create(new { bindings = b.Select(x => new { orgRef = x.Org, companyId = x.Company.ToString() }).ToArray() })
        };
        put.Headers.Add("X-Service-Secret", service);
        var resp = await http.SendAsync(put);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        return (await new RustSyncTarget(http, new ServiceSecretCredential(service), conn).GetSyncConfigAsync(conn, Ct)).Partitions;
    }

    private static async Task<(HttpStatusCode Status, string Body)> Get(HttpClient http, string path, string? bearer = null, string? secret = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (secret is not null) req.Headers.Add("X-Service-Secret", secret);
        var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static string? Error(string body) { try { return (string?)JsonNode.Parse(body)?["error"]; } catch { return null; } }

    private static RustSyncTarget AsUser(HttpClient http, string token, string conn) => new(http, new BearerCredential(_ => Task.FromResult(token)), conn);

    [Fact]
    public async Task AUserReachesOnlyTheCompaniesAibaNextGivesThem()
    {
        if (Load() is not { } e) return;
        var http = Http(e.Url);
        string mine = await CreateConnectionAsync(http, e.Service, 6001), viaRole = await CreateConnectionAsync(http, e.Service, 6004),
               viaGrant = await CreateConnectionAsync(http, e.Service, 6005), theirs = await CreateConnectionAsync(http, e.Service, 7777);
        string ali = Token(e.Jwt, "ali", e.Tenant), vali = Token(e.Jwt, "vali", e.Tenant), boss = Token(e.Jwt, "boss", e.Tenant, "tenant_admin"),
               oldAdmin = Token(e.Jwt, "old", e.Tenant, isAdmin: true);
        foreach (var c in new[] { mine, viaRole, viaGrant })
            Assert.Equal(HttpStatusCode.OK, (await Get(http, $"api/sync/v1/connections/{c}/config", ali)).Status);
        // Not theirs: exactly what a connection that does not exist answers.
        var refused = await Get(http, $"api/sync/v1/connections/{theirs}/config", ali);
        var missing = await Get(http, "api/sync/v1/connections/987654321/config", ali);
        Assert.Equal((HttpStatusCode.NotFound, "connection_not_found"), (refused.Status, Error(refused.Body)));
        Assert.Equal((missing.Status, Error(missing.Body)), (refused.Status, Error(refused.Body)));
        Assert.DoesNotContain("7777", refused.Body);                                          // nothing about the owner leaks
        Assert.Equal(HttpStatusCode.NotFound, (await Get(http, $"api/sync/v1/connections/{mine}/config", vali)).Status);
        // A write is refused the same way, and nothing is stored.
        var w = await AsUser(http, ali, theirs).UploadRowsAsync(new UploadBatch(B(), theirs, Cat, new[] { Row(G(), "{}") }), Ct);
        Assert.Equal(Outcome.Gone, w.Outcome);                                                // 404 → Gone
        Assert.Equal(0, (await new RustSyncTarget(http, new ServiceSecretCredential(e.Service), theirs).CountsAsync(theirs, Ct, Cat))[Cat]);
        // Tenant admins (new role claim or legacy is_admin) reach every company of the tenant.
        Assert.Equal(HttpStatusCode.OK, (await Get(http, $"api/sync/v1/connections/{theirs}/config", boss)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get(http, $"api/sync/v1/connections/{theirs}/config", oldAdmin)).Status);
    }

    [Fact]
    public async Task AnOrganisationPartitionOfAnotherCompanyIsNotThereForTheUser()
    {
        if (Load() is not { } e) return;
        var http = Http(e.Url);
        string conn = await CreateConnectionAsync(http, e.Service, 6001);
        string orgMine = G(), orgTheirs = G();
        var parts = await BindAsync(http, e.Service, conn, (orgMine, 6004), (orgTheirs, 6002));
        string pMine = parts.Single(p => p.OrgRef == orgMine).PartitionId, pTheirs = parts.Single(p => p.OrgRef == orgTheirs).PartitionId;
        var ali = AsUser(http, Token(e.Jwt, "ali", e.Tenant), conn);
        string d = G();
        var ok = await ali.UploadRowsAsync(new UploadBatch(B(), pMine, Doc, new[] { Row(d, $"{{\"id\":\"{d}\",\"orgRef\":\"{orgMine}\"}}") }), Ct);
        Assert.Equal((Outcome.Ok, 1), (ok.Outcome, ok.Applied));
        var refused = await ali.UploadRowsAsync(new UploadBatch(B(), pTheirs, Doc, new[] { Row(G(), $"{{\"orgRef\":\"{orgTheirs}\"}}") }), Ct);
        var noSuch = await ali.UploadRowsAsync(new UploadBatch(B(), "987654321", Doc, new[] { Row(G(), "{}") }), Ct);
        Assert.Equal(Outcome.Validation, refused.Outcome);
        Assert.Contains("partition_not_in_connection", refused.Message);
        Assert.Equal(noSuch.Message!.Replace("987654321", "X"), refused.Message!.Replace(pTheirs, "X"));   // indistinguishable
        // The recorder call cannot name it either, and reads of it are refused.
        var unit = await ali.SyncRecorderAtomicAsync(new RecorderSync(B(), d, 1, Doc, null, null, new[] { pMine, pTheirs }, new[] { Acc }, Array.Empty<PartitionMovements>()), Ct);
        Assert.Equal(Outcome.Validation, unit.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ali.CountsAsync(pTheirs, Ct, Doc));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in ali.ReadRowsAsync(pTheirs, Doc, ct: Ct)) { } });
    }

    [Fact]
    public async Task EveryAuthFailureLooksTheSameAndSaysNothing()
    {
        if (Load() is not { } e) return;
        var http = Http(e.Url);
        string conn = await CreateConnectionAsync(http, e.Service, 6001);
        string path = $"api/sync/v1/connections/{conn}/config";
        var tries = new (string What, string? Bearer, string? Secret)[]
        {
            ("no credential", null, null),
            ("other tenant", Token(e.Jwt, "ali", "t-other"), null),
            ("no tenant claim", Token(e.Jwt, "ali", null), null),
            ("expired", Token(e.Jwt, "ali", e.Tenant, life: TimeSpan.FromHours(-2)), null),
            ("forged", Token("not-the-secret", "ali", e.Tenant), null),
            ("superadmin", Token(e.Jwt, "root", null, "superadmin"), null),
            ("garbage token", "a.b.c", null),
            ("secret prefix", null, e.Service[..^1]),
            ("secret longer", null, e.Service + "x"),
            ("empty secret", null, "")
        };
        string? first = null;
        foreach (var (what, bearer, secret) in tries)
        {
            var (status, body) = await Get(http, path, bearer, secret);
            Assert.True(status == HttpStatusCode.Unauthorized, $"{what}: {(int)status} {body}");
            first ??= body;
            Assert.True(body == first, $"{what}: body differs: {body}");
        }
        Assert.Equal("{\"error\":\"unauthorized\"}", first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(http, "api/sync/v1/capabilities")).Status);
    }

    [Fact]
    public async Task MalformedOrForgedIdentifiersAreRefused()
    {
        if (Load() is not { } e) return;
        var http = Http(e.Url);
        string conn = await CreateConnectionAsync(http, e.Service, 6001), other = await CreateConnectionAsync(http, e.Service, 6001);
        var parts = await BindAsync(http, e.Service, conn, (G(), 6004));
        string binding = parts[0].PartitionId;
        string ali = Token(e.Jwt, "ali", e.Tenant);
        foreach (var bad in new[] { "abc", "-1", "0", "99999999999999999999", "13%20OR%201%3D1", "13;DROP", "1e3", "%20" })
        {
            var (status, body) = await Get(http, $"api/sync/v1/connections/{bad}/config", secret: e.Service);
            Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{bad}: {(int)status} {body}");
        }
        // A binding id is not a connection: the service is told so, a user only "not found".
        Assert.Equal("not_a_connection", Error((await Get(http, $"api/sync/v1/connections/{binding}/config", secret: e.Service)).Body));
        var asUser = await Get(http, $"api/sync/v1/connections/{binding}/config", ali);
        Assert.Equal((HttpStatusCode.NotFound, "connection_not_found"), (asUser.Status, Error(asUser.Body)));
        // A partition of another connection, or a forged one, is refused before anything is written.
        var svc = new RustSyncTarget(http, new ServiceSecretCredential(e.Service), conn);
        foreach (var p in new[] { other, "abc", "-5", "", " " + conn, conn + "0" })
        {
            var r = await svc.UploadRowsAsync(new UploadBatch(B(), p, Cat, new[] { Row(G(), "{}") }), Ct);
            Assert.True(r.Outcome == Outcome.Validation && r.Message!.Contains("partition_not_in_connection"), $"'{p}': {r.Outcome} {r.Message}");
        }
        Assert.Equal(0, (await new RustSyncTarget(http, new ServiceSecretCredential(e.Service), other).CountsAsync(other, Ct, Cat))[Cat]);
        // Keys and recorder ids that are not canonical are refused per row / per unit.
        // A NUL anywhere (key or data) must cost that row only, never the batch: jsonb cannot hold U+0000.
        string good = G();
        var keys = await svc.UploadRowsAsync(new UploadBatch(B(), conn, Acc, new[] { Row("x#1", "{}"), Row(G() + "#-1", "{}"), Row(G().ToUpperInvariant(), "{}"),
                                                                                       Row("a\u0000b", "{}"), Row(G(), "{\"s\":\"a\\u0000b\"}"), Row(good, "{\"s\":\"ok\"}") }), Ct);
        Assert.True(keys.Ok, keys.Message);
        Assert.Equal(new[] { "bad_recorder_key", "bad_recorder_key", "guid_not_lowercase", "key_control_char", "nul_in_data" }, keys.Rejected.Select(r => r.Reason));
        Assert.Equal(1, keys.Applied);
        var nulDelete = await svc.DeleteRowsAsync(new DeleteBatch(B(), conn, Acc, new[] { "a\u0000b" }, "x"), Ct);
        Assert.Equal(Outcome.Validation, nulDelete.Outcome);
        Assert.True((await svc.ReportStatusAsync(new BaseStatus(conn, "error", null, null, "1C said \u0000 oops"), Ct)).Ok);
        var rec = await svc.SyncRecorderAtomicAsync(new RecorderSync(B(), "not-a-guid", 1, Doc, null, null, new[] { conn }, new[] { Acc }, Array.Empty<PartitionMovements>()), Ct);
        Assert.Equal(Outcome.Validation, rec.Outcome);
    }

    [Fact]
    public async Task ReconcileDeleteAndPurgeNeverLeaveTheNamedPartition()
    {
        if (Load() is not { } e) return;
        var http = Http(e.Url);
        string conn = await CreateConnectionAsync(http, e.Service, 6001);
        string orgA = G(), orgB = G();
        var parts = await BindAsync(http, e.Service, conn, (orgA, 6004), (orgB, 6005));
        string pA = parts.Single(p => p.OrgRef == orgA).PartitionId, pB = parts.Single(p => p.OrgRef == orgB).PartitionId;
        var t = new RustSyncTarget(http, new ServiceSecretCredential(e.Service), conn);
        string d = G();
        SyncRow Line(string org, int n) => Row($"{d}#{n}", $"{{\"recorderRef\":\"{d}\",\"lineNo\":{n},\"orgRef\":\"{org}\"}}");
        // The same recorder's lines and the same document key in both organisations (as after a bad
        // historical copy): every operation below names A only.
        Assert.True((await t.UploadRowsAsync(new UploadBatch(B(), pA, Acc, new[] { Line(orgA, 1), Line(orgA, 2) }), Ct)).Ok);
        Assert.True((await t.UploadRowsAsync(new UploadBatch(B(), pB, Acc, new[] { Line(orgB, 1), Line(orgB, 2) }), Ct)).Ok);
        Assert.True((await t.UploadRowsAsync(new UploadBatch(B(), pA, Doc, new[] { Row(d, $"{{\"orgRef\":\"{orgA}\"}}") }), Ct)).Ok);
        Assert.True((await t.UploadRowsAsync(new UploadBatch(B(), pB, Doc, new[] { Row(d, $"{{\"orgRef\":\"{orgB}\"}}") }), Ct)).Ok);

        var unit = await t.SyncRecorderAtomicAsync(new RecorderSync(B(), d, null, "", null, null, new[] { pA }, new[] { Acc }, Array.Empty<PartitionMovements>()), Ct);
        Assert.True(unit.Ok, unit.Message);
        Assert.Equal(2, unit.MovementsRemoved);
        var del = await t.DeleteRowsAsync(new DeleteBatch(B(), pA, Doc, new[] { d }, "deleted in 1C"), Ct);
        Assert.Equal(1, del.Deleted);
        var purge = await t.PurgeTableAsync(pA, Acc, Ct);
        Assert.True(purge.Ok, purge.Message);

        var a = await t.CountsAsync(pA, Ct, Acc, Doc);
        var b = await t.CountsAsync(pB, Ct, Acc, Doc);
        Assert.Equal((0L, 0L), (a[Acc], a[Doc]));
        Assert.Equal((2L, 1L), (b[Acc], b[Doc]));                                                // B untouched by all three
        Assert.Equal(0, (await t.CountsAsync(conn, Ct, Acc, Doc)).Values.Sum());
    }

    [Fact]
    public async Task TenantsAreSeparateDatabasesAndATokenOpensOnlyItsOwn()
    {
        if (Load() is not { } e) return;
        var t1 = Http(e.MultiUrl, "t1");
        var t2 = Http(e.MultiUrl, "t2");
        // The same numeric id exists in both tenants (each tenant DB numbers its own connections).
        string c1 = await CreateConnectionAsync(t1, e.Service, 6001);
        string c2 = await CreateConnectionAsync(t2, e.Service, 6001);
        string k1 = G(), k2 = G();
        Assert.True((await new RustSyncTarget(t1, new ServiceSecretCredential(e.Service), c1).UploadRowsAsync(new UploadBatch(B(), c1, Cat, new[] { Row(k1, "{\"t\":1}") }), Ct)).Ok);
        Assert.True((await new RustSyncTarget(t2, new ServiceSecretCredential(e.Service), c2).UploadRowsAsync(new UploadBatch(B(), c2, Cat, new[] { Row(k2, "{\"t\":2}") }), Ct)).Ok);

        string ali1 = Token(e.Jwt, "ali", "t1");
        var mine = AsUser(t1, ali1, c1);
        var rows = new List<StoredRow>();
        await foreach (var r in mine.ReadRowsAsync(c1, Cat, ct: Ct)) rows.Add(r);
        Assert.DoesNotContain(rows, r => r.Key == k2);                                          // t2's rows never through t1
        // A t1 token sent to t2 (by header): refused outright, whatever the id.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(t2, $"api/sync/v1/connections/{c2}/config", ali1)).Status);
        // An id that exists only in t2, asked in t1: not found.
        string onlyIn2 = await CreateConnectionAsync(t2, e.Service, 6001);
        for (int i = 0; i < 3; i++) await CreateConnectionAsync(t2, e.Service, 6001);
        string far = (long.Parse(onlyIn2) + 2).ToString();
        var (s, _) = await Get(t1, $"api/sync/v1/connections/{far}/config", ali1);
        Assert.Equal(HttpStatusCode.NotFound, s);
        // An unknown tenant is not found even for the service.
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Http(e.MultiUrl, "t9"), "api/sync/v1/capabilities", secret: e.Service)).Status);
    }
}
