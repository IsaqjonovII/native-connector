using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record ChartPage(List<Dictionary<string, object?>> Rows, long TotalCount, bool HasMore, int SessionId);

/// <summary>
/// Chart-of-accounts rows (sync S3; D-7 keeps the chart) in the old <c>/api/charts/{name}</c>
/// shape (main.os ПолучитьСчета): every column of <c>ВЫБРАТЬ * ИЗ ПланСчетов.X УПОРЯДОЧИТЬ ПО Код</c>,
/// rendered by the old value rules. The row key is <c>Код</c> (§7); a chart has a few hundred
/// rows, so offset paging over the code order is enough.
/// </summary>
public sealed class ChartReadService
{
    public const int MaxLimit = 10_000;
    private readonly SessionManager _sessions;

    public ChartReadService(SessionManager sessions) => _sessions = sessions;

    public ChartPage List(string baseName, string chart, int limit, int offset, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(chart, "chart");
        if (chart.Contains('.')) throw new ArgumentException($"chart '{chart}' must be a bare name");
        if (limit is < 0 or > MaxLimit) throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be 0..{MaxLimit}");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset must be >= 0");
        return QueryKit.Healing(baseName, () => _sessions.Use(baseName, ctx => Execute(ctx, chart, limit, offset, ct), ct));
    }

    private static ChartPage Execute(SessionContext ctx, string chart, int limit, int offset, CancellationToken ct)
    {
        string table = "ПланСчетов." + chart;
        var cols = SchemaCache.Get(ctx, "chart", chart, (c, n) =>
        {
            using var s = new ComScope();
            var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
            MetadataShapes.Find(c, s, md, "ПланыСчетов", n, "chart");
            return RegisterSchemas.Columns(c, md, "ПланСчетов." + n);
        });
        using var scope = new ComScope();
        long total = CatalogReadService.Count(ctx, scope, table, null, Array.Empty<(string, string, object?)>());
        if (limit == 0) return new ChartPage(new(), total, false, ctx.SessionId);

        var select = cols.Select((c, i) => LegacyValue.Select(QueryKit.Field(c.Name), "c" + i, c));
        var q = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ {limit + offset} {string.Join(", ", select)} ИЗ {table} КАК {QueryKit.Alias} УПОРЯДОЧИТЬ ПО {QueryKit.Field("Код")}");
        var cursor = QueryKit.Execute(ctx, scope, q);
        var rows = new List<Dictionary<string, object?>>();
        using var batch = new RefBatch(ctx);
        int seen = 0;
        while (rows.Count < limit && cursor.CallBool("Следующий", ctx.Error))
        {
            ct.ThrowIfCancellationRequested();
            if (seen++ < offset) continue;
            var row = new Dictionary<string, object?>(cols.Count, StringComparer.Ordinal);
            for (int i = 0; i < cols.Count; i++) row[cols[i].Name] = LegacyValue.Read(cursor, "c" + i, cols[i], ctx, batch);
            rows.Add(row);
        }
        batch.Patch(rows);
        return new ChartPage(rows, total, rows.Count >= limit, ctx.SessionId);
    }
}
