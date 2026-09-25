using System.Diagnostics;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Milestone 2.2 regressions: several bases inside one host, the shared connector, and the
/// working-set budget under pressure.
/// </summary>
[Collection("onec-live")]
public class MultiBaseTests
{
    private static readonly ReadQuery Q = new()
    { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 20 };

    private readonly LiveFixture _f;
    public MultiBaseTests(LiveFixture f) => _f = f;

    private bool NoBoth => !_f.Available || _f.Server is null || _f.File is null;

    /// <summary>
    /// Releasing the last connector and creating another crashed comcntr with 0xC0000005.
    /// Managers must now share one process-lifetime connector and survive being recreated.
    /// </summary>
    [Fact]
    public void ManagersCanBeRecreatedBecauseTheConnectorIsShared()
    {
        if (!_f.Available) return;
        var first = ComActivator.SharedConnector();
        for (int i = 0; i < 3; i++)
        {
            using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions());
            foreach (var b in _f.Bases) m.Register(b);
            Assert.NotEmpty(new ReadService(m).Read((_f.Server ?? _f.File!).Name, Q).Rows);
        }
        Assert.Same(first, ComActivator.SharedConnector());
    }

    [Fact]
    public void TwoBasesServeConcurrentlyWithinTheirOwnCaps()
    {
        if (NoBoth) return;
        using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions { GlobalMaxSessions = 8 });
        m.Register(_f.Server!); m.Register(_f.File!);
        var svc = new ReadService(m);
        int errors = 0;

        var threads = new List<Thread>();
        foreach (var (name, n) in new[] { (_f.Server!.Name, 4), (_f.File!.Name, 3) })
            for (int i = 0; i < n; i++)
                threads.Add(new Thread(() =>
                {
                    for (int r = 0; r < 15; r++)
                        try { svc.Read(name, Q); } catch { Interlocked.Increment(ref errors); }
                }));
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(0, errors);
        var s = m.Stats().Single(p => p.BaseName == _f.Server!.Name);
        var f = m.Stats().Single(p => p.BaseName == _f.File!.Name);
        Assert.InRange(s.Live, 1, 4);
        Assert.InRange(f.Live, 1, 2);             // 3 file readers, file cap is 2
        Assert.True(m.BudgetUsed <= 6);
    }

    /// <summary>
    /// Under a tight working-set ceiling a busy base must evict the idle sessions of a quiet
    /// base — and must never evict its own idle session to open another for itself.
    /// </summary>
    [Fact]
    public void PressureEvictsTheQuietBaseNeverTheRequester()
    {
        if (NoBoth) return;
        long ws = WorkingSetMb();
        using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions
        {
            MaxWorkingSetMb = (int)ws + 150,
            IdleTimeout = TimeSpan.FromHours(1),
            SweepInterval = TimeSpan.FromHours(1)
        });
        m.Register(_f.Server!); m.Register(_f.File!);
        var svc = new ReadService(m);

        // Two warm, idle server sessions. Errors are counted, never thrown on the raw thread:
        // an unhandled exception there terminates the whole test process.
        int errors = 0;
        var warm = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            for (int r = 0; r < 10; r++)
                try { svc.Read(_f.Server!.Name, Q); } catch { Interlocked.Increment(ref errors); }
        })).ToList();
        warm.ForEach(t => t.Start()); warm.ForEach(t => t.Join());

        // Two file readers now compete for room that only eviction can make.
        var fileThreads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            for (int r = 0; r < 20; r++)
                try { svc.Read(_f.File!.Name, Q); } catch { Interlocked.Increment(ref errors); }
        })).ToList();
        fileThreads.ForEach(t => t.Start()); fileThreads.ForEach(t => t.Join());

        var server = m.Stats().Single(p => p.BaseName == _f.Server!.Name);
        var file = m.Stats().Single(p => p.BaseName == _f.File!.Name);
        Assert.Equal(0, errors);
        Assert.True(server.Evicted >= 1, $"quiet server base was not evicted: {server}");
        Assert.Equal(0, file.Evicted);                       // no self-eviction
        Assert.Equal(1, file.Created);                       // readers shared one session
    }

    [Fact]
    public void AQuietBaseGivesItsSessionsBack()
    {
        if (NoBoth) return;
        using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(2),
            SweepInterval = TimeSpan.FromSeconds(1)
        });
        m.Register(_f.Server!); m.Register(_f.File!);
        var svc = new ReadService(m);

        var sw = Stopwatch.StartNew();
        svc.Read(_f.Server!.Name, Q);
        // Keep the file base busy past the server's idle timeout.
        while (sw.Elapsed < TimeSpan.FromSeconds(4.5)) svc.Read(_f.File!.Name, Q);

        Assert.Equal(0, m.Stats().Single(p => p.BaseName == _f.Server!.Name).Live);
        Assert.Equal(1, m.Stats().Single(p => p.BaseName == _f.File!.Name).Live);
    }

    private static long WorkingSetMb()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1024 / 1024;
    }
}
