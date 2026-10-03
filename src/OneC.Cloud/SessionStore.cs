using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneC.Cloud;

/// <summary>The signed-in user as the app keeps it. Tokens exist in clear only in memory.</summary>
public sealed record CloudSession(string Env, string UserId, string Phone, string FirstName, string LastName,
                                  string AccessToken, string RefreshToken)
{
    public string DisplayName => $"{FirstName} {LastName}".Trim() is { Length: > 0 } n ? n : Phone;
}

/// <summary>
/// <c>session.json</c> (the signed-in user) and <c>cloud-settings.json</c> (the chosen environment)
/// under <c>%LOCALAPPDATA%\AIBA\Connector</c>. Both tokens are DPAPI-protected for the current
/// Windows user; the login password is never written anywhere. The old Connector kept both tokens
/// as plain JSON in the WebView's localStorage (spec §7) — deliberately not copied.
/// </summary>
public sealed class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIBA.Connector.cloud-session.v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Dir { get; }
    private string SessionPath => Path.Combine(Dir, "session.json");
    private string SettingsPath => Path.Combine(Dir, "cloud-settings.json");

    public SessionStore(string? dir = null) =>
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector");

    private sealed record Stored(string Env, string UserId, string Phone, string FirstName, string LastName,
                                 string Access, string Refresh, DateTime SavedUtc);

    public CloudSession? Load(string env)
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(SessionPath));
            if (s is null || s.Env != env) return null;           // a session is valid only for the env it logged in under
            return new CloudSession(s.Env, s.UserId, s.Phone, s.FirstName, s.LastName, Unprotect(s.Access), Unprotect(s.Refresh));
        }
        catch (Exception e) when (e is JsonException or CryptographicException or FormatException or IOException)
        {
            return null;                                           // unreadable or another Windows account: sign in again
        }
    }

    public void Save(CloudSession s) => Write(SessionPath, JsonSerializer.Serialize(
        new Stored(s.Env, s.UserId, s.Phone, s.FirstName, s.LastName, Protect(s.AccessToken), Protect(s.RefreshToken), DateTime.UtcNow), Json));

    public void Clear()
    {
        try { File.Delete(SessionPath); } catch (IOException) { }
    }

    /// <summary><c>cloud-settings.json</c>: the environment and, per (env, user), the chosen company.</summary>
    private sealed class Settings
    {
        public string Env { get; set; } = CloudEnvironment.Prod.Key;
        public Dictionary<string, string> Company { get; set; } = new();
    }

    private Settings ReadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                : new();
        }
        catch (Exception e) when (e is JsonException or IOException) { return new(); }
    }

    private void WriteSettings(Settings s) => Write(SettingsPath, JsonSerializer.Serialize(s, Json));

    public string LoadEnvKey() => ReadSettings().Env is { Length: > 0 } e ? e : CloudEnvironment.Prod.Key;

    public void SaveEnvKey(string key)
    {
        var s = ReadSettings();
        s.Env = key;
        WriteSettings(s);
    }

    public string? LoadCompany(string env, string userId) => ReadSettings().Company.GetValueOrDefault($"{env}|{userId}");

    public void SaveCompany(string env, string userId, string companyId)
    {
        var s = ReadSettings();
        s.Company[$"{env}|{userId}"] = companyId;
        WriteSettings(s);
    }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Protect(string s) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string b64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser));
}
