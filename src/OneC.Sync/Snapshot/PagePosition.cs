using System.Text.Json;
using OneC.Sync.Source;

namespace OneC.Sync.Snapshot;

/// <summary>
/// Where a slice's reader stands, persisted in <c>snapshot_slices.cursor</c> after each accepted
/// page: a date cursor with its skip (documents, periodic registers), a keyset GUID (catalogs), a
/// natural key (independent information registers) or an offset (charts).
/// </summary>
public sealed record PagePosition(string? Date = null, int Skip = 0, string? After = null, string? Key = null, int Offset = 0)
{
    public static readonly PagePosition Start = new();

    public string Encode() => JsonSerializer.Serialize(this);

    public static PagePosition Decode(string? s) => s is null ? Start : JsonSerializer.Deserialize<PagePosition>(s) ?? Start;

    /// <summary>The position after <paramref name="page"/> was read from <paramref name="at"/>.</summary>
    public static PagePosition Next(string family, PagePosition at, SourcePage page) => family switch
    {
        Families.Catalog => at with { After = page.NextAfter },
        Families.Chart => at with { Offset = at.Offset + page.Rows.Count },
        Families.IndependentInfoRegister => at with { Key = page.NextKey },
        _ => at with { Date = page.NextCursorDate ?? at.Date, Skip = page.NextCursorDate is null ? at.Skip : page.NextSkip }
    };
}
