using OneC.Interop;

namespace OneC.Sessions;

/// <summary>
/// Which host process a base belongs to. A process can host exactly one comcntr, and
/// bitness is a process property, so the identity is (version, bitness) — DECISIONS D02.
/// Bases that resolve to the same install share one process; nothing else does.
/// </summary>
public sealed record HostAssignment(PlatformInstall Install, List<OneCBase> Bases)
{
    public string Key => Install.HostKey;
}

/// <summary>
/// Pure planning: no COM, no processes. Lives here rather than in the host so the
/// supervisor — which must never load comcntr — can use it.
/// </summary>
public static class HostPlan
{
    /// <summary>
    /// Groups bases into the minimum number of host processes. Returns the plan plus any
    /// base that has no usable install, rather than silently dropping it.
    /// </summary>
    public static (List<HostAssignment> Hosts, List<(OneCBase Base, string Reason)> Unplaceable)
        Build(IEnumerable<OneCBase> bases, IReadOnlyList<PlatformInstall> installs)
    {
        var hosts = new Dictionary<string, HostAssignment>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<(OneCBase, string)>();
        var all = bases.ToList();

        // Server bases first: their version is fixed, so they decide which hosts exist.
        // File bases then reuse one of those hosts whenever any install they accept is
        // already running — every extra host is another process baseline (DECISIONS D03).
        foreach (var b in all.Where(b => !b.IsFile).Concat(all.Where(b => b.IsFile)))
        {
            var candidates = PlatformCatalog.Candidates(installs, b.PlatformVersion, b.IsFile);
            if (candidates.Count == 0)
            {
                bad.Add((b, b.IsFile
                    ? $"no install at or above {b.PlatformVersion}"
                    : $"no install matching {b.PlatformVersion} exactly (server bases require it)"));
                continue;
            }

            var install = candidates.FirstOrDefault(c => hosts.ContainsKey(c.HostKey)) ?? candidates[0];
            if (!hosts.TryGetValue(install.HostKey, out var h))
                hosts[install.HostKey] = h = new HostAssignment(install, new List<OneCBase>());
            h.Bases.Add(b);
        }

        return (hosts.Values.OrderBy(h => h.Key).ToList(), bad);
    }
}
