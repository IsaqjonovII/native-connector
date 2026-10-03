using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneC.Ipc;

namespace OneC.Supervisor;

/// <summary>
/// The one HTTP surface (IPC_CONTRACT.md §7, DECISIONS D28): loopback only, every endpoint
/// except /v1/health behind X-AIBA-Token. It translates HTTP to pipe requests and pipe errors
/// to HTTP statuses; it holds no 1C logic.
/// </summary>
public sealed class EdgeServer : IAsyncDisposable
{
    private readonly Supervisor _sup;
    private WebApplication? _app;

    /// <summary>The bound port — the real one after StartAsync when 0 was requested.</summary>
    public int Port { get; private set; }

    /// <summary>One line per pipe-backed request, newest last. Bounded: never grows past 500.</summary>
    public sealed record Activity(DateTime Utc, string Method, string Path, string Base, string Op,
                                  int Status, long Ms, string? ErrorLayer, string? Error);

    private readonly Queue<Activity> _activity = new();
    private const int ActivityLimit = 500;

    private void Record(Activity a)
    {
        lock (_activity)
        {
            _activity.Enqueue(a);
            while (_activity.Count > ActivityLimit) _activity.Dequeue();
        }
    }

    public IReadOnlyList<Activity> RecentActivity(int limit)
    {
        lock (_activity) return _activity.Reverse().Take(Math.Clamp(limit, 1, ActivityLimit)).ToList();
    }
    public string Token { get; }

    /// <summary>The sync engine (D42), served under <c>/v1/sync</c>; null = not running.</summary>
    public OneC.Sync.Engine.SyncEngineHost? Sync { get; set; }

