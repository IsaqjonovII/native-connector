using System.Text;
using OneC.EventLog;

// S0 spike Q3/Q4: the Data field (record field 11) and presentation (field 12) of every data
// event, grouped by metadata kind and event. Read-only.
//   LogShapes <logDir> [--mb N] [--file name.lgp] [--from-start] [--samples N] [--meta substring]
string dir = args[0];
string? Opt(string k) { int i = Array.IndexOf(args, "--" + k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
long mb = long.Parse(Opt("mb") ?? "64");
int samples = int.Parse(Opt("samples") ?? "3");
string? metaFilter = Opt("meta");
bool fromStart = args.Contains("--from-start");

var dict = new LogDictionary();
using (var lgf = new StreamReader(new FileStream(Path.Combine(dir, "1Cv8.lgf"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8))
    LogFormat.ParseDictionary(lgf.ReadToEnd(), dict);
string file = Opt("file") ?? Directory.EnumerateFiles(dir, "*.lgp").Select(Path.GetFileName).Order().Last()!;
string path = Path.Combine(dir, file);
using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
long len = fs.Length, window = Math.Min(len, mb * 1048576);
long start = fromStart ? 0 : len - window;
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"{file}: {len / 1048576} MB, window {start / 1048576}..{(start + window) / 1048576} MB");

var groups = new Dictionary<string, (long Count, Dictionary<string, long> Shapes, List<string> Samples)>();
long records = 0, dataEvents = 0;
string firstTs = "", lastTs = "";
const int Chunk = 32 * 1048576;
var buf = new byte[Chunk];
long at = start, stop = start + window;
string carry = "";
bool aligned = fromStart;
var dec = Encoding.UTF8.GetDecoder();
var docTx = new HashSet<string>();
var regEvents = new List<(string Meta, string TxRef)>();
var cbuf = new char[Chunk + 16];
while (at < stop)
{
fs.Seek(at, SeekOrigin.Begin);
int n = fs.ReadAtLeast(buf, (int)Math.Min(Chunk, stop - at), throwOnEndOfStream: false);
if (n == 0) break;
at += n;
int cn = dec.GetChars(buf, 0, n, cbuf, 0, flush: false);
string text = carry + new string(cbuf, 0, cn);
// Align to a record start: a line that begins with "{2" (timestamp).
int pos = aligned ? 0 : text.IndexOf("\n{2", StringComparison.Ordinal) + 1;
aligned = true;
int lastGood = pos;
while (pos < text.Length)
{
    int s = text.IndexOf('{', pos);
    if (s < 0) break;
    var fields = Brace.TopFields(text, s, out int end);
    if (fields is null) break;
    pos = end;
    lastGood = end;
    if (fields.Count < 13) continue;
    records++;
    string ts = text.Substring(fields[0].S, fields[0].L);
    if (firstTs == "") firstTs = ts;
    lastTs = ts;
    if (!int.TryParse(text.AsSpan(fields[7].S, fields[7].L), out int ev) || !dict.Events.TryGetValue(ev, out var evName)) continue;
    if (!evName.StartsWith("_$Data$_.")) continue;
    int.TryParse(text.AsSpan(fields[10].S, fields[10].L), out int mid);
    string meta = dict.Metadata.GetValueOrDefault(mid) ?? "?";
    if (metaFilter is not null && !meta.Contains(metaFilter)) continue;
    if (Opt("skip") is { } skip && meta.Contains(skip)) continue;
    dataEvents++;
    string kind = meta.Split('.')[0];
    string data = text.Substring(fields[11].S, fields[11].L).Replace("\r", "").Replace("\n", "");
    string pres = text.Substring(fields[12].S, fields[12].L).Replace("\r", "").Replace("\n", "");
    string key = args.Contains("--by-meta") ? $"{meta} {evName[9..]}" : $"{kind} {evName[9..]}";
    string shape = Brace.Shape(data);
    if (!groups.TryGetValue(key, out var g)) groups[key] = g = (0, new(), new());
    g.Count++;
    g.Shapes[shape] = g.Shapes.GetValueOrDefault(shape) + 1;
    if (g.Samples.Count < samples && !g.Samples.Any(x => x.StartsWith(meta + " ")))
        g.Samples.Add($"{meta} tx={text.Substring(fields[1].S, fields[1].L)} data={Trim(data, 300)} pres={Trim(pres, 160)}");
    groups[key] = g;
    // Pairing: does a register event's ref also appear on a document event in the same transaction?
    string tx = text.Substring(fields[2].S, fields[2].L).Replace("\r", "").Replace("\n", "");
    string? refHex = data.StartsWith("{\"R\",") ? data[5..^1].Split(':').ElementAtOrDefault(1) : null;
    if (refHex is not null)
    {
        if (kind == "Документ") docTx.Add(tx + "|" + refHex);
        else if (kind.StartsWith("Регистр")) regEvents.Add((meta, tx + "|" + refHex));
    }
}
carry = text[lastGood..];
}
Console.WriteLine($"{records} records {firstTs}..{lastTs}, {dataEvents} data events");
var unpaired = regEvents.Where(r => !docTx.Contains(r.TxRef)).ToList();
Console.WriteLine($"register events with a recorder ref: {regEvents.Count}; with a document event for the same ref in the same transaction: {regEvents.Count - unpaired.Count}; without: {unpaired.Count}");
foreach (var g in unpaired.GroupBy(u => u.Meta).OrderByDescending(g => g.Count()).Take(10)) Console.WriteLine($"   unpaired {g.Key}: {g.Count()}  e.g. {g.First().TxRef}");
foreach (var (k, g) in groups.OrderBy(x => x.Key))
{
    Console.WriteLine($"\n== {k}: {g.Count}");
    foreach (var (sh, c) in g.Shapes.OrderByDescending(x => x.Value).Take(8)) Console.WriteLine($"   shape {sh}: {c}");
    foreach (var smp in g.Samples) Console.WriteLine($"   e.g. {smp}");
}
return 0;

static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static class Brace
{
    /// <summary>Top-level fields of the record starting at <paramref name="s"/> as (start, length) spans.</summary>
    public static List<(int S, int L)>? TopFields(string t, int s, out int end)
    {
        var list = new List<(int, int)>();
        int i = s + 1, depth = 0, fieldStart = i;
        bool q = false;
        for (; i < t.Length; i++)
        {
            char c = t[i];
            if (q) { if (c == '"') { if (i + 1 < t.Length && t[i + 1] == '"') i++; else q = false; } continue; }
            if (c == '"') q = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth == 0) { list.Add(Span(t, fieldStart, i)); end = i + 1; return list; }
                depth--;
            }
            else if (c == ',' && depth == 0) { list.Add(Span(t, fieldStart, i)); fieldStart = i + 1; }
        }
        end = t.Length;
        return null;
    }

    private static (int, int) Span(string t, int a, int b)
    {
        while (a < b && char.IsWhiteSpace(t[a])) a++;
        while (b > a && char.IsWhiteSpace(t[b - 1])) b--;
        return (a, b - a);
    }

    /// <summary>A structural fingerprint: tags and nesting, values elided.</summary>
    public static string Shape(string d)
    {
        var sb = new StringBuilder();
        bool q = false; int i = 0;
        for (; i < d.Length && sb.Length < 80; i++)
        {
            char c = d[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < d.Length && d[i + 1] == '"') { i++; continue; } q = false; }
                continue;
            }
            if (c == '"')
            {
                int close = d.IndexOf('"', i + 1);
                string lit = close > i ? d.Substring(i + 1, close - i - 1) : "";
                // Keep short tag literals ("R", "S", "U", "#", "P"…), elide others.
                sb.Append(lit.Length <= 2 ? $"\"{lit}\"" : "\"…\"");
                if (close > i) i = close; else q = true;
                continue;
            }
            if (c is '{' or '}' or ',') sb.Append(c);
            else if (sb.Length == 0 || sb[^1] != '·') sb.Append('·');
        }
        return sb.ToString();
    }
}
