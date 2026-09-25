using System.Collections.Concurrent;
using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Milestone 2.2: several bases active inside ONE host. Four phases, each answering one
/// question about the shared budget:
///   A  both bases busy at once — combined memory, per-base session counts
///   B  one base goes quiet — does it give its sessions back, or keep K forever?
///   C  memory pressure — does a busy base evict the idle sessions of a quiet one?
///   D  read/write fairness on one base — what do writers at the cap do to read latency?
/// Measurement code, not production code: it lives beside Program, not in the libraries.
/// </summary>
public static class MultiBaseScenario
{
    private static readonly ReadQuery Q = new()
    {
        Entity = "Справочник.Номенклатура",
        Fields = new[] { "Наименование" },
        Limit = 50
    };

    public static int Run(List<OneCBase> bases, string comcntr, int phaseSeconds, int idleSeconds,
                          string? writeDoc, int pressureMb, string phases = "ABCD")
    {
        var server = bases.FirstOrDefault(b => !b.IsFile)
                     ?? throw new ArgumentException("scenario needs a server base");
        var file = bases.FirstOrDefault(b => b.IsFile)
                   ?? throw new ArgumentException("scenario needs a file base");

        Console.WriteLine($"multi-base scenario: server={server.Name} file={file.Name} " +
                          $"phase={phaseSeconds}s idle={idleSeconds}s");
        Console.WriteLine(ResourceSampler.Take("start").Line());

        if (phases.Contains('A'))
        using (var m = new SessionManager(comcntr, new PoolOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(idleSeconds),
            SweepInterval = TimeSpan.FromSeconds(1)
        }))
        {
            m.Register(server); m.Register(file);
            var svc = new ReadService(m);
            Console.WriteLine($"\nbudget: {m.Options.GlobalMaxSessions} sessions, {m.Options.MaxWorkingSetMb} MB");

            // ---- A: both busy ----
            Console.WriteLine($"\n[A] both bases busy: {server.Name} ×4 readers, {file.Name} ×2 readers");
            var stopServer = new CancellationTokenSource();
            var stopFile = new CancellationTokenSource();
            var counts = new ConcurrentDictionary<string, int>();
            var errors = new ConcurrentBag<string>();
            var threads = Readers(svc, server.Name, 4, stopServer.Token, counts, errors)
                .Concat(Readers(svc, file.Name, 2, stopFile.Token, counts, errors)).ToList();
            threads.ForEach(t => t.Start());
            Watch(m, phaseSeconds, "A");
            Console.WriteLine($"    reads: {Fmt(counts)}  errors: {errors.Count}");

            // ---- B: server goes quiet ----
            Console.WriteLine($"\n[B] {server.Name} goes quiet, {file.Name} keeps working (idle timeout {idleSeconds}s)");
            stopServer.Cancel();
            counts.Clear();
            Watch(m, idleSeconds + 5, "B");
            var srvStats = m.Stats().FirstOrDefault(s => s.BaseName == server.Name);
            Console.WriteLine($"    {server.Name} after quiet period: live={srvStats?.Live} retired={srvStats?.Retired}");
            Console.WriteLine($"    reads: {Fmt(counts)}  errors: {errors.Count}");
            stopFile.Cancel();
            threads.ForEach(t => t.Join());
            foreach (var e in errors.Distinct().Take(3)) Console.WriteLine("    err: " + e);
        }
        Console.WriteLine(ResourceSampler.Take("after A+B host disposed").Line());

        // ---- C: memory pressure ----
        if (phases.Contains('C'))
        using (var m = new SessionManager(comcntr, new PoolOptions
        {
            MaxWorkingSetMb = pressureMb,
            IdleTimeout = TimeSpan.FromHours(1),
            SweepInterval = TimeSpan.FromHours(1)
        }))
        {
            Console.WriteLine($"\n[C] pressure: MaxWorkingSetMb={pressureMb}, idle timeout long — only eviction can free memory");
            m.Register(server); m.Register(file);
            var svc = new ReadService(m);
            var counts = new ConcurrentDictionary<string, int>();
            var errors = new ConcurrentBag<string>();

            // Warm the server base to K=4, then leave it idle.
            var cts = new CancellationTokenSource();
            var warm = Readers(svc, server.Name, 4, cts.Token, counts, errors).ToList();
            warm.ForEach(t => t.Start());
            Thread.Sleep(3000);
            cts.Cancel(); warm.ForEach(t => t.Join());
            Console.WriteLine("    server warmed and idle: " + Line(m));

            // Now the file base needs sessions it can only get by evicting.
            var cts2 = new CancellationTokenSource();
            var fileThreads = Readers(svc, file.Name, 2, cts2.Token, counts, errors).ToList();
            fileThreads.ForEach(t => t.Start());
            Watch(m, phaseSeconds, "C");
            cts2.Cancel(); fileThreads.ForEach(t => t.Join());

            foreach (var s in m.Stats()) Console.WriteLine("    " + s);
            Console.WriteLine($"    reads: {Fmt(counts)}  errors: {errors.Count}");
            foreach (var e in errors.Distinct().Take(3)) Console.WriteLine("    err: " + e);
        }

