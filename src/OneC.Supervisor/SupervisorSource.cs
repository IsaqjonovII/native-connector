using System.Text.Json.Nodes;
using OneC.EventLog;
using OneC.Ipc;
using OneC.Sync;

namespace OneC.Supervisor;

/// <summary>The sync's reads (<see cref="IOneCSource"/>) over this supervisor's host ops and change feed.</summary>
public sealed class SupervisorSource : IOneCSource
{
    private readonly Supervisor _sup;
    public int DeadlineMs { get; init; } = 600_000;

    public SupervisorSource(Supervisor sup) => _sup = sup;

    public async Task<long> CountAsync(string baseName, SyncTable t, CancellationToken ct)
    {
        var a = t.Kind switch
        {
            TableKind.Catalog => new JsonObject { ["catalog"] = t.OneCName, ["limit"] = 0 },
            TableKind.Document => With(new JsonObject { ["document"] = t.OneCName, ["limit"] = 0 }, "from", t.From),
            _ => new JsonObject { ["kind"] = t.RegisterKind, ["register"] = t.OneCName, ["limit"] = 0 }
        };
        var r = await Call(baseName, t.Kind switch { TableKind.Catalog => Ops.Catalog, TableKind.Document => Ops.Document, _ => Ops.Register }, a, ct);
        return r["totalCount"]!.GetValue<long>();
    }

    public async Task<List<JsonObject>> CatalogPageAsync(string baseName, string catalog, string after, int limit, CancellationToken ct) =>
        Rows(await Call(baseName, Ops.Catalog, new JsonObject { ["catalog"] = catalog, ["limit"] = limit, ["after"] = after, ["skipTotal"] = true }, ct));

    public async Task<JsonObject?> CatalogByIdAsync(string baseName, string catalog, string id, CancellationToken ct)
    {
        var resp = await _sup.SendAsync(baseName, Ops.Catalog, new JsonObject { ["catalog"] = catalog, ["id"] = id }, DeadlineMs, ct);
        if (!resp.Ok && resp.Error!.Kind == ErrorKinds.NotFound) return null;
        return Ok(resp)["row"] as JsonObject;
    }

    public async Task<(List<JsonObject> Rows, string? NextCursorDate, int NextSkip)> DocumentPageAsync(
        string baseName, string document, string? cursorDate, int skip, int limit, DateTime? from, CancellationToken ct)
    {
        var a = new JsonObject { ["document"] = document, ["limit"] = limit, ["offset"] = skip, ["order"] = "asc", ["skipTotal"] = true };
        if (cursorDate is not null) a["cursorDate"] = cursorDate;
        With(a, "from", from);
        var r = await Call(baseName, Ops.Document, a, ct);
        return (Rows(r), r["nextCursorDate"]?.GetValue<string>(), r["nextCursorSkip"]?.GetValue<int>() ?? 0);
    }

    public async Task<List<JsonObject>> DocumentsByIdsAsync(string baseName, string document, IReadOnlyList<string> ids, CancellationToken ct) =>
        Rows(await Call(baseName, Ops.Document,
                        new JsonObject { ["document"] = document, ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) }, ct));

    public async Task<(List<JsonObject> Rows, string? NextCursorDate, int NextSkip, bool HasMore)> RegisterPageAsync(
        string baseName, SyncTable t, string? cursorDate, int skip, int limit, CancellationToken ct)
    {
        var a = new JsonObject { ["kind"] = t.RegisterKind, ["register"] = t.OneCName, ["limit"] = limit, ["offset"] = skip, ["order"] = "asc", ["skipTotal"] = true };
        if (cursorDate is not null) a["cursorDate"] = cursorDate;
        var r = await Call(baseName, Ops.Register, a, ct);
        return (Rows(r), r["nextCursorDate"]?.GetValue<string>(), r["nextCursorSkip"]?.GetValue<int>() ?? 0, r["hasMore"]?.GetValue<bool>() ?? false);
    }

    public async Task<List<JsonObject>> RegisterByRecorderAsync(string baseName, SyncTable t, string recorderDocument, string recorderId, CancellationToken ct) =>
        Rows(await Call(baseName, Ops.Register, new JsonObject
        {
            ["kind"] = t.RegisterKind, ["register"] = t.OneCName, ["limit"] = 100_000, ["skipTotal"] = true,
            ["recorderDocument"] = recorderDocument, ["recorderId"] = recorderId
        }, ct));

    public Task<ChangeBatch> ChangesAsync(string baseName, LogCursor? cursor, CancellationToken ct) =>
        Task.Run(() => _sup.Changes(baseName, cursor, EventLogReader.DefaultMaxBytes), ct);

    private async Task<JsonObject> Call(string baseName, string op, JsonObject args, CancellationToken ct) =>
        Ok(await _sup.SendAsync(baseName, op, args, DeadlineMs, ct));

    private static JsonObject Ok(IpcResponse r) =>
        r.Ok ? r.Result as JsonObject ?? new JsonObject()
             : throw new InvalidOperationException($"{r.Error!.Layer}: {r.Error.Message}");

    private static List<JsonObject> Rows(JsonObject r) =>
        r["rows"] is JsonArray a ? a.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() : new List<JsonObject>();

    private static JsonObject With(JsonObject a, string key, DateTime? d)
    {
        if (d is { } v) a[key] = v.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }
}
