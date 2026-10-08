using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;

namespace OneC.Sync.Targets.Python;

/// <summary>The caller's credential for backend/1c: a user JWT, refreshed once on 401 (the desktop's CloudClient).</summary>
public interface ITokenSource
{
    Task<string> GetAsync(CancellationToken ct);
    /// <summary>Called after a 401: true when a new token is available for one retry.</summary>
    Task<bool> RefreshAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IBackendSyncTarget"/> over backend/1c's current routes (§18) — what the old Connector
/// calls, so the backend changes nothing:
/// <list type="bullet">
/// <item>rows: <c>POST /api/v2/entity/upload</c> multipart (<c>oneCId</c> + a JSON file
/// <c>{table: [rows]}</c> with a filename; no gzip), <c>__rowKey</c> in every row; 413 splits;</item>
/// <item>deletes: <c>POST /api/v1/onec/{id}/prune-missing</c> — the backend caps one call at
/// max(1, 5 %) of the stored rows and refuses an empty table (409 → <see cref="Outcome.Policy"/>);</item>
/// <item>movements: <c>POST /api/v1/onec/{id}/reconcile-recorder</c> with the full live key set;</item>
/// <item>config: <c>GET /api/v2/onec/{id}/org-bindings</c> + <c>GET /api/v1/onec/{id}/sync-tables</c>;</item>
/// <item>status: <c>PATCH /api/v2/onec/connection/{id}</c>.</item>
/// </list>
/// A partition is a backend oneCId: the connection's own (shared) or an organisation binding's.
/// Deletes and reconciles on the shared id use <c>scope=connection</c> (a row may sit in any
/// partition of the connection), on a binding's id <c>scope=self</c>.
/// <para><b>Known v2 limits</b> (not fixable here, §18): no per-row results; a matching reported
/// count is not proof of storage (its totalSkipped is defined as totalItems − totalInserted);
/// concurrent writers lose silently; no transaction between a document and its movements.</para>
/// </summary>
public sealed class PythonMongoSyncTarget(HttpClient http, ITokenSource tokens, string connectionId) : IBackendSyncTarget, ICoverageTarget
{
    /// <summary>backend/1c <c>POST /api/v2/entity/data-coverage</c> (v2 entity.py:614): merged per table into <c>OneC.dataCoverage</c>.</summary>
    public async Task<TargetResult> ReportCoverageAsync(string partitionId, IReadOnlyList<TableCoverage> tables, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["oneCId"] = partitionId,
            ["tables"] = new JsonArray(tables.Select(t => (JsonNode)new JsonObject
            {
                ["table"] = t.Table, ["dataFrom"] = t.DataFrom?.ToString("yyyy-MM-dd"), ["complete"] = t.Complete
            }).ToArray())
        };
        var (r, _) = await SendJsonAsync(HttpMethod.Post, "api/v2/entity/data-coverage", body, ct);
        return ForPartition(r, partitionId);
    }

    public string Kind => "python-v2";
    public int MaxBatchBytes { get; init; } = 34 * 1024 * 1024;

