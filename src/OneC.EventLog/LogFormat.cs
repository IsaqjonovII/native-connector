using System.Globalization;
using System.Text;

namespace OneC.EventLog;

/// <summary>
/// One record of a <c>.lgp</c> file, as far as a change feed needs it. <c>TxId</c> is the
/// transaction the record belongs to (null outside one). The data records of a transaction
/// all say <c>U</c>; its outcome is on the transaction's own marker record — <c>R</c> on the
/// <c>_$Transaction$_.Begin</c> of a rolled-back one, <c>C</c> on a commit (seen on bilim,
/// 2026-09-24, cross-checked against 1C's export).
/// </summary>
public sealed record LogRecord(string Ts, string TxStatus, int EventId, int MetadataId, string? RefHex, string? TxId = null);

/// <summary>The <c>1Cv8.lgf</c> dictionary: instance GUID (changes when the log is recreated) and per-base ids.</summary>
public sealed class LogDictionary
{
    public string InstanceGuid { get; internal set; } = "";
    public Dictionary<int, string> Events { get; private init; } = new();
    public Dictionary<int, string> Metadata { get; private init; } = new();

    internal LogDictionary Copy() => new()
    {
        InstanceGuid = InstanceGuid,
        Events = new Dictionary<int, string>(Events),
        Metadata = new Dictionary<int, string>(Metadata)
    };
}

