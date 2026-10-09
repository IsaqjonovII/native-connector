using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Compare-and-set for writes to existing objects (D53): the caller says what it read — field values
/// and/or the object's ВерсияДанных — and the write happens only if 1C still holds exactly that. A
/// difference refuses the whole write and names each field with what 1C holds now (<c>conflict</c>), so a
/// cloud that read the object earlier never overwrites a change it has not seen.
/// </summary>
internal static class ExpectedState
{
    public const string VersionField = "ВерсияДанных";

    /// <summary>The differences; empty = the object is as the caller expected. Unknown fields count as conflicts.</summary>
    public static List<WriteDiagnostic> Conflicts(SessionContext ctx, object obj, JsonObject? expected, string? expectedVersion,
                                                 Func<string, bool> hasField, string what)
    {
        var conflicts = new List<WriteDiagnostic>();
        if (expectedVersion is not null)
        {
            string now = Dispatch.GetString(obj, VersionField, ctx.Error) ?? "";
            if (now != expectedVersion)
                conflicts.Add(new WriteDiagnostic("conflict", WriteDiagnostic.Error, VersionField, $"expected {Show(expectedVersion)}, 1C holds {Show(now)}"));
        }
        foreach (var (field, want) in expected ?? new JsonObject())
        {
            if (!hasField(field))
            {
                conflicts.Add(new WriteDiagnostic("unknown_attribute", WriteDiagnostic.Error, field, $"{what} has no '{field}'"));
                continue;
            }
            string now = Normal(ctx, Dispatch.Get(obj, field, ctx.Error));
            string was = Normal(want);
            if (now != was) conflicts.Add(new WriteDiagnostic("conflict", WriteDiagnostic.Error, field, $"expected {Show(was)}, 1C holds {Show(now)}"));
        }
        return conflicts;
    }

    public static WriteRejected Refusal(List<WriteDiagnostic> conflicts) =>
        new($"compare-and-set refused: {conflicts.Count} field(s) differ from what the caller expected; nothing written",
            unprocessable: true, conflicts);

    /// <summary>A 1C value in the comparable form: text trimmed, numbers by value, dates ISO, a reference by GUID, empty = "".</summary>
    public static string Normal(SessionContext ctx, object? v) => v switch
    {
        null => "",
        string s => s.TrimEnd(),
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        decimal or double or float or int or long or short => System.Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("0.############", CultureInfo.InvariantCulture),
        _ when OneCValue.IsCom(v) => OneCValue.RefGuid(v, ctx) is { } g && g != Guid.Empty.ToString() ? g : "",
        _ => v.ToString() ?? ""
    };

    /// <summary>The caller's expected value in the same form: a string, number, boolean, null, or {"id": guid}.</summary>
    public static string Normal(JsonNode? n) => n switch
    {
        null => "",
        JsonObject o => (o["id"] as JsonValue)?.TryGetValue(out string? g) == true ? g!.ToLowerInvariant() : o.ToJsonString(),
        JsonValue v when v.TryGetValue(out bool b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue(out decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue(out string? s) => DocumentWriteBody.ParseDate(s!) is { } dt && s!.Length >= 10 && s[4] == '-'
            ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : s!.TrimEnd(),
        _ => n.ToJsonString()
    };

    private static string Show(string s) => s.Length == 0 ? "(empty)" : $"'{(s.Length > 80 ? s[..80] + "…" : s)}'";
}
