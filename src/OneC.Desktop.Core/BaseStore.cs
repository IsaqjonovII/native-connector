using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneC.Desktop.Services;

public enum BaseKind { Server, File }

/// <summary>One configured infobase, as the user entered it. Password is never kept in clear.</summary>
public sealed class StoredBase
{
    public string Name { get; set; } = "";
    public BaseKind Kind { get; set; } = BaseKind.Server;
    public string Server { get; set; } = "";
    public string Ref { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>DPAPI (CurrentUser) protected, base64. Only this Windows user can decrypt it.</summary>
    public string PasswordProtected { get; set; } = "";
    public string PlatformVersion { get; set; } = "";

    public string Location => Kind == BaseKind.Server ? $"{Server} / {Ref}" : FilePath;

    public string ConnectionString(string password) =>
        (Kind == BaseKind.Server ? $"Srvr=\"{Esc(Server)}\";Ref=\"{Esc(Ref)}\";" : $"File=\"{Esc(FilePath)}\";")
        + $"Usr=\"{Esc(User)}\";Pwd=\"{Esc(password)}\";";

    private static string Esc(string s) => s.Replace("\"", "\"\"");
}

/// <summary>
/// The desktop app's list of infobases: <c>%LOCALAPPDATA%\AIBA\Connector\bases.json</c>.
/// Passwords are protected with DPAPI for the current Windows user, so the file is useless
/// on another account or machine. Plain-text credentials exist only in memory and on the
/// supervisor's stdin (DECISIONS D25) — never on disk.
/// </summary>
public sealed class BaseStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIBA.Connector.bases.v1");

    public string FilePath { get; }
    public List<StoredBase> Bases { get; private set; } = new();
    public event EventHandler? Changed;

