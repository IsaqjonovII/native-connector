using System.Runtime.InteropServices;

namespace OneC.Interop;

/// <summary>
/// Owns exactly one 1C COM object and releases it deterministically on the thread that
/// created it. Leaving 1C RCWs to the GC finalizer kills the process with 0xC0000005 —
/// see DOTNET_ONEC_CONCURRENCY_RESEARCH.md. Nothing in this stack may rely on the finalizer,
/// so every COM object crossing our boundary is wrapped here.
/// </summary>
public sealed class ComRef : IDisposable
{
    private object? _obj;
    private readonly int _ownerThread;

    public string Label { get; }
    public bool IsDisposed => _obj is null;

    private ComRef(object obj, string label)
    {
        _obj = obj;
        Label = label;
        _ownerThread = Environment.CurrentManagedThreadId;
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _created);
    }

    /// <summary>Takes ownership. Null is legal and yields a ref that owns nothing.</summary>
    public static ComRef Own(object? obj, string label) =>
        obj is null ? new ComRef(NullSentinel, label) : new ComRef(obj, label);

    private static readonly object NullSentinel = new();

    public object Target => _obj ?? throw new ObjectDisposedException($"ComRef({Label})");

    public bool HoldsComObject => _obj is not null && !ReferenceEquals(_obj, NullSentinel);

    public void Dispose()
    {
        var o = Interlocked.Exchange(ref _obj, null);
        if (o is null) return;
        Interlocked.Decrement(ref _live);

        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            Interlocked.Increment(ref _wrongThread);
            if (StrictThreadAffinity)
                throw new InvalidOperationException(
                    $"ComRef({Label}) created on thread {_ownerThread} released on " +
                    $"{Environment.CurrentManagedThreadId}. 1C COM objects must be released " +
                    $"on their creating thread.");
        }

        if (ReferenceEquals(o, NullSentinel)) return;
        try { if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        catch (Exception) { Interlocked.Increment(ref _releaseFailures); }
    }

    // ---- process-wide leak accounting, read by the tests and the metrics endpoint ----

    private static int _live, _created, _wrongThread, _releaseFailures;

    /// <summary>Set false only in a test that deliberately exercises the wrong-thread path.</summary>
    public static bool StrictThreadAffinity { get; set; } = true;

    public static int Live => Volatile.Read(ref _live);
    public static long Created => Volatile.Read(ref _created);
    public static int WrongThreadReleases => Volatile.Read(ref _wrongThread);
    public static int ReleaseFailures => Volatile.Read(ref _releaseFailures);

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _created, 0);
        Interlocked.Exchange(ref _wrongThread, 0);
        Interlocked.Exchange(ref _releaseFailures, 0);
    }
}

/// <summary>
/// A bag of ComRefs released in reverse creation order when the scope ends. 1C dislikes
/// outliving parents: a Выборка released after its Запрос is how the old code cracked.
/// </summary>
public sealed class ComScope : IDisposable
{
    private readonly List<ComRef> _refs = new();
    private bool _disposed;

    public ComRef Add(object? obj, string label)
    {
        var r = ComRef.Own(obj, label);
        _refs.Add(r);
        return r;
    }

    public object Track(object? obj, string label) => Add(obj, label).Target;

    public int Count => _refs.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _refs.Count - 1; i >= 0; i--) _refs[i].Dispose();
        _refs.Clear();
    }
}
