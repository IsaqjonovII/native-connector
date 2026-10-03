using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record DocumentQuery
{
    /// <summary>Document type without the prefix; bank types accept either configuration's name.</summary>
    public required string Document { get; init; }

    /// <summary>Page size. 0 = count only.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Rows to skip after the contract filters (the old code skipped in every mode).</summary>
    public int Offset { get; init; }

    /// <summary>Null = every attribute; empty = none (base fields); unknown names are ignored and reported.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>See <see cref="DocumentFilters"/>.</summary>
    public IReadOnlyDictionary<string, object?>? Filters { get; init; }

    /// <summary>Date cursor: asc reads Дата &gt;= it, desc Дата &lt;= it; pair with <see cref="Offset"/> = next skip.</summary>
    public DateTime? CursorDate { get; init; }

    /// <summary>"asc" or "desc" (default) — the Дата order of a non-keyset read.</summary>
    public string Order { get; init; } = "desc";

    /// <summary>Keyset cursor: GUID of the last row of the previous page; the zero GUID starts.</summary>
    public string? After { get; init; }

    /// <summary>Read tabular sections (default). False = headers only.</summary>
    public bool Tabular { get; init; } = true;

    /// <summary>Window [From, To) on Дата — for the count too.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    public bool SkipTotal { get; init; }

    /// <summary>Sync: each row also carries <c>dataVersion</c> (ВерсияДанных from the same query).</summary>
    public bool WithVersion { get; init; }
}

public sealed record DocumentPage(
    List<Dictionary<string, object?>> Rows,
    long TotalCount,
    string? NextCursorDate,
    int NextCursorSkip,
    string? Next,
    IReadOnlyList<string> IgnoredFields,
    string Document,
    int SessionId);

/// <summary>
/// Document reads with the old adapter's row shape (ПолучитьДокументы, main.os:7421):
/// <c>id, number, date, posted, deletionMark, &lt;attributes&gt;, orgRef, tabularSections</c>.
/// Lists carry only sections that have rows; by-id and batch carry every section, empty ones
/// as [] (main.os:8413) — both shapes as the old code had them, because the connector
/// fingerprints each route's rows. Values by <see cref="LegacyValue"/>.
/// </summary>
public sealed class DocumentReadService
{
    public const int MaxLimit = 100_000;
    public const int BatchChunk = 200;

    private readonly SessionManager _sessions;

    public DocumentReadService(SessionManager sessions) => _sessions = sessions;

