using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OneC.Host;

/// <summary>One line of <c>fillDiagnostics</c> — the old adapter's codes where the meaning is the same.</summary>
public sealed record WriteDiagnostic(string Code, string Level, string? Field = null, string? Message = null)
{
    public const string Warn = "WARN";
    public const string Error = "ERROR";
    public const string Info = "INFO";
}

/// <summary>
/// A write the host refuses before or instead of writing: bad input (<see cref="Unprocessable"/>
/// false → 400) or a request it understood but cannot carry out as given, such as a reference
/// that resolves to nothing (true → 422). Nothing has been written to the document when this is
/// thrown.
/// </summary>
public sealed class WriteRejected : Exception
{
    public bool Unprocessable { get; }
    public IReadOnlyList<WriteDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Not a refusal of the request: an earlier write with the same idempotency marker is still inside
    /// 1C (its caller timed out, the host did not). Its outcome is unknown, so the answer is "busy, try
    /// again" — a retry later finds the marker and returns that document (R9, 2026-10-08: a create that
    /// outlived its 245 s deadline in a bilim stall made the retry report a final failure).
    /// </summary>
    public bool InFlight { get; private init; }

    public WriteRejected(string message, bool unprocessable, IReadOnlyList<WriteDiagnostic>? diagnostics = null)
        : base(message)
    {
        Unprocessable = unprocessable;
        Diagnostics = diagnostics ?? Array.Empty<WriteDiagnostic>();
    }

    public static WriteRejected Bad(string message) => new(message, unprocessable: false);

    public static WriteRejected MarkerInFlight(string marker) =>
        new($"another write with marker '{marker}' is still running", unprocessable: false) { InFlight = true };
}

/// <summary>
/// The old adapter's create body (СоздатьДокумент, main.os:15158), parsed and checked before any
/// COM call. Kept: the keys, the <c>tabularSections</c> object and bare section arrays, the
/// <c>_idempotencyMarker</c> directive. Changed (D38): a body that is not a JSON object, a bad
/// date, a missing AIBA marker or an unsupported directive is refused here instead of producing
/// an empty or half-built document.
/// </summary>
public sealed record DocumentWriteBody(
    DateTime Date,
    string? Number,
    string Comment,
    string? IdempotencyMarker,
    IReadOnlyList<KeyValuePair<string, JsonNode>> Header,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<JsonObject>>> Tabular,
    IReadOnlyList<WriteDiagnostic> Diagnostics)
{
    public const string CommentField = "Комментарий";

    /// <summary><c>AIBA_&lt;KIND&gt;_&lt;id&gt;</c>: the marker as it appears before the colon in Комментарий.</summary>
    private static readonly Regex Marker = new(@"^AIBA_[^\s:]+$", RegexOptions.CultureInvariant);

    public static DocumentWriteBody Parse(JsonObject body, string ownedPrefix)
    {
        DateTime? date = null;
        string? number = null, comment = null, marker = null;
        var header = new List<KeyValuePair<string, JsonNode>>();
        var tabular = new List<KeyValuePair<string, IReadOnlyList<JsonObject>>>();
        var diagnostics = new List<WriteDiagnostic>();

        foreach (var (key, value) in body)
        {
            if (key is "Date" or "date" or "Дата")
            {
                if (value is not JsonValue v || !v.TryGetValue(out string? text))
                    throw WriteRejected.Bad($"'{key}' must be a string like 2026-09-24 or 2026-09-24T10:30:00");
                date = ParseDate(text) ?? throw WriteRejected.Bad($"'{key}': '{text}' is not a date like 2026-09-24 or 2026-09-24T10:30:00");
                continue;
            }
            if (key.Equals("Номер", StringComparison.OrdinalIgnoreCase) || key.Equals("number", StringComparison.OrdinalIgnoreCase))
            {
                number = value is null ? null : Text(value, key);
                continue;
            }
            if (key == CommentField) { comment = value is null ? null : Text(value, key); continue; }
            if (key == "tabularSections")
            {
                if (value is not JsonObject sections)
                    throw WriteRejected.Bad("'tabularSections' must be an object of { \"<section>\": [rows] }");
                foreach (var (name, rows) in sections) AddSection(tabular, name, rows);
                continue;
            }
            if (key.StartsWith('_'))
            {
                switch (key)
                {
                    case "_idempotencyMarker":
                        marker = value is null ? null : Text(value, key);
                        break;
                    case "_autofill":
                        break;                        // accepted; only fill-empty rules exist here (D38)
                    case "_monthlyUpsert":
                        throw WriteRejected.Bad("'_monthlyUpsert' is not supported by this host (D38)");
                    default:
                        diagnostics.Add(new WriteDiagnostic("unknown_directive", WriteDiagnostic.Warn, key, "ignored"));
                        break;
                }
                continue;
            }
            if (value is null) continue;                                // "not given", as before
            if (value is JsonArray) { AddSection(tabular, key, value); continue; }
            header.Add(new(key, value));
        }

        if (date is null) throw WriteRejected.Bad("'Date' is required (yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss)");
        if (string.IsNullOrEmpty(comment) || !comment.StartsWith(ownedPrefix, StringComparison.Ordinal))
            throw WriteRejected.Bad($"'{CommentField}' must start with an AIBA marker ('{ownedPrefix}…'): the host only writes documents it can recognise as its own (D18)");
        if (!string.IsNullOrEmpty(marker))
        {
            if (!Marker.IsMatch(marker))
                throw WriteRejected.Bad($"'_idempotencyMarker' must look like AIBA_<KIND>_<id> (no spaces, no colon), got '{marker}'");
            if (!comment.Contains(marker + ":", StringComparison.Ordinal))
                throw WriteRejected.Bad($"'{CommentField}' must contain '{marker}:' — that is what a retry looks for");
        }
        return new DocumentWriteBody(date.Value, string.IsNullOrEmpty(number) ? null : number, comment,
                                     string.IsNullOrEmpty(marker) ? null : marker, header, tabular, diagnostics);
    }

    private static void AddSection(List<KeyValuePair<string, IReadOnlyList<JsonObject>>> tabular, string name, JsonNode? rows)
    {
        if (rows is not JsonArray array)
            throw WriteRejected.Bad($"tabular section '{name}' must be an array of row objects");
        if (tabular.Any(t => t.Key == name))
            throw WriteRejected.Bad($"tabular section '{name}' is given twice");
        var list = new List<JsonObject>(array.Count);
        foreach (var row in array)
            list.Add(row as JsonObject ?? throw WriteRejected.Bad($"tabular section '{name}': every row must be an object"));
        tabular.Add(new(name, list));
    }

    private static string Text(JsonNode value, string key) =>
        value is JsonValue v && v.TryGetValue(out string? s) ? s
        : value is JsonValue n && (n.TryGetValue(out long _) || n.TryGetValue(out decimal _)) ? n.ToJsonString()
        : throw WriteRejected.Bad($"'{key}' must be a string");

    /// <summary>
    /// <c>yyyy-MM-dd</c> or <c>yyyy-MM-ddTHH:mm:ss</c>; anything after the seconds (fraction,
    /// zone) is ignored, as the old parser did by position (main.os:15496). Null when neither.
    /// </summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string head = s.Length >= 19 && (s[10] == 'T' || s[10] == ' ') ? s[..19].Replace(' ', 'T')
                    : s.Length == 10 ? s
                    : "";
        return DateTime.TryParseExact(head, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var d) ? d : null;
    }
}
