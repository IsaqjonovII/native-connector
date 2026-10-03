using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record ObjectVersion(string Id, string? Version);

/// <param name="Next">The last id of a full page: pass back as <c>after</c>; null = the table ended.</param>
public sealed record VersionPage(List<ObjectVersion> Rows, string? Next, int SessionId);

/// <summary>
/// Sync (§6 no-op suppression, §12 verify pass; S0 measured ~9 400 docs/s on KAN): the narrow
/// <c>Ссылка, ВерсияДанных</c> read. A keyset page walks a whole table in 1C's own ref order; a
/// list of ids answers "which of these changed / still exist". Versions are read on their own,
/// before the object, so the row shapes of catalog and document reads stay as they are (D33) —
/// and a change between the two reads leaves an older version stored, which the next event or
/// scan corrects; never the reverse.
/// </summary>
public sealed class VersionReadService
{
    public const int MaxLimit = 10_000;
    private readonly SessionManager _sessions;

    public VersionReadService(SessionManager sessions) => _sessions = sessions;

    public VersionPage Page(string baseName, string table, string? after, int limit, CancellationToken ct = default)
    {
        var (managers, name) = Parse(table);
        if (limit is < 1 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be 1..{MaxLimit}");
        if (after is not null && !Guid.TryParse(after, out _)) throw new ArgumentException($"after '{after}' is not a GUID");
        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx =>
        {
            using var scope = new ComScope();
            var q = QueryKit.NewQuery(ctx, scope,
                $"ВЫБРАТЬ ПЕРВЫЕ {limit} Т.Ссылка КАК r, Т.ВерсияДанных КАК v ИЗ {table} КАК Т " +
                (after is null ? "" : "ГДЕ Т.Ссылка > &after ") + "УПОРЯДОЧИТЬ ПО Т.Ссылка");
            if (after is not null) QueryKit.SetParameter(ctx, q, "after", QueryKit.RefByGuid(ctx, scope, managers, name, after));
            var rows = Read(ctx, scope, q, ct);
            return new VersionPage(rows, rows.Count >= limit ? rows[^1].Id : null, ctx.SessionId);
        }, ct));
    }

    /// <summary>The versions of the given ids that exist; an id missing from the answer is gone from 1C.</summary>
    public List<ObjectVersion> ByIds(string baseName, string table, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var (managers, name) = Parse(table);
        if (ids.Count > MaxLimit) throw new ArgumentOutOfRangeException(nameof(ids), ids.Count, $"at most {MaxLimit} ids");
        foreach (var id in ids) if (!Guid.TryParse(id, out _)) throw new ArgumentException($"'{id}' is not a GUID");
        if (ids.Count == 0) return new();
        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx =>
        {
            using var scope = new ComScope();
            var q = QueryKit.NewQuery(ctx, scope, $"ВЫБРАТЬ Т.Ссылка КАК r, Т.ВерсияДанных КАК v ИЗ {table} КАК Т ГДЕ Т.Ссылка В (&ids)");
            var manager = QueryKit.Manager(ctx, scope, managers, name);
            var arr = QueryKit.NewArray(ctx, scope);
            foreach (var id in ids) QueryKit.Add(ctx, arr, QueryKit.RefFrom(ctx, scope, manager, id));
            QueryKit.SetParameter(ctx, q, "ids", arr);
            return Read(ctx, scope, q, ct);
        }, ct));
    }

    private static List<ObjectVersion> Read(SessionContext ctx, ComScope scope, object query, CancellationToken ct)
    {
        var cursor = QueryKit.Execute(ctx, scope, query);
        var rows = new List<ObjectVersion>();
        while (cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            using var row = new ComScope();
            var r = row.Track(cursor.Get("r", ctx.Error), "Ссылка");
            rows.Add(new ObjectVersion(OneCValue.RefGuid(r!, ctx)!, cursor.Get("v", ctx.Error) as string));
        }
        return rows;
    }

    /// <summary>"Документ.X" / "Справочник.X" → (manager collection, name).</summary>
    internal static (string Managers, string Name) Parse(string table)
    {
        int dot = table.IndexOf('.');
        string prefix = dot > 0 ? table[..dot] : "";
        string name = dot > 0 ? table[(dot + 1)..] : "";
        string managers = prefix switch
        {
            "Документ" => "Документы",
            "Справочник" => "Справочники",
            "ПланСчетов" => "ПланыСчетов",
            _ => throw new ArgumentException($"table '{table}' must be Документ.X, Справочник.X or ПланСчетов.X")
        };
        ReadService.ValidateIdentifier(name, "table");
        if (name.Contains('.')) throw new ArgumentException($"table '{table}' must name one object");
        return (managers, name);
    }
}