    public DocumentPage List(string baseName, DocumentQuery q, CancellationToken ct = default)
    {
        Validate(q);
        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx => ExecuteList(ctx, q, ct), ct));
    }

    /// <summary>One document in the by-id shape; not found is an error (404 at the edge).</summary>
    public Dictionary<string, object?> ById(string baseName, string document, string id, CancellationToken ct = default, bool withVersion = false)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException($"'{id}' is not a GUID", nameof(id));
        var rows = ByIds(baseName, document, new[] { id }, ct, withVersion);
        return rows.Count > 0 ? rows[0]
            : throw OneCException.Host($"Документ.{document} {id} not found", ErrorContext.None, "document", document);
    }

    /// <summary>
    /// Several documents in the by-id shape, in request order; unknown ids are simply absent and
    /// malformed ones skipped (the old batch route, main.os:8317). 1 + T queries per 200 ids.
    /// </summary>
    public List<Dictionary<string, object?>> ByIds(string baseName, string document, IReadOnlyList<string> ids, CancellationToken ct = default,
                                                   bool withVersion = false)
    {
        ValidateName(document);
        var wanted = ids.Select(i => Guid.TryParse(i?.Trim(), out var g) ? g.ToString("D") : null).OfType<string>().ToList();
        if (wanted.Count == 0) return new();

        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx =>
        {
            string name = DocumentSchemas.ResolveBankAlias(ctx, document);
            var schema = DocumentSchemas.Get(ctx, name);
            var attrs = schema.Attributes.ToList();
            var result = new List<Dictionary<string, object?>>(wanted.Count);
            foreach (var chunk in wanted.Chunk(BatchChunk))
            {
                using var scope = new ComScope();
                var manager = QueryKit.Manager(ctx, scope, "Документы", name);
                var refs = QueryKit.NewArray(ctx, scope);
                foreach (var g in chunk)
                {
                    using var one = new ComScope();
                    QueryKit.Add(ctx, refs, QueryKit.RefFrom(ctx, one, manager, g));
                }

                var query = QueryKit.NewQuery(ctx, scope, BuildSelect(schema, attrs, null, "Ссылка В (&refs)", null));
                QueryKit.SetParameter(ctx, query, "refs", refs);
                using var batch = new RefBatch(ctx);
                var rows = ReadRows(ctx, scope, query, schema, attrs, batch, ct, withVersion);
                var byId = rows.ToDictionary(r => (string)r["id"]!, StringComparer.Ordinal);
                TabularSections.Load(ctx, "Документ." + name, schema.Tabular, refs, byId, includeEmpty: true, ct, batch);
                batch.Patch(rows);
                foreach (var g in chunk)
                    if (byId.TryGetValue(g, out var row)) result.Add(row);
            }
            return result;
        }, ct));
    }

    private DocumentPage ExecuteList(SessionContext ctx, DocumentQuery q, CancellationToken ct)
    {
        string name = DocumentSchemas.ResolveBankAlias(ctx, q.Document);
        var schema = DocumentSchemas.Get(ctx, name);
        var (attrs, ignored) = Resolve(schema, q.Fields);
        var filters = DocumentFilters.Parse(q.Filters, schema);
        bool asc = q.Order.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase);

        var window = new List<(string Term, string Param, object? Value)>();
        if (q.From is { } from) window.Add(("Дата >= &fromBound", "fromBound", from));
        if (q.To is { } to) window.Add(("Дата < &toBound", "toBound", to));
        string? windowWhere = QueryKit.Join(window.Select(w => w.Term).ToArray());

        using var scope = new ComScope();

        // The count covers the window only — not the filters, not the cursor — because the
        // connector's back-dated gate compares it with what it holds for that window
        // (main.os:7435). skipTotal skips it (-1) except for a count-only call.
        long total = q.Limit <= 0 || !q.SkipTotal ? CatalogReadService.Count(ctx, scope, "Документ." + name, windowWhere, window) : -1;
        if (q.Limit <= 0) return new DocumentPage(new(), total, null, 0, null, ignored, name, ctx.SessionId);

        var cursorTerm = q.CursorDate is null ? null : asc ? "Дата >= &cursorDate" : "Дата <= &cursorDate";
        string? where = QueryKit.Join(filters.Where, cursorTerm, windowWhere);
        bool keyset = !string.IsNullOrEmpty(q.After);

        // Phase 1 — which documents, in which order: a plain query the planner serves from the
        // Дата / Ссылка index. With the rendered columns' LEFT JOINs in the same query,
        // PostgreSQL sorted the whole table instead: 4.6 s for a 20-row page of KAN
        // РеализацияТоваровУслуг.
        string pick = keyset
            // "Ссылка > &after" even for the zero GUID (the old document code dropped it there);
            // without it ORDER BY Ссылка is a scan + sort on PostgreSQL (main.os:6189).
            ? $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit} Ссылка КАК id ИЗ Документ.{name} ГДЕ {QueryKit.Join(where, "Ссылка > &after")} УПОРЯДОЧИТЬ ПО Ссылка"
            // Ссылка breaks ties inside one second, so date-cursor pages are exact prefixes.
            : $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset} Ссылка КАК id ИЗ Документ.{name}" + (where is null ? "" : " ГДЕ " + where) +
              $" УПОРЯДОЧИТЬ ПО Дата {(asc ? "ВОЗР" : "УБЫВ")}, Ссылка";

        var query = QueryKit.NewQuery(ctx, scope, pick);
        foreach (var f in filters.Terms) QueryKit.SetParameter(ctx, query, f.Param, f.Value);
        if (q.CursorDate is { } cd) QueryKit.SetParameter(ctx, query, "cursorDate", cd);
        foreach (var w in window) QueryKit.SetParameter(ctx, query, w.Param, w.Value);
        if (keyset) QueryKit.SetParameter(ctx, query, "after", QueryKit.RefByGuid(ctx, scope, "Документы", name, q.After!));

        var order = new List<string>();
        var refs = QueryKit.NewArray(ctx, scope);
        var picked = QueryKit.Execute(ctx, scope, query);
        while (picked.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            using var one = new ComScope();
            var r = one.Track(picked.Get("id", ctx.Error), "Ссылка");
            order.Add(OneCValue.RefGuid(r, ctx)!);
            QueryKit.Add(ctx, refs, r);
        }

        // Phase 2 — render just those documents, then keep the phase-1 order. The contract
        // filters, the offset and the limit apply in that order, as in the old loop.
        var rendered = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        using var batch = new RefBatch(ctx);
        if (order.Count > 0)
        {
            var render = QueryKit.NewQuery(ctx, scope, BuildSelect(schema, attrs, null, "Ссылка В (&refs)", null));
            QueryKit.SetParameter(ctx, render, "refs", refs);
            foreach (var row in ReadRows(ctx, scope, render, schema, attrs, batch, ct, q.WithVersion)) rendered[(string)row["id"]!] = row;
            // The contract filters below read rendered values; resolve before filtering.
            batch.Patch(rendered.Values);
        }
        var rows = new List<Dictionary<string, object?>>(Math.Min(q.Limit, order.Count));
        int skipped = 0;
        foreach (var id in order)
        {
            if (rows.Count >= q.Limit) break;
            if (!rendered.TryGetValue(id, out var row)) continue;          // gone between the two queries
            if (filters.HasPostFilter && !filters.Matches(row)) continue;
            if (skipped < q.Offset) { skipped++; continue; }
            rows.Add(row);
        }

        // The tabular query takes phase 1's refs; lines of documents not returned are skipped.
        if (q.Tabular && schema.Tabular.Count > 0 && rows.Count > 0)
        {
            using var linesBatch = new RefBatch(ctx);
            TabularSections.Load(ctx, "Документ." + name, schema.Tabular, refs,
                                 rows.ToDictionary(r => (string)r["id"]!, StringComparer.Ordinal), includeEmpty: false, ct, linesBatch);
            linesBatch.Patch(rows);
        }

        var (nextDate, nextSkip) = NextDateCursor(rows, q.CursorDate, q.Offset);
        // A full keyset page may have more after it; a short one was the end.
        string? next = keyset && order.Count == q.Limit ? order[^1] : null;
        return new DocumentPage(rows, total, nextDate, nextSkip, next, ignored, name, ctx.SessionId);
    }

    /// <summary>
    /// next_cursor_date / next_cursor_skip (main.os:8082): the last row's date and how many
    /// returned rows share it; accumulated with the incoming offset when the page did not get
    /// past the incoming cursor date (a long run of one date).
    /// </summary>
    internal static (string? Date, int Skip) NextDateCursor(List<Dictionary<string, object?>> rows, DateTime? cursorDate, int offset)
    {
        if (rows.Count == 0) return (null, 0);
        var last = rows[^1]["date"] as string;
        int onBoundary = 0;
        for (int i = rows.Count - 1; i >= 0 && Equals(rows[i]["date"], last); i--) onBoundary++;
        string incoming = cursorDate is { } c ? (string)LegacyValue.Scalar(c)! : "";
        return (last, incoming == last ? offset + onBoundary : onBoundary);
    }

    // ---------------- rows ----------------

    /// <summary>Every row of <paramref name="query"/>, rendered in the old shape (no tabular sections).</summary>
    private static List<Dictionary<string, object?>> ReadRows(
        SessionContext ctx, ComScope scope, object query, DocumentSchema schema, List<AttributeShape> attrs, RefBatch batch, CancellationToken ct,
        bool withVersion = false)
    {
        var cursor = QueryKit.Execute(ctx, scope, query);
        int orgIndex = schema.OrgAttribute is null ? -1 : attrs.FindIndex(a => a.Name == schema.OrgAttribute);
        var rows = new List<Dictionary<string, object?>>();
        while (cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            using var rowScope = new ComScope();
            var idRef = rowScope.Track(cursor.Get("id", ctx.Error), "Ссылка");
            var row = new Dictionary<string, object?>(attrs.Count + 8, StringComparer.Ordinal) { ["id"] = OneCValue.RefGuid(idRef, ctx) };
            if (schema.HasNumber) row["number"] = LegacyValue.Scalar(cursor.Get("num", ctx.Error));
            row["date"] = LegacyValue.Scalar(cursor.Get("d", ctx.Error));
            row["posted"] = cursor.GetBool("pst", ctx.Error);
            row["deletionMark"] = cursor.GetBool("dm", ctx.Error);
            for (int i = 0; i < attrs.Count; i++)
                row[attrs[i].Name] = LegacyValue.Read(cursor, "a" + i, attrs[i], ctx, batch);
            if (orgIndex >= 0 && QueryKit.OrgGuid(cursor, "a" + orgIndex, ctx) is { } org)
                row["orgRef"] = org;
            if (withVersion) row["dataVersion"] = cursor.Get("dv", ctx.Error) as string;
            rows.Add(row);
        }
        return rows;
    }

    // ---------------- query text ----------------

    internal static string BuildSelect(DocumentSchema s, IReadOnlyList<AttributeShape> attrs, int? first, string? where, string? orderBy)
    {
        static string F(string name) => QueryKit.Field(name);
        var cols = new List<string> { $"{F("Ссылка")} КАК id", $"{F("Дата")} КАК d", $"{F("Проведен")} КАК pst", $"{F("ПометкаУдаления")} КАК dm" };
        if (s.HasNumber) cols.Add($"{F("Номер")} КАК num");
        for (int i = 0; i < attrs.Count; i++) cols.Add(LegacyValue.Select(F(attrs[i].Name), "a" + i, attrs[i]));
        // Sync (S3): the version from the same query as the row, surfaced only with WithVersion.
        cols.Add($"{F("ВерсияДанных")} КАК dv");

        string sql = "ВЫБРАТЬ " + (first is { } n ? $"ПЕРВЫЕ {n} " : "") + string.Join(", ", cols) + $" ИЗ Документ.{s.Name} КАК {QueryKit.Alias}";
        if (where is not null) sql += " ГДЕ " + where;
        if (orderBy is not null) sql += " УПОРЯДОЧИТЬ ПО " + orderBy;
        return sql;
    }

    /// <summary>
    /// Projection like catalogs: unknown names dropped and reported; the organisation attribute
    /// always read (main.os:7566).
    /// </summary>
    internal static (List<AttributeShape> Attrs, List<string> Ignored) Resolve(DocumentSchema s, IReadOnlyList<string>? fields)
    {
        var ignored = new List<string>();
        List<AttributeShape> attrs;
        if (fields is null) attrs = s.Attributes.ToList();
        else
        {
            attrs = new List<AttributeShape>();
            foreach (var raw in fields)
            {
                string f = raw.Trim();
                if (s.Find(f) is { } a) { if (!attrs.Contains(a)) attrs.Add(a); }
                else if (f.Length > 0) ignored.Add(f);
            }
        }
        if (s.OrgAttribute is { } org && attrs.All(a => a.Name != org)) attrs.Add(s.Find(org)!);
        return (attrs, ignored);
    }

    private static void ValidateName(string document)
    {
        ReadService.ValidateIdentifier(document, "document");
        if (document.Contains('.')) throw new ArgumentException($"document '{document}' must be a bare name");
    }

    private static void Validate(DocumentQuery q)
    {
        ValidateName(q.Document);
        if (q.Limit is < 0 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(q.Limit), q.Limit, $"limit must be 0..{MaxLimit}");
        if (q.Offset < 0 || (long)q.Offset + q.Limit > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(q.Offset), q.Offset, "offset must be >= 0 and offset + limit <= 1000000");
        if (!string.IsNullOrEmpty(q.After) && !Guid.TryParse(q.After, out _))
            throw new ArgumentException($"after '{q.After}' is not a GUID");
        if (q.Order?.Trim().ToLowerInvariant() is not ("asc" or "desc"))
            throw new ArgumentException($"order must be asc or desc, got '{q.Order}'");
    }
}
