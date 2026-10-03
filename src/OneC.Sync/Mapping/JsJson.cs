using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneC.Sync.Mapping;

/// <summary>
/// JSON as JavaScript's JSON.stringify writes it — what the connector sent until now. Numbers in
/// their shortest form (<c>3790</c>, not the host's <c>3790.00</c>): backend/1c parses with Python,
/// where 3790 and 3790.0 are int and float and hash differently, so a different spelling of the
/// same number would rewrite every row the connector already stored. Strings escape only what JSON
/// requires; Cyrillic stays as is.
///
/// Kept from the milestone 5.9 engine (§27 "keep RowIdentity/JsJson canonical JSON").
/// </summary>
public static class JsJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder(1024);
        Write(sb, node);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                bool first = true;
                foreach (var (k, v) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    String(sb, k);
                    sb.Append(':');
                    Write(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
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

    public static string? Text(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Number(v),
        _ => null
    };

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
