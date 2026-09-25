namespace OneC.Sync;

public enum TableKind { Catalog, Document, AccountingRegister, AccumulationRegister, InformationRegister, ChartOfAccounts }

/// <summary>
/// One table the sync uploads, by the backend's name (<c>Catalog_Контрагенты</c>,
/// <c>AccountingRegister_Хозрасчетный_RecordType</c>, …). The names are backend/1c's own
/// (<c>app/utils/onec.py</c> PROVIDER_MAPPINGS) and not fully regular — the <c>_RecordType</c>
/// suffix is on some registers only — so they come from configuration; this only parses them.
/// </summary>
/// <param name="From">First date a cold read of a document or register goes back to; null = everything.</param>
public sealed record SyncTable(string Name, TableKind Kind, string OneCName, DateTime? From = null)
{
    public bool IsRegister => Kind is TableKind.AccountingRegister or TableKind.AccumulationRegister or TableKind.InformationRegister;

    /// <summary>The event-log metadata name of the object (<c>Документ.X</c>, <c>РегистрБухгалтерии.X</c>).</summary>
    public string Metadata => Kind switch
    {
        TableKind.Catalog => "Справочник." + OneCName,
        TableKind.Document => "Документ." + OneCName,
        TableKind.AccountingRegister => "РегистрБухгалтерии." + OneCName,
        TableKind.AccumulationRegister => "РегистрНакопления." + OneCName,
        TableKind.InformationRegister => "РегистрСведений." + OneCName,
        _ => "ПланСчетов." + OneCName
    };

    /// <summary>The host's register op kind.</summary>
    public string RegisterKind => Kind switch
    {
        TableKind.AccountingRegister => "accounting",
        TableKind.AccumulationRegister => "accumulation",
        TableKind.InformationRegister => "information",
        _ => throw new InvalidOperationException($"{Name} is not a register")
    };

    public static SyncTable Parse(string name, DateTime? from = null)
    {
        foreach (var (prefix, kind) in new[]
                 {
                     ("Catalog_", TableKind.Catalog), ("Document_", TableKind.Document),
                     ("AccountingRegister_", TableKind.AccountingRegister), ("AccumulationRegister_", TableKind.AccumulationRegister),
                     ("InformationRegister_", TableKind.InformationRegister), ("ChartOfAccounts_", TableKind.ChartOfAccounts)
                 })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) continue;
            string oneC = name[prefix.Length..];
            if (oneC.EndsWith("_RecordType", StringComparison.Ordinal)) oneC = oneC[..^"_RecordType".Length];
            return new SyncTable(name, kind, oneC, from);
        }
        throw new ArgumentException($"'{name}' is not a Catalog_/Document_/…Register_/ChartOfAccounts_ table name");
    }
}