/// <summary>
/// 1C's classic event-log format (<c>1Cv8Log\1Cv8.lgf</c> + <c>*.lgp</c>), ported from the
/// connector's verified Rust reader (connector `handlers/eventlog.rs`, format research
/// 2026-08-07, live-validated on KAN, Kanstik and four file bases):
/// <list type="bullet">
/// <item>UTF-8 with BOM; two header lines (<c>1CV8LOG(ver 2.0)</c>, instance GUID), then brace
/// records <c>{…},</c>; strings escape <c>"</c> by doubling.</item>
/// <item>.lgf: <c>{4,"_$Data$_.Update",8}</c> = event id 8; <c>{5,guid,"Справочник.X",2}</c> =
/// metadata id 2. Ids are per base.</item>
/// <item>.lgp: <c>{ts,txStatus,{txId},user,computer,app,conn,eventId,severity,comment,
/// metadataId,data,presentation,…}</c>; data <c>{"R",type:hex32}</c> for an object reference.</item>
/// </list>
/// </summary>
public static class LogFormat
{
    /// <summary>lgp stores refs as d4a(4)+d4b(12)+d3(4)+d2(4)+d1(8); the UUID is d1-d2-d3-d4a-d4b.</summary>
    public static string? HexToUuid(string? hex)
    {
        if (hex is null || hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return null;
        return $"{hex[24..32]}-{hex[20..24]}-{hex[16..20]}-{hex[0..4]}-{hex[4..16]}".ToLowerInvariant();
    }

    /// <summary>The five data events a change feed cares about; everything else is null.</summary>
    public static string? DataEventKind(string name) => name switch
    {
        "_$Data$_.New" => "New",
        "_$Data$_.Update" => "Update",
        "_$Data$_.Post" => "Post",
        "_$Data$_.Unpost" => "Unpost",
        "_$Data$_.Delete" => "Delete",
        _ => null
    };

    // ---------------- the brace format ----------------

    private abstract record Val;
    private sealed record Str(string Value) : Val;
    private sealed record Raw(string Value) : Val;
    private sealed record Lst(List<Val> Items) : Val;

    /// <summary>Recursive descent; null when the input ends before the value closes (a truncated tail).</summary>
    private static Val? Parse(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return null;
        switch (s[i])
        {
            case '{':
            {
                i++;
                var items = new List<Val>();
                while (true)
                {
                    while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                    if (i >= s.Length) return null;
                    if (s[i] == '}') { i++; return new Lst(items); }
                    var v = Parse(s, ref i);
                    if (v is null) return null;
                    items.Add(v);
                }
            }
            case '"':
            {
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) return null;
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        return new Str(sb.ToString());
                    }
                    sb.Append(s[i++]);
                }
            }
            default:
            {
                int start = i;
                while (i < s.Length && s[i] is not (',' or '}' or '{' or '\r' or '\n')) i++;
                return new Raw(s[start..i].Trim());
            }
        }
    }

    private static int? Int(Val? v) => v is Raw r && int.TryParse(r.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>Skips BOMs, whitespace and commas between records.</summary>
    private static void SkipGap(string s, ref int i)
    {
        while (i < s.Length && (s[i] == '﻿' || char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
    }

    /// <summary>
    /// Complete records from a chunk of a .lgp file, and how many characters they (and any
    /// header lines) used. A truncated last record is left for the next read.
    /// </summary>
    public static (List<LogRecord> Records, int Consumed) ParseRecords(string text)
    {
        var records = new List<LogRecord>();
        int i = 0, consumed = 0;
        while (true)
        {
            SkipGap(text, ref i);
            if (i >= text.Length) { consumed = i; break; }
            if (text[i] != '{')
            {
                // A header line: consumed only once its newline is there.
                int nl = text.IndexOf('\n', i);
                if (nl < 0) break;
                i = consumed = nl + 1;
                continue;
            }
            int start = i;
            if (Parse(text, ref i) is Lst l)
            {
                consumed = i;
                if (Record(l.Items) is { } r) records.Add(r);
            }
            else { i = start; break; }
        }
        return (records, consumed);
    }

    private static LogRecord? Record(List<Val> f)
    {
        if (f.Count < 13) return null;
        if (f[0] is not Raw { Value: { Length: 14 } ts } || !ts.All(char.IsAsciiDigit)) return null;
        if (f[1] is not Raw { Value: { Length: > 0 } tx }) return null;
        if (Int(f[7]) is not { } eventId) return null;
        string? refHex = f[11] is Lst { Items: [Str { Value: "R" }, Raw ref_, ..] } ? ref_.Value.Split(':').ElementAtOrDefault(1) : null;
        string? txId = f[2] is Lst { Items: [Raw a, Raw b, ..] } && !(a.Value == "0" && b.Value == "0") ? $"{a.Value},{b.Value}" : null;
        return new LogRecord($"{ts[..4]}-{ts[4..6]}-{ts[6..8]}T{ts[8..10]}:{ts[10..12]}:{ts[12..14]}", tx, eventId, Int(f[10]) ?? 0, refHex, txId);
    }

    /// <summary>Parses (a tail of) 1Cv8.lgf into <paramref name="into"/>; returns the characters consumed.</summary>
    public static int ParseDictionary(string text, LogDictionary into)
    {
        int i = 0, consumed = 0;
        while (true)
        {
            SkipGap(text, ref i);
            if (i >= text.Length) { consumed = i; break; }
            if (text[i] != '{')
            {
                int nl = text.IndexOf('\n', i);
                if (nl < 0) break;
                string line = text[i..nl].Trim().TrimStart('﻿');
                if (into.InstanceGuid.Length == 0 && Guid.TryParseExact(line, "D", out _)) into.InstanceGuid = line;
                i = consumed = nl + 1;
                continue;
            }
            int start = i;
            if (Parse(text, ref i) is not Lst { Items: var f }) { i = start; break; }
            consumed = i;
            switch (Int(f.ElementAtOrDefault(0)))
            {
                case 4 when f.ElementAtOrDefault(1) is Str name && Int(f.ElementAtOrDefault(2)) is { } id:
                    into.Events[id] = name.Value; break;
                case 5 when f.ElementAtOrDefault(2) is Str meta && Int(f.ElementAtOrDefault(3)) is { } mid:
                    into.Metadata[mid] = meta.Value; break;
            }
        }
        return consumed;
    }
}
