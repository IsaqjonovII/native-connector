using System.Text.Json;

namespace OneC.Cloud;

/// <summary>
/// Which AIBA cloud the app talks to. Production is the default, as in the old Connector
/// (connector src/stores/app.ts:15,48); Development is its Ctrl+Shift+D mode. A third, "custom",
/// comes from <c>%LOCALAPPDATA%\AIBA\Connector\cloud.json</c> — used for the local stub.
/// Base URIs end with '/' so relative paths append instead of replacing the last segment.
/// </summary>
public sealed record CloudEnvironment(string Key, string Label, Uri ApiBase, Uri OneCBase)
{
    public static readonly CloudEnvironment Prod =
        new("prod", "Production", new Uri("https://api.aiba.group/api/v1/"), new Uri("https://1c.aiba.group/api/v2/"));

    public static readonly CloudEnvironment Dev =
        new("dev", "Development", new Uri("https://api-dev.aiba.uz/api/v1/"), new Uri("https://aiba-1c-dev.aiba.uz/api/v2/"));

    /// <summary>Production, Development, and the custom one when <c>cloud.json</c> exists and is valid.</summary>
    public static IReadOnlyList<CloudEnvironment> Available(string settingsDir)
    {
        var list = new List<CloudEnvironment> { Prod, Dev };
        if (Custom(settingsDir) is { } c) list.Add(c);
        return list;
    }

    public static CloudEnvironment Find(string settingsDir, string? key) =>
        Available(settingsDir).FirstOrDefault(e => e.Key == key) ?? Prod;

    /// <summary><c>{ "label": "...", "apiBase": "...", "oneCBase": "..." }</c>. Plain http only on loopback.</summary>
    public static CloudEnvironment? Custom(string settingsDir)
    {
        string path = Path.Combine(settingsDir, "cloud.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var api = Slash(r.GetProperty("apiBase").GetString()!);
            var onec = Slash(r.GetProperty("oneCBase").GetString()!);
            if (!Allowed(api) || !Allowed(onec)) return null;
            string label = r.TryGetProperty("label", out var l) ? l.GetString() ?? "Custom" : "Custom";
            return new CloudEnvironment("custom", label, api, onec);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Tokens only ever travel over TLS, except to this machine (the stub).</summary>
    public static bool Allowed(Uri u) => u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);

    private static Uri Slash(string s) => new(s.EndsWith('/') ? s : s + "/");
}
