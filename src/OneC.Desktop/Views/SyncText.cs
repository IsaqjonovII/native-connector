using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Desktop.Controls;

namespace OneC.Desktop.Views;

/// <summary>
/// Sync states in the words an accountant would use — shared by the Infobases cards and
/// a base's sync screen, so both say the same thing about the same base.
/// </summary>
internal static class SyncText
{
    public static readonly CultureInfo Numbers = new("ru-RU");          // 18 915 285

    /// <summary>What the engine is doing with one base (an entry of /v1/sync).</summary>
    public static (string Text, Pill Pill) State(JsonObject b)
    {
        string mode = (string)b["mode"]!;
        long pending = b["pendingWork"]?.GetValue<long>() ?? 0;
        int done = b["tablesDone"]?.GetValue<int>() ?? 0, total = b["tablesTotal"]?.GetValue<int>() ?? 0;
        int dead = b["deadLetters"]?.GetValue<int>() ?? 0;
        string? reason = (string?)b["reason"];
        return mode switch
        {
            "snapshot" => ($"First copy · {done} of {total} tables", Pill.Info),
            "incremental" when pending > 0 => ($"Sending {Count(pending, "change")}", Pill.Info),
            "incremental" when dead > 0 => ("Synced, with problems", Pill.Warn),
            "incremental" => ("Up to date", Pill.Ok),
            "recovery" => ("Checking what changed", Pill.Warn),
            "fallback" => ("No 1C change log · checks every 15 min", Pill.Warn),
            "paused" when reason?.StartsWith("old Connector", StringComparison.Ordinal) == true => ("Paused · the old Connector syncs it", Pill.Warn),
            "paused" when reason?.StartsWith("auth", StringComparison.Ordinal) == true => ("Paused · sign in to AIBA again", Pill.Bad),
            "paused" => ("Paused" + (reason is { Length: > 0 } r && r != "paused by the user" ? " · " + r : ""), Pill.Neutral),
            _ => ("Starting", Pill.Neutral)
        };
    }

    /// <summary>A table's state on the base's sync screen.</summary>
    public static (string Text, Pill Pill) TableState(JsonObject t) =>
        ((string)t["state"]!, t["failed"]?.GetValue<int>() ?? 0, t["pending"]?.GetValue<long>() ?? 0) switch
        {
            ("missing", _, _) => ("Not in this 1C", Pill.Neutral),
            (_, > 0, _) => ("Problem", Pill.Bad),
            ("waiting", _, _) => ("Waiting", Pill.Neutral),
            ("copying", _, _) => ("Copying", Pill.Info),
            (_, _, > 0) => ("Sending", Pill.Info),
            _ => ("Synced", Pill.Ok)
        };

    /// <summary>Document_РеализацияТоваровУслуг → "РеализацияТоваровУслуг".</summary>
    public static string Name(string table)
    {
        if (table.EndsWith("_RecordType", StringComparison.Ordinal)) table = table[..^"_RecordType".Length];   // backend name of two registers
        return table.IndexOf('_') is var u and >= 0 ? table[(u + 1)..] : table;
    }

    /// <summary>Document_РеализацияТоваровУслуг → "Documents".</summary>
    public static string Kind(string table) => (table.IndexOf('_') is var u and >= 0 ? table[..u] : "") switch
    {
        "Catalog" => "List",
        "Document" => "Documents",
        "ChartOfAccounts" => "Chart of accounts",
        "AccountingRegister" => "Accounting entries",
        "AccumulationRegister" or "InformationRegister" => "Register",
        var k => k
    };

    public static string Number(long n) => n.ToString("N0", Numbers);

    public static string Count(long n, string word) => $"{Number(n)} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"today 19:29", "yesterday 08:10", "28.09.2026 19:29"; null → "—".</summary>
    public static string When(DateTimeOffset? at) => at is { } a ? $"{Day(a.ToLocalTime())} {a.ToLocalTime():HH:mm}" : "—";

    /// <summary>"today", "yesterday" or "28.09.2026", of a local time.</summary>
    public static string Day(DateTimeOffset local) => (DateTime.Today - local.Date).Days switch
    {
        0 => "today",
        1 => "yesterday",
        _ => local.ToString("dd.MM.yyyy", Numbers)
    };

    public static DateTimeOffset? Time(JsonNode? n) =>
        n?.GetValue<string>() is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
}
