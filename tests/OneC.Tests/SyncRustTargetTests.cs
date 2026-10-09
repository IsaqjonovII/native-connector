using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;
using OneC.Sync.Targets.Rust;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// R1/R2 (PYTHON_TO_RUST_MIGRATION_PLAN): the Rust target against the real Rust onec module — an
/// ISOLATED LOCAL instance (loopback, its own Postgres cluster and database, test secrets made for
/// it). Runs only with AIBA_RUST_SYNC_SECRETS = a JSON file {url, service}. Every claim is checked by
/// reading the stored rows back, never by trusting an answer's numbers.
/// </summary>
public sealed class SyncRustTargetTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string? SecretsFile = Environment.GetEnvironmentVariable("AIBA_RUST_SYNC_SECRETS");
    private const string Cat = "Catalog_Номенклатура";
    private const string Doc = "Document_РеализацияТоваровУслуг";
    private const string Acc = "AccountingRegister_Хозрасчетный_RecordType";
    private const string Acc2 = "AccumulationRegister_ТоварыНаСкладах_RecordType";

    private static string G() => Guid.NewGuid().ToString("D");
    private static string B() => Guid.NewGuid().ToString("N");
    private static SyncRow Row(string key, string json, long? v = null) => new(key, v, Encoding.UTF8.GetBytes(json));

    private sealed record Local(HttpClient Http, string Service, RustSyncTarget Target, string Connection);

    private static async Task<Local?> ConnectAsync()
    {
        if (string.IsNullOrEmpty(SecretsFile)) return null;
        var s = JsonNode.Parse(await File.ReadAllTextAsync(SecretsFile))!;
        string url = (string)s["url"]!, service = (string)s["service"]!;
        if (!new Uri(url).IsLoopback) throw new InvalidOperationException("the Rust sync tests run against a loopback instance only");
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        string conn = await CreateConnectionAsync(http, service, Random.Shared.Next(100_000, 900_000));
        return new Local(http, service, new RustSyncTarget(http, new ServiceSecretCredential(service), conn), conn);
    }

    private static async Task<string> CreateConnectionAsync(HttpClient http, string service, int companyId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/onec")
        {
            Content = JsonContent.Create(new { companyId, provider = "unisoft", name = "AIBA_REWRITE_rust_contract", odataName = "rusttest-" + B()[..8] })
        };
        req.Headers.Add("X-Service-Secret", service);
        var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, text);
        return (string)JsonNode.Parse(text)!["id"]!;
    }

    /// <summary>Two organisations bound to two companies on the connection; returns (orgA, bindingA, orgB, bindingB).</summary>
    private static async Task<(string OrgA, string PartA, string OrgB, string PartB)> BindTwoOrgsAsync(Local l)
    {
        string orgA = G(), orgB = G();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"api/v2/onec/{l.Connection}/org-bindings")
        {
            Content = JsonContent.Create(new
            {
                bindings = new[]
                {
                    new { orgRef = orgA, companyId = Random.Shared.Next(100_000, 900_000).ToString() },
                    new { orgRef = orgB, companyId = Random.Shared.Next(100_000, 900_000).ToString() }
                }
            })
        };
        put.Headers.Add("X-Service-Secret", l.Service);
        var resp = await l.Http.SendAsync(put);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var cfg = await l.Target.GetSyncConfigAsync(l.Connection, Ct);
        return (orgA, cfg.Partitions.Single(p => p.OrgRef == orgA).PartitionId, orgB, cfg.Partitions.Single(p => p.OrgRef == orgB).PartitionId);
    }

    private static async Task<Dictionary<string, StoredRow>> StoredAsync(Local l, string partition, string table)
    {
        var d = new Dictionary<string, StoredRow>(StringComparer.Ordinal);
        await foreach (var r in l.Target.ReadRowsAsync(partition, table, ct: Ct)) d.Add(r.Key, r);
        return d;
    }

    /// <summary>A failed command's answer carries the host's error as an OBJECT: reading it must not throw (R9, 2026-10-08).</summary>
    [Fact]
    public void AnAnswerWhoseErrorIsAnObjectIsClassifiedNotThrown()
    {
        var ok = RustSyncTarget.Classify(HttpStatusCode.OK, JsonNode.Parse("{\"state\":\"failed\",\"error\":{\"layer\":\"Runtime\",\"message\":\"x\"}}"), "");
        Assert.True(ok.Ok);
        var bad = RustSyncTarget.Classify(HttpStatusCode.BadRequest, JsonNode.Parse("{\"error\":\"command_refused\",\"detail\":\"no\"}"), "");
        Assert.Equal((Outcome.Validation, "command_refused: no"), (bad.Outcome, bad.Message));
        var odd = RustSyncTarget.Classify(HttpStatusCode.BadRequest, JsonNode.Parse("{\"error\":{\"a\":1},\"detail\":[1]}"), "raw");
        Assert.Equal(Outcome.Validation, odd.Outcome);
    }

    [Fact]
    public async Task EveryRowGetsItsOwnOutcomeAndARetryIsReplayed()
    {
        if (await ConnectAsync() is not { } l) return;
        string a = G(), b = G(), c = G();
        var first = await l.Target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Cat,
            new[] { Row(a, $"{{\"id\":\"{a}\",\"__rowKey\":\"{a}\",\"n\":1}}", 5), Row(b, $"{{\"id\":\"{b}\",\"n\":2}}", 5) }), Ct);
        Assert.Equal((Outcome.Ok, 2), (first.Outcome, first.Applied));

        string id = B();
        var batch = new UploadBatch(id, l.Connection, Cat, new[]
        {
            Row(a, $"{{\"id\":\"{a}\",\"n\":1}}", 6),          // same content → unchanged
            Row(b, $"{{\"id\":\"{b}\",\"n\":3}}", 4),          // older version → stale, not written
            Row(c, $"{{\"id\":\"{c}\",\"n\":4}}", 1),          // new → inserted
            Row(c.ToUpperInvariant(), "{}", 1)                // not canonical → rejected
        });
        var r = await l.Target.UploadRowsAsync(batch, Ct);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Applied);
        Assert.Equal(new[] { b }, r.Stale);
        Assert.Equal(("guid_not_lowercase", false), (Assert.Single(r.Rejected).Reason, r.Rejected[0].Retryable));

        var stored = await StoredAsync(l, l.Connection, Cat);
        Assert.Equal(new[] { a, b, c }.Order(StringComparer.Ordinal), stored.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, (int)stored[b].Data!["n"]!);                         // the newer row survived
        Assert.Equal(6, stored[a].Version);                                  // unchanged, version raised
        Assert.Null(stored[a].Data!["__rowKey"]);                            // stored as the legacy readers expect

        var replay = await l.Target.UploadRowsAsync(batch, Ct);              // the same batch again: the stored answer
        Assert.Equal((r.Applied, r.Stale.Count, r.Rejected.Count), (replay.Applied, replay.Stale.Count, replay.Rejected.Count));
        var reused = await l.Target.UploadRowsAsync(batch with { Rows = new[] { Row(G(), "{}") } }, Ct);
        Assert.Equal(Outcome.Validation, reused.Outcome);                    // a batch id never means two requests
        Assert.Contains("batch_id_reused", reused.Message);
        Assert.Equal(3, (await l.Target.CountsAsync(l.Connection, Ct, Cat))[Cat]);
    }

    [Fact]
    public async Task ADuplicateKeyRefusesTheWholeBatch()
    {
        if (await ConnectAsync() is not { } l) return;
        string a = G();
        var r = await l.Target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Cat, new[] { Row(a, "{\"v\":1}"), Row(a, "{\"v\":2}") }), Ct);
        Assert.Equal(Outcome.Validation, r.Outcome);
        Assert.Contains(a, r.Message);
        Assert.Empty(await StoredAsync(l, l.Connection, Cat));
    }

    [Fact]
    public async Task TheAtomicRecorderReplacesMovementsAndRefusesAStaleRetry()
    {
        if (await ConnectAsync() is not { } l) return;
        string d = G(), p = l.Connection;
        RecorderSync Unit(long v, string? docJson, params PartitionMovements[] m) =>
            new(B(), d, v, Doc, docJson is null ? null : p, docJson is null ? null : Row(d, docJson, v), new[] { p }, new[] { Acc, Acc2 }, m);
        PartitionMovements M(string table, params int[] lines) =>
            new(p, table, lines.Select(n => Row($"{d}#{n}", $"{{\"recorderRef\":\"{d}\",\"lineNo\":{n},\"sum\":{n * 10}}}", 1)).ToList());

        var post = await l.Target.SyncRecorderAtomicAsync(Unit(1, $"{{\"id\":\"{d}\",\"posted\":true}}", M(Acc, 1, 2, 3), M(Acc2, 1)), Ct);
        Assert.True(post.Ok, post.Message);
        Assert.Equal((4, 0), (post.MovementsWritten, post.MovementsRemoved));

        // Repost with fewer lines and a changed amount: line 3 and the Acc2 line go, line 1 changes.
        var repost = await l.Target.SyncRecorderAtomicAsync(Unit(2, $"{{\"id\":\"{d}\",\"posted\":true}}",
            new PartitionMovements(p, Acc, new[] { Row($"{d}#1", $"{{\"recorderRef\":\"{d}\",\"lineNo\":1,\"sum\":15}}", 2), Row($"{d}#2", $"{{\"recorderRef\":\"{d}\",\"lineNo\":2,\"sum\":20}}", 2) })), Ct);
        Assert.True(repost.Ok, repost.Message);
        Assert.Equal((1, 2), (repost.MovementsWritten, repost.MovementsRemoved));
        var acc = await StoredAsync(l, p, Acc);
        Assert.Equal(new[] { $"{d}#1", $"{d}#2" }, acc.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(15, (int)acc[$"{d}#1"].Data!["sum"]!);
        Assert.Empty(await StoredAsync(l, p, Acc2));

        // A late retry of the first version changes nothing.
        var stale = await l.Target.SyncRecorderAtomicAsync(Unit(1, $"{{\"id\":\"{d}\",\"posted\":false}}", M(Acc, 1, 2, 3)), Ct);
        Assert.True(stale.StaleDocument);
        Assert.Equal(2, (await StoredAsync(l, p, Acc)).Count);
        Assert.True((bool)(await StoredAsync(l, p, Doc))[d].Data!["posted"]!);

        // Unpost: the document stays, its movements go.
        var unpost = await l.Target.SyncRecorderAtomicAsync(Unit(3, $"{{\"id\":\"{d}\",\"posted\":false}}"), Ct);
        Assert.Equal(2, unpost.MovementsRemoved);
        Assert.Empty(await StoredAsync(l, p, Acc));

        // Deleted in 1C: everything of the recorder goes.
        var gone = await l.Target.SyncRecorderAtomicAsync(Unit(4, null), Ct);
        Assert.True(gone.Ok, gone.Message);
        Assert.Empty(await StoredAsync(l, p, Doc));
        var counts = await l.Target.CountsAsync(p, Ct, Doc, Acc, Acc2);
        Assert.Equal(0, counts[Doc] + counts[Acc] + counts[Acc2]);
    }

    [Fact]
    public async Task ADocumentChangingOrganisationMovesInOneTransactionAndTouchesNothingElse()
    {
        if (await ConnectAsync() is not { } l) return;
        var (orgA, partA, orgB, partB) = await BindTwoOrgsAsync(l);
        string d = G(), other = G();
        string DocJson(string org) => $"{{\"id\":\"{d}\",\"orgRef\":\"{org}\"}}";
        SyncRow Line(string rec, string org, int n) => Row($"{rec}#{n}", $"{{\"recorderRef\":\"{rec}\",\"lineNo\":{n},\"orgRef\":\"{org}\"}}", 1);
        var all = new[] { l.Connection, partA, partB };

        Assert.True((await l.Target.SyncRecorderAtomicAsync(new RecorderSync(B(), d, 1, Doc, partA, Row(d, DocJson(orgA), 1), all, new[] { Acc },
            new[] { new PartitionMovements(partA, Acc, new[] { Line(d, orgA, 1), Line(d, orgA, 2) }) }), Ct)).Ok);
        Assert.True((await l.Target.SyncRecorderAtomicAsync(new RecorderSync(B(), other, 1, Doc, partA, Row(other, $"{{\"id\":\"{other}\",\"orgRef\":\"{orgA}\"}}", 1),
            all, new[] { Acc }, new[] { new PartitionMovements(partA, Acc, new[] { Line(other, orgA, 1) }) }), Ct)).Ok);

        // The organisation changed in 1C: the document and its lines now belong to B.
        var moved = await l.Target.SyncRecorderAtomicAsync(new RecorderSync(B(), d, 2, Doc, partB, Row(d, DocJson(orgB), 2), all, new[] { Acc },
            new[] { new PartitionMovements(partB, Acc, new[] { Line(d, orgB, 1) }) }), Ct);
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(new[] { other }, (await StoredAsync(l, partA, Doc)).Keys);
        Assert.Equal(new[] { $"{other}#1" }, (await StoredAsync(l, partA, Acc)).Keys);       // the other recorder untouched
        Assert.Equal(new[] { d }, (await StoredAsync(l, partB, Doc)).Keys);
        Assert.Equal(new[] { $"{d}#1" }, (await StoredAsync(l, partB, Acc)).Keys);
        Assert.Empty(await StoredAsync(l, l.Connection, Doc));
    }

    [Fact]
    public async Task TheServerRefusesRowsFiledUnderTheWrongOrganisationAndAppliesNothingOfTheUnit()
    {
        if (await ConnectAsync() is not { } l) return;
        var (orgA, partA, orgB, partB) = await BindTwoOrgsAsync(l);
        string d = G();
        // A's row sent to B's partition, and a bound organisation's document into the shared partition.
        var wrongOrg = await l.Target.UploadRowsAsync(new UploadBatch(B(), partB, Doc, new[] { Row(d, $"{{\"id\":\"{d}\",\"orgRef\":\"{orgA}\"}}") }), Ct);
        Assert.Equal("org_mismatch", Assert.Single(wrongOrg.Rejected).Reason);
        var toShared = await l.Target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Doc, new[] { Row(d, $"{{\"id\":\"{d}\",\"orgRef\":\"{orgA}\"}}") }), Ct);
        Assert.Equal("org_bound_elsewhere", Assert.Single(toShared.Rejected).Reason);
        var catalogInOrg = await l.Target.UploadRowsAsync(new UploadBatch(B(), partA, Cat, new[] { Row(d, "{}") }), Ct);
        Assert.Equal("reference_in_org_partition", Assert.Single(catalogInOrg.Rejected).Reason);

        // In a recorder unit one misfiled line refuses the whole unit: nothing half-applied.
        var unit = await l.Target.SyncRecorderAtomicAsync(new RecorderSync(B(), d, 1, Doc, partA, Row(d, $"{{\"id\":\"{d}\",\"orgRef\":\"{orgA}\"}}", 1),
            new[] { partA, partB }, new[] { Acc }, new[]
            {
                new PartitionMovements(partA, Acc, new[] { Row($"{d}#1", $"{{\"orgRef\":\"{orgA}\"}}") }),
                new PartitionMovements(partB, Acc, new[] { Row($"{d}#2", $"{{\"orgRef\":\"{orgA}\"}}") })
            }), Ct);
        Assert.Equal(Outcome.Validation, unit.Outcome);
        Assert.Contains("org_mismatch", unit.Message);
        Assert.Empty(await StoredAsync(l, partA, Doc));
        Assert.Empty(await StoredAsync(l, partA, Acc));

        // A movement outside the named scope is refused before anything runs.
        var outside = await l.Target.SyncRecorderAtomicAsync(new RecorderSync(B(), d, 1, Doc, null, null, new[] { partA }, new[] { Acc },
            new[] { new PartitionMovements(partB, Acc, new[] { Row($"{d}#1", $"{{\"orgRef\":\"{orgB}\"}}") }) }), Ct);
        Assert.Equal(Outcome.Validation, outside.Outcome);
        Assert.Contains("out_of_scope", outside.Message);
        // A partition of another connection is refused.
        var foreign = await l.Target.UploadRowsAsync(new UploadBatch(B(), "999999999", Cat, new[] { Row(G(), "{}") }), Ct);
        Assert.Equal(Outcome.Validation, foreign.Outcome);
        Assert.Contains("partition_not_in_connection", foreign.Message);
    }

    [Fact]
    public async Task DeletesAreExplicitIdempotentAndUncappedAndPurgeEmptiesOneTable()
    {
        if (await ConnectAsync() is not { } l) return;
        var keys = Enumerable.Range(0, 50).Select(_ => G()).ToList();
        Assert.True((await l.Target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Cat, keys.Select(k => Row(k, $"{{\"id\":\"{k}\"}}")).ToList()), Ct)).Ok);
        Assert.True((await l.Target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Doc, new[] { Row(keys[0], "{}") }), Ct)).Ok);

        var del = await l.Target.DeleteRowsAsync(new DeleteBatch(B(), l.Connection, Cat, keys.Take(40).Append(G()).ToList(), "deleted in 1C"), Ct);
        Assert.Equal((Outcome.Ok, 40), (del.Outcome, del.Deleted));               // 80 % of the table: no cap, explicit keys
        var again = await l.Target.DeleteRowsAsync(new DeleteBatch(B(), l.Connection, Cat, keys.Take(40).ToList(), "deleted in 1C"), Ct);
        Assert.Equal((Outcome.Ok, 0), (again.Outcome, again.Deleted));
        Assert.Equal(10, (await l.Target.CountsAsync(l.Connection, Ct, Cat))[Cat]);

        var purge = await l.Target.PurgeTableAsync(l.Connection, Cat, Ct);
        Assert.Equal((Outcome.Ok, 10), (purge.Outcome, purge.Deleted));
        var counts = await l.Target.CountsAsync(l.Connection, Ct, Cat, Doc);
        Assert.Equal((0L, 1L), (counts[Cat], counts[Doc]));                      // only that table
    }

    [Fact]
    public async Task CoverageMergesAndConfigKeepsTheEmptyList()
    {
        if (await ConnectAsync() is not { } l) return;
        Assert.True((await l.Target.PutCoverageAsync(l.Connection, new[] { (Doc, (DateTime?)new DateTime(2025, 1, 1), true) }, Ct)).Ok);
        Assert.True((await l.Target.PutCoverageAsync(l.Connection, new[] { (Acc, (DateTime?)null, false) }, Ct)).Ok);
        var cov = await l.Target.GetCoverageAsync(l.Connection, Ct);
        Assert.Equal("2025-01-01", (string?)cov[Doc]!["dataFrom"]);
        Assert.True((bool)cov[Doc]!["complete"]!);
        Assert.False((bool)cov[Acc]!["complete"]!);                             // the second write did not erase the first

        Assert.False((await l.Target.GetSyncConfigAsync(l.Connection, Ct)).TableListStored);   // never set
        using var put = new HttpRequestMessage(HttpMethod.Put, $"api/v1/onec/{l.Connection}/sync-tables") { Content = JsonContent.Create(new { tables = Array.Empty<object>() }) };
        put.Headers.Add("X-Service-Secret", l.Service);
        Assert.True((await l.Http.SendAsync(put)).IsSuccessStatusCode);
        var cfg = await l.Target.GetSyncConfigAsync(l.Connection, Ct);
        Assert.True(cfg.TableListStored);                                        // deliberately nothing, not "unset"
        Assert.Empty(cfg.Tables);
        Assert.True((await l.Target.ReportStatusAsync(new BaseStatus(l.Connection, "running", 10, 50, null), Ct)).Ok);
        Assert.False((await l.Target.GetPresenceAsync(l.Connection, Ct)).OtherConnectorOnline);   // no old Connector socket
    }

    [Fact]
    public async Task ABigBatchIsSplitAndEveryRowIsStored()
    {
        if (await ConnectAsync() is not { } l) return;
        var target = new RustSyncTarget(l.Http, new ServiceSecretCredential(l.Service), l.Connection) { MaxPartBytes = 64 * 1024 };
        string pad = new('x', 900);
        var rows = Enumerable.Range(0, 1500).Select(_ => G()).Select(k => Row(k, $"{{\"id\":\"{k}\",\"pad\":\"{pad}\"}}")).ToList();
        var r = await target.UploadRowsAsync(new UploadBatch(B(), l.Connection, Cat, rows), Ct);
        Assert.Equal((Outcome.Ok, 1500), (r.Outcome, r.Applied));
        Assert.Equal(1500, (await StoredAsync(l, l.Connection, Cat)).Count);
    }

    [Fact]
    public async Task TheStatusHeartbeatPutsTheBaseAndItsBindingsOnlineAndStopTakesThemOff()
    {
        if (await ConnectAsync() is not { } l) return;
        var (_, partA, _, partB) = await BindTwoOrgsAsync(l);
        async Task<string?> StatusOf(string id)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"api/internal/admin/onecs/{id}");
            req.Headers.Add("X-Service-Secret", l.Service);
            var resp = await l.Http.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, text);
            return (string?)JsonNode.Parse(text)!["status"];
        }
        Assert.True((await l.Target.ReportStatusAsync(new BaseStatus(l.Connection, "active", null, null, null), Ct)).Ok);
        foreach (var id in new[] { l.Connection, partA, partB }) Assert.Equal("active", await StatusOf(id));
        Assert.True((await l.Target.ReportStatusAsync(new BaseStatus(l.Connection, "stopped", null, null, null), Ct)).Ok);
        foreach (var id in new[] { l.Connection, partA, partB }) Assert.Equal("inactive", await StatusOf(id));
    }

    [Fact]
    public async Task OnlyTheHolderOfTheCurrentLeaseReportsACommandsOutcome()
    {
        if (await ConnectAsync() is not { } l) return;
        var payload = new JsonObject { ["docType"] = "ПоступлениеТоваровУслуг", ["ref"] = G() };
        var (e, id, _) = await l.Target.EnqueueCommandAsync("document.post", "lease-" + B(), l.Connection, payload, Ct);
        Assert.True(e.Ok, e.Message);
        var first = Assert.Single(await l.Target.LeaseCommandsAsync(1, 30, Ct));
        Assert.Equal(id, first.CommandId);
        Assert.Matches("^[0-9a-f]{64}$", first.LeaseToken);

        // Anyone else with access to the connection: no token, a made-up one — refused, nothing changes.
        foreach (var token in new[] { "", new string('0', 64) })
        {
            var forged = await l.Target.ReportCommandAsync(first with { LeaseToken = token }, true, new JsonObject { ["posted"] = true }, null, Ct);
            Assert.False(forged.Ok);
            Assert.Contains("lease_not_held", forged.Message);
        }
        Assert.Equal("dispatched", (string?)(await l.Target.GetCommandAsync(id!, Ct))!["state"]);

        // The holder reports; the same report again is a replay; a forged one after it is still refused.
        var ok = await l.Target.ReportCommandAsync(first, false, null, new JsonObject { ["message"] = "test" }, Ct);
        Assert.True(ok.Ok, ok.Message);
        Assert.True((await l.Target.ReportCommandAsync(first, false, null, new JsonObject { ["message"] = "test" }, Ct)).Ok);
        Assert.False((await l.Target.ReportCommandAsync(first with { LeaseToken = new string('1', 64) }, false, null, null, Ct)).Ok);
        Assert.Equal("failed", (string?)(await l.Target.GetCommandAsync(id!, Ct))!["state"]);
    }
}
