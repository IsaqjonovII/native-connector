using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;

namespace OneC.Sync.Targets.Rust;

/// <summary>How a request to the Rust module is authenticated: the user's Bearer JWT (desktop) or a service secret (tests).</summary>
public interface IRustCredential
{
    void Apply(HttpRequestMessage request, string token);
    Task<string> GetAsync(CancellationToken ct);
    /// <summary>Called after a 401: true when a new credential is available for one retry.</summary>
    Task<bool> RefreshAsync(CancellationToken ct);
}

public sealed class ServiceSecretCredential(string secret) : IRustCredential
{
    public void Apply(HttpRequestMessage request, string token) => request.Headers.Add("X-Service-Secret", token);
    public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(secret);
    public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
}

public sealed class BearerCredential(Func<CancellationToken, Task<string>> get, Func<CancellationToken, Task<bool>>? refresh = null) : IRustCredential
{
    public void Apply(HttpRequestMessage request, string token) => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    public Task<string> GetAsync(CancellationToken ct) => get(ct);
    public Task<bool> RefreshAsync(CancellationToken ct) => refresh?.Invoke(ct) ?? Task.FromResult(false);
}

/// <summary>One stored row as the verification read returns it (RUST_SYNC_CONTRACT §4.8).</summary>
public sealed record StoredRow(string Key, long? Version, string Hash, JsonObject? Data);

/// <summary>
/// <see cref="IBackendSyncTarget"/> over the Rust onec module's <c>/api/sync/v1</c>
/// (RUST_SYNC_CONTRACT.md): per-row outcomes, idempotent batch ids, a source-version guard, the atomic
/// multi-partition recorder call, explicit deletes, a confirmed table purge, exact counts. A partition
/// is a Rust <c>onec_id</c>: the connection's own (shared) or an organisation binding's.
/// </summary>
public sealed class RustSyncTarget(HttpClient http, IRustCredential credential, string connectionId) : IBackendSyncTarget, ICoverageTarget
{
    /// <summary>Uncompressed bytes per request part; the module caps the wire at 8 MiB and the decoded body at 64 MiB.</summary>
    public int MaxPartBytes { get; init; } = 6 * 1024 * 1024;
    /// <summary>Bodies larger than this go gzip-compressed.</summary>
    public int GzipAbove { get; init; } = 64 * 1024;

    public string Kind => "rust";
    public string ConnectionId => connectionId;