    public EdgeServer(Supervisor sup, int port, string? token = null)
    {
        _sup = sup;
        Port = port;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, Port, l => l.Protocols = HttpProtocols.Http1);
            k.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
        });
        var app = b.Build();

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/v1/health" &&
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers["X-AIBA-Token"].ToString()),
                    System.Text.Encoding.UTF8.GetBytes(Token)))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = new { layer = "Edge", message = "missing or wrong X-AIBA-Token" } });
                return;
            }
            await next();
        });

        Map(app);
        await app.StartAsync();
        _app = app;
        if (Port == 0)
        {
            var addr = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                          .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                          .Addresses.First();
            Port = new Uri(addr).Port;
        }
    }

    private void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/v1/health", () => Results.Json(new
        {
            ok = true,
            pid = Environment.ProcessId,
            hosts = _sup.Hosts.Select(h => new { h.Key, state = h.State.ToString(), h.Pid })
        }));

        r.MapGet("/v1/hosts", async () =>
        {
            var list = new List<object>();
            foreach (var h in _sup.Hosts)
            {
                var (ws, priv) = h.Memory();
                var stats = h.State == HostState.Ready ? await h.SendAsync(Ops.Stats, deadlineMs: 10_000) : null;
                list.Add(new
                {
                    h.Key, state = h.State.ToString(), h.Pid, h.PipeName,
                    comcntrVersion = h.ReadyInfo?["comcntrVersion"]?.GetValue<string>(),
                    workingSetMb = ws, privateMb = priv, restarts = _sup.Restarts(h.Key),
                    bases = h.Assignment.Bases.Select(b => b.Name),
                    stats = stats?.Ok == true ? stats.Result : null
                });
            }
            return Results.Json(list);
        });

        r.MapGet("/v1/bases", () => Results.Json(_sup.Bases().Select(b => new { name = b.Base, kind = b.Kind, host = b.HostKey })));

        r.MapGet("/v1/events", (int? limit) => Results.Json(
            _sup.Events.Reverse().Take(Math.Clamp(limit ?? 200, 1, 1000))
                .Select(e => new { utc = e.Utc, host = e.Key, what = e.What })));

        r.MapGet("/v1/activity", (int? limit) => Results.Json(RecentActivity(limit ?? 200)));

        MapSync(r);

        r.MapGet("/v1/supervisor", () =>
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            return Results.Json(new
            {
                pid = p.Id, workingSetMb = p.WorkingSet64 / 1024 / 1024,
                privateMb = p.PrivateMemorySize64 / 1024 / 1024, threads = p.Threads.Count,
                handles = p.HandleCount, cpuSeconds = p.TotalProcessorTime.TotalSeconds,
                unplaceable = _sup.Unplaceable.Select(u => new { name = u.Base.Name, reason = u.Reason })
            });
        });

        r.MapGet("/v1/bases/{b}/version", (string b, HttpContext c) => Pipe(c, b, Ops.Version, null));
        r.MapGet("/v1/bases/{b}/test", (string b, HttpContext c) => Pipe(c, b, Ops.Test, null));
        // Every table of the configuration (names, synonyms); ?details=Document_X,InformationRegister_Y: those, engine-ready.
        r.MapGet("/v1/bases/{b}/tables", (string b, string? details, HttpContext c) => Pipe(c, b, Ops.Tables,
            details is { Length: > 0 } ? new JsonObject { ["details"] = new JsonArray(details.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => (JsonNode)t).ToArray()) } : null));
        r.MapPost("/v1/bases/{b}/read", async (string b, HttpContext c) => await Pipe(c, b, Ops.Read, await Body(c)));

        r.MapGet("/v1/bases/{b}/catalogs/{name}", (string b, string name, HttpContext c) =>
            Pipe(c, b, Ops.Catalog, CatalogArgs(name, c.Request.Query)));
        r.MapGet("/v1/bases/{b}/catalogs/{name}/{id}", (string b, string name, string id, HttpContext c) =>
            Pipe(c, b, Ops.Catalog, With(With(new JsonObject(), "catalog", name), "id", id)));

        // The change feed (D36): read by the supervisor itself from the base's event-log files.
        r.MapGet("/v1/bases/{b}/changes", async (string b, string? cursor, long? maxBytes, HttpContext c) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IResult result; int status;
            try
            {
                var from = string.IsNullOrEmpty(cursor) ? null : OneC.EventLog.LogCursor.Parse(cursor);
                var batch = await Task.Run(() => _sup.Changes(b, from, Math.Clamp(maxBytes ?? OneC.EventLog.EventLogReader.DefaultMaxBytes, 4096, 64L << 20)));
                status = 200;
                result = Results.Json(new
                {
                    events = batch.Events, cursor = batch.Cursor?.ToString(), reset = batch.Reset,
                    resetReason = batch.ResetReason, more = batch.More, records = batch.Records
                }, IpcJson.Options);
            }
            catch (ArgumentException e) { status = 400; result = Results.Json(new { error = IpcError.Of(Layers.Validation, e.Message) }, IpcJson.Options, statusCode: status); }
            catch (KeyNotFoundException e) { status = 404; result = Results.Json(new { error = IpcError.Of(Layers.Host, e.Message, kind: ErrorKinds.NotFound) }, IpcJson.Options, statusCode: status); }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                status = 409;
                result = Results.Json(new { error = IpcError.Of(Layers.Host, e.Message) }, IpcJson.Options, statusCode: status);
            }
            Record(new Activity(DateTime.UtcNow, c.Request.Method, c.Request.Path, b, "changes", status, sw.ElapsedMilliseconds, null, null));
            return result;
        });

        r.MapGet("/v1/bases/{b}/slices/{kind}/{name}", (string b, string kind, string name, HttpContext c) =>
        {
            var a = new JsonObject { ["kind"] = kind, ["name"] = name };
            var q = c.Request.Query;
            if (q.TryGetValue("parts", out var p)) a["parts"] = int.TryParse(p, out int pi) ? pi : (JsonNode)p.ToString();
            foreach (var k in new[] { "from", "to" })
                if (q.TryGetValue(k, out var d) && d.ToString().Length > 0) a[k] = d.ToString();
            return Pipe(c, b, Ops.Slices, a);
        });

        // Old routes: /registers/{name} is an accounting register; /registers/{kind}/{name}.
        r.MapGet("/v1/bases/{b}/registers/{name}", (string b, string name, HttpContext c) =>
            Pipe(c, b, Ops.Register, RegisterArgs("accounting", name, c.Request.Query)));
        r.MapGet("/v1/bases/{b}/registers/{kind}/{name}", (string b, string kind, string name, HttpContext c) =>
            Pipe(c, b, Ops.Register, RegisterArgs(kind, name, c.Request.Query)));

        const string Doc = "/v1/bases/{b}/documents/{docType}";
        r.MapGet(Doc, (string b, string docType, HttpContext c) =>
            Pipe(c, b, Ops.Document, DocumentArgs(docType, c.Request.Query)));
        r.MapGet(Doc + "/{id}", (string b, string docType, string id, HttpContext c) =>
            Pipe(c, b, Ops.Document, With(With(new JsonObject(), "document", docType), "id", id)));
        // Batch by ids: POST, because hundreds of GUIDs do not fit a URL (old route, main.os:22367).
        r.MapPost(Doc + "/batch", async (string b, string docType, HttpContext c) =>
        {
            var body = await Body(c);
            if (body.ContainsKey(InvalidBody)) return await Pipe(c, b, Ops.Document, body);
            var ids = body["ids"] ?? body["Ids"];
            if (ids is not JsonArray)
                return Results.Json(new { error = IpcError.Of(Layers.Validation, "expected a body like { \"ids\": [guid, …] }") },
                                    IpcJson.Options, statusCode: 400);
            return await Pipe(c, b, Ops.Document, new JsonObject { ["document"] = docType, ["ids"] = ids.DeepClone() });
        });
        // Create from the old adapter's body (D38): ?post (default true), ?exchange (refused if true).
        r.MapPost(Doc, async (string b, string docType, HttpContext c) =>
        {
            var body = await Body(c);
            if (body.ContainsKey(InvalidBody)) return await Pipe(c, b, Ops.Create, body);
            var a = new JsonObject { ["docType"] = docType, ["body"] = body };
            var q = c.Request.Query;
            if (q.TryGetValue("post", out var p)) a["post"] = Truthy(p.ToString()) ?? false;
            if (q.TryGetValue("exchange", out var x)) a["exchange"] = Truthy(x.ToString()) ?? false;
            return await Pipe(c, b, Ops.Create, a, successStatus: 201);
        });
        // The old PUT: the body IS the fields; ?post (unset = keep the posted state), ?autoUnpost, ?exchange.
        r.MapPut(Doc + "/{id}", async (string b, string docType, string id, HttpContext c) =>
        {
            var body = await Body(c);
            if (body.ContainsKey(InvalidBody)) return await Pipe(c, b, Ops.Update, body);
            return await Pipe(c, b, Ops.Update, WriteFlags(new JsonObject
            {
                ["docType"] = docType, ["ref"] = id, ["fields"] = body
            }, c.Request.Query, "post", "autoUnpost", "exchange"));
        });
        // v1 form of the same: { "fields": {…}, "post"?, "autoUnpost"? }.
        r.MapPatch(Doc + "/{id}", async (string b, string docType, string id, HttpContext c) =>
            await Pipe(c, b, Ops.Update, With(With(await Body(c), "docType", docType), "ref", id)));
        r.MapPost(Doc + "/{id}/post", async (string b, string docType, string id, HttpContext c) =>
            await Pipe(c, b, Ops.Post, WriteFlags(With(With(new JsonObject(), "docType", docType), "ref", id), c.Request.Query, "exchange")));
        r.MapPost(Doc + "/{id}/unpost", async (string b, string docType, string id, HttpContext c) =>
            await Pipe(c, b, Ops.Unpost, With(With(new JsonObject(), "docType", docType), "ref", id)));
        r.MapPost(Doc + "/{id}/mark-deleted", async (string b, string docType, string id, HttpContext c) =>
            await Pipe(c, b, Ops.MarkDeleted, With(With(await Body(c), "docType", docType), "ref", id)));
        // The old DELETE: a deletion mark; ?hard=true deletes.
        r.MapDelete(Doc + "/{id}", async (string b, string docType, string id, HttpContext c) =>
        {
            var a = With(With(new JsonObject(), "docType", docType), "ref", id);
            a["hard"] = c.Request.Query.TryGetValue("hard", out var h) && Truthy(h.ToString()) == true;
            return await Pipe(c, b, Ops.Delete, a);
        });
    }

    /// <summary>
    /// Sync control (S13, IPC_CONTRACT §7): status, dead letters, pause/resume, retry /
    /// approve / dismiss, per-table rebuild (D-3: <c>?confirm=true</c> required). 404 when not running.
    /// </summary>
    private void MapSync(IEndpointRouteBuilder r)
    {
        IResult Off() => Results.Json(new { error = IpcError.Of(Layers.Host, "sync is not running (start with --sync-config)", kind: ErrorKinds.NotFound) },
                                      IpcJson.Options, statusCode: 404);
        IResult Done(bool ok, string what) => ok ? Results.Json(new { ok = true }) :
            Results.Json(new { error = IpcError.Of(Layers.Validation, what, kind: ErrorKinds.NotFound) }, IpcJson.Options, statusCode: 404);

        r.MapGet("/v1/sync", () => Sync is { } s ? Results.Json(s.Status().Select(b => new
        {
            baseId = b.BaseId, mode = b.Mode, reason = b.Reason, pendingWork = b.PendingWork, deadLetters = b.DeadLetters, warning = b.Warning,
            cursor = b.Cursor, lastEventAt = b.LastEventAt, active = b.Active, lastError = b.LastError,
            tablesDone = b.TablesDone, tablesTotal = b.TablesTotal, missingTables = b.MissingTables,
            tables = s.Tables(b.BaseId),
            unmappedOrgs = b.UnmappedOrgs.Select(u => new { orgRef = u.OrgRef, table = u.Table, rows = u.Rows })
        }), IpcJson.Options) : Off());
        // S12: the target's HTTP calls (raw, for p50/p95 and bytes) and the failure-test switches.
        r.MapGet("/v1/sync/http", (bool? clear) =>
        {
            if (Sync is null) return Off();
            var calls = SyncMode.Http.Calls;
            if (clear == true) SyncMode.Http.ClearCalls();
            return Results.Json(new { armed = SyncMode.Http.Armed, calls }, IpcJson.Options);
        });
        r.MapPost("/v1/sync/faults", (int? fail503, int? failNetwork, int? badToken, int? reject400, bool? crashOnComplete) =>
        {
            if (Sync is null) return Off();
            try
            {
                SyncMode.Http.Arm(fail503 ?? 0, failNetwork ?? 0, badToken ?? 0, reject400 ?? 0);
                if (crashOnComplete == true) SyncMode.ArmCrashOnComplete();
            }
            catch (InvalidOperationException e) { return Results.Json(new { error = IpcError.Of(Layers.Validation, e.Message) }, IpcJson.Options, statusCode: 409); }
            return Results.Json(new { ok = true, armed = SyncMode.Http.Armed }, IpcJson.Options);
        });
        r.MapGet("/v1/sync/bases/{b}/tables", (string b) => Sync is { } s ? Results.Json(s.TableStatus(b).Select(t => new
        {
            table = t.Table, family = t.Family, state = t.State, missing = t.Missing, rows = t.Rows, copiedSoFar = t.CopiedSoFar,
            pending = t.Pending, failed = t.Failed, lastSentAt = t.LastSentAt
        }), IpcJson.Options) : Off());
        r.MapGet("/v1/sync/bases/{b}/dead-letters", (string b) => Sync is { } s ? Results.Json(s.DeadLetters(b), IpcJson.Options) : Off());
        r.MapPost("/v1/sync/bases/{b}/pause", (string b) => Sync is { } s ? Done(s.Pause(b), $"no base {b}") : Off());
        r.MapPost("/v1/sync/bases/{b}/resume", (string b) => Sync is { } s ? Done(s.Resume(b), $"no base {b}") : Off());
        r.MapPost("/v1/sync/dead-letters/{id:long}/retry", (long id) => Sync is { } s ? Done(s.Retry(id), $"no dead letter {id}") : Off());
        r.MapPost("/v1/sync/dead-letters/{id:long}/approve", (long id) => Sync is { } s ? Done(s.Approve(id), $"no approvable dead letter {id}") : Off());
        r.MapPost("/v1/sync/dead-letters/{id:long}/dismiss", (long id, string? who) => Sync is { } s ? Done(s.Dismiss(id, who ?? "user"), $"no dead letter {id}") : Off());
        r.MapPost("/v1/sync/bases/{b}/tables/{t}/rebuild", async (string b, string t, bool? confirm, HttpContext c) =>
        {
            if (Sync is not { } s) return Off();
            var (ok, msg) = await s.RebuildAsync(b, t, confirm == true, c.RequestAborted);
            return Results.Json(new { ok, message = msg }, statusCode: ok ? 200 : 409);
        });
    }

    private async Task<IResult> Pipe(HttpContext c, string baseName, string op, JsonObject? args, int successStatus = 200)
    {
        if (args?.ContainsKey(InvalidBody) == true)
            return Results.Json(new { error = IpcError.Of(Layers.Validation, "request body is not valid JSON") },
                                IpcJson.Options, statusCode: 400);
        int? deadline = int.TryParse(c.Request.Headers["X-AIBA-Deadline-Ms"], out int d) ? d : null;
        // The caller hanging up cancels the pipe request (IPC_CONTRACT.md §5).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await _sup.SendAsync(baseName, op, args, deadline, c.RequestAborted);
        int status = resp.Ok ? successStatus : StatusFor(resp.Error!);
        Record(new Activity(DateTime.UtcNow, c.Request.Method, c.Request.Path, baseName, op, status,
                            sw.ElapsedMilliseconds, resp.Error?.Layer, resp.Error?.Message));
        if (resp.Ok) return Results.Json(resp.Result, statusCode: status);

        var e = resp.Error!;
        if (status == 503) c.Response.Headers.RetryAfter = "1";
        return Results.Json(new { error = e }, IpcJson.Options, statusCode: status);
    }

    /// <summary>IPC_CONTRACT.md §7 status table.</summary>
    public static int StatusFor(IpcError e) => e switch
    {
        { Layer: Layers.Validation } => 400,
        { Kind: ErrorKinds.NotFound } => 404,
        { Kind: ErrorKinds.Forbidden } => 403,
        { Kind: ErrorKinds.Unprocessable } => 422,
        { Layer: Layers.Timeout } => 504,
        { Layer: Layers.Cancelled } => 499,
        { Layer: Layers.Busy } or { Layer: Layers.Transport } or { HostFatal: true } or { Retryable: true } => 503,
        { Layer: Layers.Runtime } => 422,
        { Layer: Layers.Connector } => 502,
        _ => 500
    };

    private const string InvalidBody = "__invalidBody";

    /// <summary>The JSON body, an empty object for no body, or a marker the pipe call turns into 400.</summary>
    private static async Task<JsonObject> Body(HttpContext c)
    {
        if (c.Request.ContentLength == 0 ||
            (c.Request.ContentLength is null && !c.Request.Headers.ContainsKey("Transfer-Encoding")))
            return new JsonObject();
        try { return await JsonNode.ParseAsync(c.Request.Body) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject { [InvalidBody] = true }; }
    }

    private static JsonObject With(JsonObject o, string k, string v) { o[k] = v; return o; }

    /// <summary>Boolean query flags of the old write routes, copied only when present.</summary>
    private static JsonObject WriteFlags(JsonObject a, IQueryCollection q, params string[] names)
    {
        foreach (var n in names)
            if (q.TryGetValue(n, out var v)) a[n] = Truthy(v.ToString()) ?? false;
        return a;
    }

    /// <summary>The document list's own control words (main.os:22309), matched exactly.</summary>
    private static readonly HashSet<string> DocumentControlParams = new(StringComparer.Ordinal)
    {
        "limit", "offset", "fields", "cursorDate", "syncOrder", "after", "tabular", "bulk", "from", "to", "skipTotal"
    };

    /// <summary>
    /// The old document list parameters: <c>cursorDate, syncOrder, tabular, from, to, after,
    /// skipTotal</c> plus the catalog ones; every other parameter is a filter. Dates stay text —
    /// the host parses them and rejects a bad one (the old code silently dropped the cursor).
    /// </summary>
    public static JsonObject DocumentArgs(string name, IQueryCollection q)
    {
        var a = new JsonObject { ["document"] = name };
        if (q.TryGetValue("limit", out var l)) a["limit"] = int.TryParse(l, out int li) ? li : (JsonNode)l.ToString();
        if (q.TryGetValue("offset", out var o)) a["offset"] = int.TryParse(o, out int oi) ? oi : (JsonNode)o.ToString();
        if (q.TryGetValue("after", out var af) && af.ToString().Length > 0) a["after"] = af.ToString();
        if (q.TryGetValue("skipTotal", out var st)) a["skipTotal"] = Truthy(st.ToString()) ?? false;
        if (q.TryGetValue("tabular", out var tb)) a["tabular"] = Truthy(tb.ToString()) ?? true;
        if (q.TryGetValue("syncOrder", out var so)) a["order"] = so.ToString().Trim().ToLowerInvariant() == "asc" ? "asc" : "desc";
        foreach (var k in new[] { "cursorDate", "from", "to" })
            if (q.TryGetValue(k, out var d) && d.ToString().Length > 0) a[k] = d.ToString();
        if (q.TryGetValue("fields", out var f) && f.ToString().Length > 0)
            a["fields"] = f == "-" ? new JsonArray()
                : new JsonArray(f.ToString().Split(',').Select(s => (JsonNode)JsonValue.Create(s.Trim())!).ToArray());

        var filters = new JsonObject();
        foreach (var (k, v) in q)
            if (!DocumentControlParams.Contains(k)) filters[k] = v.ToString();
        if (filters.Count > 0) a["filters"] = filters;
        return a;
    }

    /// <summary>The old register route's parameters (main.os:22487): limit, offset, cursorDate, syncOrder, to, skipTotal.</summary>
    public static JsonObject RegisterArgs(string kind, string name, IQueryCollection q)
    {
        var a = new JsonObject { ["kind"] = kind, ["register"] = name };
        if (q.TryGetValue("limit", out var l)) a["limit"] = int.TryParse(l, out int li) ? li : (JsonNode)l.ToString();
        if (q.TryGetValue("offset", out var o)) a["offset"] = int.TryParse(o, out int oi) ? oi : (JsonNode)o.ToString();
        if (q.TryGetValue("skipTotal", out var st)) a["skipTotal"] = Truthy(st.ToString()) ?? false;
        if (q.TryGetValue("syncOrder", out var so)) a["order"] = so.ToString().Trim().ToLowerInvariant() == "asc" ? "asc" : "desc";
        foreach (var k in new[] { "cursorDate", "to", "recorderDocument", "recorderId" })
            if (q.TryGetValue(k, out var d) && d.ToString().Length > 0) a[k] = d.ToString();
        return a;
    }

    /// <summary>ПрочитатьБулевоПараметр (main.os:3245): yes/no words, null when neither.</summary>
    private static bool? Truthy(string s) => s.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "да" or "истина" => true,
        "false" or "0" or "no" or "нет" or "ложь" => false,
        _ => null
    };

    /// <summary>
    /// Query parameters the old adapter treated as control, not as filters (main.os:21803).
    /// Matched exactly here — the old substring match turned a filter named e.g. "st" into
    /// a control word.
    /// </summary>
    private static readonly HashSet<string> ControlParams = new(StringComparer.Ordinal)
    {
        "limit", "offset", "fields", "after", "skipTotal",
        "post", "syncOrder", "cursorDate", "tabular", "exchange", "hard", "bulk", "to", "from"
    };

    /// <summary>
    /// <c>?limit&amp;offset&amp;fields&amp;after&amp;skipTotal</c> as the old adapter took them; every
    /// other parameter is an equality filter. <c>fields=-</c> means base fields only.
    /// A malformed number becomes a string the host rejects as a validation error.
    /// </summary>
    public static JsonObject CatalogArgs(string name, IQueryCollection q)
    {
        var a = new JsonObject { ["catalog"] = name };
        if (q.TryGetValue("limit", out var l)) a["limit"] = int.TryParse(l, out int li) ? li : (JsonNode)l.ToString();
        if (q.TryGetValue("offset", out var o)) a["offset"] = int.TryParse(o, out int oi) ? oi : (JsonNode)o.ToString();
        if (q.TryGetValue("after", out var af) && af.ToString().Length > 0) a["after"] = af.ToString();
        if (q.TryGetValue("skipTotal", out var st))
            a["skipTotal"] = st.ToString().ToLowerInvariant() is "1" or "true" or "yes" or "да" or "истина";
        if (q.TryGetValue("fields", out var f) && f.ToString().Length > 0)
            a["fields"] = f == "-" ? new JsonArray()
                : new JsonArray(f.ToString().Split(',').Select(s => (JsonNode)JsonValue.Create(s.Trim())!).ToArray());

        var filters = new JsonObject();
        foreach (var (k, v) in q)
            if (!ControlParams.Contains(k)) filters[k] = v.ToString();
        if (filters.Count > 0) a["filters"] = filters;
        return a;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
