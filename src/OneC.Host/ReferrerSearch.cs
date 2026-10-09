using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Sync: which synced objects show a catalog item's NAME. Rows carry a reference as its display text
/// (the old adapter's shape, D33), so renaming an item in 1C changes what every referencing document
/// shows without changing those documents — no event, no re-read, a stale name in the backend
/// (F-reference-presentation-staleness). The engine asks this only when an item's name changed, and
/// re-reads only what comes back: one query per synced table, on the columns whose type CAN hold the
/// item (<c>ОписаниеТипов.СодержитТип</c>), inside each table's window. Documents and catalogs answer
/// with themselves; registers with their recorders (re-reading a recorder re-reads its movements).
/// </summary>
public sealed class ReferrerSearch(SessionManager sessions)
{
    /// <param name="Kind">document | catalog | information | accumulation | accounting</param>
    public sealed record Target(string Kind, string Name, DateTime? From);

    /// <param name="Kind">document | catalog | recorder (a register's recorder, of any document type)</param>
    public sealed record Hit(string Kind, string Name, string Id);

    public (List<Hit> Hits, bool Truncated) Find(string baseName, string catalog, string id, IReadOnlyList<Target> targets,
                                                  int limit, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(catalog, "catalog");
        if (!Guid.TryParse(id, out _)) throw new ArgumentException($"'{id}' is not a GUID");
        foreach (var t in targets) ReadService.ValidateIdentifier(t.Name, t.Kind);
        return QueryKit.Healing(baseName, () => sessions.Use(baseName, ctx =>
        {
            var hits = new List<Hit>();
            using var s = new ComScope();
            var item = QueryKit.RefByGuid(ctx, s, "Справочники", catalog, id);
            // ТипЗнч / Тип are built-in language functions, not global-context methods: an external
            // connection does not expose them (DISP_E_UNKNOWNNAME, found live 2026-10-09). The type comes
            // from a type description built by name instead.
            var desc = s.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "ОписаниеТипов", "СправочникСсылка." + catalog), "ОписаниеТипов");
            var types = s.Track(Dispatch.Call(desc, "Типы", ctx.Error), "Типы");
            var type = s.Track(Dispatch.Call(types, "Получить", ctx.Error, 0), "Тип");
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                string? sql = Query(ctx, catalog, type, t);
                if (sql is null) continue;                                    // no column of this table can hold the item
                using var q = new ComScope();
                var query = QueryKit.NewQuery(ctx, q, sql);
                QueryKit.SetParameter(ctx, query, "item", item);
                QueryKit.SetParameter(ctx, query, "from", t.From ?? new DateTime(1, 1, 1));
                var cursor = QueryKit.Execute(ctx, q, query);
                string kind = t.Kind is "document" or "catalog" ? t.Kind : "recorder";
                while (cursor.CallBool("Следующий", ctx.Error))
                {
                    if (hits.Count >= limit) return (hits, true);
                    using var row = new ComScope();
                    var r = row.Track(cursor.Get("r", ctx.Error), "Ссылка");
                    if (OneCValue.RefGuid(r, ctx) is { Length: > 0 } g) hits.Add(new Hit(kind, kind == "recorder" ? "" : t.Name, g));
                }
            }
            return (hits, false);
        }, ct));
    }

    /// <summary>The search for one table; null when none of its columns can hold the item.</summary>
    private static string? Query(SessionContext ctx, string catalog, object type, Target t)
    {
        const string A = QueryKit.Alias;
        string Where(IEnumerable<string> cols) => "(" + string.Join(" ИЛИ ", cols.Select(c => $"{A}.{c} = &item")) + ")";
        switch (t.Kind)
        {
            case "document" or "catalog":
            {
                string table = (t.Kind == "document" ? "Документ." : "Справочник.") + t.Name;
                var parts = new List<string>();
                foreach (var (source, cols) in Sources(ctx, catalog, type, t.Kind, t.Name))
                {
                    bool header = source == table;
                    string window = t.Kind == "document" && t.From is not null ? (header ? $" И {A}.Дата >= &from" : $" И {A}.Ссылка.Дата >= &from") : "";
                    parts.Add($"ВЫБРАТЬ {A}.Ссылка КАК r ИЗ {source} КАК {A} ГДЕ {Where(cols)}{window}");
                }
                return parts.Count == 0 ? null : string.Join(" ОБЪЕДИНИТЬ ", parts);
            }
            case "information" or "accumulation" or "accounting":
            {
                var kind = t.Kind switch { "information" => RegisterKind.Information, "accumulation" => RegisterKind.Accumulation, _ => RegisterKind.Accounting };
                var schema = RegisterSchemas.Get(ctx, kind, t.Name);
                if (!schema.Recorded) return null;                            // independent registers are refreshed whole
                var (source, cols) = Sources(ctx, catalog, type, t.Kind, t.Name).FirstOrDefault();
                if (cols is null) return null;
                // The accounting register's subconto live in its virtual table, with the window as its parameter.
                string from = schema.Subconto ? $"{schema.BaseTable}.ДвиженияССубконто(&from, )" : schema.BaseTable;
                string window = !schema.Subconto && schema.Periodic && t.From is not null ? $" И {A}.Период >= &from" : "";
                return $"ВЫБРАТЬ РАЗЛИЧНЫЕ {A}.Регистратор КАК r ИЗ {from} КАК {A} ГДЕ {Where(cols)}{window}";
            }
            default:
                throw new ArgumentException($"kind must be document|catalog|information|accumulation|accounting, got '{t.Kind}'");
        }
    }

    /// <summary>
    /// (source, columns that can hold the item) for a table: the header and each tabular section of a
    /// document or catalog, or the register's own row source. Cached per base like other schemas.
    /// </summary>
    private static List<(string Source, List<string> Columns)> Sources(SessionContext ctx, string catalog, object type, string kind, string name) =>
        SchemaCache.Get(ctx, $"referrers:{catalog}:{kind}", name, (c, n) =>
        {
            var sources = new List<string>();
            if (kind is "document" or "catalog")
            {
                string table = (kind == "document" ? "Документ." : "Справочник.") + n;
                sources.Add(table);
                using var s = new ComScope();
                var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
                var meta = MetadataShapes.Find(c, s, md, kind == "document" ? "Документы" : "Справочники", n, kind);
                var sections = s.Track(Dispatch.Get(meta, "ТабличныеЧасти", c.Error), "ТабличныеЧасти");
                int ns = Dispatch.CallInt(sections, "Количество", c.Error);
                for (int i = 0; i < ns; i++)
                {
                    using var ts = new ComScope();
                    sources.Add(table + "." + Dispatch.GetString(ts.Track(Dispatch.Call(sections, "Получить", c.Error, i), "ТЧ"), "Имя", c.Error));
                }
            }
            else
            {
                var schema = RegisterSchemas.Get(c, kind switch { "information" => RegisterKind.Information, "accumulation" => RegisterKind.Accumulation, _ => RegisterKind.Accounting }, n);
                // Column types come from an empty window of the virtual table (see RegisterSchemas.Load).
                sources.Add(schema.Subconto ? $"{schema.BaseTable}.ДвиженияССубконто(ДАТАВРЕМЯ(3999, 12, 31), ДАТАВРЕМЯ(3999, 12, 31))" : schema.BaseTable);
            }
            var list = new List<(string, List<string>)>();
            foreach (var source in sources)
            {
                var cols = Columns(c, source, type);
                if (cols.Count > 0) list.Add((source, cols));
            }
            return list;
        });

    /// <summary>Columns of <c>ВЫБРАТЬ *</c> whose value type can be the item's type — no rows are read.</summary>
    private static List<string> Columns(SessionContext ctx, string source, object type)
    {
        using var scope = new ComScope();
        var q = QueryKit.NewQuery(ctx, scope, $"ВЫБРАТЬ ПЕРВЫЕ 0 * ИЗ {source}");
        var result = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
        var cols = scope.Track(Dispatch.Get(result, "Колонки", ctx.Error), "Колонки");
        int n = Dispatch.CallInt(cols, "Количество", ctx.Error);
        var list = new List<string>();
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            var c = s.Track(Dispatch.Call(cols, "Получить", ctx.Error, i), "Колонка");
            string name = Dispatch.GetString(c, "Имя", ctx.Error)!;
            if (name is "Ссылка" or "Регистратор") continue;                  // the row itself, never the item
            var typeDesc = s.Track(Dispatch.Get(c, "ТипЗначения", ctx.Error), "ОписаниеТипов");
            if (Convert.ToBoolean(Dispatch.Call(typeDesc, "СодержитТип", ctx.Error, type))) list.Add(name);
        }
        return list;
    }
}
