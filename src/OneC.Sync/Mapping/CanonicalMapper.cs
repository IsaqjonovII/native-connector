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
                             new SyncRow(key, VersionNumber(dataVersion) ?? sourceVersion, Encoding.UTF8.GetBytes(sb.ToString())), dataVersion);
    }

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

    /// <summary>Recorder GUID of a movement row (its key's prefix), lower-case.</summary>
    public static string? Recorder(JsonObject row) => Guid(JsJson.Text(row["recorderRef"]));

    private static string? Guid(string? s) =>
        s is { Length: > 0 } && System.Guid.TryParse(s, out var g) ? g.ToString("D") : null;
}
