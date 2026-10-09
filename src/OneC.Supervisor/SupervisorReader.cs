using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Ipc;
using OneC.Sync.Source;

namespace OneC.Supervisor;

/// <summary>
/// The sync engine's 1C reads (<see cref="IOneCReader"/>) over this supervisor's host ops, asking for the
/// sync-only extras (IPC_CONTRACT §3): register sync keys, natural-key paging, object versions.
/// </summary>
public sealed class SupervisorReader(Supervisor sup) : IOneCReader
{
    public int DeadlineMs { get; init; } = 600_000;

    public async Task<long> CountAsync(string baseId, TablePlan t, CancellationToken ct)
    {
        var (op, a) = t.Family switch
        {
            Families.Catalog => (Ops.Catalog, new JsonObject { ["catalog"] = t.Name, ["limit"] = 0 }),
            Families.Document => (Ops.Document, With(new JsonObject { ["document"] = t.Name, ["limit"] = 0 }, "from", t.From)),
            Families.Chart => (Ops.Chart, new JsonObject { ["chart"] = t.Name, ["limit"] = 0 }),
            _ => (Ops.Register, new JsonObject { ["kind"] = Families.RegisterKind(t.Family), ["register"] = t.Name, ["limit"] = 0 })
        };
        return (await Call(baseId, op, a, ct))["totalCount"]!.GetValue<long>();
    }

    public async Task<SourcePage> CatalogPageAsync(string baseId, string catalog, string? after, int limit, CancellationToken ct)
    {
        var r = await Call(baseId, Ops.Catalog, new JsonObject
        {
            ["catalog"] = catalog, ["limit"] = limit, ["after"] = after ?? "00000000-0000-0000-0000-000000000000",
            ["skipTotal"] = true, ["withVersion"] = true
        }, ct);
        var rows = Rows(r);
        return new SourcePage(rows, r["next"] is not null, NextAfter: r["next"]?.GetValue<string>());
    }

    public async Task<SourcePage> DocumentPageAsync(string baseId, string document, string? cursorDate, int skip, int limit,
                                                    DateTime? from, DateTime? to, CancellationToken ct)
    {
        var a = new JsonObject
        {
            ["document"] = document, ["limit"] = limit, ["offset"] = skip, ["order"] = "asc", ["skipTotal"] = true, ["withVersion"] = true
        };
        if (cursorDate is not null) a["cursorDate"] = cursorDate;
        With(a, "from", from);
        With(a, "to", to);
        var r = await Call(baseId, Ops.Document, a, ct);
        var rows = Rows(r);
        return new SourcePage(rows, rows.Count >= limit, r["nextCursorDate"]?.GetValue<string>(), r["nextCursorSkip"]?.GetValue<int>() ?? 0);
    }

    public async Task<SourcePage> RegisterPageAsync(string baseId, TablePlan t, string? cursorDate, int skip, string? afterKey, int limit,
                                                    DateTime? to, CancellationToken ct)
    {
        var a = new JsonObject
        {
            ["kind"] = Families.RegisterKind(t.Family), ["register"] = t.Name, ["limit"] = limit, ["offset"] = skip,
            ["order"] = "asc", ["skipTotal"] = true, ["syncKeys"] = true
        };
        if (cursorDate is not null) a["cursorDate"] = cursorDate;
        if (afterKey is not null) a["afterKey"] = afterKey;
        With(a, "to", to);
        var r = await Call(baseId, Ops.Register, a, ct);
        return new SourcePage(Rows(r), r["hasMore"]?.GetValue<bool>() ?? false, r["nextCursorDate"]?.GetValue<string>(),
                              r["nextCursorSkip"]?.GetValue<int>() ?? 0, NextKey: r["nextKey"]?.GetValue<string>());
    }

    public async Task<SourcePage> ChartPageAsync(string baseId, string chart, int offset, int limit, CancellationToken ct)
    {
        var r = await Call(baseId, Ops.Chart, new JsonObject { ["chart"] = chart, ["limit"] = limit, ["offset"] = offset }, ct);
        return new SourcePage(Rows(r), r["hasMore"]?.GetValue<bool>() ?? false);
    }

    public async Task<List<PeriodSlice>> SlicesAsync(string baseId, TablePlan t, int parts, CancellationToken ct)
    {
        string kind = t.Family == Families.Document ? "document" : Families.RegisterKind(t.Family);
        var a = With(new JsonObject { ["kind"] = kind, ["name"] = t.Name, ["parts"] = parts }, "from", t.From);
        var r = await Call(baseId, Ops.Slices, a, ct);
        return r["slices"]!.AsArray().Select(s => new PeriodSlice(Date(s!["from"]), Date(s["to"]), s["rows"]?.GetValue<long>() ?? 0)).ToList();
    }

    public async Task<List<JsonObject>> ByIdsAsync(string baseId, TablePlan t, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var arr = new JsonArray(ids.Select(i => (JsonNode)i).ToArray());
        if (t.Family == Families.Document)
            return Rows(await Call(baseId, Ops.Document, new JsonObject { ["document"] = t.Name, ["ids"] = arr, ["withVersion"] = true }, ct));
        // Catalogs have no batch route: one read per id; an object that is gone is "not found: object".
        var rows = new List<JsonObject>();
        foreach (var id in ids)
        {
            var r = await sup.SendAsync(baseId, Ops.Catalog, new JsonObject { ["catalog"] = t.Name, ["id"] = id, ["withVersion"] = true }, DeadlineMs, ct);
            if (!r.Ok && r.Error is { Kind: ErrorKinds.NotFound, NotFoundScope: "object" }) continue;
            if (Ok(r)["row"] is JsonObject row) { row.Parent?.AsObject().Remove("row"); rows.Add(row); }
        }
        return rows;
    }

