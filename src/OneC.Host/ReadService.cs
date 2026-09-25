using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record ReadQuery
{
    /// <summary>Full 1C table name, e.g. <c>Документ.ПоступлениеТоваровУслуг</c>.</summary>
    public required string Entity { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public int Limit { get; init; } = 100;
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? OrderBy { get; init; }
    public bool Descending { get; init; }
    /// <summary>How reference columns come back. Scalars are unaffected.</summary>
    public RefMode Refs { get; init; } = RefMode.Text;
}

public sealed record ReadResult(
    string BaseName, string Entity, IReadOnlyList<string> Fields,
    List<Dictionary<string, object?>> Rows, long ElapsedMs, int SessionId);

/// <summary>
/// The first real vertical slice: a parameterised read from a 1C table, end to end, with
/// no <c>dynamic</c> anywhere. Every COM object it touches lives in a ComScope on the
/// session thread and is released in reverse order before the call returns.
/// </summary>
public sealed class ReadService
{
    private readonly SessionManager _sessions;

    public ReadService(SessionManager sessions) => _sessions = sessions;

    public ReadResult Read(string baseName, ReadQuery q, CancellationToken ct = default)
    {
        ValidateIdentifier(q.Entity, nameof(q.Entity));
        foreach (var f in q.Fields) ValidateIdentifier(f, "field");
        if (q.OrderBy is not null) ValidateIdentifier(q.OrderBy, nameof(q.OrderBy));
        if (q.Limit is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(q.Limit), q.Limit, "limit must be 1..100000");

        var sw = Stopwatch.StartNew();
        var rows = _sessions.Use(baseName, ctx => Execute(ctx, q, ct), ct);
        return new ReadResult(baseName, q.Entity, q.Fields, rows.rows, sw.ElapsedMilliseconds, rows.sessionId);
    }

    private (List<Dictionary<string, object?>> rows, int sessionId) Execute(
        SessionContext ctx, ReadQuery q, CancellationToken ct)
    {
        using var scope = new ComScope();
        var query = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");

        Dispatch.Set(query, "Текст", BuildSql(q), ctx.Error);

        if (q.From is not null) SetParameter(ctx, query, "From", q.From.Value);
        if (q.To is not null) SetParameter(ctx, query, "To", q.To.Value);

        var result = scope.Track(Dispatch.Call(query, "Выполнить", ctx.Error), "РезультатЗапроса");
        // Same columns every row: resolve their DISPIDs once (DispatchMemo).
        var cursor = new DispatchMemo(scope.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка"));

        bool wantText = q.Refs is RefMode.Text or RefMode.Both;
        bool wantGuid = q.Refs is RefMode.Guid or RefMode.Both;

        var rows = new List<Dictionary<string, object?>>(Math.Min(q.Limit, 1024));
        while (cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            // Per-row scope: transient COM values are released as each row is read, so a
            // 10k-row read never holds 10k live RCWs.
            using var rowScope = new ComScope();
            var row = new Dictionary<string, object?>(q.Fields.Count, StringComparer.Ordinal);
            for (int i = 0; i < q.Fields.Count; i++)
            {
                var v = cursor.Get(ValueAlias(i), ctx.Error);
                if (!OneCValue.IsCom(v)) { row[q.Fields[i]] = OneCValue.Scalar(v); continue; }

                // A reference (or enum): never shipped as a COM object, never stringified
                // client-side — 1C already rendered the text in the query.
                rowScope.Add(v, "ref");
                string? text = wantText ? cursor.Get(TextAlias(i), ctx.Error)?.ToString() : null;
                string? guid = wantGuid ? OneCValue.RefGuid(v!, ctx) : null;
                row[q.Fields[i]] = q.Refs switch
                {
                    RefMode.Guid => guid,
                    RefMode.Both => new Dictionary<string, string?> { ["ref"] = guid, ["text"] = text },
                    _ => text
                };
            }
            rows.Add(row);
        }

        return (rows, ctx.SessionId);
    }

    private static string ValueAlias(int i) => "v" + i;
    private static string TextAlias(int i) => "t" + i;

    private static void SetParameter(SessionContext ctx, object query, string name, object value)
    {
        using var scope = new ComScope();
        var r = Dispatch.Call(query, "УстановитьПараметр", ctx.Error, name, value);
        if (r is not null) scope.Add(r, "УстановитьПараметр");
    }

    /// <summary>
    /// Every field is selected under a positional alias (dotted paths like
    /// Контрагент.Наименование cannot be aliases themselves), plus, unless only GUIDs are
    /// wanted, its ПРЕДСТАВЛЕНИЕ() so reference columns arrive already rendered. For scalar
    /// columns the extra column is ignored; it costs 1C almost nothing.
    /// </summary>
    internal static string BuildSql(ReadQuery q)
    {
        bool text = q.Refs != RefMode.Guid;
        string fields = string.Join(", ", q.Fields.Select((f, i) =>
            text ? $"{f} КАК {ValueAlias(i)}, ПРЕДСТАВЛЕНИЕ({f}) КАК {TextAlias(i)}"
                 : $"{f} КАК {ValueAlias(i)}"));
        var sql = $"ВЫБРАТЬ ПЕРВЫЕ {q.Limit} {fields} ИЗ {q.Entity}";

        var where = new List<string>();
        if (q.From is not null) where.Add("Дата >= &From");
        if (q.To is not null) where.Add("Дата <= &To");
        if (where.Count > 0) sql += " ГДЕ " + string.Join(" И ", where);

        if (q.OrderBy is not null)
            sql += $" УПОРЯДОЧИТЬ ПО {q.OrderBy}" + (q.Descending ? " УБЫВ" : "");

        return sql;
    }

    /// <summary>
    /// Field and table names go into query text, so they are whitelisted rather than
    /// escaped. Dotted paths (Контрагент.Наименование) are legal 1C; quotes and spaces
    /// are not.
    /// </summary>
    internal static void ValidateIdentifier(string s, string what)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"{what} is empty", what);
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '_')
                throw new ArgumentException($"{what} '{s}' contains an illegal character '{c}'", what);
    }
}