    public Task<TargetCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new TargetCapabilities(
        PerRowResults: false, AtomicRecorder: false, SourceVersionGuard: false, MaxBatchBytes: MaxBatchBytes, MaxBatchRows: 10_000,
        DeleteCapFraction: 0.05, Gzip: false, DuplicateKeys: DuplicateKeyPolicy.KeepFirstSilently, ReportsTrustworthyCounts: false));

    public async Task<SyncConfig> GetSyncConfigAsync(string connection, CancellationToken ct)
    {
        var (bs, bindings) = await SendJsonAsync(HttpMethod.Get, $"api/v2/onec/{E(connection)}/org-bindings", null, ct);
        if (bs.Outcome != Outcome.Ok) throw new InvalidOperationException($"org-bindings: {bs.Outcome} {bs.Message}");
        var (ts, tables) = await SendJsonAsync(HttpMethod.Get, $"api/v1/onec/{E(connection)}/sync-tables", null, ct);
        if (ts.Outcome != Outcome.Ok) throw new InvalidOperationException($"sync-tables: {ts.Outcome} {ts.Message}");
        var partitions = (bindings?["bindings"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(b => (string?)b["status"] != "deleting")
            .Select(b => new SyncPartition((string)b["id"]!, (string?)b["orgRef"], b["companyId"]?.ToString())).ToList();
        // backend/1c v1 onec.py get_sync_tables: {"tables": [{"table": …, "reports": …}]} or {"tables": null}
        // when nothing is stored yet (older answers used plain strings; both are read).
        var stored = tables?["tables"] as JsonArray;
        var list = stored?.Select(n => n switch
        {
            JsonObject o => (string?)o["table"],
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => null
        }).OfType<string>().ToList() ?? new List<string>();
        return new SyncConfig(connection, null, connection, partitions,
            list.Select(t => new SyncTableConfig(t, "", "", IsMovementTable(t))).ToList()) { TableListStored = stored is not null };
    }

    /// <summary>backend/1c <c>app/utils/onec_scope.py::is_movement_table</c>, ported: the engine must route exactly as the backend reads.</summary>
    public static bool IsMovementTable(string table) =>
        table == "stock" || table.StartsWith("Document_", StringComparison.Ordinal) || table.StartsWith("AccountingRegister_", StringComparison.Ordinal) ||
        table.StartsWith("AccumulationRegister_", StringComparison.Ordinal) || table.StartsWith("InformationRegister_", StringComparison.Ordinal);

    public async Task<UploadResult> UploadRowsAsync(UploadBatch batch, CancellationToken ct)
    {
        long sent = 0, reported = 0;
        foreach (var part in Split(batch.Rows))
        {
            var (r, n) = await UploadPartAsync(batch.PartitionId, batch.Table, part, 0, ct);
            if (!r.Ok) return UploadResult.Failed(r.Outcome, r.HttpStatus, r.Message, r.RetryAfter);
            sent += part.Count;
            reported += n;
        }
        return new UploadResult(Outcome.Ok, (int)sent, Array.Empty<string>(), Array.Empty<RowRejection>(), ReportedCount: reported);
    }

    private IEnumerable<List<SyncRow>> Split(IReadOnlyList<SyncRow> rows)
    {
        var part = new List<SyncRow>();
        long bytes = 0;
        foreach (var r in rows)
        {
            if (part.Count > 0 && bytes + r.Json.Length + 1 > MaxBatchBytes) { yield return part; part = new(); bytes = 0; }
            part.Add(r);
            bytes += r.Json.Length + 1;
        }
        if (part.Count > 0) yield return part;
    }

    private async Task<(TargetResult Result, long Reported)> UploadPartAsync(string oneCId, string table, List<SyncRow> rows, int depth, CancellationToken ct)
    {
        // One buffer of exactly the body's size, sent as is (no growth copies, no ToArray: up to ~3×
        // an 8 MB batch before, 2026-10-01 review).
        byte[] head = Encoding.UTF8.GetBytes("{" + JsonSerializer.Serialize(table) + ":[");
        int size = head.Length + 2 + rows.Sum(r => r.Json.Length) + Math.Max(0, rows.Count - 1);
        var file = new MemoryStream(size);
        file.Write(head);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) file.WriteByte((byte)',');
            file.Write(rows[i].Json);
        }
        file.Write("]}"u8);
        byte[] buffer = file.GetBuffer();
        int length = (int)file.Length;
        var (status, json, text, retryAfter) = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent { { new StringContent(oneCId), "oneCId" } };
            var part = new ByteArrayContent(buffer, 0, length);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(part, "file", "chunk.json");                       // without a filename Starlette refuses parts over 1 MB
            return new HttpRequestMessage(HttpMethod.Post, "api/v2/entity/upload") { Content = form };
        }, ct);
        if (status == HttpStatusCode.RequestEntityTooLarge && rows.Count > 1 && depth < 10)
        {
            int half = rows.Count / 2;
            var a = await UploadPartAsync(oneCId, table, rows.GetRange(0, half), depth + 1, ct);
            if (!a.Result.Ok) return a;
            var b = await UploadPartAsync(oneCId, table, rows.GetRange(half, rows.Count - half), depth + 1, ct);
            return b.Result.Ok ? (b.Result, a.Reported + b.Reported) : b;
        }
        // One row over the edge's body limit: splitting cannot help, retrying never will — a dead letter.
        var r = status == HttpStatusCode.RequestEntityTooLarge && rows.Count <= 1
            ? new TargetResult(Outcome.Validation, 413, $"one {table} row ({length:N0} bytes) is larger than the server accepts")
            : ForPartition(Classify(status, text, retryAfter), oneCId);
        long reported = json is JsonObject o ? Long(o["totalInserted"]) + Long(o["totalSkipped"]) : 0;
        return (r, reported);
    }

    public async Task<DeleteResult> DeleteRowsAsync(DeleteBatch b, CancellationToken ct)
    {
        if (b.Keys.Count == 0) return new DeleteResult(Outcome.Ok, 0);
        // An approved delete over the cap goes in cap-sized calls: the backend enforces its own
        // bound per call and recounts the table each time, so each call stays inside it.
        var keys = b.Keys.ToList();
        int deleted = 0;
        for (int i = 0; i < keys.Count;)
        {
            int chunk = keys.Count;
            if (b.Approved)
            {
                long stored = (await CountsAsync(b.PartitionId, ct)).GetValueOrDefault(b.Table);
                if (stored == 0) break;                                      // nothing left to delete
                chunk = Math.Max(1, (int)(stored * 0.05));                    // backend: max(1, int(stored × PRUNE_MAX_FRACTION))
            }
            var part = keys.Skip(i).Take(chunk).ToList();
            i += part.Count;
            var body = new JsonObject
            {
                ["tableName"] = b.Table, ["missingKeys"] = new JsonArray(part.Select(k => (JsonNode)k).ToArray()), ["liveCount"] = 0
            };
            var (r, json) = await SendJsonAsync(HttpMethod.Post, $"api/v1/onec/{E(b.PartitionId)}/prune-missing?scope={Scope(b.PartitionId)}", body, ct);
            // "nothing stored … refusing to prune": the rows are not there — what a delete wants.
            if (r.Outcome == Outcome.Policy && r.Message?.Contains("nothing stored", StringComparison.OrdinalIgnoreCase) == true) break;
            r = ForPartition(r, b.PartitionId);
            if (!r.Ok) return new DeleteResult(r.Outcome, deleted, r.HttpStatus, r.Message);
            deleted += (int)Long(json?["deleted"]);
        }
        return new DeleteResult(Outcome.Ok, deleted);
    }

    public async Task<ReconcileResult> ReconcileRecorderAsync(RecorderReconcile rc, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["tableName"] = rc.Table, ["recorderRef"] = rc.RecorderKey,
            ["liveKeys"] = new JsonArray(rc.LiveKeys.Select(k => (JsonNode)k).ToArray())
        };
        var (r, json) = await SendJsonAsync(HttpMethod.Post, $"api/v1/onec/{E(rc.PartitionId)}/reconcile-recorder?scope={Scope(rc.PartitionId)}", body, ct);
        r = ForPartition(r, rc.PartitionId);
        return new ReconcileResult(r.Outcome, (int)Long(json?["deleted"]), r.HttpStatus, r.Message);
    }

    /// <summary>
    /// v2 has no per-table purge for a user token (<c>/refids</c> lists ids only, so register rows
    /// keyed the old way cannot even be enumerated); rebuilds of such tables need a backend purge —
    /// today the ops runbook (backend/1c docs/fleet-resync-runbook.md).
    /// </summary>
    public Task<DeleteResult> PurgeTableAsync(string partitionId, string table, CancellationToken ct) =>
        Task.FromResult(new DeleteResult(Outcome.Policy, 0, null, "backend/1c v2 has no table purge route; see its fleet-resync runbook"));

    public Task<RecorderSyncResult> SyncRecorderAtomicAsync(RecorderSync sync, CancellationToken ct) =>
        throw new NotSupportedException("backend/1c v2 has no atomic recorder call");

    public async Task<TargetResult> ReportStatusAsync(BaseStatus s, CancellationToken ct)
    {
        var body = new JsonObject { ["status"] = s.State };
        if (s.TotalCount is { } n) body["totalCount"] = n;
        var (r, _) = await SendJsonAsync(HttpMethod.Patch, $"api/v2/onec/connection/{E(s.ConnectionId)}", body, ct);
        return r;
    }

    /// <summary>
    /// backend/1c's own view (<c>connection_state</c>, connection_state.py): anything but
    /// <c>offline</c> means a connector socket serves the base — this engine opens none (D41), so
    /// it is the old Connector. Unreadable → unknown.
    /// </summary>
    public async Task<Presence> GetPresenceAsync(string connection, CancellationToken ct)
    {
        var (r, json) = await SendJsonAsync(HttpMethod.Get, $"api/v2/onec/{E(connection)}", null, ct);
        if (!r.Ok || json?["connection_state"] is not JsonValue v) return new Presence(null, r.Ok ? "no connection_state" : r.Message);
        string state = v.GetValue<string>();
        return new Presence(state is not ("offline" or "deleting"), "connection_state=" + state);
    }

    /// <summary>Stored rows per table, for the count comparisons live verification does (§18: a hint, not proof).</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountsAsync(string oneCId, CancellationToken ct)
    {
        var (status, json, text, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/v1/onec/{E(oneCId)}/counts?scope={Scope(oneCId)}"), ct);
        if (status != HttpStatusCode.OK) throw new InvalidOperationException($"counts: {(int)status} {text}");
        return (json as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .ToDictionary(o => (string)o["tableName"]!, o => Long(o["quantity"]), StringComparer.Ordinal);
    }

    // ---------------- HTTP ----------------

    /// <summary>
    /// Always <c>self</c>: the engine names every partition it touches. backend/1c's <c>connection</c>
    /// scope widens a reconcile or prune to every partition of the base, keeping only the keys sent
    /// (v1 onec.py reconcile_recorder/prune_missing, utils/onec_scope.py connection_partition_ids) —
    /// a call for the shared partition with its own live set then deleted an organisation's rows
    /// (2026-10-01, <c>SyncPythonTargetTests.AReconcileOrPruneOfTheSharedPartition…</c>).
    /// </summary>
    private static string Scope(string partition) => "self";

    private async Task<(TargetResult, JsonNode?)> SendJsonAsync(HttpMethod m, string url, JsonNode? body, CancellationToken ct)
    {
        var (status, json, text, retryAfter) = await SendAsync(() => new HttpRequestMessage(m, url)
        {
            Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        }, ct);
        return (Classify(status, text, retryAfter), json);
    }

    /// <summary>One request with the token; a 401 refreshes once and retries once. Network failure → status 0.</summary>
    private async Task<(HttpStatusCode Status, JsonNode? Json, string Text, TimeSpan? RetryAfter)> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (HttpRequestException e) { return (0, null, e.Message, null); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (0, null, "timeout", null); }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await tokens.RefreshAsync(ct)) continue;
                JsonNode? json = null;
                try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
                return (resp.StatusCode, json, text, resp.Headers.RetryAfter?.Delta);
            }
        }
    }

    /// <summary>§18 mapping: 400/422 validation, 401/403 auth, 409 gone (deleting) or policy (prune caps), 429 rate, 5xx/network transient.</summary>
    /// <summary>
    /// "Being deleted" (409) from an organisation's binding — one organisation was unmapped — is not the
    /// base going away: the next run re-reads the bindings, which then leave that partition out. Only
    /// the connection itself being deleted pauses the base (Gone).
    /// </summary>
    private TargetResult ForPartition(TargetResult r, string partition) =>
        r.Outcome == Outcome.Gone && partition != connectionId
            ? r with { Outcome = Outcome.Transient, Message = "organisation binding being deleted; bindings re-read next run: " + r.Message }
            : r;

    public static TargetResult Classify(HttpStatusCode status, string text, TimeSpan? retryAfter = null)
    {
        int code = (int)status;
        string msg = text.Length > 300 ? text[..300] : text;
        return code switch
        {
            >= 200 and < 300 => TargetResult.Success,
            // get_onec_with_ownership answers 400 "OneC connection not found" — also when its Mongo
            // lookup failed (the repository returns nothing on any error), and for a binding removed
            // since the config was read. Not a row the user can fix: retried, never a dead letter.
            400 when text.Contains("not found", StringComparison.OrdinalIgnoreCase) => new TargetResult(Outcome.Transient, code, msg),
            400 or 422 => new TargetResult(Outcome.Validation, code, msg),
            401 or 403 => new TargetResult(Outcome.Auth, code, msg),
            409 when text.Contains("being deleted", StringComparison.OrdinalIgnoreCase) => new TargetResult(Outcome.Gone, code, msg),
            409 => new TargetResult(Outcome.Policy, code, msg),
            429 => new TargetResult(Outcome.RateLimited, code, msg, retryAfter),
            _ => new TargetResult(Outcome.Transient, code == 0 ? null : code, msg)
        };
    }

    private static string E(string s) => Uri.EscapeDataString(s);
    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? l : 0;
}
