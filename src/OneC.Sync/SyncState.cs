using System.Text.Json;

namespace OneC.Sync;

/// <summary>Progress of one table: the cold read's resume point, then only "done".</summary>
public sealed class TableState
{
    public bool ColdDone { get; set; }
    /// <summary>Catalogs: the last uploaded GUID (keyset).</summary>
    public string? After { get; set; }
    /// <summary>Documents and registers: the asc date cursor and how many rows at that date are already done.</summary>
    public string? CursorDate { get; set; }
    public int CursorSkip { get; set; }
    public long Uploaded { get; set; }
}

/// <summary>
/// One base's sync state on disk: the change-feed cursor and each table's cold-read progress.
/// Written after every uploaded page (write-then-rename), so a crash resumes where it stopped
/// and never skips rows — a page is only marked done after the backend accepted it.
/// </summary>
public sealed class BaseState
{
    public string? FeedCursor { get; set; }
    public Dictionary<string, TableState> Tables { get; set; } = new(StringComparer.Ordinal);

    public TableState Table(string name) => Tables.TryGetValue(name, out var t) ? t : Tables[name] = new TableState();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BaseState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<BaseState>(File.ReadAllText(path)) ?? new BaseState() : new BaseState();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
