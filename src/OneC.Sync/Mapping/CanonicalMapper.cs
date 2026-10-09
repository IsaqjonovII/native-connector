using System.Text;
using System.Text.Json.Nodes;
using OneC.Sync.Abstractions;
using OneC.Sync.Source;

namespace OneC.Sync.Mapping;

/// <summary>A row the mapper cannot key: a configuration or host bug, never skipped silently.</summary>
public sealed class RowMappingException(string table, string message) : Exception($"{table}: {message}")
{
    public string Table { get; } = table;
}

/// <summary>One row ready for routing: its key, its organisation (movement tables), its 1C version and its bytes.</summary>
public sealed record MappedRow(string Key, string? OrgRef, SyncRow Row, string? DataVersion = null)
{
    public int Bytes => Row.Json.Length;

    /// <summary>A catalog item only: a hash of what a reference to it shows in other rows (<see cref="CanonicalMapper.ShownOf"/>).</summary>
    public string? Shown { get; init; }
}

/// <summary>
/// Host row → canonical row (§7): the key by the table family's strategy, stamped into the row as
/// <c>__rowKey</c> (backend/1c upserts on it), GUIDs lower-cased, and the JSON written once — the
/// batcher counts these bytes, nothing serialises the row again.
/// </summary>
public static class CanonicalMapper
{
    /// <param name="sourceVersion">For rows without their own version (register rows): the caller's.</param>
    public static MappedRow Map(TablePlan t, JsonObject row, long? sourceVersion = null)
    {
        string key = Key(t, row);
        // dataVersion is sync metadata (read with the row, S3), not part of the row the backend stores.
        string? dataVersion = JsJson.Text(row["dataVersion"]);
        row.Remove("dataVersion");
        row["__rowKey"] = key;
        DropEmptySections(row);
        string? org = JsJson.Text(row["orgRef"]);
        var sb = new StringBuilder(512);
        JsJson.Write(sb, row);
        return new MappedRow(key, string.IsNullOrEmpty(org) ? null : org.ToLowerInvariant(),
                             new SyncRow(key, VersionNumber(dataVersion) ?? sourceVersion, Encoding.UTF8.GetBytes(sb.ToString())), dataVersion)
        {
            Shown = t.Family == Families.Catalog ? ShownOf(row) : null
        };
    }

    /// <summary>
    /// What a reference to a catalog item shows in OTHER rows: the Host renders a reference as the item's
    /// Наименование, else its Код (LegacyValue.Read, the old adapter's order). Hashed, so the local state
    /// stays small. A change of it is a rename every referencing row must follow (ReferrerSearch).
    /// </summary>
    public static string? ShownOf(JsonObject row)
    {
        string? name = JsJson.Text(row["name"]), code = JsJson.Text(row["code"]);
        if (name is null && code is null) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name + "\u0001" + code)), 0, 12);
    }

    /// <summary>Local state key of a catalog item's <see cref="ShownOf"/>.</summary>
    public static string ShownKey(string baseId, string table, string key) => $"shown:{baseId}:{table}:{key}";

    /// <summary>Set once every item of a catalog table has its <see cref="ShownKey"/> (its snapshot, or the seeding pass).</summary>
    public static string ShownSeededKey(string baseId, string table) => $"shown_seeded:{baseId}:{table}";

    /// <summary>
    /// ВерсияДанных is 8 bytes, base64; every write of the object makes it larger when read as a
    /// big-endian number (S0: AAAAAAAAAAE= → AAAAAAAAAAI= on KAN, AAACJwAAAAA= → AAACKAAAAAA= on
    /// bilim) — a ready-made source version for the v3 stale guard.
    /// </summary>
    public static long? VersionNumber(string? dataVersion)
    {
        if (string.IsNullOrEmpty(dataVersion)) return null;
        Span<byte> b = stackalloc byte[8];
        return Convert.TryFromBase64String(dataVersion, b, out int n) && n == 8
            ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b) & long.MaxValue
            : null;
    }

    /// <summary>
    /// One canonical shape per object, whichever read produced it: the host's list read leaves an
    /// empty table part out, its by-id read (the old adapter's by-id shape, main.os:8413) lists it as
    /// []. Without this, the same unchanged document stored by the snapshot and re-read by an
    /// incremental item differed (R5, 2026-10-07: a new document compared unequal to 1C's list read).
    /// Empty parts are dropped; no parts at all = no <c>tabularSections</c> key.
    /// </summary>
    public static void DropEmptySections(JsonObject row)
    {
        if (row["tabularSections"] is not JsonObject ts) return;
        foreach (var name in ts.Where(kv => kv.Value is JsonArray { Count: 0 }).Select(kv => kv.Key).ToList()) ts.Remove(name);
        if (ts.Count == 0) row.Remove("tabularSections");
    }

    public static string Key(TablePlan t, JsonObject row)
    {
        string? key = Families.KeyStrategy(t.Family) switch
        {
            "guid" => Guid(JsJson.Text(row["id"])),
            "code" => JsJson.Text(row["Код"]) is { Length: > 0 } c ? c : null,
            "natural_key" => JsJson.Text(row["naturalKey"]),
            "recorder_line" => Guid(JsJson.Text(row["recorderRef"])) is { } rec && JsJson.Text(row["lineNo"]) is { Length: > 0 } line
                ? rec + "#" + line : null,
            _ => null
        };
        return key ?? throw new RowMappingException(t.Table, $"row has no {Families.KeyStrategy(t.Family)} key (read without sync keys?)");
    }

    /// <summary>
    /// Inside the table's window (<see cref="TablePlan.From"/>): a document by its date, a register row by
    /// its Период — the bound the snapshot reads with (Дата >= From). Incremental work must keep to it too,
    /// or a change to an old object puts rows into the backend that its coverage says it does not hold
    /// (R9, 2026-10-08: reposting a 2025-04-29 document sent its movements past a window from 2025-08-01).
    /// A row without a readable date stays in: nothing is dropped on a guess.
    /// </summary>
    public static bool InWindow(TablePlan t, JsonObject row)
    {
        if (t.From is not { } from) return true;
        string? field = t.Family == Families.Document ? "date" : Families.IsRegister(t.Family) ? "Период" : null;
        if (field is null || JsJson.Text(row[field]) is not { Length: > 0 } s) return true;
        return !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) || d >= from;
    }

    /// <summary>Recorder GUID of a movement row (its key's prefix), lower-case.</summary>
    public static string? Recorder(JsonObject row) => Guid(JsJson.Text(row["recorderRef"]));

    private static string? Guid(string? s) =>
        s is { Length: > 0 } && System.Guid.TryParse(s, out var g) ? g.ToString("D") : null;
}
