using System.Diagnostics;

namespace OneC.Sessions;

/// <summary>
/// Machine-wide queue for Connects to FILE bases. Ported from the old adapter
/// (connector main.os:2098 ПутьБлокировкиКоннекта / :2105 ЗахватитьБлокировкуКоннекта):
/// simultaneous file-base Connects contend on machine-global platform state — ~64 s each
/// instead of ~1.7 s one after another — and, in this rewrite, produced the transient
/// "…/1Cv8tmp.1CD" sharing/creation errors (DECISIONS D26).
///
/// Same lock file and same semantics as the old adapter, on purpose: while both engines are
/// installed on one machine they queue behind each other instead of colliding.
///   - the lock is an exclusively opened handle on %TEMP%\aiba-1c-connect.lock;
///   - the OS drops it when the holder dies, so there are no stale locks to clean;
///   - wait up to 90 s polling every 250 ms, then connect WITHOUT the lock (a rare overlap
///     beats failing a whole class of requests) — also the old adapter's choice.
/// Server bases do not contend (rphost sessions) and never take it.
/// </summary>
public static class FileConnectLock
{
    public static string LockPath { get; set; } = Path.Combine(Path.GetTempPath(), "aiba-1c-connect.lock");
    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(90);

    private static long _waitedMs, _acquired, _timeouts;
    public static long TotalWaitedMs => Interlocked.Read(ref _waitedMs);
    public static long Acquired => Interlocked.Read(ref _acquired);
    public static long Timeouts => Interlocked.Read(ref _timeouts);

    /// <summary>The held lock, or null if it could not be had within <see cref="MaxWait"/>.</summary>
    public static IDisposable? Acquire(CancellationToken ct = default) => Acquire(LockPath, MaxWait, ct);

    /// <summary>Same, on an explicit path — lets tests exercise it without touching the real queue.</summary>
    public static IDisposable? Acquire(string path, TimeSpan maxWait, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                Interlocked.Increment(ref _acquired);
                Interlocked.Add(ref _waitedMs, sw.ElapsedMilliseconds);
                return fs;
            }
            catch (IOException) { /* held by another connect, in this or another process */ }
            catch (UnauthorizedAccessException) { /* same, as reported by some Windows builds */ }

            if (sw.Elapsed >= maxWait)
            {
                Interlocked.Increment(ref _timeouts);
                Interlocked.Add(ref _waitedMs, sw.ElapsedMilliseconds);
                return null;
            }
            if (ct.WaitHandle.WaitOne(250)) ct.ThrowIfCancellationRequested();
        }
    }
}
