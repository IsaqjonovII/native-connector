using System.Text.Json.Serialization;

namespace OneC.Sessions;

/// <summary>One configured 1C infobase.</summary>
public sealed record OneCBase
{
    public required string Name { get; init; }
    public required string ConnectionString { get; init; }

    /// <summary>Platform version this base needs. Exact for server bases, floor for file bases.</summary>
    public string PlatformVersion { get; init; } = "8.3.15.1565";

    [JsonIgnore]
    public bool IsFile => ConnectionString.StartsWith("File=", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder of a file base (the File="…" value, "" doubled-quote unescaped).</summary>
    [JsonIgnore]
    public string? FilePath
    {
        get
        {
            if (!IsFile) return null;
            var m = System.Text.RegularExpressions.Regex.Match(ConnectionString, "^File=\"((?:[^\"]|\"\")*)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Replace("\"\"", "\"");
            var bare = System.Text.RegularExpressions.Regex.Match(ConnectionString, "^File=([^;]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return bare.Success ? bare.Groups[1].Value : null;
        }
    }

    [JsonIgnore]
    public string Kind => IsFile ? "FILE" : "SERVER";

    /// <summary>
    /// Per-base concurrency ceiling. Server bases read usefully to K=4. File bases were
    /// also K=4 on throughput but are RAM-bound, and the rewrite measured the cost:
    /// ~206 MB per file-base session against ~50 MB for a server session, so four of them
    /// is 0.9 GB for one base. Two is the default; raise it per base if the box can afford it.
    /// This is a ceiling on *useful* concurrency, never a number of sessions to pre-create.
    /// </summary>
    [JsonIgnore]
    public int MaxConcurrency => MaxConcurrencyOverride ?? (IsFile ? 2 : 4);

    public int? MaxConcurrencyOverride { get; init; }

    /// <summary>Measured cost of one warm session, used by the memory budget.</summary>
    [JsonIgnore]
    public int EstimatedSessionMb => IsFile ? 210 : 55;

    /// <summary>Connection string with the password blanked, for logs and errors.</summary>
    [JsonIgnore]
    public string SafeConnectionString =>
        System.Text.RegularExpressions.Regex.Replace(
            ConnectionString, "Pwd=[^;]*", "Pwd=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
