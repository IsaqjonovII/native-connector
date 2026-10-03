using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>One table of a base's configuration that sync can copy (Sync, "add a table").</summary>
/// <param name="Table">The backend table name, <c>Document_РеализацияТоваровУслуг</c>.</param>
/// <param name="Family">The engine's family: catalog, document, chart, reg_accounting, reg_accumulation, reg_info_recorded, reg_info_independent.</param>
/// <param name="IsMovement">A document that writes movements, or a register written by documents.</param>
public sealed record TableEntry(string Table, string Name, string Synonym, string Family, bool IsMovement);

/// <summary>
/// Every catalog, document, chart of accounts and register of a base's configuration, with what
/// the sync engine needs to plan it. Read from <c>Метаданные</c> once and cached like other schemas;
/// the desktop's "Add table" searches this list.
/// </summary>
public sealed class TableCatalog(SessionManager sessions)
{
    private static readonly (string Collection, string Prefix, string Family)[] Collections =
    {
        ("Справочники", "Catalog", "catalog"),
        ("Документы", "Document", "document"),
        ("ПланыСчетов", "ChartOfAccounts", "chart"),
        ("РегистрыБухгалтерии", "AccountingRegister", "reg_accounting"),
        ("РегистрыНакопления", "AccumulationRegister", "reg_accumulation"),
        ("РегистрыСведений", "InformationRegister", "reg_info")
    };

    /// <summary>
    /// Names and synonyms only — two COM reads per object, so a large configuration lists in seconds.
    /// The family is the collection's; whether a document writes movements and how an information
    /// register is written are left out (family "reg_info", IsMovement false): <see cref="Details"/>
    /// reads them for the few tables the user picks.
    /// </summary>
    public IReadOnlyList<TableEntry> List(string baseName, CancellationToken ct = default) =>
        QueryKit.Healing(baseName, () => sessions.Use(baseName, ctx => SchemaCache.Get(ctx, "tables", "", (c, _) => Load(c)), ct));

    /// <summary>The engine-ready entries of the given tables (<c>Document_X</c>, ...); unknown names are skipped.</summary>
    public IReadOnlyList<TableEntry> Details(string baseName, IReadOnlyList<string> tables, CancellationToken ct = default) =>
        QueryKit.Healing(baseName, () => sessions.Use(baseName, c =>
        {
            using var s = new ComScope();
            var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
            var list = new List<TableEntry>();
            foreach (var table in tables)
            {
                int u = table.IndexOf('_');
                if (u < 0 || Collections.FirstOrDefault(x => x.Prefix == table[..u]) is not { Collection: not null } col) continue;
                string name = table[(u + 1)..];
                ReadService.ValidateIdentifier(name, "table");
                using var es = new ComScope();
                var items = es.Track(Dispatch.Get(md, col.Collection, c.Error), col.Collection);
                var found = Dispatch.Call(items, "Найти", c.Error, name);
                if (found is null or DBNull) continue;                       // Неопределено: no such object
                var o = es.Track(found, col.Collection);
                string synonym = Dispatch.GetString(o, "Синоним", c.Error) ?? "";
                string family = col.Family;
                bool movement = family is "reg_accounting" or "reg_accumulation";
                if (family == "document")
                {
                    var moves = es.Track(Dispatch.Get(o, "Движения", c.Error), "Движения");
                    movement = Dispatch.CallInt(moves, "Количество", c.Error) > 0;
                }
                else if (family == "reg_info")
                {
                    // Written by documents when some document's Движения contain it. (Reading РежимЗаписи
                    // needs the global Строка(), which the COM connection does not expose.)
                    var docs = es.Track(Dispatch.Get(md, "Документы", c.Error), "Документы");
                    int n = Dispatch.CallInt(docs, "Количество", c.Error);
                    for (int i = 0; i < n && !movement; i++)
                    {
                        using var ds = new ComScope();
                        var doc = ds.Track(Dispatch.Call(docs, "Получить", c.Error, i), "Документ");
                        var moves = ds.Track(Dispatch.Get(doc, "Движения", c.Error), "Движения");
                        movement = Convert.ToBoolean(Dispatch.Call(moves, "Содержит", c.Error, o));
                    }
                    family = movement ? "reg_info_recorded" : "reg_info_independent";
                }
                list.Add(new TableEntry(table, name, synonym, family, movement));
            }
            return list;
        }, ct));

    private static List<TableEntry> Load(SessionContext c)
    {
        using var s = new ComScope();
        var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
        var list = new List<TableEntry>();
        foreach (var (collection, prefix, family) in Collections)
        {
            var items = s.Track(Dispatch.Get(md, collection, c.Error), collection);
            int n = Dispatch.CallInt(items, "Количество", c.Error);
            for (int i = 0; i < n; i++)
            {
                using var es = new ComScope();
                var o = es.Track(Dispatch.Call(items, "Получить", c.Error, i), collection);
                string name = Dispatch.GetString(o, "Имя", c.Error)!;
                list.Add(new TableEntry($"{prefix}_{name}", name, Dispatch.GetString(o, "Синоним", c.Error) ?? "", family,
                                        family is "reg_accounting" or "reg_accumulation"));
            }
        }
        return list;
    }
}
