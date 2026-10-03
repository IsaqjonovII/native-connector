using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record CatalogQuery
{
    /// <summary>Catalog name without the prefix, e.g. <c>Номенклатура</c>.</summary>
    public required string Catalog { get; init; }

    /// <summary>Page size. 0 = count only (old adapter: <c>limit=0</c>).</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Rows to skip. Only without <see cref="After"/>; keyset paging never skips.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// Attribute projection. Null = every attribute plus tabular sections; empty = base fields
    /// only (the old <c>fields=-</c>). Names that are not attributes are ignored, as before,
    /// and reported back in <see cref="CatalogPage.IgnoredFields"/>.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>Equality filters, field → value. <c>_ownerInn</c> filters on Владелец.ИНН.</summary>
    public IReadOnlyDictionary<string, object?>? Filters { get; init; }

    /// <summary>Keyset cursor: GUID of the last row of the previous page; the zero GUID starts.</summary>
    public string? After { get; init; }

    /// <summary>Skip the count (a full scan). Filtered reads skip it anyway.</summary>
    public bool SkipTotal { get; init; }

    /// <summary>Sync: each row also carries <c>dataVersion</c> (ВерсияДанных from the same query).</summary>
    public bool WithVersion { get; init; }
}

public sealed record CatalogPage(
    List<Dictionary<string, object?>> Rows,
    long TotalCount,
    string? Next,
    IReadOnlyList<string> IgnoredFields,
    int SessionId,
    long ElapsedMs);

/// <summary>
/// Catalog reads with the old adapter's row shape (ПолучитьЭлементыСправочника, main.os:5983):
/// <c>id, code, name, deletionMark, parent, isFolder, Владелец, &lt;attributes&gt;, orgRef,
/// tabularSections</c>, values rendered by <see cref="LegacyValue"/>. Paging, the count rule,
/// the owner filter and the organisation column follow the old code; failures are errors
/// instead of an empty page (D33).
/// </summary>
public sealed class CatalogReadService
{
    public const int MaxLimit = 100_000;
    private const string OwnerInnFilter = "_ownerInn";

    private readonly SessionManager _sessions;

    public CatalogReadService(SessionManager sessions) => _sessions = sessions;

