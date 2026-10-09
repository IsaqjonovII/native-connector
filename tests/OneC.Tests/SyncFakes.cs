using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Sync.Source;

namespace OneC.Tests;

/// <summary>
/// An in-memory 1C for sync tests: tables of rows with the host's paging rules (catalog keyset by
/// id, date cursor + skip for documents and registers, natural-key keyset, chart offset).
/// </summary>
internal sealed class FakeOneC : IOneCReader
{
    public readonly Dictionary<string, List<JsonObject>> Tables = new(StringComparer.Ordinal);
    public int ReadDelayMs;
    public int Reads;
    public int ConcurrentReads;
    public int PeakConcurrentReads;
    public Func<string, bool>? FailRead;

    public static JsonObject CatalogRow(int i) => new() { ["id"] = Guid(i), ["name"] = "item " + i, ["deletionMark"] = false, ["dataVersion"] = Version(1) };

    public static JsonObject DocumentRow(int i, DateTime date) => new()
    {
        ["id"] = Guid(i), ["number"] = "N" + i, ["date"] = date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["posted"] = true, ["Сумма"] = i * 10.5m, ["orgRef"] = Guid(9_000_000), ["dataVersion"] = Version(3)
    };

    public static JsonObject MovementRow(int recorder, int line, DateTime date) => new()
    {
        ["Период"] = date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["Регистратор"] = "doc " + recorder,
        ["recorderRef"] = Guid(recorder), ["lineNo"] = line, ["НомерСтроки"] = line, ["Сумма"] = line * 1.25m, ["orgRef"] = Guid(9_000_000)
    };

    public static string Guid(int i) => new Guid(i, 0, 0, new byte[8]).ToString("D");
    public static string Version(long v) => Convert.ToBase64String(BitConverter.GetBytes(v).Reverse().ToArray());

    private async Task<T> Read<T>(string table, Func<T> f, CancellationToken ct)
    {
        int now = Interlocked.Increment(ref ConcurrentReads);
        lock (this) PeakConcurrentReads = Math.Max(PeakConcurrentReads, now);
        try
        {
            Interlocked.Increment(ref Reads);
            if (ReadDelayMs > 0) await Task.Delay(ReadDelayMs, ct);
            if (FailRead?.Invoke(table) == true) throw new IOException("1C read failed");
            return f();
        }
        finally { Interlocked.Decrement(ref ConcurrentReads); }
    }

    private List<JsonObject> Rows(string name) => Tables[name].Select(r => (JsonObject)r.DeepClone()).ToList();

    public Task<long> CountAsync(string baseId, TablePlan t, CancellationToken ct) => Task.FromResult((long)Tables[t.Name].Count);

    public Task<SourcePage> CatalogPageAsync(string baseId, string catalog, string? after, int limit, CancellationToken ct) => Read(catalog, () =>
    {
        var rows = Rows(catalog).OrderBy(r => (string)r["id"]!, StringComparer.Ordinal)
                                .Where(r => after is null || string.CompareOrdinal((string)r["id"]!, after) > 0).Take(limit).ToList();
        return new SourcePage(rows, rows.Count == limit, NextAfter: rows.Count == limit ? (string)rows[^1]["id"]! : null);
    }, ct);

    public Task<SourcePage> DocumentPageAsync(string baseId, string document, string? cursorDate, int skip, int limit,
                                              DateTime? from, DateTime? to, CancellationToken ct) =>
        Read(document, () => DatePage(Rows(document), "date", cursorDate, skip, limit, from, to), ct);

    public Task<SourcePage> RegisterPageAsync(string baseId, TablePlan t, string? cursorDate, int skip, string? afterKey, int limit,
                                              DateTime? to, CancellationToken ct) => Read(t.Name, () =>
    {
        if (t.Family == Families.IndependentInfoRegister)
        {
            var rows = Rows(t.Name).OrderBy(r => (string)r["naturalKey"]!, StringComparer.Ordinal)
                                   .Where(r => afterKey is null || string.CompareOrdinal((string)r["naturalKey"]!, afterKey) > 0).Take(limit).ToList();
            return new SourcePage(rows, rows.Count == limit, NextKey: rows.Count == limit ? (string)rows[^1]["naturalKey"]! : null);
        }
        return DatePage(Rows(t.Name), "Период", cursorDate, skip, limit, null, to);
    }, ct);

    public Task<SourcePage> ChartPageAsync(string baseId, string chart, int offset, int limit, CancellationToken ct) => Read(chart, () =>
    {
        var rows = Rows(chart).OrderBy(r => (string)r["Код"]!, StringComparer.Ordinal).Skip(offset).Take(limit).ToList();
        return new SourcePage(rows, rows.Count == limit);
    }, ct);

    public Task<List<PeriodSlice>> SlicesAsync(string baseId, TablePlan t, int parts, CancellationToken ct)
    {
        string field = t.Family == Families.Document ? "date" : "Период";
        var dates = Tables[t.Name].Select(r => DateTime.Parse((string)r[field]!, CultureInfo.InvariantCulture)).Order().ToList();
        var bounds = Enumerable.Range(1, parts - 1).Select(i => dates[i * dates.Count / parts]).Distinct().ToList();
        var slices = new List<PeriodSlice>();
        DateTime? lo = null;
        foreach (var b in bounds) { slices.Add(new PeriodSlice(lo, b, 0)); lo = b; }
        slices.Add(new PeriodSlice(lo, null, 0));
        return Task.FromResult(slices);
    }

