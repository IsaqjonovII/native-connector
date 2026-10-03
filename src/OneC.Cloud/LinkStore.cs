using System.Text.Json;

namespace OneC.Cloud;

/// <summary>
/// One local infobase linked to a cloud 1C record. <see cref="CreatedHere"/> = this app made the
/// record (a fresh partition); only those get the status heartbeat, so the app never marks
/// another connector's record active (spec §9 q2–q4).
/// </summary>
public sealed record CloudLink(string Env, string UserId, string BaseName, string OneCId, string CompanyId,
                               string CompanyName, string RecordName, string OdataName, string Provider,
                               bool CreatedHere, DateTime LinkedUtc);

/// <summary>
/// <c>%LOCALAPPDATA%\AIBA\Connector\links.json</c>, keyed (env, user, base). Separate from the
/// infobase list: bases belong to the machine, links to a cloud account in one environment.
/// Holds no token and no password.
/// </summary>
public sealed class LinkStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private List<CloudLink> _links = new();

    public event EventHandler? Changed;

    public LinkStore(string? dir = null)
    {
        _path = Path.Combine(dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector"),
                             "links.json");
        try
        {
            if (File.Exists(_path)) _links = JsonSerializer.Deserialize<List<CloudLink>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException) { _links = new(); }
    }

    public IReadOnlyList<CloudLink> For(string env, string userId) =>
        _links.Where(l => l.Env == env && l.UserId == userId).ToList();

    public CloudLink? Get(string env, string userId, string baseName) =>
        _links.FirstOrDefault(l => l.Env == env && l.UserId == userId && Same(l.BaseName, baseName));

    public void Set(CloudLink link)
    {
        _links.RemoveAll(l => l.Env == link.Env && l.UserId == link.UserId && Same(l.BaseName, link.BaseName));
        _links.Add(link);
        Save();
    }

    public void Remove(string env, string userId, string baseName)
    {
        if (_links.RemoveAll(l => l.Env == env && l.UserId == userId && Same(l.BaseName, baseName)) > 0) Save();
    }

    /// <summary>Forgets the link to a cloud record that was deleted.</summary>
    public void RemoveRecord(string env, string userId, string oneCId)
    {
        if (_links.RemoveAll(l => l.Env == env && l.UserId == userId && l.OneCId == oneCId) > 0) Save();
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_links, Json));
        File.Move(tmp, _path, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
