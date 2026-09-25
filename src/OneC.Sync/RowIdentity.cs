using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneC.Sync;

/// <summary>
/// What the connector stamps on every uploaded row (connector <c>utils/row-identity.ts</c>):
/// <c>__rowKey</c> — the object GUID, a chart-of-accounts code, or <c>recorderRef#lineNo</c> for
/// accounting/accumulation register rows — and <c>__rowHash</c>, sha256 of the row's canonical
/// JSON. backend/1c upserts on (oneCId, table, rowKey) and pops both before its own hash.
/// </summary>
public static class RowIdentity
{
    public static string? RowKey(SyncTable table, JsonObject row)
    {
        if ((Text(row["id"]) ?? Text(row["Ref_Key"])) is { Length: > 0 } id) return id;
        if (table.Kind == TableKind.ChartOfAccounts) return Text(row["code"]);
        if (table.Kind is TableKind.AccountingRegister or TableKind.AccumulationRegister &&
            Text(row["recorderRef"]) is { Length: > 0 } rec && Text(row["lineNo"]) is { Length: > 0 } line)
            return rec + "#" + line;
        return null;
    }

    /// <summary>sha256 (lower-case hex) of the canonical JSON: keys sorted at every level, arrays in order.</summary>
    public static string Hash(JsonNode? row)
    {
        var sb = new StringBuilder(512);
        JsJson.Write(sb, row, sortKeys: true);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string? Text(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsJson.Number(v),
        _ => null
    };
}

/// <summary>
/// JSON as JavaScript's JSON.stringify writes it — what the connector sent until now. Numbers
/// in their shortest form (<c>3790</c>, not the host's <c>3790.00</c>): backend/1c parses the file
/// with Python, where 3790 and 3790.0 are int and float and hash differently, so a different
/// spelling of the same number would rewrite every row the connector already stored.
/// Strings escape only what JSON requires; Cyrillic stays as is.
/// </summary>
public static class JsJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder(1024);
        Write(sb, node, sortKeys: false);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, JsonNode? node, bool sortKeys)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                bool first = true;
                var props = sortKeys ? o.OrderBy(p => p.Key, StringComparer.Ordinal) : o.AsEnumerable();
                foreach (var (k, v) in props)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    String(sb, k);
                    sb.Append(':');
                    Write(sb, v, sortKeys);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i], sortKeys);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String: String(sb, v.GetValue<string>()); break;
                    case JsonValueKind.Number: sb.Append(Number(v)); break;
                    case JsonValueKind.True: sb.Append("true"); break;
                    case JsonValueKind.False: sb.Append("false"); break;
                    default: sb.Append("null"); break;
                }
                break;
        }
    }

    /// <summary>The shortest spelling: no trailing zeros, no exponent for the magnitudes 1C uses.</summary>
    public static string Number(JsonValue v)
    {
        // A parsed node converts to any numeric type; a node built from a CLR value only to its own.
        if (v.TryGetValue(out decimal d))
        {
            d /= 1.000000000000000000000000000000000m;              // drops trailing zeros
            return d == 0 ? "0" : d.ToString(CultureInfo.InvariantCulture);
        }
        if (v.TryGetValue(out long l)) return l.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue(out int i)) return i.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue(out double db)) return db.ToString("R", CultureInfo.InvariantCulture);
        if (v.TryGetValue(out float f)) return ((double)f).ToString("R", CultureInfo.InvariantCulture);
        return v.ToJsonString();
    }

    private static void String(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    bool loneSurrogate = char.IsSurrogate(c) &&
                        !(char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) &&
                        !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < 0x20 || loneSurrogate) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
