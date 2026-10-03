using System.Text.Json.Nodes;

namespace OneC.Sync.Source;

/// <summary>Where a table's rows come from in 1C and how they are keyed (§7, §8, §13 sync_tables).</summary>
public static class Families
{
    public const string Catalog = "catalog";
    public const string Document = "document";
    public const string Chart = "chart";
    public const string AccountingRegister = "reg_accounting";
    public const string AccumulationRegister = "reg_accumulation";
    /// <summary>Information register written by documents: keyed and reconciled by recorder.</summary>
    public const string RecordedInfoRegister = "reg_info_recorded";
    /// <summary>Information register written on its own: natural key, refresh diff (§8).</summary>
    public const string IndependentInfoRegister = "reg_info_independent";

    public static bool IsRegister(string f) => f.StartsWith("reg_", StringComparison.Ordinal);
    public static bool IsRecorded(string f) => f is AccountingRegister or AccumulationRegister or RecordedInfoRegister;

    public static string KeyStrategy(string f) => f switch
    {
        Catalog or Document => "guid",
        Chart => "code",
        IndependentInfoRegister => "natural_key",
        _ when IsRecorded(f) => "recorder_line",
        _ => throw new ArgumentException($"unknown family '{f}'")
    };

    /// <summary>The host's register kind for a register family.</summary>
    public static string RegisterKind(string f) => f switch
    {
        AccountingRegister => "accounting",
        AccumulationRegister => "accumulation",
        RecordedInfoRegister or IndependentInfoRegister => "information",
        _ => throw new ArgumentException($"'{f}' is not a register family")
    };
}

/// <param name="Table">Backend table name (<c>Document_РеализацияТоваровУслуг</c>).</param>
/// <param name="Name">The 1C object's bare name (<c>РеализацияТоваровУслуг</c>).</param>
/// <param name="From">Documents and periodic registers: history before this date is not synced (null = all).</param>
/// <param name="RefreshEvery">Independent information registers (D-6): at most one refresh per this interval
/// (null = as soon as an event asks). Per table, never one global constant.</param>
public sealed record TablePlan(string Table, string Name, string Family, bool IsMovement, DateTime? From = null, TimeSpan? RefreshEvery = null);

public sealed record SourcePage(List<JsonObject> Rows, bool HasMore, string? NextCursorDate = null, int NextSkip = 0,
                                string? NextAfter = null, string? NextKey = null, int? NextLine = null);

public sealed record PeriodSlice(DateTime? From, DateTime? To, long Rows);

/// <summary>
/// The 1C reads sync needs, in the host's row shapes (IPC_CONTRACT §3). Implemented by the
/// Supervisor over its pipe ops; by fakes in tests. Every call may run on a sync session lease
/// the caller already holds.
/// </summary>
public interface IOneCReader
{
    Task<long> CountAsync(string baseId, TablePlan t, CancellationToken ct);

    /// <summary>Catalog keyset page after <paramref name="after"/> (null = first).</summary>
    Task<SourcePage> CatalogPageAsync(string baseId, string catalog, string? after, int limit, CancellationToken ct);

    /// <summary>Documents in date order, optionally inside a period slice [from, to).</summary>
    Task<SourcePage> DocumentPageAsync(string baseId, string document, string? cursorDate, int skip, int limit,
                                       DateTime? from, DateTime? to, CancellationToken ct);

    /// <summary>
    /// Register rows with sync keys (recorderRef/lineNo/orgRef, naturalKey): periodic ones in period
    /// order within [cursorDate, to), independent information registers by natural key after <paramref name="afterKey"/>.
    /// </summary>
    Task<SourcePage> RegisterPageAsync(string baseId, TablePlan t, string? cursorDate, int skip, string? afterKey, int limit,
                                       DateTime? to, CancellationToken ct);

    Task<SourcePage> ChartPageAsync(string baseId, string chart, int offset, int limit, CancellationToken ct);

    /// <summary>Balanced period slices for a parallel cold read (D35), for documents and periodic registers, from <see cref="TablePlan.From"/>.</summary>
    Task<List<PeriodSlice>> SlicesAsync(string baseId, TablePlan t, int parts, CancellationToken ct);

    /// <summary>
    /// Catalog elements / documents by id, current state, each with <c>dataVersion</c>. An id
    /// missing from the answer does not exist in 1C (object not found); a missing table throws
    /// <see cref="OneCMetadataMissingException"/> — never read as "deleted" (§10, P6).
    /// </summary>
    Task<List<JsonObject>> ByIdsAsync(string baseId, TablePlan t, IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>(id, ВерсияДанных) of a catalog/document table in 1C's ref order, a page after <paramref name="after"/> (§12).</summary>
    Task<(List<(string Id, string? Version)> Rows, string? Next)> VersionPageAsync(string baseId, TablePlan t, string? after, int limit, CancellationToken ct);

    /// <summary>Document types that write movements to the register (cached by the host).</summary>
    Task<IReadOnlyList<string>> RecorderTypesAsync(string baseId, TablePlan register, CancellationToken ct);

    /// <summary>The document type of a recorder GUID among the registers' recorder types; null when it exists in none (deleted).</summary>
    Task<string?> RecorderOfAsync(string baseId, IReadOnlyList<TablePlan> registers, string id, CancellationToken ct);

    /// <summary>A recorder's movements in one register, a page after <paramref name="afterLine"/> (R-8), with sync keys.</summary>
    Task<SourcePage> MovementsAsync(string baseId, TablePlan register, string recorderDocument, string recorderId, int? afterLine, int limit,
                                    CancellationToken ct);
}

/// <summary>The table itself is missing in 1C (renamed, removed, other configuration): a config problem, not a delete.</summary>
public sealed class OneCMetadataMissingException(string message) : Exception(message);
