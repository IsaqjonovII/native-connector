using System.Diagnostics;
using OneC.Interop;

namespace OneC.Host;

/// <summary>
/// One process resource reading. Working set and private bytes are the numbers that matter
/// here: 1C lives in native memory, so the managed heap can look tiny while the process is
/// huge. Both are always reported together for exactly that reason.
/// </summary>
public readonly record struct ResourceSample(
    string Label,
    long WorkingSetMb,
    long PrivateBytesMb,
    long ManagedHeapMb,
    long GcCommittedMb,
    int Gen0, int Gen1, int Gen2,
    int Threads,
    int Handles,
    double CpuSeconds,
    int LiveComRefs,
    int Sessions)
{
    public string Line() =>
        $"{Label,-28} ws={WorkingSetMb,5} MB  priv={PrivateBytesMb,5} MB  managed={ManagedHeapMb,4} MB " +
        $"gcCommit={GcCommittedMb,4} MB  thr={Threads,3} hnd={Handles,5} cpu={CpuSeconds,6:F1}s " +
        $"comrefs={LiveComRefs,3} sessions={Sessions}";

    public string Csv() =>
        $"{Label},{WorkingSetMb},{PrivateBytesMb},{ManagedHeapMb},{GcCommittedMb}," +
        $"{Gen0},{Gen1},{Gen2},{Threads},{Handles},{CpuSeconds:F2},{LiveComRefs},{Sessions}";

    public static string CsvHeader =>
        "label,workingSetMb,privateBytesMb,managedHeapMb,gcCommittedMb,gen0,gen1,gen2," +
        "threads,handles,cpuSeconds,liveComRefs,sessions";
}

public static class ResourceSampler
{
    /// <summary>
    /// Settle first: a reading taken while the GC still holds garbage tells us nothing about
    /// whether native memory actually came back.
    /// </summary>
    public static ResourceSample Take(string label, int sessions = 0, bool collect = true)
    {
        if (collect)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var info = GC.GetGCMemoryInfo();

        return new ResourceSample(
            label,
            p.WorkingSet64 / 1024 / 1024,
            p.PrivateMemorySize64 / 1024 / 1024,
            GC.GetTotalMemory(false) / 1024 / 1024,
            info.TotalCommittedBytes / 1024 / 1024,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            p.Threads.Count,
            p.HandleCount,
            p.TotalProcessorTime.TotalSeconds,
            ComRef.Live,
            sessions);
    }

    /// <summary>Difference in the two numbers that reflect real memory cost.</summary>
    public static string Delta(ResourceSample from, ResourceSample to) =>
        $"{to.Label,-28} Δws={to.WorkingSetMb - from.WorkingSetMb,+5} MB  " +
        $"Δpriv={to.PrivateBytesMb - from.PrivateBytesMb,+5} MB  " +
        $"Δthr={to.Threads - from.Threads,+3}  Δhnd={to.Handles - from.Handles,+5}";
}
