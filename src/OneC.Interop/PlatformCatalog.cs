using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneC.Interop;

public sealed record PlatformInstall(string Version, string Bitness, string ComcntrPath)
{
    /// <summary>The process identity from research §8: one host per (version, bitness).</summary>
    public string HostKey => $"{Version}-{Bitness}";
}

/// <summary>Finds the 1C platform installs on this machine by looking at disk, not at HKCR.</summary>
public static class PlatformCatalog
{
    private static readonly string[] Roots =
    {
        @"C:\Program Files\1cv8",
        @"C:\Program Files (x86)\1cv8"
    };

    public static IReadOnlyList<PlatformInstall> Discover(IEnumerable<string>? roots = null)
    {
        var found = new List<PlatformInstall>();
        foreach (var root in roots ?? Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (!Version.TryParse(name, out _)) continue;   // skips common/conf/srvinfo
                string dll = Path.Combine(dir, "bin", "comcntr.dll");
                if (!File.Exists(dll)) continue;
                found.Add(new PlatformInstall(name, PeBitness(dll), dll));
            }
        }
        return found.OrderByDescending(f => Version.Parse(f.Version)).ToList();
    }

    /// <summary>
    /// Exact match for a server base, newest install at or above the requested version for a
    /// file base — measured in research §2 ("server = exact / file = floor").
    /// </summary>
    public static PlatformInstall? Select(IReadOnlyList<PlatformInstall> installs,
                                          string requestedVersion, bool fileBase)
    {
        var want = Version.Parse(requestedVersion);
        if (!fileBase)
            return installs.FirstOrDefault(i => Version.Parse(i.Version) == want)
                   ?? installs.FirstOrDefault(i => SameMinor(Version.Parse(i.Version), want));
        // "Floor" means the OLDEST acceptable install, not the newest: that way a file base
        // lands on a host some server base already needs, instead of dragging in a second
        // process just to run a newer platform.
        return installs.Where(i => Version.Parse(i.Version) >= want)
                       .OrderBy(i => Version.Parse(i.Version))
                       .FirstOrDefault();
    }

    /// <summary>Every install that can open this base, best first.</summary>
    public static IReadOnlyList<PlatformInstall> Candidates(
        IReadOnlyList<PlatformInstall> installs, string requestedVersion, bool fileBase)
    {
        var want = Version.Parse(requestedVersion);
        if (!fileBase)
            return installs.Where(i => Version.Parse(i.Version) == want).ToList();
        return installs.Where(i => Version.Parse(i.Version) >= want)
                       .OrderBy(i => Version.Parse(i.Version))
                       .ToList();
    }

    private static bool SameMinor(Version a, Version b) =>
        a.Major == b.Major && a.Minor == b.Minor && a.Build == b.Build;

    /// <summary>Reads the PE header. A host process can only load a comcntr of its own bitness.</summary>
    public static string PeBitness(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            int e = br.ReadInt32();
            fs.Position = e + 4;
            return br.ReadUInt16() switch { 0x8664 => "x64", 0x014C => "x86", _ => "?" };
        }
        catch { return "?"; }
    }

    public static string CurrentProcessBitness => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => "?"
    };

    public static string? FileVersionOf(string dll)
    {
        try { return FileVersionInfo.GetVersionInfo(dll).FileVersion; }
        catch { return null; }
    }
}
