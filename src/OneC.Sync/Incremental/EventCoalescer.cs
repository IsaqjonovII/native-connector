using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Source;

namespace OneC.Sync.Incremental;

/// <summary>Work-item priorities inside one base (§5 rule 5): destructive first, catalogs before documents.</summary>
public static class ItemPriority
{
    public const int Destructive = 0;   // deletes, recorder syncs (they reconcile)
    public const int Catalog = 1;       // referenced by documents: executed first in a round
    public const int Document = 2;
    public const int Refresh = 5;       // independent information registers
    public const int Verify = 9;
}

/// <summary>The configured tables of a base, by 1C metadata full name (<c>Документ.X</c>).</summary>
public sealed class TableMap
{
    private readonly Dictionary<string, TablePlan> _byMeta = new(StringComparer.Ordinal);

    public TableMap(IEnumerable<TablePlan> tables)
    {
        foreach (var t in tables) _byMeta[FullName(t)] = t;
    }

    public TablePlan? Find(string metadata) => _byMeta.GetValueOrDefault(metadata);
    public IEnumerable<TablePlan> Tables => _byMeta.Values;

    public static string FullName(TablePlan t) => t.Family switch
    {
        Families.Catalog => "Справочник." + t.Name,
        Families.Document => "Документ." + t.Name,
        Families.Chart => "ПланСчетов." + t.Name,
        Families.AccountingRegister => "РегистрБухгалтерии." + t.Name,
        Families.AccumulationRegister => "РегистрНакопления." + t.Name,
        _ => "РегистрСведений." + t.Name
    };
}

/// <summary>
/// One feed batch → work intents (§6). Every event of an object folds into flags on one item;
/// only configured tables produce work. A register event names its recorder (S0: always, and
/// always next to the recorder's own document event in the same transaction), so it marks the
/// <em>recorder</em> for a movement sync — even when the document type itself is not configured
/// (R-9); an independent information register's event names nothing and refreshes its table.
/// </summary>
public static class EventCoalescer
{
    /// <summary>Items keyed by recorder when its document type is unknown: the executor resolves it (S8).</summary>
    public const string UnknownRecorderTable = "?recorder";

    public static List<WorkRequest> Coalesce(string baseId, IReadOnlyList<ChangeEvent> events, TableMap map)
    {
        // Pass 1: which document type each ref belongs to, from the document events of this batch.
        var docOf = new Dictionary<string, TablePlan?>(StringComparer.Ordinal);
        var docMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in events)
            if (e.Ref is { } r && e.Metadata.StartsWith("Документ.", StringComparison.Ordinal))
            {
                docMeta[r] = e.Metadata;
                docOf[r] = map.Find(e.Metadata);
            }

        var items = new Dictionary<(string Table, string Key), (WorkFlags Flags, int Priority)>();
        void Add(string table, string key, WorkFlags f, int p) =>
            items[(table, key)] = items.TryGetValue((table, key), out var old)
                ? (WorkKinds.Merge(old.Flags, f), Math.Min(old.Priority, p))
                : (f, p);

        foreach (var e in events)
        {
            var t = map.Find(e.Metadata);
            string kind = e.Metadata[..Math.Max(0, e.Metadata.IndexOf('.'))];
            switch (kind)
            {
                // A chart is keyed by Код and has a few hundred rows: any change refreshes the chart.
                case "ПланСчетов" when t is not null:
                    Add(t.Table, t.Table, WorkFlags.Refresh, ItemPriority.Catalog);
                    break;
                case "Справочник" or "Документ" when t is not null && e.Ref is { } id:
                {
                    bool doc = t.Family == Families.Document;
                    var f = e.Kind switch
                    {
                        "New" => WorkFlags.Changed | WorkFlags.Recreated,
                        "Update" => WorkFlags.Changed,
                        "Post" or "Unpost" when doc => WorkFlags.Changed | WorkFlags.Movements,
                        "Delete" => WorkFlags.Deleted,
                        _ => WorkFlags.Changed
                    };
                    int p = f.HasFlag(WorkFlags.Deleted) || f.HasFlag(WorkFlags.Movements) ? ItemPriority.Destructive
                          : doc ? ItemPriority.Document : ItemPriority.Catalog;
                    Add(t.Table, id, f, p);
                    break;
                }
                case "РегистрБухгалтерии" or "РегистрНакопления" or "РегистрСведений" or "РегистрРасчета" when e.Ref is { } rec:
                {
                    // Only if the register (or its recorder's type) is ours; the movements of every
                    // configured register are reconciled by the recorder's item.
                    bool registerOurs = t is not null;
                    var docTable = docOf.GetValueOrDefault(rec);
                    if (docTable is not null) Add(docTable.Table, rec, WorkFlags.Movements, ItemPriority.Destructive);
                    else if (registerOurs) Add(docMeta.ContainsKey(rec) ? "?" + docMeta[rec] : UnknownRecorderTable, rec, WorkFlags.Movements, ItemPriority.Destructive);
                    break;
                }
                case "РегистрСведений" when t is { Family: Families.IndependentInfoRegister }:
                    Add(t.Table, t.Table, WorkFlags.Refresh, ItemPriority.Refresh);
                    break;
            }
        }
        return items.Select(kv => new WorkRequest(baseId, kv.Key.Table, kv.Key.Key, kv.Value.Flags, kv.Value.Priority)).ToList();
    }
}