    public CatalogPage List(string baseName, CatalogQuery q, CancellationToken ct = default)
    {
        Validate(q);
        var sw = Stopwatch.StartNew();
        var page = QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx => ExecuteList(ctx, q, ct), ct));
        return page with { ElapsedMs = sw.ElapsedMilliseconds };
    }

    /// <summary>
    /// One element with every attribute. Unlike the old by-id route it carries tabular
    /// sections too, so it is the same row the list returns (D33).
    /// </summary>
    public Dictionary<string, object?> ById(string baseName, string catalog, string id, CancellationToken ct = default, bool withVersion = false)
    {
        ReadService.ValidateIdentifier(catalog, "catalog");
        if (catalog.Contains('.')) throw new ArgumentException($"catalog '{catalog}' must be a bare name", nameof(catalog));
        if (!Guid.TryParse(id, out _)) throw new ArgumentException($"'{id}' is not a GUID", nameof(id));

        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx =>
            ReadOne(ctx, catalog, id, withTabular: true, ct, withVersion)
            ?? throw OneCException.Host($"Справочник.{catalog} {id} not found", ctx.Error, "catalog", catalog), ct));
    }

    /// <summary>One element on the caller's session, or null. <paramref name="withTabular"/>
    /// false gives the old by-id row (no sections), which registers embed.</summary>
    internal static Dictionary<string, object?>? ReadOne(SessionContext ctx, string catalog, string id, bool withTabular, CancellationToken ct,
                                                        bool withVersion = false)
    {
        var schema = CatalogSchemas.Get(ctx, catalog);
        var attrs = schema.Attributes.ToList();
        using var scope = new ComScope();
        var query = QueryKit.NewQuery(ctx, scope, BuildSelect(schema, attrs, first: null, where: "Ссылка = &id", orderBy: null));
        QueryKit.SetParameter(ctx, query, "id", QueryKit.RefByGuid(ctx, scope, "Справочники", catalog, id));
        var rows = ReadRows(ctx, scope, query, schema, attrs, withTabular, skip: 0, take: 1, ct, withVersion);
        return rows.Count == 1 ? rows[0] : null;
    }

    private CatalogPage ExecuteList(SessionContext ctx, CatalogQuery q, CancellationToken ct)
    {
        var schema = CatalogSchemas.Get(ctx, q.Catalog);
        var (attrs, ignored) = Resolve(schema, q.Fields);
        var filters = FilterTerms(q.Filters);
        string? filterWhere = filters.Count > 0 ? string.Join(" И ", filters.Select(f => f.Term)) : null;

        using var scope = new ComScope();

        // The count is a full scan; the old adapter skips it for skipTotal and for any filtered
        // read (-1), but always runs it for a count-only call (main.os:6004).
        bool skipCount = (q.SkipTotal || filters.Count > 0) && q.Limit > 0;
        long total = skipCount ? -1 : Count(ctx, scope, "Справочник." + q.Catalog, filterWhere, filters);
        if (q.Limit <= 0)
            return new CatalogPage(new(), total, null, ignored, ctx.SessionId, 0);

        bool keyset = !string.IsNullOrEmpty(q.After);
        string sql = keyset
            // Always "Ссылка > &after", even for the zero GUID: without the WHERE, ORDER BY Ссылка
            // is a seq-scan + sort on PostgreSQL — 48 s vs 65 ms per page on KAN (main.os:6189).
            ? BuildSelect(schema, attrs, q.Limit, QueryKit.Join(filterWhere, "Ссылка > &after"), "Ссылка")
            : BuildSelect(schema, attrs, q.Limit + q.Offset, filterWhere,
                          schema.HasName ? "Наименование, Ссылка" : "Ссылка");

        var query = QueryKit.NewQuery(ctx, scope, sql);
        foreach (var f in filters) QueryKit.SetParameter(ctx, query, f.Param, f.Value);
        if (keyset) QueryKit.SetParameter(ctx, query, "after", QueryKit.RefByGuid(ctx, scope, "Справочники", q.Catalog, q.After!));

        var rows = ReadRows(ctx, scope, query, schema, attrs, withTabular: q.Fields is null,
                            skip: keyset ? 0 : q.Offset, take: q.Limit, ct, q.WithVersion);
        string? next = keyset && rows.Count == q.Limit ? (string?)rows[^1]["id"] : null;
        return new CatalogPage(rows, total, next, ignored, ctx.SessionId, 0);
    }

    // ---------------- rows ----------------

    private static List<Dictionary<string, object?>> ReadRows(
        SessionContext ctx, ComScope scope, object query, CatalogSchema schema, List<AttributeShape> attrs,
        bool withTabular, int skip, int take, CancellationToken ct, bool withVersion = false)
    {
        var cursor = QueryKit.Execute(ctx, scope, query);
        withTabular &= schema.Tabular.Count > 0;
        var pageRefs = withTabular ? QueryKit.NewArray(ctx, scope) : null;
        int orgIndex = schema.OrgAttribute is null ? -1 : attrs.FindIndex(a => a.Name == schema.OrgAttribute);
        using var batch = new RefBatch(ctx);

        var rows = new List<Dictionary<string, object?>>(Math.Min(take, 1024));
        int seen = 0;
        var parentShape = schema.ParentShape;
        while (rows.Count < take && cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            if (seen++ < skip) continue;

            using var rowScope = new ComScope();
            var idRef = rowScope.Track(cursor.Get("id", ctx.Error), "Ссылка");
            var row = new Dictionary<string, object?>(attrs.Count + 8, StringComparer.Ordinal)
            {
                ["id"] = OneCValue.RefGuid(idRef, ctx)
            };
            if (schema.HasCode) row["code"] = LegacyValue.Scalar(cursor.Get("code", ctx.Error));
            if (schema.HasName) row["name"] = LegacyValue.Scalar(cursor.Get("name", ctx.Error));
            row["deletionMark"] = cursor.GetBool("dm", ctx.Error);
            if (schema.Hierarchical) row["parent"] = LegacyValue.Read(cursor, "p", parentShape, ctx, batch);
            if (schema.HasFolders) row["isFolder"] = cursor.GetBool("g", ctx.Error);
            // Always present, null for a catalog without owners — the old JSON row's width.
            row["Владелец"] = schema.HasOwner ? LegacyValue.Scalar(cursor.Get("o", ctx.Error)) : null;
            for (int i = 0; i < attrs.Count; i++)
                row[attrs[i].Name] = LegacyValue.Read(cursor, "a" + i, attrs[i], ctx, batch);
            if (orgIndex >= 0 && QueryKit.OrgGuid(cursor, "a" + orgIndex, ctx) is { } org)
                row["orgRef"] = org;
            if (withVersion) row["dataVersion"] = cursor.Get("dv", ctx.Error) as string;

            if (pageRefs is not null) QueryKit.Add(ctx, pageRefs, idRef);
            rows.Add(row);
        }

        if (pageRefs is not null && rows.Count > 0)
            TabularSections.Load(ctx, "Справочник." + schema.Name, schema.Tabular, pageRefs,
                                 rows.ToDictionary(r => (string)r["id"]!, StringComparer.Ordinal), includeEmpty: false, ct, batch);
        batch.Patch(rows);
        return rows;
    }

    // ---------------- query text ----------------

    internal static string BuildSelect(CatalogSchema s, IReadOnlyList<AttributeShape> attrs, int? first, string? where, string? orderBy)
    {
        static string F(string name) => QueryKit.Field(name);
        var cols = new List<string> { $"{F("Ссылка")} КАК id", $"{F("ПометкаУдаления")} КАК dm" };
        if (s.HasCode) cols.Add($"{F("Код")} КАК code");
        if (s.HasName) cols.Add($"{F("Наименование")} КАК name");
        if (s.Hierarchical) cols.Add(LegacyValue.Select(F("Родитель"), "p", s.ParentShape));
        if (s.HasFolders) cols.Add($"{F("ЭтоГруппа")} КАК g");
        // Server-side: reading the owner reference's fields over COM gave Null for every row
        // (main.os:6130).
        if (s.HasOwner) cols.Add($"ПРЕДСТАВЛЕНИЕ({F("Владелец")}) КАК o");
        for (int i = 0; i < attrs.Count; i++) cols.Add(LegacyValue.Select(F(attrs[i].Name), "a" + i, attrs[i]));
        // Sync (S3): the object's version from the same query as its row, surfaced only with
        // WithVersion — a separate later read could store a version newer than the row sent.
        cols.Add($"{F("ВерсияДанных")} КАК dv");

        string sql = "ВЫБРАТЬ " + (first is { } n ? $"ПЕРВЫЕ {n} " : "") + string.Join(", ", cols) + $" ИЗ Справочник.{s.Name} КАК {QueryKit.Alias}";
        if (where is not null) sql += " ГДЕ " + where;
        if (orderBy is not null) sql += " УПОРЯДОЧИТЬ ПО " + orderBy;
        return sql;
    }

    /// <summary>
    /// Requested attributes in the caller's order, unknown names dropped (the old code did the
    /// same silently). The organisation attribute is always read — orgRef needs it — and so
    /// appears in the row even under a projection, as before (main.os:6077).
    /// </summary>
    internal static (List<AttributeShape> Attrs, List<string> Ignored) Resolve(CatalogSchema s, IReadOnlyList<string>? fields)
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

    internal static List<(string Term, string Param, object? Value)> FilterTerms(IReadOnlyDictionary<string, object?>? filters)
    {
        var list = new List<(string, string, object?)>();
        if (filters is null) return list;
        int i = 0;
        foreach (var (key, value) in filters)
        {
            string field = key == OwnerInnFilter ? "Владелец.ИНН" : key;
            ReadService.ValidateIdentifier(field, "filter");
            string p = "f" + i++;
            list.Add(($"{field} = &{p}", p, value));
        }
        return list;
    }

    private static void Validate(CatalogQuery q)
    {
        ReadService.ValidateIdentifier(q.Catalog, "catalog");
        if (q.Catalog.Contains('.')) throw new ArgumentException($"catalog '{q.Catalog}' must be a bare name");
        if (q.Limit is < 0 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(q.Limit), q.Limit, $"limit must be 0..{MaxLimit}");
        if (q.Offset < 0 || (long)q.Offset + q.Limit > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(q.Offset), q.Offset, "offset must be >= 0 and offset + limit <= 1000000");
        if (!string.IsNullOrEmpty(q.After) && !Guid.TryParse(q.After, out _))
            throw new ArgumentException($"after '{q.After}' is not a GUID");
    }

    // ---------------- COM helpers ----------------

    internal static long Count(SessionContext ctx, ComScope scope, string table, string? where,
                               IEnumerable<(string Term, string Param, object? Value)> parameters)
    {
        var q = QueryKit.NewQuery(ctx, scope, $"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК n ИЗ {table}" + (where is null ? "" : " ГДЕ " + where));
        foreach (var p in parameters) QueryKit.SetParameter(ctx, q, p.Param, p.Value);
        var cursor = QueryKit.Execute(ctx, scope, q);
        return cursor.CallBool("Следующий", ctx.Error) ? Convert.ToInt64(cursor.Get("n", ctx.Error)) : 0;
    }
}
