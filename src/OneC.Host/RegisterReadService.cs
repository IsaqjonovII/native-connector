using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record RegisterQuery
{
    public required RegisterKind Kind { get; init; }
    public required string Register { get; init; }

    /// <summary>Page size. 0 = count only.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Rows to skip — with a cursor, the previous page's next skip.</summary>
    public int Offset { get; init; }

    /// <summary>asc reads Период &gt;= it, desc Период &lt;= it.</summary>
    public DateTime? CursorDate { get; init; }

    public string Order { get; init; } = "desc";

    /// <summary>Exclusive upper bound on Период (a parallel cold-read slice).</summary>
    public DateTime? To { get; init; }

    public bool SkipTotal { get; init; }

    /// <summary>
    /// Only the movements of one document: its type (<c>РеализацияТоваровУслуг</c>) and GUID.
    /// The sync re-reads a reposted document's rows this way (5.9); cursor and window do not apply.
    /// </summary>
    public (string Document, string Id)? Recorder { get; init; }
}

public sealed record RegisterPage(
    List<Dictionary<string, object?>> Rows,
    long TotalCount,
    string? NextCursorDate,
    int NextCursorSkip,
    bool HasMore,
    string Register,
    int SessionId);

/// <summary>
/// Register reads with the old adapter's rows (main.os:17911 / :18078 / :18286): every column of
/// <c>ВЫБРАТЬ *</c> in order, rendered by <see cref="LegacyValue"/>; accounting rows also carry
/// <c>СчетДтКод/СчетКтКод</c>, <c>recorderRef</c>, <c>lineNo</c>, <c>orgRef</c>. Accounting
/// registers read <c>.ДвиженияССубконто</c> with the period as a virtual-table parameter and a
/// window sized by row count — the old code's measured fix (592 s → 3.4 s on KAN), kept as is.
/// </summary>
public sealed class RegisterReadService
{
    public const int MaxLimit = 100_000;
    private readonly SessionManager _sessions;

    public RegisterReadService(SessionManager sessions) => _sessions = sessions;

