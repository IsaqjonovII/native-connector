using System.Diagnostics;
using System.Text;

namespace OneC.EventLog;

/// <summary>An infobase registered in a cluster's <c>1CV8Clst.lst</c>.</summary>
public sealed record ClusterInfobase(string Guid, string Name, string Dbms);

/// <summary>The running server agent's cluster location: <c>-d &lt;srvinfo&gt;</c>, <c>-regport &lt;port&gt;</c>.</summary>
public sealed record RagentInfo(string Srvinfo, int Regport, string Exe);

/// <summary>
/// Where a base's event log lives (ported from the connector's `eventlog_discovery.rs`):
/// a file base's is <c>&lt;dir&gt;\1Cv8Log</c>; a server base's is
/// <c>&lt;srvinfo&gt;\reg_&lt;port&gt;\&lt;infobase guid&gt;\1Cv8Log</c> — readable only when the cluster
/// runs on this machine. The GUID comes only from the cluster list's own entry for the name,
/// so an orphaned GUID directory can never be picked up.
/// </summary>
public sealed class LogLocator
{
    private readonly Func<IReadOnlyList<string>> _ragentCommandLines;
    private (DateTime At, RagentInfo? Info, string? Error)? _ragent;
    public static TimeSpan RagentTtl { get; set; } = TimeSpan.FromMinutes(1);

    public LogLocator(Func<IReadOnlyList<string>>? ragentCommandLines = null) =>
        _ragentCommandLines = ragentCommandLines ?? QueryRagentCommandLines;

    /// <summary>The log directory, or why there is none (a short reason code and detail).</summary>
    public (string? Dir, string? Error) Resolve(string connectionString)
    {
        var kv = ConnectionString(connectionString);
        if (kv.TryGetValue("file", out var file))
        {
            string dir = Path.Combine(file, "1Cv8Log");
            return Directory.Exists(dir) ? (dir, null) : (null, "no_log_dir");
        }
        if (!kv.TryGetValue("srvr", out var host) || !kv.TryGetValue("ref", out var name)) return (null, "unsupported_connection_string");
        if (!IsLocalHost(host)) return (null, "not_local");

        var (info, err) = Ragent();
        if (info is null) return (null, err ?? "ragent_not_found");
        string reg = Path.Combine(info.Srvinfo, $"reg_{info.Regport}");
        string list = Path.Combine(reg, "1CV8Clst.lst");
        string text;
        try { text = File.ReadAllText(list, Encoding.UTF8); } catch (IOException) { return (null, "cluster_list_unreadable"); }
        catch (UnauthorizedAccessException) { return (null, "cluster_list_unreadable"); }
        var guid = ParseClusterList(text).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.Guid;
        if (guid is null) return (null, "infobase_not_in_cluster");
        string logDir = Path.Combine(reg, guid, "1Cv8Log");
        return Directory.Exists(logDir) ? (logDir, null) : (null, "no_log_dir");
    }

    private (RagentInfo?, string?) Ragent()
    {
        if (_ragent is { } c && DateTime.UtcNow - c.At < RagentTtl) return (c.Info, c.Error);
        RagentInfo? info = null; string? error = null;
        try
        {
            var infos = _ragentCommandLines().Select(ParseRagentCommandLine).OfType<RagentInfo>().Distinct().ToList();
            // More than one running cluster location: refuse to guess (the wrong one would
            // tail another cluster's log for a base of the same name).
            if (infos.Count == 1) info = infos[0];
            else error = infos.Count == 0 ? "ragent_not_found" : "ragent_ambiguous";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            error = "ragent_query_failed";
        }
        _ragent = (DateTime.UtcNow, info, error);
        return (info, error);
    }

    // ---------------- pure parts ----------------

