using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>A period slice [From, To) of a table and how many rows it holds. To null = open end.</summary>
public sealed record Slice(DateTime From, DateTime? To, long Rows);

/// <summary>
/// Splits a register's or document type's rows into period slices of about equal row count,
/// so a cold read can walk them on several sessions at once (server bases only — file bases
/// are ~15× slower in parallel, memory: oscript threading findings).
///
/// Counts come from the base table grouped by day (index-fast; KAN: all 3 M accounting rows by
/// year in 524 ms). The old connector split the time range evenly, and the slices came out as
/// uneven as the data (2016: 6 793 movements a year, 2025: 387 433) — wall time is the biggest
/// slice. A single day is never split.
/// </summary>
public sealed class SlicePlanner
{
    private readonly SessionManager _sessions;
    public SlicePlanner(SessionManager sessions) => _sessions = sessions;

    /// <param name="table">Full table name: <c>РегистрБухгалтерии.Хозрасчетный</c>, <c>Документ.X</c>, …</param>
    /// <param name="dateField">Период for registers, Дата for documents.</param>
    public List<Slice> Plan(string baseName, string table, string dateField, int parts, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        foreach (var part in table.Split('.')) ReadService.ValidateIdentifier(part, "table");
        if (table.Split('.').Length != 2) throw new ArgumentException($"table '{table}' must be <Kind>.<Name>");
        if (dateField is not ("Период" or "Дата")) throw new ArgumentException("dateField must be Период or Дата");
        if (parts is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(parts), parts, "parts must be 1..64");

        var days = QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx => Days(ctx, table, dateField, from, to, ct), ct));
        return Cut(days, parts, from, to);
    }

    private static List<(DateTime Day, long Rows)> Days(SessionContext ctx, string table, string field, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var where = new List<string>();
        if (from is not null) where.Add($"{field} >= &from");
        if (to is not null) where.Add($"{field} < &to");
        using var scope = new ComScope();
        var q = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ НАЧАЛОПЕРИОДА({field}, ДЕНЬ) КАК d, КОЛИЧЕСТВО(*) КАК n ИЗ {table}" +
            (where.Count > 0 ? " ГДЕ " + string.Join(" И ", where) : "") +
            $" СГРУППИРОВАТЬ ПО НАЧАЛОПЕРИОДА({field}, ДЕНЬ) УПОРЯДОЧИТЬ ПО d");
        if (from is { } f) QueryKit.SetParameter(ctx, q, "from", f);
        if (to is { } t) QueryKit.SetParameter(ctx, q, "to", t);
        var cursor = QueryKit.Execute(ctx, scope, q);
        var days = new List<(DateTime, long)>();
        while (cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            days.Add(((DateTime)cursor.Get("d", ctx.Error)!, Convert.ToInt64(cursor.Get("n", ctx.Error))));
        }
        return days;
    }

    /// <summary>
    /// Cuts the day histogram where the running total crosses each 1/parts of the whole. The
    /// first slice starts at <paramref name="from"/> (or the first day), the last one ends at
    /// <paramref name="to"/> (or stays open), so together they cover the range with no gap.
    /// </summary>
    internal static List<Slice> Cut(List<(DateTime Day, long Rows)> days, int parts, DateTime? from, DateTime? to)
    {
        var slices = new List<Slice>();
        if (days.Count == 0) return slices;
        long total = days.Sum(d => d.Rows), running = 0, inSlice = 0;
        DateTime start = from ?? days[0].Day;
        int next = 1;
        for (int i = 0; i < days.Count; i++)
        {
            running += days[i].Rows;
            inSlice += days[i].Rows;
            bool last = i == days.Count - 1;
            if (!last && next < parts && running * parts >= total * next)
            {
                var end = days[i + 1].Day;
                slices.Add(new Slice(start, end, inSlice));
                start = end; inSlice = 0;
                while (next < parts && running * parts >= total * next) next++;
            }
        }
        slices.Add(new Slice(start, to, inSlice));
        return slices;
    }
}