    public BaseStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector", "bases.json");
        Load();
    }

    public void Load()
    {
        try
        {
            Bases = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<StoredBase>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException) { Bases = new(); }
    }

    public void Save()
    {
        // One order everywhere: screens index rows by position in this list.
        Bases.Sort((a, c) => string.Compare(a.Name, c.Name, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Bases, Json));
        File.Move(tmp, FilePath, overwrite: true);          // never a half-written file
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Upsert(StoredBase b, string? oldName = null)
    {
        Bases.RemoveAll(x => x.Name.Equals(oldName ?? b.Name, StringComparison.OrdinalIgnoreCase));
        Bases.Add(b);
        Save();
    }

    public void Remove(string name)
    {
        Bases.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy,
                                                               DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// The JSON line the supervisor reads from stdin (`run --bases-stdin`). A password that
    /// cannot be decrypted (file copied from another Windows account) is sent empty, so the
    /// base shows a clear "wrong password" error instead of the whole engine failing to start.
    /// </summary>
    public string SupervisorPayload() => JsonSerializer.Serialize(Bases.Select(b =>
    {
        string pwd;
        try { pwd = Unprotect(b.PasswordProtected); }
        catch (CryptographicException)
        {
            DesktopLog.Write($"password for '{b.Name}' cannot be decrypted by this Windows account — re-enter it");
            pwd = "";
        }
        return new { Name = b.Name, ConnectionString = b.ConnectionString(pwd), PlatformVersion = b.PlatformVersion };
    }));

    /// <summary>
    /// Imports a developer bases file (the `bases.local.json` shape: Name, ConnectionString,
    /// PlatformVersion). Passwords are protected on the way in; the source file is not touched.
    /// </summary>
    public int ImportConnectionStrings(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int n = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string name = e.GetProperty("Name").GetString()!;
            string cs = e.GetProperty("ConnectionString").GetString()!;
            var parts = ParseConnectionString(cs);
            var b = new StoredBase
            {
                Name = name,
                Kind = parts.ContainsKey("file") ? BaseKind.File : BaseKind.Server,
                Server = parts.GetValueOrDefault("srvr", ""),
                Ref = parts.GetValueOrDefault("ref", ""),
                FilePath = parts.GetValueOrDefault("file", ""),
                User = parts.GetValueOrDefault("usr", ""),
                PasswordProtected = Protect(parts.GetValueOrDefault("pwd", "")),
                PlatformVersion = e.TryGetProperty("PlatformVersion", out var pv) ? pv.GetString() ?? "" : ""
            };
            Bases.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Bases.Add(b);
            n++;
        }
        Save();
        return n;
    }

    /// <summary>
    /// The old Connector's adapter configs on this machine (Tauri app-data folders), newest
    /// first. Read-only: importing never changes them.
    /// </summary>
    public static IReadOnlyList<string> FindOldConnectorConfigs()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[] { "aiba.uz", "uz.aiba.connector.next" }
            .Select(app => Path.Combine(roaming, app, "1c-adapter", "config.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// The old Connector's saved login for a base (<c>databases.&lt;name&gt;</c>, keyed by the
    /// launcher name), to fill in the Connect form; null when it has none. Read-only.
    /// </summary>
    public static (string User, string Password, string Version)? OldConnectorLogin(string name)
    {
        foreach (var path in FindOldConnectorConfigs())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("databases", out var dbs) || dbs.ValueKind != JsonValueKind.Object) continue;
                foreach (var db in dbs.EnumerateObject())
                {
                    if (!db.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    string S(string k) => db.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    return (S("user"), S("password"), S("comVersion"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    public sealed record ImportReport(int Imported, List<string> Skipped);

    /// <summary>
    /// Imports the old Connector's <c>config.json</c> (<c>databases.&lt;name&gt;</c> with
    /// type/server/database/path/user/password/comVersion — main.os:2145 ПодключитьсяКБазе).
    /// HTTP / cloud / OData bases are skipped: this engine is COM-only. Mapping follows the old
    /// adapter exactly: type "server", or "file" WITH a server, means a server base.
    /// </summary>
    public ImportReport ImportOldConnectorConfig(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var skipped = new List<string>();
        int n = 0;
        if (!doc.RootElement.TryGetProperty("databases", out var dbs) || dbs.ValueKind != JsonValueKind.Object)
            return new ImportReport(0, skipped);

        foreach (var db in dbs.EnumerateObject())
        {
            string S(string k) => db.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string type = S("type").ToLowerInvariant();
            if (type is "http" or "cloud" or "odata") { skipped.Add($"{db.Name} ({type}: not a COM base)"); continue; }

            bool server = type == "server" || (type == "file" && S("server").Length > 0);
            var b = new StoredBase
            {
                Name = db.Name,
                Kind = server ? BaseKind.Server : BaseKind.File,
                Server = S("server"),
                Ref = S("database"),
                FilePath = S("path"),
                User = S("user"),
                PasswordProtected = Protect(S("password")),
                PlatformVersion = S("comVersion")
            };
            Bases.RemoveAll(x => x.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase));
            Bases.Add(b);
            n++;
        }
        Save();
        return new ImportReport(n, skipped);
    }

    /// <summary>1C connection strings: Key="value";Key=value; with "" as an escaped quote.</summary>
    public static Dictionary<string, string> ParseConnectionString(string cs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < cs.Length)
        {
            int eq = cs.IndexOf('=', i);
            if (eq < 0) break;
            string key = cs[i..eq].Trim().TrimStart(';').Trim();
            i = eq + 1;
            var val = new StringBuilder();
            if (i < cs.Length && cs[i] == '"')
            {
                i++;
                while (i < cs.Length)
                {
                    if (cs[i] == '"')
                    {
                        if (i + 1 < cs.Length && cs[i + 1] == '"') { val.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    val.Append(cs[i++]);
                }
                while (i < cs.Length && cs[i] != ';') i++;
            }
            else
            {
                while (i < cs.Length && cs[i] != ';') val.Append(cs[i++]);
            }
            i++;                                            // skip ';'
            if (key.Length > 0) d[key.ToLowerInvariant()] = val.ToString();
        }
        return d;
    }

    /// <summary>1C platform installs on this machine, newest first — for the version picker.</summary>
    public static IReadOnlyList<string> InstalledPlatforms()
    {
        var list = new List<Version>();
        foreach (var root in new[] { @"C:\Program Files\1cv8", @"C:\Program Files (x86)\1cv8" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root))
                if (Version.TryParse(Path.GetFileName(d), out var v) && File.Exists(Path.Combine(d, "bin", "comcntr.dll")))
                    list.Add(v);
        }
        return list.Distinct().OrderByDescending(v => v).Select(v => v.ToString()).ToList();
    }
}
