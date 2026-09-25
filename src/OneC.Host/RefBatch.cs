using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>A reference waiting for its page's bulk lookup; never leaves <see cref="RefBatch"/>'s page.</summary>
public sealed record DeferredRef(string Table, string Guid);

/// <summary>
/// Renders the references of one page that the query cannot dereference (wide composite types:
/// Регистратор, субконто, "any document" attributes) with one query per referenced type,
/// instead of reading <c>ref.Номер</c> / <c>ref.Наименование</c> over COM per value.
///
/// Why (P §5.3): every such property read makes 1C load the referenced object into the
/// session's client-side cache. Walking KAN sales that cost ~9 MB of native memory per
/// 250-document page, without bound (1.2 GB after 25 000 documents), and 3× the CPU. The old
/// adapter read every reference that way. With the bulk lookup the same walk stays flat.
///
/// The output is the old ПреобразоватьЗначение result: null when empty, else Наименование,
/// trimmed Код, Номер, else the GUID; enums by XML name. Session thread only.
/// </summary>
public sealed class RefBatch : IDisposable
{
    private static readonly (string Xml, string Table)[] Kinds =
    {
        ("CatalogRef.", "Справочник."), ("DocumentRef.", "Документ."), ("ChartOfAccountsRef.", "ПланСчетов."),
        ("ChartOfCharacteristicTypesRef.", "ПланВидовХарактеристик."), ("ChartOfCalculationTypesRef.", "ПланВидовРасчета."),
        ("ExchangePlanRef.", "ПланОбмена."), ("BusinessProcessRef.", "БизнесПроцесс."), ("TaskRef.", "Задача.")
    };

    private readonly SessionContext _ctx;
    private readonly ComScope _scope = new();
    private readonly Dictionary<string, object> _arrays = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string)> _seen = new();
    private Dictionary<(string, string), object?>? _values;

    public RefBatch(SessionContext ctx) => _ctx = ctx;

    public static long Deferred => Interlocked.Read(ref _deferred);
    private static long _deferred;

    /// <summary>
    /// The rendered value if it needs no lookup (empty reference, enum, non-reference), else a
    /// <see cref="DeferredRef"/> to be replaced by <see cref="Patch"/>.
    /// </summary>
    public object? Defer(object com)
    {
        string? xmlType = XmlTypeName(com);
        string? table = xmlType is null ? null : TableOf(xmlType);
        if (table is null)
        {
            // Enums: the XML form is the value name, blank for the empty value.
            if (xmlType is not null && xmlType.StartsWith("EnumRef.", StringComparison.Ordinal))
                return Dispatch.Call(_ctx.Connection, "XMLСтрока", _ctx.Error, com) is string e && e.Length > 0 ? e : null;
            return LegacyValue.Slow(com, _ctx);   // УникальныйИдентификатор, value storage, …
        }
        string guid = OneCValue.RefGuid(com, _ctx) ?? "";
        if (guid.Length == 0) return LegacyValue.Slow(com, _ctx);
        if (guid == LegacyValue.EmptyGuid) return null;            // Пустая()

        // One lookup entry per distinct reference: a register page repeats the same Регистратор
        // on every line of a document.
        if (_seen.Add((table, guid)))
        {
            if (!_arrays.TryGetValue(table, out var array))
                _arrays[table] = array = QueryKit.NewArray(_ctx, _scope);
            QueryKit.Add(_ctx, array, com);
        }
        Interlocked.Increment(ref _deferred);
        return new DeferredRef(table, guid);
    }

    /// <summary>Runs the lookups and replaces every <see cref="DeferredRef"/> in the rows, tabular sections included.</summary>
    public void Patch(IEnumerable<Dictionary<string, object?>> rows)
    {
        if (_arrays.Count == 0) return;
        _values ??= Lookup();
        foreach (var row in rows) PatchOne(row);
    }

    private void PatchOne(Dictionary<string, object?> row)
    {
        foreach (var key in row.Keys.ToList())
        {
            switch (row[key])
            {
                case DeferredRef d:
                    row[key] = _values!.TryGetValue((d.Table, d.Guid), out var v) ? v : d.Guid;   // broken ref → GUID
                    break;
                case Dictionary<string, object?> sections when key == "tabularSections":
                    foreach (var lines in sections.Values.OfType<List<Dictionary<string, object?>>>())
                        foreach (var line in lines) PatchOne(line);
                    break;
            }
        }
    }

    private Dictionary<(string, string), object?> Lookup()
    {
        var values = new Dictionary<(string, string), object?>();
        foreach (var (table, refs) in _arrays)
        {
            var (name, code, number) = Fields(table);
            var cols = new List<string> { "Ссылка КАК r" };
            if (name) cols.Add("Наименование КАК n");
            if (code) cols.Add("Код КАК c");
            if (number) cols.Add("Номер КАК num");

            using var scope = new ComScope();
            var q = QueryKit.NewQuery(_ctx, scope, $"ВЫБРАТЬ {string.Join(", ", cols)} ИЗ {table} ГДЕ Ссылка В (&refs)");
            QueryKit.SetParameter(_ctx, q, "refs", refs);
            var cursor = QueryKit.Execute(_ctx, scope, q);
            while (cursor.CallBool("Следующий", _ctx.Error))
            {
                using var row = new ComScope();
                string guid = OneCValue.RefGuid(row.Track(cursor.Get("r", _ctx.Error), "Ссылка"), _ctx)!;
                object? value =
                    name && cursor.Get("n", _ctx.Error) is string n && LegacyValue.Filled(n) ? n
                    : code && LegacyValue.CodeText(cursor.Get("c", _ctx.Error)) is { Length: > 0 } c ? c
                    : number && cursor.Get("num", _ctx.Error) is { } num and not DBNull && LegacyValue.Filled(num) ? num
                    : guid;
                values[(table, guid)] = value;
            }
        }
        return values;
    }

    /// <summary>Which of Наименование / Код / Номер the type has (cached per base).</summary>
    private (bool Name, bool Code, bool Number) Fields(string table) =>
        SchemaCache.Get(_ctx, "reftype", table, (ctx, t) =>
        {
            using var scope = new ComScope();
            var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
            var meta = Dispatch.Call(md, "НайтиПоПолномуИмени", ctx.Error, t);
            if (!OneCValue.IsCom(meta)) return new RefFields(false, false, false);
            scope.Add(meta, t);
            return new RefFields(MetadataShapes.Length(ctx, meta!, "ДлинаНаименования") > 0,
                                 MetadataShapes.Length(ctx, meta!, "ДлинаКода") > 0,
                                 MetadataShapes.Length(ctx, meta!, "ДлинаНомера") > 0);
        }).Tuple;

    private sealed record RefFields(bool Name, bool Code, bool Number)
    {
        public (bool, bool, bool) Tuple => (Name, Code, Number);
    }

    private string? XmlTypeName(object com)
    {
        try
        {
            using var s = new ComScope();
            var x = Dispatch.Call(_ctx.Connection, "XMLТипЗнч", _ctx.Error, com);
            if (!OneCValue.IsCom(x)) return null;
            s.Add(x, "ТипДанныхXML");
            return Dispatch.GetString(x!, "ИмяТипа", _ctx.Error);
        }
        catch (OneCException) { return null; }
    }

    private static string? TableOf(string xmlType)
    {
        foreach (var (xml, table) in Kinds)
            if (xmlType.StartsWith(xml, StringComparison.Ordinal)) return table + xmlType[xml.Length..];
        return null;
    }

    public void Dispose() => _scope.Dispose();
}