        // ---- D: fairness ----
        if (writeDoc is not null && phases.Contains('D'))
            Fairness(server, comcntr, writeDoc, phaseSeconds);

        Console.WriteLine();
        Console.WriteLine(ResourceSampler.Take("end").Line());
        Console.WriteLine($"comrefs created={ComRef.Created} live={ComRef.Live} " +
                          $"wrongThread={ComRef.WrongThreadReleases} releaseFailures={ComRef.ReleaseFailures}");
        return 0;
    }

    /// <summary>Read latency on one base with 0 writers, then with writers holding the cap.</summary>
    private static void Fairness(OneCBase b, string comcntr, string doc, int seconds)
    {
        Console.WriteLine($"\n[D] fairness on {b.Name}: read latency alone vs with 4 writers ({doc})");
        using var m = new SessionManager(comcntr, new PoolOptions());
        m.Register(b);
        var rs = new ReadService(m);
        var ws = new WriteService(m, "AIBA_REWRITE_");
        string marker = "AIBA_REWRITE_FAIR_" + DateTime.Now.ToString("MMddHHmmss");

        rs.Read(b.Name, Q);                                    // warm one session
        foreach (int writers in new[] { 0, 4 })
        {
            var lat = new ConcurrentBag<long>();
            var created = new ConcurrentBag<string>();
            var stop = new CancellationTokenSource();

            var wt = Enumerable.Range(0, writers).Select(i => new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                    try { created.Add(ws.CreateByClone(b.Name, doc, $"{marker} w{i}").Ref); } catch { }
            }) { IsBackground = true }).ToList();
            var reader = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    try { rs.Read(b.Name, Q); lat.Add(sw.ElapsedMilliseconds); } catch { }
                }
            }) { IsBackground = true };

            wt.ForEach(t => t.Start()); reader.Start();
            Thread.Sleep(seconds * 1000);
            stop.Cancel(); wt.ForEach(t => t.Join()); reader.Join();

            var l = lat.OrderBy(x => x).ToList();
            long P(double p) => l.Count == 0 ? -1 : l[Math.Clamp((int)Math.Ceiling(p * l.Count) - 1, 0, l.Count - 1)];
            Console.WriteLine($"    writers={writers}: reads={l.Count} p50={P(0.50)} ms p95={P(0.95)} ms " +
                              $"max={(l.Count == 0 ? -1 : l[^1])} ms  creates={created.Count}");

            int deleted = 0;
            foreach (var r in created) { try { ws.Delete(b.Name, doc, r); deleted++; } catch { } }
            if (created.Count > 0) Console.WriteLine($"    cleanup: deleted {deleted}/{created.Count}");
        }
        Console.WriteLine($"    marker docs left: {ws.FindOwned(b.Name, doc, marker).Count}");
    }

    private static IEnumerable<Thread> Readers(ReadService svc, string baseName, int n, CancellationToken ct,
                                               ConcurrentDictionary<string, int> counts, ConcurrentBag<string> errors)
    {
        for (int i = 0; i < n; i++)
            yield return new Thread(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { svc.Read(baseName, Q); counts.AddOrUpdate(baseName, 1, (_, v) => v + 1); }
                    catch (Exception e) { errors.Add($"{baseName}: {e.Message}"); Thread.Sleep(100); }
                }
            }) { IsBackground = true };
    }

    private static void Watch(SessionManager m, int seconds, string phase)
    {
        for (int t = 0; t < seconds; t += 2)
        {
            Thread.Sleep(2000);
            Console.WriteLine($"    {phase} t={t + 2,3}s " + Line(m));
        }
    }

    private static string Line(SessionManager m)
    {
        var s = ResourceSampler.Take("", m.BudgetUsed, collect: false);
        string pools = string.Join("  ", m.Stats().Select(p => $"{p.BaseName}={p.Live}(ev{p.Evicted},rt{p.Retired})"));
        return $"ws={s.WorkingSetMb,5} MB priv={s.PrivateBytesMb,5} MB sessions={m.BudgetUsed}  {pools}";
    }

    private static string Fmt(ConcurrentDictionary<string, int> c) =>
        string.Join(", ", c.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
}