    public Task<TargetCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new TargetCapabilities(
        PerRowResults: true, AtomicRecorder: true, SourceVersionGuard: true, MaxBatchBytes: MaxPartBytes, MaxBatchRows: 2000,
        DeleteCapFraction: null, Gzip: true, DuplicateKeys: DuplicateKeyPolicy.Reject, ReportsTrustworthyCounts: true));

    private string Base => $"api/sync/v1/connections/{Uri.EscapeDataString(connectionId)}";

    public async Task<SyncConfig> GetSyncConfigAsync(string connection, CancellationToken ct)
    {
        var (r, json) = await SendAsync(HttpMethod.Get, $"api/sync/v1/connections/{Uri.EscapeDataString(connection)}/config", null, ct);
        if (!r.Ok || json is not JsonObject o) throw new InvalidOperationException($"config: {r.Outcome} {r.Message}");
        var partitions = (o["partitions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(p => (string?)p["status"] != "deleting")
            .Select(p => new SyncPartition((string)p["partitionId"]!, (string?)p["orgRef"], (string?)p["companyId"])).ToList();
        var stored = o["tables"] as JsonArray;
        var tables = (stored ?? new JsonArray()).OfType<JsonObject>().Select(t => (string?)t["table"]).OfType<string>()
            .Select(t => new SyncTableConfig(t, "", "", IsMovementTable(t))).ToList();
        return new SyncConfig(connection, (string?)o["provider"], (string)o["sharedPartition"]!, partitions, tables) { TableListStored = stored is not null };
    }

    /// <summary>The module's <c>scope::is_movement_table</c> — the same rule the server's organisation guard applies.</summary>
    public static bool IsMovementTable(string table) =>
        table.StartsWith("Document_", StringComparison.Ordinal) || table.StartsWith("AccountingRegister_", StringComparison.Ordinal) ||
        table.StartsWith("AccumulationRegister_", StringComparison.Ordinal) || table.StartsWith("InformationRegister_", StringComparison.Ordinal);

    public async Task<UploadResult> UploadRowsAsync(UploadBatch batch, CancellationToken ct)
    {
        int applied = 0;
        var stale = new List<string>();
        var rejected = new List<RowRejection>();
        int part = 0;
        foreach (var rows in Split(batch.Rows))
        {
            // A batch split for size gets one id per part, derived from its own: a retry of the
            // whole batch replays every part that already committed.
            var r = await UploadPartAsync(batch.Rows.Count == rows.Count ? batch.BatchId : $"{batch.BatchId}.{part}", batch, rows, 0, ct);
            part++;
            if (!r.Ok) return r;
            applied += r.Applied;
            stale.AddRange(r.Stale);
            rejected.AddRange(r.Rejected);
        }
        return new UploadResult(Outcome.Ok, applied, stale, rejected);
    }

    private IEnumerable<List<SyncRow>> Split(IReadOnlyList<SyncRow> rows)
    {
        var part = new List<SyncRow>();
        long bytes = 0;
        foreach (var r in rows)
        {
            if (part.Count > 0 && (bytes + r.Json.Length + 128 > MaxPartBytes || part.Count >= 2000)) { yield return part; part = new(); bytes = 0; }
            part.Add(r);
            bytes += r.Json.Length + 128;
        }
        if (part.Count > 0) yield return part;
    }

    private async Task<UploadResult> UploadPartAsync(string batchId, UploadBatch batch, List<SyncRow> rows, int depth, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("batchId", batchId);
            w.WriteString("partition", batch.PartitionId);
            w.WriteString("table", batch.Table);
            w.WriteStartArray("rows");
            foreach (var r in rows) WriteRow(w, r);
            w.WriteEndArray();
        });
        var (res, json) = await SendAsync(HttpMethod.Post, $"{Base}/rows", body, ct);
        if (res.HttpStatus == 413 && rows.Count > 1 && depth < 10)
        {
            int half = rows.Count / 2;
            var a = await UploadPartAsync(batchId + ".a", batch, rows.GetRange(0, half), depth + 1, ct);
            if (!a.Ok) return a;
            var b = await UploadPartAsync(batchId + ".b", batch, rows.GetRange(half, rows.Count - half), depth + 1, ct);
            if (!b.Ok) return b;
            return new UploadResult(Outcome.Ok, a.Applied + b.Applied, a.Stale.Concat(b.Stale).ToList(), a.Rejected.Concat(b.Rejected).ToList());
        }
        if (res.HttpStatus == 413) return UploadResult.Failed(Outcome.Validation, 413, $"one {batch.Table} row is larger than the server accepts");
        if (!res.Ok) return UploadResult.Failed(res.Outcome, res.HttpStatus, res.Message, res.RetryAfter);
        string status = (string?)json?["status"] ?? "";
        if (status.Length != rows.Count)
            return UploadResult.Failed(Outcome.Transient, 200, $"answer has {status.Length} row outcomes for {rows.Count} rows");
        int applied = 0;
        var stale = new List<string>();
        for (int i = 0; i < status.Length; i++)
            switch (status[i])
            {
                case 'i' or 'u' or 'n': applied++; break;
                case 's': stale.Add(rows[i].Key); break;
            }
        var rejected = (json?["rejected"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(x => new RowRejection((string)x["key"]!, (string)x["reason"]!, (bool?)x["retryable"] ?? false)).ToList();
        return new UploadResult(Outcome.Ok, applied, stale, rejected);
    }

    public async Task<DeleteResult> DeleteRowsAsync(DeleteBatch b, CancellationToken ct)
    {
        if (b.Keys.Count == 0) return new DeleteResult(Outcome.Ok, 0);
        int deleted = 0, part = 0;
        foreach (var chunk in b.Keys.Chunk(10_000))
        {
            byte[] body = WriteBody(w =>
            {
                w.WriteString("batchId", b.Keys.Count <= 10_000 ? b.BatchId : $"{b.BatchId}.{part}");
                w.WriteString("partition", b.PartitionId);
                w.WriteString("table", b.Table);
                w.WriteStartArray("keys");
                foreach (var k in chunk) w.WriteStringValue(k);
                w.WriteEndArray();
                w.WriteString("reason", b.Reason);
            });
            part++;
            var (r, json) = await SendAsync(HttpMethod.Post, $"{Base}/delete", body, ct);
            if (!r.Ok) return new DeleteResult(r.Outcome, deleted, r.HttpStatus, r.Message);
            deleted += (int?)json?["deleted"] ?? 0;
        }
        return new DeleteResult(Outcome.Ok, deleted);
    }

    /// <summary>
    /// The non-atomic engine path; Rust advertises the atomic recorder, so the engine only calls this
    /// to clear a deleted document's movements (empty live set). A non-empty live set would need the
    /// rows themselves, which this call does not carry.
    /// </summary>
    public async Task<ReconcileResult> ReconcileRecorderAsync(RecorderReconcile rc, CancellationToken ct)
    {
        if (rc.LiveKeys.Count > 0)
            return new ReconcileResult(Outcome.Validation, 0, null, "the Rust target reconciles a non-empty live set only through the atomic recorder call");
        var r = await SyncRecorderAtomicAsync(new RecorderSync(rc.BatchId, rc.RecorderKey, null, "", null, null,
            new[] { rc.PartitionId }, new[] { rc.Table }, Array.Empty<PartitionMovements>()), ct);
        return new ReconcileResult(r.Outcome, r.MovementsRemoved, r.HttpStatus, r.Message);
    }

    public async Task<RecorderSyncResult> SyncRecorderAtomicAsync(RecorderSync s, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("batchId", s.BatchId);
            w.WriteString("recorderKey", s.RecorderKey);
            if (s.SourceVersion is { } v) w.WriteNumber("version", v); else w.WriteNull("version");
            w.WriteString("documentTable", s.DocumentTable);
            if (s.Document is not null && s.DocumentPartition is not null)
            {
                w.WriteStartObject("document");
                w.WriteString("partition", s.DocumentPartition);
                w.WritePropertyName("data");
                w.WriteRawValue(s.Document.Json, skipInputValidation: true);
                w.WriteEndObject();
            }
            else w.WriteNull("document");
            w.WriteStartArray("partitions");
            foreach (var p in s.Partitions.Distinct(StringComparer.Ordinal)) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("movementTables");
            foreach (var t in s.MovementTables.Distinct(StringComparer.Ordinal)) w.WriteStringValue(t);
            w.WriteEndArray();
            w.WriteStartArray("movements");
            foreach (var m in s.Movements.Where(m => m.Rows.Count > 0))
            {
                w.WriteStartObject();
                w.WriteString("partition", m.PartitionId);
                w.WriteString("table", m.Table);
                w.WriteStartArray("rows");
                foreach (var r in m.Rows) WriteRow(w, r);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        var (res, json) = await SendAsync(HttpMethod.Post, $"{Base}/recorder", body, ct);
        if (!res.Ok) return new RecorderSyncResult(res.Outcome, false, 0, 0, res.HttpStatus, res.Message);
        if ((bool?)json?["stale"] == true) return new RecorderSyncResult(Outcome.Ok, true, 0, 0);
        var m = json?["movements"];
        return new RecorderSyncResult(Outcome.Ok, false, ((int?)m?["inserted"] ?? 0) + ((int?)m?["updated"] ?? 0), (int?)m?["removed"] ?? 0);
    }

    public async Task<TargetResult> ReportStatusAsync(BaseStatus s, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("state", s.State);
            if (s.TotalCount is { } n) w.WriteNumber("totalCount", n);
            if (s.Percentage is { } p) w.WriteNumber("percentage", p);
            if (s.LastError is { } e) w.WriteString("lastError", e);
        });
        var (r, _) = await SendAsync(HttpMethod.Post, $"api/sync/v1/connections/{Uri.EscapeDataString(s.ConnectionId)}/status", body, ct);
        return r;
    }

    /// <summary>D-1: an old Connector's socket on the module's hub for this connection. Unreadable → unknown (the engine then waits).</summary>
    public async Task<Presence> GetPresenceAsync(string connection, CancellationToken ct)
    {
        var (r, json) = await SendAsync(HttpMethod.Get, $"api/sync/v1/connections/{Uri.EscapeDataString(connection)}/presence", null, ct);
        if (!r.Ok || json?["otherConnectorOnline"] is not JsonValue v) return new Presence(null, r.Ok ? "no otherConnectorOnline" : r.Message);
        return new Presence(v.GetValue<bool>(), (string?)json["detail"]);
    }

    public async Task<DeleteResult> PurgeTableAsync(string partitionId, string table, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("partition", partitionId);
            w.WriteString("table", table);
            w.WriteString("confirm", "purge " + table);
        });
        var (r, json) = await SendAsync(HttpMethod.Post, $"{Base}/purge", body, ct);
        return new DeleteResult(r.Outcome, (int?)json?["deleted"] ?? 0, r.HttpStatus, r.Message);
    }

    // ---------------- verification, counts, coverage (beyond the engine interface) ----------------

    /// <summary>Exact stored rows per table in one partition (§4.7) — computed by the server, never a cached counter.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountsAsync(string partition, CancellationToken ct, params string[] tables)
    {
        string q = "partition=" + Uri.EscapeDataString(partition) + string.Concat(tables.Select(t => "&table=" + Uri.EscapeDataString(t)));
        var (r, json) = await SendAsync(HttpMethod.Get, $"{Base}/counts?{q}", null, ct);
        if (!r.Ok) throw new InvalidOperationException($"counts: {r.Outcome} {r.Message}");
        return (json?["tables"] as JsonObject ?? new JsonObject()).ToDictionary(kv => kv.Key, kv => (long?)kv.Value ?? 0, StringComparer.Ordinal);
    }

    /// <summary>Every stored row of one table in one partition, in key order (§4.8).</summary>
    public async IAsyncEnumerable<StoredRow> ReadRowsAsync(string partition, string table, bool keysOnly = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? after = "";
        while (after is not null)
        {
            string url = $"{Base}/rows?partition={Uri.EscapeDataString(partition)}&table={Uri.EscapeDataString(table)}&limit=5000" +
                         $"&after={Uri.EscapeDataString(after)}{(keysOnly ? "&fields=key" : "")}";
            var (r, json) = await SendAsync(HttpMethod.Get, url, null, ct);
            if (!r.Ok) throw new InvalidOperationException($"rows {partition}/{table}: {r.Outcome} {r.Message}");
            foreach (var row in (json?["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                yield return new StoredRow((string)row["key"]!, (long?)row["version"], (string)row["hash"]!, row["data"] as JsonObject);
            after = (string?)json?["next"];
        }
    }

    public Task<TargetResult> ReportCoverageAsync(string partitionId, IReadOnlyList<TableCoverage> tables, CancellationToken ct) =>
        PutCoverageAsync(partitionId, tables.Select(t => (t.Table, t.DataFrom, t.Complete)), ct);

    public async Task<TargetResult> PutCoverageAsync(string partition, IEnumerable<(string Table, DateTime? DataFrom, bool Complete)> tables, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("partition", partition);
            w.WriteStartArray("tables");
            foreach (var (t, from, complete) in tables)
            {
                w.WriteStartObject();
                w.WriteString("table", t);
                if (from is { } f) w.WriteString("dataFrom", f.ToString("yyyy-MM-dd")); else w.WriteNull("dataFrom");
                w.WriteBoolean("complete", complete);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        var (r, _) = await SendAsync(HttpMethod.Put, $"{Base}/coverage", body, ct);
        return r;
    }

    public async Task<JsonObject> GetCoverageAsync(string partition, CancellationToken ct)
    {
        var (r, json) = await SendAsync(HttpMethod.Get, $"{Base}/coverage?partition={Uri.EscapeDataString(partition)}", null, ct);
        if (!r.Ok) throw new InvalidOperationException($"coverage: {r.Outcome} {r.Message}");
        return json?["tables"] as JsonObject ?? new JsonObject();
    }

    // ---------------- commands: normal writes to 1C (RUST_SYNC_CONTRACT §9) ----------------

    /// <summary>Stores a command (the cloud's side; used by tools and tests here). The same key with the same request is the same command.</summary>
    public async Task<(TargetResult Result, string? CommandId, bool Deduplicated)> EnqueueCommandAsync(string kind, string idempotencyKey, string partition,
        JsonObject payload, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("kind", kind);
            w.WriteString("idempotencyKey", idempotencyKey);
            w.WriteString("partition", partition);
            w.WritePropertyName("payload");
            payload.WriteTo(w);
        });
        var (r, json) = await SendAsync(HttpMethod.Post, $"{Base}/commands", body, ct);
        return (r, (string?)json?["commandId"], (bool?)json?["deduplicated"] ?? false);
    }

    /// <param name="LeaseToken">Handed out with this lease only; the report must quote it (another caller, or this
    /// Connector after the lease ran out and was handed on, is refused <c>lease_not_held</c>).</param>
    public sealed record LeasedCommand(string CommandId, string Kind, string Partition, JsonObject Payload, int Attempt, string LeaseToken);

    /// <summary>The next commands of this connection, oldest first, leased to this Connector for <paramref name="leaseSeconds"/>.</summary>
    public async Task<IReadOnlyList<LeasedCommand>> LeaseCommandsAsync(int max, int leaseSeconds, CancellationToken ct)
    {
        byte[] body = WriteBody(w => { w.WriteNumber("max", max); w.WriteNumber("leaseSeconds", leaseSeconds); });
        var (r, json) = await SendAsync(HttpMethod.Post, $"{Base}/commands/lease", body, ct);
        if (!r.Ok) throw new InvalidOperationException($"lease: {r.Outcome} {r.Message}");
        return (json?["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(c => new LeasedCommand((string)c["commandId"]!, (string)c["kind"]!, (string)c["partition"]!,
                                           (JsonObject)c["payload"]!.DeepClone(), (int?)c["attempt"] ?? 1,
                                           (string?)c["leaseToken"] ?? "")).ToList();
    }

    public async Task<TargetResult> ReportCommandAsync(LeasedCommand c, bool succeeded, JsonNode? result, JsonNode? error, CancellationToken ct)
    {
        byte[] body = WriteBody(w =>
        {
            w.WriteString("state", succeeded ? "succeeded" : "failed");
            w.WriteString("leaseToken", c.LeaseToken);
            w.WritePropertyName("result");
            if (result is null) w.WriteNullValue(); else result.WriteTo(w);
            w.WritePropertyName("error");
            if (error is null) w.WriteNullValue(); else error.WriteTo(w);
        });
        var (r, _) = await SendAsync(HttpMethod.Post, $"{Base}/commands/{Uri.EscapeDataString(c.CommandId)}/result", body, ct);
        return r;
    }

    public async Task<JsonObject?> GetCommandAsync(string commandId, CancellationToken ct)
    {
        var (r, json) = await SendAsync(HttpMethod.Get, $"{Base}/commands/{Uri.EscapeDataString(commandId)}", null, ct);
        return r.Ok ? json as JsonObject : null;
    }

    // ---------------- HTTP ----------------

    private static void WriteRow(Utf8JsonWriter w, SyncRow r)
    {
        w.WriteStartObject();
        w.WriteString("key", r.Key);
        if (r.SourceVersion is { } v) w.WriteNumber("version", v);
        w.WritePropertyName("data");
        w.WriteRawValue(r.Json, skipInputValidation: true);   // the mapper's canonical JSON, never re-serialised
        w.WriteEndObject();
    }

    private static byte[] WriteBody(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private async Task<(TargetResult Result, JsonNode? Json)> SendAsync(HttpMethod method, string url, byte[]? body, CancellationToken ct)
    {
        byte[]? wire = body;
        bool gzip = body is not null && body.Length > GzipAbove;
        if (gzip)
        {
            using var ms = new MemoryStream(body!.Length / 4 + 64);
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(body);
            wire = ms.ToArray();
        }
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (wire is not null)
            {
                req.Content = new ByteArrayContent(wire);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (gzip) req.Content.Headers.ContentEncoding.Add("gzip");
            }
            credential.Apply(req, await credential.GetAsync(ct));
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (HttpRequestException e) { return (new TargetResult(Outcome.Transient, null, e.Message), null); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (new TargetResult(Outcome.Transient, null, "timeout"), null); }
            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await credential.RefreshAsync(ct)) continue;
                JsonNode? json = null;
                try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
                return (Classify(resp.StatusCode, json, text, resp.Headers.RetryAfter?.Delta), json);
            }
        }
    }

    /// <summary>RUST_SYNC_CONTRACT §6: HTTP + the module's error code → the canonical outcome.</summary>
    public static TargetResult Classify(HttpStatusCode status, JsonNode? json, string text, TimeSpan? retryAfter = null)
    {
        int code = (int)status;
        // Only the module's own error envelope has string `error`/`detail`; a command's answer carries
        // `error` as the host's error OBJECT (or null) and must not be read as one.
        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        string error = Text(json?["error"]) ?? "";
        string msg = (error.Length > 0 ? error + ": " : "") + (Text(json?["detail"]) ?? (text.Length > 300 ? text[..300] : text));
        if (json?["keys"] is JsonArray keys) msg += " [" + string.Join(",", keys.Select(k => (string?)k)) + "]";
        if (json?["rejected"] is JsonArray rej && code >= 400)
            msg += " " + string.Join("; ", rej.OfType<JsonObject>().Take(5).Select(r => $"{r["partition"]}/{r["table"]}/{r["key"]}: {r["reason"]}"));
        return code switch
        {
            >= 200 and < 300 => TargetResult.Success,
            409 when error == "connection_deleting" => new TargetResult(Outcome.Gone, code, msg),
            409 when error == "partition_deleting" => new TargetResult(Outcome.Transient, code, msg),
            400 or 409 or 413 or 422 => new TargetResult(Outcome.Validation, code, msg),
            401 or 403 => new TargetResult(Outcome.Auth, code, msg),
            404 => new TargetResult(Outcome.Gone, code, msg),
            429 => new TargetResult(Outcome.RateLimited, code, msg, retryAfter),
            _ => new TargetResult(Outcome.Transient, code == 0 ? null : code, msg)
        };
    }
}