    public RegisterPage List(string baseName, RegisterQuery q, CancellationToken ct = default)
    {
        Validate(q);
        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx => Execute(ctx, q, ct), ct));
    }

    private static RegisterPage Execute(SessionContext ctx, RegisterQuery q, CancellationToken ct)
    {
        var s = RegisterSchemas.Get(ctx, q.Kind, q.Register);
        using var scope = new ComScope();
        long total = q.Limit <= 0 || !q.SkipTotal
            ? CatalogReadService.Count(ctx, scope, s.BaseTable, null, Array.Empty<(string, string, object?)>())
            : -1;
        if (q.Limit <= 0) return new RegisterPage(new(), total, null, 0, false, q.Register, ctx.SessionId);
        if (q.Recorder is { } rec) return ByRecorder(ctx, scope, s, q, rec, total, ct);

        bool asc = q.Order.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase);
        string dir = asc ? "ВОЗР" : "УБЫВ";
        var parameters = new List<(string Name, object? Value)>();
        string sql;
        int skip;

        if (!s.Periodic)
        {
            // A non-periodic information register: no cursor, no order (the old fallback).
            sql = Select(s, q.Limit + q.Offset, s.Source, null, null);
            skip = q.Offset;
        }
        else
        {
            var where = new List<string>();
            if (q.CursorDate is { } c) { where.Add(asc ? "Период >= &cursorDate" : "Период <= &cursorDate"); parameters.Add(("cursorDate", c)); }
            if (q.To is { } to) { where.Add("Период < &toBound"); parameters.Add(("toBound", to)); }
            // Период is not unique (every line of a document shares it). The accounting read
            // always broke ties by Регистратор, НомерСтроки; the other registers did not, so
            // their page boundaries were unstable (memory: "registers order by Период ONLY").
            string order = $"Период {dir}" + (s.Recorded ? $", Регистратор {dir}, НомерСтроки {dir}" : "");

            if (s.Subconto)
            {
                var (start, edge, inner) = Window(ctx, s, q, asc, where, parameters);
                skip = start is null ? q.Offset : inner;
                string left = asc ? (start is not null ? "&windowStart" : q.CursorDate is not null ? "&cursorDate" : "")
                                  : (edge is not null ? "&windowEdge" : "");
                string right = asc ? (edge is not null ? "&windowEdge" : "")
                                   : (start is not null ? "&windowStart" : q.CursorDate is not null ? "&cursorDate" : "");
                parameters.RemoveAll(p => p.Name == "toBound");
                if (start is not null) parameters.Add(("windowStart", start));
                if (edge is not null) parameters.Add(("windowEdge", edge));
                sql = Select(s, q.Limit + skip, $"{s.Source}({left}, {right})", null, order);
            }
            else
            {
                sql = Select(s, q.Limit + q.Offset, s.Source, where.Count > 0 ? string.Join(" И ", where) : null, order);
                skip = q.Offset;
            }
        }

        var query = QueryKit.NewQuery(ctx, scope, sql);
        // Only parameters the final text uses (the window replaces the cursor in the VT form).
        foreach (var (name, value) in parameters.DistinctBy(p => p.Name).Where(p => sql.Contains("&" + p.Name, StringComparison.Ordinal)))
            QueryKit.SetParameter(ctx, query, name, value);
        var rows = ReadRows(ctx, scope, query, s, skip, q.Limit, ct, out var lastPeriod, out int onLast);

        string? nextDate = null;
        int nextSkip = 0;
        if (lastPeriod is { } lp)
        {
            nextDate = (string)LegacyValue.Scalar(lp)!;
            nextSkip = onLast + (q.CursorDate == lp ? q.Offset : 0);
        }
        return new RegisterPage(rows, total, nextDate, nextSkip, rows.Count >= q.Limit, q.Register, ctx.SessionId);
    }

    /// <summary>
    /// One document's movements, in line order. An accounting register filters inside the
    /// virtual table (its condition parameter), so subconto are joined for these rows only.
    /// </summary>
    private static RegisterPage ByRecorder(SessionContext ctx, ComScope scope, RegisterSchema s, RegisterQuery q,
                                           (string Document, string Id) rec, long total, CancellationToken ct)
    {
        if (!s.Recorded) throw new ArgumentException($"register '{s.Name}' has no recorder");
        ReadService.ValidateIdentifier(rec.Document, "recorder document");
        if (!Guid.TryParse(rec.Id, out _)) throw new ArgumentException($"recorder id '{rec.Id}' is not a GUID");
        var recorder = QueryKit.RefByGuid(ctx, scope, "Документы", rec.Document, rec.Id);
        string order = s.Periodic ? "Период, НомерСтроки" : "НомерСтроки";
        string sql = s.Subconto
            ? Select(s, q.Limit, $"{s.Source}(, , Регистратор = &recorder)", null, order)
            : Select(s, q.Limit, s.Source, $"{QueryKit.Field("Регистратор")} = &recorder", order);
        var query = QueryKit.NewQuery(ctx, scope, sql);
        QueryKit.SetParameter(ctx, query, "recorder", recorder);
        var rows = ReadRows(ctx, scope, query, s, 0, q.Limit, ct, out _, out _);
        return new RegisterPage(rows, total, null, 0, rows.Count >= q.Limit, q.Register, ctx.SessionId);
    }

    /// <summary>
    /// The row-count window (main.os:18357): the base table — no subconto, so index-fast —
    /// gives the Период of row #offset (window start, plus how many earlier rows share it) and
    /// of row #(offset+limit) (window edge). The virtual table then reads about one page, not
    /// every movement after the cursor. A tail shorter than a page leaves the edge open, except
    /// under an upper bound, which caps it.
    /// </summary>
    private static (DateTime? Start, DateTime? Edge, int InnerSkip) Window(
        SessionContext ctx, RegisterSchema s, RegisterQuery q, bool asc, List<string> where,
        List<(string Name, object? Value)> parameters)
    {
        using var scope = new ComScope();
        var query = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit + q.Offset + 1} Период КАК p ИЗ {s.BaseTable}" +
            (where.Count > 0 ? " ГДЕ " + string.Join(" И ", where) : "") + $" УПОРЯДОЧИТЬ ПО Период {(asc ? "ВОЗР" : "УБЫВ")}");
        foreach (var (name, value) in parameters) QueryKit.SetParameter(ctx, query, name, value);
        var cursor = QueryKit.Execute(ctx, scope, query);

        DateTime? start = null, edge = null, previous = null;
        int rows = 0, run = 0, inner = 0;
        while (cursor.CallBool("Следующий", ctx.Error))
        {
            var p = (DateTime)cursor.Get("p", ctx.Error)!;
            run = previous == p ? run + 1 : 0;
            previous = p;
            if (rows == q.Offset) { start = p; inner = run; }
            edge = p;
            rows++;
        }
        if (rows <= q.Limit + q.Offset) edge = null;
        if (rows <= q.Offset) { start = null; inner = 0; }
        // The virtual table's bounds are inclusive and 1C dates have whole seconds, so the
        // exclusive bound is one second earlier. The old code used `to` itself: the boundary
        // second landed in two neighbouring slices (D34 #6).
        if (q.To is { } to && edge is null) edge = to.AddSeconds(-1);
        return (start, edge, inner);
    }

    private static List<Dictionary<string, object?>> ReadRows(
        SessionContext ctx, ComScope scope, object query, RegisterSchema s, int skip, int take, CancellationToken ct,
        out DateTime? lastPeriod, out int onLast)
    {
        var cursor = QueryKit.Execute(ctx, scope, query);
        var cols = s.Columns;
        bool accounting = s.Kind == RegisterKind.Accounting;
        bool people = s.Kind == RegisterKind.Information && s.Name == "ДокументыФизическихЛиц";
        int periodIndex = cols.ToList().FindIndex(c => c.Name == "Период");
        var peopleCache = people ? new Dictionary<string, object?>(StringComparer.Ordinal) : null;

        var rows = new List<Dictionary<string, object?>>(Math.Min(take, 1024));
        using var batch = new RefBatch(ctx);
        lastPeriod = null; onLast = 0;
        int seen = 0;
        while (rows.Count < take && cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            if (seen++ < skip) continue;

            var row = new Dictionary<string, object?>(cols.Count + 4, StringComparer.Ordinal);
            for (int i = 0; i < cols.Count; i++)
            {
                string name = cols[i].Name, alias = "c" + i;
                if (peopleCache is not null && name == "Физлицо") { row[name] = Person(ctx, cursor, alias, peopleCache, ct); continue; }
                row[name] = LegacyValue.Read(cursor, alias, cols[i], ctx, batch);
                if (!accounting) continue;
                switch (name)
                {
                    case "СчетДт" or "СчетКт":
                        if (cursor.Get(alias + "k", ctx.Error) is { } code and not DBNull && LegacyValue.Filled(code)) row[name + "Код"] = code;
                        break;
                    case "Регистратор":
                        if (QueryKit.OrgGuid(cursor, alias, ctx) is { } rr) row["recorderRef"] = rr;
                        break;
                    case "НомерСтроки":
                        row["lineNo"] = row[name];
                        break;
                    case "Организация":
                        if (QueryKit.OrgGuid(cursor, alias, ctx) is { } org) row["orgRef"] = org;
                        break;
                }
            }
            if (periodIndex >= 0 && cursor.Get("c" + periodIndex, ctx.Error) is DateTime period)
            {
                if (lastPeriod == period) onLast++;
                else { lastPeriod = period; onLast = 1; }
            }
            rows.Add(row);
        }
        batch.Patch(rows);
        return rows;
    }

    /// <summary>
    /// ДокументыФизическихЛиц.Физлицо: the old read put the whole person there — the catalog
    /// element as its by-id route returned it, no tabular sections (main.os:18041).
    /// </summary>
    private static object? Person(SessionContext ctx, DispatchMemo cursor, string alias, Dictionary<string, object?> cache, CancellationToken ct)
    {
        var v = cursor.Get(alias, ctx.Error);
        if (!OneCValue.IsCom(v)) return LegacyValue.Scalar(v);
        using var s = new ComScope();
        s.Add(v, "Физлицо");
        string? id = OneCValue.RefGuid(v!, ctx);
        if (id is null) return LegacyValue.Slow(v, ctx);
        if (cache.TryGetValue(id, out var hit)) return hit;
        object? person = id != LegacyValue.EmptyGuid
            ? CatalogReadService.ReadOne(ctx, "ФизическиеЛица", id, withTabular: false, ct)
            : null;
        person ??= LegacyValue.RefObject(v, ctx);
        cache[id] = person;
        return person;
    }

    /// <summary>
    /// Columns are table-qualified: registers of the МИКО telephony extension have a column
    /// named <c>end</c>, a query keyword — fine under <c>ВЫБРАТЬ *</c>, a syntax error as a bare
    /// name, fine as <c>Т.end</c>.
    /// </summary>
    internal static string Select(RegisterSchema s, int first, string source, string? where, string? orderBy)
    {
        var cols = new List<string>(s.Columns.Count * 2);
        for (int i = 0; i < s.Columns.Count; i++)
        {
            var c = s.Columns[i];
            cols.Add(LegacyValue.Select(QueryKit.Field(c.Name), "c" + i, c));
            if (s.Kind == RegisterKind.Accounting && c.Name is "СчетДт" or "СчетКт") cols.Add($"{QueryKit.Field(c.Name)}.Код КАК c{i}k");
        }
        string sql = $"ВЫБРАТЬ ПЕРВЫЕ {first} {string.Join(", ", cols)} ИЗ {source} КАК {QueryKit.Alias}";
        if (where is not null) sql += " ГДЕ " + where;
        if (orderBy is not null) sql += " УПОРЯДОЧИТЬ ПО " + orderBy;
        return sql;
    }

    private static void Validate(RegisterQuery q)
    {
        ReadService.ValidateIdentifier(q.Register, "register");
        if (q.Register.Contains('.')) throw new ArgumentException($"register '{q.Register}' must be a bare name");
        if (q.Limit is < 0 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(q.Limit), q.Limit, $"limit must be 0..{MaxLimit}");
        if (q.Offset < 0 || (long)q.Offset + q.Limit > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(q.Offset), q.Offset, "offset must be >= 0 and offset + limit <= 1000000");
        if (q.Order?.Trim().ToLowerInvariant() is not ("asc" or "desc"))
            throw new ArgumentException($"order must be asc or desc, got '{q.Order}'");
    }
}
