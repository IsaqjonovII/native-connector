using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public enum RegisterKind { Information, Accumulation, Accounting }

/// <param name="Source">What the rows are read from: the register table, or for accounting
/// registers its <c>.ДвиженияССубконто</c> virtual table (the base table has no subconto).</param>
/// <param name="BaseTable">The register table itself — index-fast for counts and window edges.</param>
/// <param name="Columns">The columns of <c>ВЫБРАТЬ * ИЗ Source</c>, in that order — the old row's keys.</param>
public sealed record RegisterSchema(
    RegisterKind Kind,
    string Name,
    string BaseTable,
    string Source,
    bool Subconto,
    IReadOnlyList<AttributeShape> Columns)
{
    /// <summary>The register's dimensions (<c>Измерения</c>), in metadata order — the natural key of an independent information register (§8).</summary>
    public IReadOnlyList<string> Dimensions { get; init; } = Array.Empty<string>();

    public bool Has(string column) => Columns.Any(c => c.Name == column);
    public bool Periodic => Has("Период");
    public bool Recorded => Has("Регистратор") && Has("НомерСтроки");
    /// <summary>An information register written on its own, not by a document: no recorder, keyed by Период + dimensions.</summary>
    public bool Independent => Kind == RegisterKind.Information && !Recorded;
}

/// <summary>Register columns, read once per base and register (<see cref="SchemaCache"/>).</summary>
public static class RegisterSchemas
{
    public static RegisterSchema Get(SessionContext ctx, RegisterKind kind, string name) =>
        SchemaCache.Get(ctx, "register:" + kind, name, (c, n) => Load(c, kind, n));

    public static string Prefix(RegisterKind kind) => kind switch
    {
        RegisterKind.Information => "РегистрСведений",
        RegisterKind.Accumulation => "РегистрНакопления",
        _ => "РегистрБухгалтерии"
    };

    private static string Collection(RegisterKind kind) => kind switch
    {
        RegisterKind.Information => "РегистрыСведений",
        RegisterKind.Accumulation => "РегистрыНакопления",
        _ => "РегистрыБухгалтерии"
    };

    internal static RegisterSchema Load(SessionContext ctx, RegisterKind kind, string name)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var meta = MetadataShapes.Find(ctx, scope, md, Collection(kind), name, "register");
        var dims = scope.Track(Dispatch.Get(meta, "Измерения", ctx.Error), "Измерения");
        int nd = Dispatch.CallInt(dims, "Количество", ctx.Error);
        var dimensions = new List<string>(nd);
        for (int i = 0; i < nd; i++)
        {
            using var ds = new ComScope();
            dimensions.Add(Dispatch.GetString(ds.Track(Dispatch.Call(dims, "Получить", ctx.Error, i), "Измерение"), "Имя", ctx.Error)!);
        }

        string table = $"{Prefix(kind)}.{name}";
        string source = table;
        bool subconto = false;
        string probe = table;
        if (kind == RegisterKind.Accounting)
        {
            // An EMPTY window: the virtual table without period parameters builds the subconto
            // join over the whole register first — minutes on KAN's 3 M rows (P §5.4); a window
            // with nothing in it costs milliseconds and has the same columns.
            string vt = table + ".ДвиженияССубконто";
            string empty = $"{vt}(ДАТАВРЕМЯ(3999, 12, 31), ДАТАВРЕМЯ(3999, 12, 31))";
            // The old ladder's last rung read the base table and silently lost the subconto
            // (main.os:18630); a configuration without the virtual table is the only reason to.
            try { Probe(ctx, empty); source = vt; probe = empty; subconto = true; }
            catch (OneCException e) when (e.Layer == OneCLayer.Runtime) { }
        }
        return new RegisterSchema(kind, name, table, source, subconto, Columns(ctx, md, probe)) { Dimensions = dimensions };
    }

    private static void Probe(SessionContext ctx, string source)
    {
        using var scope = new ComScope();
        var q = QueryKit.NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ 1 * ИЗ {source}");
        scope.Add(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
    }

    /// <summary>Names and types of <c>ВЫБРАТЬ *</c> — no rows are read.</summary>
    internal static List<AttributeShape> Columns(SessionContext ctx, object md, string source)
    {
        using var scope = new ComScope();
        var q = QueryKit.NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ 0 * ИЗ {source}");
        var result = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
        var cols = scope.Track(Dispatch.Get(result, "Колонки", ctx.Error), "Колонки");
        int n = Dispatch.CallInt(cols, "Количество", ctx.Error);
        var list = new List<AttributeShape>(n);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            var c = s.Track(Dispatch.Call(cols, "Получить", ctx.Error, i), "Колонка");
            string name = Dispatch.GetString(c, "Имя", ctx.Error)!;
            var type = s.Track(Dispatch.Get(c, "ТипЗначения", ctx.Error), "ОписаниеТипов");
            list.Add(MetadataShapes.OfColumn(ctx, md, name, type));
        }
        return list;
    }
}
