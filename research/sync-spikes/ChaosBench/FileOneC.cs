using System.Text.Json.Nodes;
using OneC.Sync.Source;

namespace ChaosBench;

/// <summary>The engine's view of the fake 1C: reads the world file (re-loaded when it changes), with the host's paging rules.</summary>
public sealed class FileOneC(string path) : IOneCReader
{
    private World? _w;
    private DateTime _at;
    private readonly Lock _gate = new();

    private World W()
    {
        lock (_gate)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(path);
                    if (_w is null || t != _at) { _w = World.Load(path); _at = t; }
                    return _w;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(20); }      // the mutator is replacing it
            }
        }
    }

    public Task<long> CountAsync(string baseId, TablePlan t, CancellationToken ct) =>
        Task.FromResult((long)(t.Name == "Cat" ? W().Cats.Count : t.Name == "Doc" ? W().Docs.Count : W().Docs.Values.Sum(d => d.Lines.Count)));

    public Task<SourcePage> CatalogPageAsync(string baseId, string catalog, string? after, int limit, CancellationToken ct)
    {
        var rows = W().Cats.OrderBy(kv => kv.Key, StringComparer.Ordinal).Where(kv => after is null || string.CompareOrdinal(kv.Key, after) > 0)
                          .Take(limit).Select(kv => World.CatRow(kv.Key, kv.Value)).ToList();
        return Task.FromResult(new SourcePage(rows, rows.Count == limit, NextAfter: rows.Count == limit ? (string)rows[^1]["id"]! : null));
    }

    public Task<SourcePage> DocumentPageAsync(string baseId, string document, string? cursorDate, int skip, int limit, DateTime? from, DateTime? to, CancellationToken ct) =>
        Task.FromResult(DatePage(W().Docs.Select(kv => World.DocRow(kv.Key, kv.Value)).ToList(), "date", cursorDate, skip, limit));

    public Task<SourcePage> RegisterPageAsync(string baseId, TablePlan t, string? cursorDate, int skip, string? afterKey, int limit, DateTime? to, CancellationToken ct) =>
        Task.FromResult(DatePage(W().Docs.SelectMany(kv => World.Moves(kv.Key, kv.Value)).ToList(), "Период", cursorDate, skip, limit));

    public Task<SourcePage> ChartPageAsync(string baseId, string chart, int offset, int limit, CancellationToken ct) => Task.FromResult(new SourcePage(new(), false));
    public Task<List<PeriodSlice>> SlicesAsync(string baseId, TablePlan t, int parts, CancellationToken ct) => Task.FromResult(new List<PeriodSlice> { new(null, null, 0) });

    public Task<List<JsonObject>> ByIdsAsync(string baseId, TablePlan t, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var w = W();
        return Task.FromResult(ids.Select(id => t.Name == "Cat"
            ? (w.Cats.TryGetValue(id, out var c) ? World.CatRow(id, c) : null)
            : (w.Docs.TryGetValue(id, out var d) ? World.DocRow(id, d) : null)).OfType<JsonObject>().ToList());
    }

    public Task<(List<(string Id, string? Version)> Rows, string? Next)> VersionPageAsync(string baseId, TablePlan t, string? after, int limit, CancellationToken ct)
    {
        var w = W();
        var all = t.Name == "Cat" ? w.Cats.Select(kv => (kv.Key, (string?)World.Version(kv.Value.V))) : w.Docs.Select(kv => (kv.Key, (string?)World.Version(kv.Value.V)));
        var rows = all.OrderBy(x => x.Key, StringComparer.Ordinal).Where(x => after is null || string.CompareOrdinal(x.Key, after) > 0).Take(limit).ToList();
        return Task.FromResult((rows, rows.Count == limit ? rows[^1].Key : (string?)null));
    }

    public Task<IReadOnlyList<string>> RecorderTypesAsync(string baseId, TablePlan register, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "Doc" });

    public Task<string?> RecorderOfAsync(string baseId, IReadOnlyList<TablePlan> registers, string id, CancellationToken ct) =>
        Task.FromResult(W().Docs.ContainsKey(id) ? "Doc" : (string?)null);

    public Task<SourcePage> MovementsAsync(string baseId, TablePlan register, string recorderDocument, string recorderId, int? afterLine, int limit, CancellationToken ct)
    {
        var rows = W().Docs.TryGetValue(recorderId, out var d)
            ? World.Moves(recorderId, d).Where(m => afterLine is null || (int)m["lineNo"]! > afterLine).Take(limit).ToList() : new List<JsonObject>();
        return Task.FromResult(new SourcePage(rows, rows.Count >= limit, NextLine: rows.Count > 0 ? (int)rows[^1]["lineNo"]! : null));
    }

    private static SourcePage DatePage(List<JsonObject> all, string field, string? cursorDate, int skip, int limit)
    {
        var ordered = all.OrderBy(r => (string)r[field]!, StringComparer.Ordinal)
                         .ThenBy(r => (string?)r["id"] ?? (string?)r["recorderRef"], StringComparer.Ordinal).ThenBy(r => (int?)r["lineNo"] ?? 0)
                         .Where(r => cursorDate is null || string.CompareOrdinal((string)r[field]!, cursorDate) >= 0).ToList();
        var rows = ordered.Skip(skip).Take(limit).ToList();
        if (rows.Count == 0) return new SourcePage(rows, false);
        string last = (string)rows[^1][field]!;
        int onLast = rows.Count(r => (string)r[field]! == last);
        return new SourcePage(rows, rows.Count == limit, last, cursorDate == last ? skip + onLast : onLast);
    }
}