    public async Task<(List<(string Id, string? Version)> Rows, string? Next)> VersionPageAsync(string baseId, TablePlan t, string? after, int limit,
                                                                                             CancellationToken ct)
    {
        string prefix = t.Family switch { Families.Document => "Документ.", Families.Catalog => "Справочник.", _ => "ПланСчетов." };
        var a = new JsonObject { ["table"] = prefix + t.Name, ["limit"] = limit };
        if (after is not null) a["after"] = after;
        var r = await Call(baseId, Ops.Versions, a, ct);
        var rows = r["rows"]!.AsArray().Select(n => (n!["id"]!.GetValue<string>(), n["version"]?.GetValue<string>())).ToList();
        return (rows, r["next"]?.GetValue<string>());
    }

    public async Task<IReadOnlyList<string>> RecorderTypesAsync(string baseId, TablePlan register, CancellationToken ct) =>
        (await Call(baseId, Ops.Recorders, new JsonObject { ["kind"] = Families.RegisterKind(register.Family), ["register"] = register.Name }, ct))
            ["types"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    public async Task<string?> RecorderOfAsync(string baseId, IReadOnlyList<TablePlan> registers, string id, CancellationToken ct) =>
        (await Call(baseId, Ops.RecorderOf, new JsonObject
        {
            ["id"] = id,
            ["registers"] = new JsonArray(registers.Select(r => (JsonNode)new JsonObject
            {
                ["kind"] = Families.RegisterKind(r.Family), ["register"] = r.Name
            }).ToArray())
        }, ct))["type"]?.GetValue<string>();

    public async Task<SourcePage> MovementsAsync(string baseId, TablePlan register, string recorderDocument, string recorderId, int? afterLine,
                                                 int limit, CancellationToken ct)
    {
        var a = new JsonObject
        {
            ["kind"] = Families.RegisterKind(register.Family), ["register"] = register.Name, ["limit"] = limit, ["skipTotal"] = true,
            ["recorderDocument"] = recorderDocument, ["recorderId"] = recorderId, ["syncKeys"] = true
        };
        if (afterLine is { } l) a["afterLine"] = l;
        var r = await Call(baseId, Ops.Register, a, ct);
        return new SourcePage(Rows(r), r["hasMore"]?.GetValue<bool>() ?? false, NextLine: r["nextLine"]?.GetValue<int>());
    }

    public async Task<ReferrerHits> ReferrersAsync(string baseId, TablePlan catalog, string id, IReadOnlyList<TablePlan> targets, CancellationToken ct)
    {
        var byName = new Dictionary<(string, string), TablePlan>();
        var list = new JsonArray();
        foreach (var t in targets)
        {
            string? kind = t.Family switch
            {
                Families.Document => "document",
                Families.Catalog => "catalog",
                _ when Families.IsRecorded(t.Family) => Families.RegisterKind(t.Family),
                _ => null                                                   // charts and independent registers are refreshed whole
            };
            if (kind is null) continue;
            byName[(kind, t.Name)] = t;
            var o = new JsonObject { ["kind"] = kind, ["name"] = t.Name };
            if (t.From is { } f) o["from"] = f.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            list.Add(o);
        }
        if (list.Count == 0) return new ReferrerHits(new List<(string, string)>(), false);
        var r = await Call(baseId, Ops.Referrers, new JsonObject { ["catalog"] = catalog.Name, ["id"] = id, ["targets"] = list }, ct);
        var hits = new List<(string, string)>();
        foreach (var h in r["hits"]!.AsArray().OfType<JsonObject>())
        {
            string kind = (string)h["kind"]!, hid = ((string)h["id"]!).ToLowerInvariant();
            if (kind == "recorder") hits.Add((OneC.Sync.Incremental.EventCoalescer.UnknownRecorderTable, hid));
            else if (byName.TryGetValue((kind, (string)h["name"]!), out var t)) hits.Add((t.Table, hid));
        }
        return new ReferrerHits(hits, (bool?)r["truncated"] ?? false);
    }

    private async Task<JsonObject> Call(string baseId, string op, JsonObject args, CancellationToken ct) =>
        Ok(await sup.SendAsync(baseId, op, args, DeadlineMs, ct));

    private static JsonObject Ok(IpcResponse r)
    {
        if (r.Ok) return r.Result as JsonObject ?? new JsonObject();
        if (r.Error is { Kind: ErrorKinds.NotFound, NotFoundScope: "metadata" }) throw new OneCMetadataMissingException(r.Error.Message);
        throw new OneCReadException(r.Error!);
    }

    /// <summary>The rows, detached from the response (no copy: the response is not used again).</summary>
    private static List<JsonObject> Rows(JsonObject r)
    {
        if (r["rows"] is not JsonArray a) return new List<JsonObject>();
        var rows = a.OfType<JsonObject>().ToList();
        a.Clear();
        return rows;
    }

    private static JsonObject With(JsonObject a, string key, DateTime? d)
    {
        if (d is { } v) a[key] = v.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return a;
    }

    private static DateTime? Date(JsonNode? n) =>
        n?.GetValue<string>() is { Length: > 0 } s ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
}

/// <summary>A host op failed; the IPC error travels with it (layer, not-found scope, retryable …).</summary>
public sealed class OneCReadException(IpcError error) : Exception($"{error.Layer}: {error.Message}")
{
    public IpcError Error { get; } = error;
}
