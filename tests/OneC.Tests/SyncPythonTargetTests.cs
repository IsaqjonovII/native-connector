using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;
using OneC.Sync.Targets.Python;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Sync block S12: the Python v2 target against a real backend/1c — an ISOLATED LOCAL instance
/// (own port, own throwaway Mongo database, test secrets made for it), never the shared dev server.
/// Runs only with AIBA_SYNC_BACKEND (its URL) and AIBA_SYNC_SECRETS (its secrets file) set.
/// Stored rows are read back through the backend's own entity route: a reported count is not proof.
/// </summary>
public sealed class SyncPythonTargetTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static string? Url => Environment.GetEnvironmentVariable("AIBA_SYNC_BACKEND");
    private static readonly string UserId = "5f0c2a8e-1c1e-4d7b-9a55-5ab1c0de0001";

    private sealed class Tokens(string token) : ITokenSource
    {
        public string Token = token;
        public int Refreshes;
        public Func<string>? OnRefresh;
        public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(Token);
        public Task<bool> RefreshAsync(CancellationToken ct)
        {
            Refreshes++;
            if (OnRefresh is null) return Task.FromResult(false);
            Token = OnRefresh();
            return Task.FromResult(true);
        }
    }

    private static (string Jwt, string Service) Secrets()
    {
        var s = JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("AIBA_SYNC_SECRETS")!))!;
        return ((string)s["jwt"]!, (string)s["service"]!);
    }

    /// <summary>HS256, what backend/1c's decode_access_token accepts; a test token for the isolated instance only.</summary>
    private static string Mint(string secret, string sub, TimeSpan life)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string head = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
        string body = B64(Encoding.UTF8.GetBytes($"{{\"sub\":\"{sub}\",\"exp\":{DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds()}}}"));
        string sig = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)));
        return $"{head}.{body}.{sig}";
    }

    private static async Task<(HttpClient Http, string OneCId, Tokens Tokens)> Connection(string? companyId = null)
    {
        var (jwt, service) = Secrets();
        var http = new HttpClient { BaseAddress = new Uri(Url!.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/onec")
        {
            Content = JsonContent.Create(new
            {
                userId = UserId, provider = "unisoft", odataName = "synctest-" + Guid.NewGuid().ToString("N")[..8],
                companyId = companyId ?? Guid.NewGuid().ToString(), name = "Sync contract test"
            })
        };
        req.Headers.Add("X-Service-Secret", service);
        var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, text);
        string id = (string)JsonNode.Parse(text)!["id"]!;
        return (http, id, new Tokens(Mint(jwt, UserId, TimeSpan.FromHours(1))));
    }

    /// <summary>
    /// A multi-organisation connection on the real server code: the connection (shared partition)
    /// plus one org binding (its own partition, companyId = a second company this user has a
    /// connection for — what backend/1c's binding check accepts).
    /// </summary>
    private static async Task<(HttpClient Http, string Connection, string Binding, Tokens Tokens)> MultiOrgConnection()
    {
        string companyB = Guid.NewGuid().ToString();
        var (_, _, _) = await Connection(companyB);                          // makes companyB "this user's"
        var (http, conn, tokens) = await Connection();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"api/v2/onec/{conn}/org-bindings")
        {
            Content = JsonContent.Create(new { bindings = new[] { new { orgRef = Guid.NewGuid().ToString(), companyId = companyB } } })
        };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.Token);
        var resp = await http.SendAsync(put);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"PUT org-bindings: {(int)resp.StatusCode} {text}");
        var config = await new PythonMongoSyncTarget(http, tokens, conn).GetSyncConfigAsync(conn, Ct);
        string binding = Assert.Single(config.Partitions).PartitionId;
        Assert.NotEqual(conn, binding);
        return (http, conn, binding, tokens);
    }

    /// <summary>
    /// The engine addresses every partition itself. backend/1c's <c>scope=connection</c> widens a
    /// reconcile or prune to EVERY partition of the base, keeping only the keys sent — so a call made
    /// for the shared partition with its own (empty) live set deleted the movements just uploaded to
    /// an organisation's partition (found 2026-10-01 against origin/development; the stub, being
    /// per-partition, could not show it). Each call must stay in the partition it names.
    /// </summary>
    [Fact]
    public async Task AReconcileOrPruneOfTheSharedPartitionLeavesAnOrganisationsRowsAlone()
    {
        if (Gate.Skip(Url is null, "AIBA_SYNC_BACKEND not set: the isolated local backend/1c is required (scratchpad s12/start-backend.ps1)")) return;
        var (http, conn, binding, tokens) = await MultiOrgConnection();
        var target = new PythonMongoSyncTarget(http, tokens, conn);
        const string reg = "AccountingRegister_SyncTest", doc = "Document_SyncTest";
        string recorder = Guid.NewGuid().ToString();
        var lines = new[] { Row($"{recorder}#1", "{\"Сумма\":10}"), Row($"{recorder}#2", "{\"Сумма\":20}") };
        Assert.True((await target.UploadRowsAsync(new UploadBatch("m1", binding, reg, lines), Ct)).Ok);
        Assert.True((await target.UploadRowsAsync(new UploadBatch("d1", binding, doc, new[] { Row(recorder, "{\"n\":1}") }), Ct)).Ok);

        // What RecorderHandler does after a repost: the shared partition has no live movements of it.
        var rc = await target.ReconcileRecorderAsync(new RecorderReconcile("r1", conn, reg, recorder, Array.Empty<string>()), Ct);
        Assert.True(rc.Ok, rc.Message);
        // …and a document row "left" the shared partition (organisation history).
        var del = await target.DeleteRowsAsync(new DeleteBatch("p1", conn, doc, new[] { recorder }, "left the partition"), Ct);
        Assert.True(del.Ok, del.Message);

        Assert.Equal(new[] { $"{recorder}#1", $"{recorder}#2" }, (await Stored(http, tokens, binding, reg)).Keys.Order());
        Assert.Equal(new[] { recorder }, (await Stored(http, tokens, binding, doc)).Keys);

        // sync-tables, read the way backend/1c answers it: null until a list is stored, then [{table, reports}].
        Assert.False((await target.GetSyncConfigAsync(conn, Ct)).TableListStored);
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"api/v1/onec/{conn}/sync-tables")
        {
            Content = JsonContent.Create(new { tables = new[] { new { table = reg, reports = false }, new { table = "Catalog_SyncTest", reports = true } } })
        })
        {
            put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.Token);
            var pr = await http.SendAsync(put);
            Assert.True(pr.IsSuccessStatusCode, await pr.Content.ReadAsStringAsync());
        }
        var cfg = await target.GetSyncConfigAsync(conn, Ct);
        Assert.True(cfg.TableListStored);
        Assert.Equal(new[] { reg, "Catalog_SyncTest" }, cfg.Tables.Select(t => t.Table).Order(StringComparer.Ordinal));
        Assert.True(cfg.Tables.Single(t => t.Table == reg).IsMovement);

        // And within its own partition a reconcile still removes what 1C no longer has.
        var own = await target.ReconcileRecorderAsync(new RecorderReconcile("r2", binding, reg, recorder, new[] { $"{recorder}#1" }), Ct);
        Assert.True(own.Ok, own.Message);
        Assert.Equal(new[] { $"{recorder}#1" }, (await Stored(http, tokens, binding, reg)).Keys);
    }

    /// <summary>The key also goes into a plain field "k": the backend keeps __rowKey out of rawData.</summary>
    private static SyncRow Row(string key, string json) => new(key, null, Encoding.UTF8.GetBytes(json.Replace("}", $",\"k\":\"{key}\",\"__rowKey\":\"{key}\"}}")));

    /// <summary>The stored rows of one table, key → rawData, read back through the backend.</summary>
    private static async Task<Dictionary<string, JsonObject>> Stored(HttpClient http, Tokens t, string oneCId, string table)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v2/entity?oneCId={oneCId}&pageSize=1000");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t.Token);
        var page = JsonNode.Parse(await (await http.SendAsync(req)).Content.ReadAsStringAsync())!;
        return page["results"]!.AsArray().OfType<JsonObject>().Where(i => (string)i["tableName"]! == table)
            .ToDictionary(i => (string)i["rawData"]!["k"]!, i => (JsonObject)i["rawData"]!);
    }

    [Fact]
    public async Task RowsDeletesAndReconcilesBehaveAsTheContractSays()
    {
        if (Gate.Skip(Url is null, "AIBA_SYNC_BACKEND not set: the isolated local backend/1c is required (scratchpad s12/start-backend.ps1)")) return;
        var (http, id, tokens) = await Connection();
        var target = new PythonMongoSyncTarget(http, tokens, id);
        const string cat = "Catalog_SyncTest", reg = "AccumulationRegister_SyncTest";

        // Upload, re-upload: idempotent by key.
        var rows = Enumerable.Range(1, 30).Select(i => Row(Guid.NewGuid().ToString(), $"{{\"id\":\"x{i}\",\"n\":{i}}}")).ToList();
        Assert.True((await target.UploadRowsAsync(new UploadBatch("b1", id, cat, rows), Ct)).Ok);
        Assert.True((await target.UploadRowsAsync(new UploadBatch("b2", id, cat, rows), Ct)).Ok);
        var stored = await Stored(http, tokens, id, cat);
        Assert.Equal(30, stored.Count);
        Assert.Equal(30, (await target.CountsAsync(id, Ct))[cat]);

        // A changed row on the same key is overwritten, not duplicated.
        var changed = Row(rows[0].Key, "{\"id\":\"x1\",\"n\":100}");
        await target.UploadRowsAsync(new UploadBatch("b3", id, cat, new[] { changed }), Ct);
        stored = await Stored(http, tokens, id, cat);
        Assert.Equal(30, stored.Count);
        Assert.Equal(100, (int)stored[rows[0].Key]["n"]!);

        // v2 quirk, pinned: two rows with one key in a batch — the backend keeps one, and its totals still match.
        string dup = Guid.NewGuid().ToString();
        var r = await target.UploadRowsAsync(new UploadBatch("b4", id, cat, new[] { Row(dup, "{\"id\":\"d\",\"n\":1}"), Row(dup, "{\"id\":\"d\",\"n\":2}") }), Ct);
        Assert.True(r.Ok);
        Assert.Equal(2, r.ReportedCount);                                              // "all stored"…
        Assert.Equal(31, (await Stored(http, tokens, id, cat)).Count);                 // …one was

        // Deletes: within the cap; over the cap refused as Policy; approved goes in cap-sized calls.
        var del = await target.DeleteRowsAsync(new DeleteBatch("d1", id, cat, new[] { rows[1].Key }, "test"), Ct);
        Assert.Equal((Outcome.Ok, 1), (del.Outcome, del.Deleted));
        var over = await target.DeleteRowsAsync(new DeleteBatch("d2", id, cat, rows.Skip(2).Take(10).Select(x => x.Key).ToList(), "test"), Ct);
        Assert.Equal(Outcome.Policy, over.Outcome);
        Assert.Equal(30, (await Stored(http, tokens, id, cat)).Count);
        var approved = await target.DeleteRowsAsync(new DeleteBatch("d3", id, cat, rows.Skip(2).Take(10).Select(x => x.Key).ToList(), "test", Approved: true), Ct);
        Assert.Equal((Outcome.Ok, 10), (approved.Outcome, approved.Deleted));
        Assert.Equal(20, (await Stored(http, tokens, id, cat)).Count);
        var empty = await target.DeleteRowsAsync(new DeleteBatch("d4", id, "Catalog_Empty", new[] { "k" }, "test"), Ct);
        Assert.Equal((Outcome.Ok, 0), (empty.Outcome, empty.Deleted));               // backend refuses (nothing stored) = already gone

        // Reconcile: one recorder's rows not in the live set go; other recorders stay.
        string rec = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString();
        var moves = new[] { Row(rec + "#1", "{\"s\":1}"), Row(rec + "#2", "{\"s\":2}"), Row(rec + "#3", "{\"s\":3}"), Row(other + "#1", "{\"s\":9}") };
        await target.UploadRowsAsync(new UploadBatch("m1", id, reg, moves), Ct);
        var rc = await target.ReconcileRecorderAsync(new RecorderReconcile("r1", id, reg, rec, new[] { rec + "#1" }), Ct);
        Assert.Equal((Outcome.Ok, 2), (rc.Outcome, rc.Removed));
        Assert.Equal(new[] { other + "#1", rec + "#1" }.Order(), (await Stored(http, tokens, id, reg)).Keys.Order());
        Assert.Equal(1, (await target.ReconcileRecorderAsync(new RecorderReconcile("r2", id, reg, rec, Array.Empty<string>()), Ct)).Removed);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefreshedOnceAndLostAuthIsReportedAsAuth()
    {
        if (Gate.Skip(Url is null, "AIBA_SYNC_BACKEND not set: the isolated local backend/1c is required (scratchpad s12/start-backend.ps1)")) return;
        var (http, id, tokens) = await Connection();
        var (jwt, _) = Secrets();
        tokens.Token = Mint(jwt, UserId, TimeSpan.FromSeconds(-60));                  // expired
        tokens.OnRefresh = () => Mint(jwt, UserId, TimeSpan.FromHours(1));
        var target = new PythonMongoSyncTarget(http, tokens, id);
        Assert.True((await target.UploadRowsAsync(new UploadBatch("a1", id, "Catalog_Auth", new[] { Row(Guid.NewGuid().ToString(), "{\"n\":1}") }), Ct)).Ok);
        Assert.Equal(1, tokens.Refreshes);

        tokens.Token = Mint("not-the-secret", UserId, TimeSpan.FromHours(1));
        tokens.OnRefresh = null;                                                        // refresh refused: signed out
        var lost = await target.UploadRowsAsync(new UploadBatch("a2", id, "Catalog_Auth", new[] { Row(Guid.NewGuid().ToString(), "{\"n\":1}") }), Ct);
        Assert.Equal(Outcome.Auth, lost.Outcome);
    }

    [Fact]
    public void HttpStatusesMapToTheCanonicalOutcomes()
    {
        Assert.Equal(Outcome.Validation, PythonMongoSyncTarget.Classify(HttpStatusCode.BadRequest, "bad").Outcome);
        Assert.Equal(Outcome.Auth, PythonMongoSyncTarget.Classify(HttpStatusCode.Forbidden, "no").Outcome);
        Assert.Equal(Outcome.Gone, PythonMongoSyncTarget.Classify(HttpStatusCode.Conflict, "{\"detail\":\"This 1C connection is being deleted\"}").Outcome);
        Assert.Equal(Outcome.Policy, PythonMongoSyncTarget.Classify(HttpStatusCode.Conflict, "over the 5% cap").Outcome);
        Assert.Equal(Outcome.RateLimited, PythonMongoSyncTarget.Classify((HttpStatusCode)429, "", TimeSpan.FromSeconds(3)).Outcome);
        Assert.Equal(Outcome.Transient, PythonMongoSyncTarget.Classify(HttpStatusCode.BadGateway, "").Outcome);
        // get_onec_with_ownership's 400 (also on a failed Mongo lookup): retried, never a dead letter.
        Assert.Equal(Outcome.Transient, PythonMongoSyncTarget.Classify(HttpStatusCode.BadRequest, "{\"detail\":\"OneC connection not found\"}").Outcome);
        Assert.Equal(Outcome.Transient, PythonMongoSyncTarget.Classify(0, "connection refused").Outcome);
        Assert.True(PythonMongoSyncTarget.IsMovementTable("InformationRegister_КурсыВалют"));   // backend's rule, not the engine's wish
        Assert.False(PythonMongoSyncTarget.IsMovementTable("Catalog_Контрагенты"));
    }
}