    /// <summary>Tables that "do not exist" in this 1C: reads throw like the host's metadata not-found.</summary>
    public readonly HashSet<string> MissingTables = new(StringComparer.Ordinal);

    public Task<List<JsonObject>> ByIdsAsync(string baseId, TablePlan t, IReadOnlyList<string> ids, CancellationToken ct) => Read(t.Name, () =>
    {
        if (MissingTables.Contains(t.Name)) throw new OneCMetadataMissingException($"{t.Name} not found");
        var want = new HashSet<string>(ids, StringComparer.Ordinal);
        return Rows(t.Name).Where(r => want.Contains((string)r["id"]!)).ToList();
    }, ct);

    public Task<(List<(string Id, string? Version)> Rows, string? Next)> VersionPageAsync(string baseId, TablePlan t, string? after, int limit,
                                                                                       CancellationToken ct) => Read(t.Name, () =>
    {
        // 1C's own ref order is not the GUID text order: the fake uses a different order (reversed) on purpose.
        var rows = Rows(t.Name).OrderByDescending(r => (string)r["id"]!, StringComparer.Ordinal)
                               .SkipWhile(r => after is not null && string.CompareOrdinal((string)r["id"]!, after) >= 0).Take(limit)
                               .Select(r => ((string)r["id"]!, (string?)r["dataVersion"])).ToList();
        return (rows, rows.Count == limit ? rows[^1].Item1 : (string?)null);
    }, ct);

    /// <summary>register name → document types posting to it; default: "Doc".</summary>
    public readonly Dictionary<string, List<string>> RecorderTypes = new(StringComparer.Ordinal);
    /// <summary>recorder GUID → document type, for recorders not in a document table here.</summary>
    public readonly Dictionary<string, string> RecorderKinds = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> RecorderTypesAsync(string baseId, TablePlan register, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(RecorderTypes.TryGetValue(register.Name, out var l) ? l : new List<string> { "Doc" });

    public Task<string?> RecorderOfAsync(string baseId, IReadOnlyList<TablePlan> registers, string id, CancellationToken ct)
    {
        if (RecorderKinds.TryGetValue(id, out var k)) return Task.FromResult<string?>(k);
        var types = registers.SelectMany(r => RecorderTypes.TryGetValue(r.Name, out var l) ? l : new List<string> { "Doc" }).Distinct();
        return Task.FromResult(types.FirstOrDefault(t => Tables.TryGetValue(t, out var rows) && rows.Any(r => (string?)r["id"] == id)));
    }

    public Task<SourcePage> MovementsAsync(string baseId, TablePlan register, string recorderDocument, string recorderId, int? afterLine,
                                           int limit, CancellationToken ct) => Read(register.Name, () =>
    {
        var rows = (Tables.TryGetValue(register.Name, out var all) ? all : new List<JsonObject>())
            .Where(r => (string?)r["recorderRef"] == recorderId && (afterLine is null || (int)r["lineNo"]! > afterLine))
            .OrderBy(r => (int)r["lineNo"]!).Take(limit).Select(r => (JsonObject)r.DeepClone()).ToList();
        return new SourcePage(rows, rows.Count >= limit, NextLine: rows.Count > 0 ? (int)rows[^1]["lineNo"]! : null);
    }, ct);

    /// <summary>catalog item GUID → the (table, id) rows that reference it (what ReferrerSearch would find in 1C).</summary>
    public readonly Dictionary<string, List<(string Table, string Id)>> Referrers = new(StringComparer.Ordinal);
    public int ReferrerSearches;

    public Task<ReferrerHits> ReferrersAsync(string baseId, TablePlan catalog, string id, IReadOnlyList<TablePlan> targets, CancellationToken ct)
    {
        Interlocked.Increment(ref ReferrerSearches);
        var tables = targets.Select(t => t.Table).Append(Sync.Incremental.EventCoalescer.UnknownRecorderTable).ToHashSet(StringComparer.Ordinal);
        var hits = Referrers.TryGetValue(id, out var l) ? l.Where(h => tables.Contains(h.Table)).ToList() : new List<(string, string)>();
        return Task.FromResult(new ReferrerHits(hits, false));
    }

    /// <summary>The host's date cursor (D33/D34): Дата ≥ cursor, skip the rows already read on that date.</summary>
    private static SourcePage DatePage(List<JsonObject> all, string field, string? cursorDate, int skip, int limit, DateTime? from, DateTime? to)
    {
        DateTime D(JsonObject r) => DateTime.Parse((string)r[field]!, CultureInfo.InvariantCulture);
        var ordered = all.Where(r => (from is null || D(r) >= from) && (to is null || D(r) < to))
                         .OrderBy(D).ThenBy(r => (string?)r["id"] ?? (string?)r["recorderRef"], StringComparer.Ordinal).ThenBy(r => (int?)r["lineNo"] ?? 0)
                         .Where(r => cursorDate is null || D(r) >= DateTime.Parse(cursorDate, CultureInfo.InvariantCulture)).ToList();
        var rows = ordered.Skip(skip).Take(limit).ToList();
        if (rows.Count == 0) return new SourcePage(rows, false);
        string last = (string)rows[^1][field]!;
        int onLast = rows.Count(r => (string)r[field]! == last);
        int nextSkip = cursorDate == last ? skip + onLast : onLast;
        return new SourcePage(rows, rows.Count == limit, NextCursorDate: last, NextSkip: nextSkip);
    }
}
