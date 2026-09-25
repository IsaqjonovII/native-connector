using System.Collections.Concurrent;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public enum WriteKind { Create, Update, Post, MarkDeleted, Delete }

/// <summary>
/// Per-base, per-operation concurrency ceilings for writes, separate from the session pool
/// cap. The numbers are the measured limits from DOTNET_ONEC_CONCURRENCY_RESEARCH.md
/// (DECISIONS D12): direct delete ran ~10× slower than create with a 9 s tail, and
/// mixed-type posting at K=4 failed 6 of 20. Gates are taken BEFORE renting a session, so a
/// queued post never sits on a session a read could use.
/// </summary>
public sealed class WriteGates
{
    private readonly ConcurrentDictionary<(string Base, WriteKind Kind), SemaphoreSlim> _gates = new();

    public static int Limit(OneCBase b, WriteKind k) => k switch
    {
        WriteKind.Create => b.IsFile ? 2 : 4,
        WriteKind.Update => 2,
        WriteKind.MarkDeleted => 2,
        WriteKind.Post => 1,
        WriteKind.Delete => 1,
        _ => 1
    };

    public T Run<T>(OneCBase b, WriteKind k, TimeSpan timeout, Func<T> work, CancellationToken ct)
    {
        // One gate per (registered base, kind): bounded by configuration, not by traffic.
        var gate = _gates.GetOrAdd((b.Name.ToLowerInvariant(), k), _ => new SemaphoreSlim(Limit(b, k)));
        if (!gate.Wait(timeout, ct))
            throw OneCException.Host(
                $"{k} gate for '{b.Name}' stayed full for {timeout.TotalSeconds:F0}s (limit {Limit(b, k)})",
                new ErrorContext(b.Name, null), k.ToString());
        try { return work(); }
        finally { gate.Release(); }
    }
}
