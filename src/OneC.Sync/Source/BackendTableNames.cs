namespace OneC.Sync.Source;

/// <summary>
/// The table name a row is uploaded under must be the one backend/1c and the old Connector use, or
/// the rows land as a second table next to the old ones and every consumer filtering by entity type
/// misses them. Those names are not fully regular: two registers carry 1C OData's <c>_RecordType</c>
/// suffix (backend/1c <c>app/utils/onec.py</c> PROVIDER_MAPPINGS; connector <c>src/core/config.ts</c>
/// :281-282), every other table is <c>Kind_Name</c> (e.g. <c>AccumulationRegister_РозничнаяВыручка</c>).
/// The 1C name to read (<see cref="TablePlan.Name"/>) never carries the suffix.
/// </summary>
public static class BackendTableNames
{
    private static readonly HashSet<string> Suffixed = new(StringComparer.Ordinal)
    {
        "AccountingRegister_Хозрасчетный",
        "AccumulationRegister_СебестоимостьПродажи"
    };

    /// <summary><c>AccountingRegister_Хозрасчетный</c> → <c>AccountingRegister_Хозрасчетный_RecordType</c>; any other name as given.</summary>
    public static string Of(string table) => Suffixed.Contains(table) ? table + "_RecordType" : table;

    /// <summary>The name without the suffix, for comparing what a user picked with what is synced.</summary>
    public static string Plain(string table) => table.EndsWith("_RecordType", StringComparison.Ordinal) ? table[..^"_RecordType".Length] : table;
}