    public static Dictionary<string, string> ConnectionString(string cs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < cs.Length)
        {
            int eq = cs.IndexOf('=', i);
            if (eq < 0) break;
            string key = cs[i..eq].Trim().Trim(';').Trim();
            i = eq + 1;
            string value;
            if (i < cs.Length && cs[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < cs.Length)
                {
                    if (cs[i] == '"') { if (i + 1 < cs.Length && cs[i + 1] == '"') { sb.Append('"'); i += 2; continue; } i++; break; }
                    sb.Append(cs[i++]);
                }
                value = sb.ToString();
                int semi = cs.IndexOf(';', i);
                i = semi < 0 ? cs.Length : semi + 1;
            }
            else
            {
                int semi = cs.IndexOf(';', i);
                value = (semi < 0 ? cs[i..] : cs[i..semi]).Trim();
                i = semi < 0 ? cs.Length : semi + 1;
            }
            if (key.Length > 0) d[key] = value;
        }
        return d;
    }

    /// <summary><c>-d</c> and <c>-regport</c> are both required; no default port (a multi-instance host would be ambiguous).</summary>
    public static RagentInfo? ParseRagentCommandLine(string line)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, has = false;
        foreach (char ch in line)
        {
            if (ch == '"') { quoted = !quoted; has = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { if (has) { tokens.Add(cur.ToString()); cur.Clear(); has = false; } continue; }
            cur.Append(ch); has = true;
        }
        if (has) tokens.Add(cur.ToString());
        if (tokens.Count == 0) return null;
        string? srvinfo = null; int? port = null;
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i] == "-d" && i + 1 < tokens.Count) srvinfo = tokens[++i];
            else if (tokens[i] == "-regport" && i + 1 < tokens.Count && int.TryParse(tokens[i + 1], out int p)) { port = p; i++; }
        }
        return srvinfo is null || port is null ? null : new RagentInfo(srvinfo, port.Value, tokens[0]);
    }

    /// <summary>Entries <c>{&lt;guid&gt;,"name","descr","DBMS",…</c>; anything else is skipped, never guessed.</summary>
    public static List<ClusterInfobase> ParseClusterList(string text)
    {
        text = text.TrimStart('﻿');
        var list = new List<ClusterInfobase>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{' || i + 38 > text.Length || text[i + 37] != ',') continue;
            string guid = text.Substring(i + 1, 36);
            if (!Guid.TryParseExact(guid, "D", out _)) continue;
            int p = i + 38;
            if (Quoted(text, ref p) is not { } name || !Comma(text, ref p) ||
                Quoted(text, ref p) is null || !Comma(text, ref p) || Quoted(text, ref p) is not { } dbms) continue;
            list.Add(new ClusterInfobase(guid, name, dbms));
            i = p - 1;
        }
        return list;
    }

    private static bool Comma(string s, ref int p) { if (p < s.Length && s[p] == ',') { p++; return true; } return false; }

    private static string? Quoted(string s, ref int p)
    {
        if (p >= s.Length || s[p] != '"') return null;
        var sb = new StringBuilder();
        int i = p + 1;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                p = i + 1;
                return sb.ToString();
            }
            sb.Append(s[i++]);
        }
        return null;
    }

    /// <summary>
    /// This machine: empty, ".", localhost, loopback, the computer name, or an FQDN in this
    /// machine's verified DNS domain. An unverifiable FQDN is not local (fail closed — a remote
    /// host with the same short name must never be read as this one). A trailing
    /// <c>:port</c> and IPv6 brackets are ignored.
    /// </summary>
    public static bool IsLocalHost(string host, string? computerName = null, string? dnsDomain = null)
    {
        string h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('[') && h.IndexOf(']') is var end and > 0) h = h[1..end];
        else if (h.LastIndexOf(':') is var colon and > 0 && h[(colon + 1)..].All(char.IsAsciiDigit) && !h[..colon].Contains(':') && colon < h.Length - 1)
            h = h[..colon];
        if (h is "" or "." or "localhost" or "localhost.localdomain" or "::1" || h.StartsWith("127.")) return true;
        string me = (computerName ?? Environment.GetEnvironmentVariable("COMPUTERNAME") ?? "").Trim().ToLowerInvariant();
        string? domain = (dnsDomain ?? Environment.GetEnvironmentVariable("USERDNSDOMAIN"))?.Trim().ToLowerInvariant();
        if (me.Length == 0) return false;
        if (h == me) return true;
        return !string.IsNullOrEmpty(domain) && h == me + "." + domain;
    }

    /// <summary>Command lines of running <c>ragent.exe</c> services (PowerShell/CIM, as the connector does).</summary>
    private static IReadOnlyList<string> QueryRagentCommandLines()
    {
        var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Service | " +
            "Where-Object { $_.PathName -like '*ragent.exe*' -and $_.State -eq 'Running' } | Select-Object -ExpandProperty PathName)\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell did not start");
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"powershell exited with {p.ExitCode}");
        return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}
