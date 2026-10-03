using System.Text;

namespace OneC.Desktop.Services;

public enum LauncherKind { File, Server, Web }

/// <summary>One base from the 1C launcher's list. Its kind comes from the Connect line itself.</summary>
public sealed record LauncherBase(string Name, LauncherKind Kind, string FilePath, string Server, string Ref, string Url)
{
    public string Location => Kind switch
    {
        LauncherKind.File => FilePath,
        LauncherKind.Server => $"{Server} / {Ref}",
        _ => Url
    };
}

/// <summary>
/// The 1C bases this Windows user has, read from the 1C launcher's own list
/// <c>%APPDATA%\1C\1CEStart\ibases.v8i</c> — the only source the old Connector used
/// (main.os:19885-20029). The user never types a connection or picks a type: <c>File=</c> is a
/// file base, <c>Srvr=</c> + <c>Ref=</c> a server base, <c>ws=</c> a web base. Read-only.
/// </summary>
public static class LauncherBases
{
    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart", "ibases.v8i");

    /// <summary>Every section with a Connect line, in file order; folder groups are skipped.</summary>
    public static List<LauncherBase> Read(string? file = null)
    {
        var list = new List<LauncherBase>();
        string path = file ?? DefaultFile;
        if (!File.Exists(path)) return list;

        string text;
        // Shared read: the 1C launcher may have the file open (the old app copied it on a lock).
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = r.ReadToEnd();

        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { name = l[1..^1].Trim(); continue; }
            if (name is null || !l.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase)) continue;

            var p = BaseStore.ParseConnectionString(l["Connect=".Length..]);
            LauncherBase? b =
                p.TryGetValue("file", out var f) ? new(name, LauncherKind.File, f, "", "", "") :
                p.TryGetValue("srvr", out var s) ? new(name, LauncherKind.Server, "", s, p.GetValueOrDefault("ref", ""), "") :
                p.TryGetValue("ws", out var ws) ? new(name, LauncherKind.Web, "", "", "", ws) :
                null;
            if (b is not null && name.Length > 0) list.Add(b);
            name = null;                                   // one Connect per section
        }
        return list;
    }
}
