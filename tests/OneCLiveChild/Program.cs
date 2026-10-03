using System.Diagnostics;
using System.Text.Json;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;

// OneCLiveChild <scenario> [args] — one live scenario that opens its own SessionManager(s), run in a
// process of its own (see the .csproj). Prints "PASS" and exits 0, or "FAIL: why" and exits 1; a
// native crash ends it with its own exit code, which the calling test reports. The scenarios and
// their checks are MultiBaseTests', ConnectionLifecycleTests' and SyncLiveTests' as they were
// (SyncScenarios.cs). Extra arguments are a run tag or a cleanup prefix, never a secret.
// The base list comes from ONEC_TEST_BASES (a file path, inherited from the test process), never
// from the command line: it holds 1C passwords.
// The process ends through NativeProcess.Exit, never a normal return (AGENT.md, D37): 1C's own
// exit code can crash and spin in its crash filter, holding the file base.
// The harness's own checks, no 1C: a reported failure, a native crash, a hang (LiveChildTests).
switch (args.FirstOrDefault())
{
    case "selftest-pass": Console.WriteLine("PASS"); return 0;
    case "selftest-fail": Console.WriteLine("FAIL: as asked"); return 1;
    case "selftest-crash": Console.Out.Flush(); Environment.FailFast("selftest: native-style crash"); return 3;
    case "selftest-hang": Console.WriteLine("hanging"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 4;
}
if (args.Length == 0 || Environment.GetEnvironmentVariable("ONEC_TEST_BASES") is not { Length: > 0 } basesFile)
{
    Console.WriteLine("FAIL: usage OneCLiveChild <scenario> [args], with ONEC_TEST_BASES set");
    return 2;
}
var bases = JsonSerializer.Deserialize<List<OneCBase>>(File.ReadAllText(basesFile))!;
var server = bases.FirstOrDefault(b => !b.IsFile);
var file = bases.FirstOrDefault(b => b.IsFile);
var pick = PlatformCatalog.Select(PlatformCatalog.Discover(), bases[0].PlatformVersion, bases[0].IsFile) ?? throw new InvalidOperationException("no matching 1C install");
string comcntr = pick.ComcntrPath;
var q = new ReadQuery { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 20 };

int code;
try
{
    string? fail = args[0] switch
    {
        "managers-recreated" => ManagersRecreated(),
        "two-bases-concurrent" => TwoBasesConcurrent(),
        "pressure-evicts" => PressureEvicts(),
        "quiet-base" => QuietBase(),
        "dead-idle-session" => DeadIdleSession(),
        "file-connect-lock" => FileConnectLockWaits(),
        "sync-lifecycle" when args.Length == 2 => WithManager(m => SyncScenarios.Lifecycle(m, new[] { server, file }.OfType<OneCBase>(), args[1]).GetAwaiter().GetResult()),
        "sync-edge" when args.Length == 2 => file is null ? "needs a file base"
            : WithManager(m => SyncScenarios.Edge(m, file, args[1]).GetAwaiter().GetResult()),
        "cleanup-owned" when args.Length == 3 => WithManager(m => SyncScenarios.CleanupOwned(m, bases, args[1], args[2])),
        _ => "unknown scenario or arguments: " + string.Join(" ", args)
    };
    Console.WriteLine(fail is null ? "PASS" : "FAIL: " + fail);
    code = fail is null ? 0 : 1;
}
catch (SyncScenarios.CheckFailed c)
{
    Console.WriteLine("FAIL: " + c.Message);
    code = 1;
}
catch (Exception e)
{
    Console.WriteLine("FAIL: " + e);
    code = 1;
}
NativeProcess.Exit(code);                       // flushes stdout/stderr, then TerminateProcess
return code;

string? WithManager(Func<SessionManager, string?> scenario)
{
    using var m = SyncScenarios.Manager(comcntr, bases);
    return scenario(m);
}

// Releasing the last connector and creating another crashed comcntr (0xC0000005): managers share
// one process-lifetime connector and survive being recreated.
string? ManagersRecreated()
{
    object? first = null;                       // bound by the first manager (in the test process, the fixture's)
    for (int i = 0; i < 3; i++)
    {
        using var m = new SessionManager(comcntr, new PoolOptions());
        first ??= ComActivator.SharedConnector();
        foreach (var b in bases) m.Register(b);
        if (new ReadService(m).Read((server ?? file!).Name, q).Rows.Count == 0) return $"round {i}: no rows";
    }
    return ReferenceEquals(first, ComActivator.SharedConnector()) ? null : "the shared connector was replaced";
}

string? TwoBasesConcurrent()
{
    if (server is null || file is null) return "needs a server and a file base";
    using var m = new SessionManager(comcntr, new PoolOptions { GlobalMaxSessions = 8 });
    m.Register(server); m.Register(file);
    var svc = new ReadService(m);
    int errors = 0;
    var threads = new List<Thread>();
    foreach (var (name, n) in new[] { (server.Name, 4), (file.Name, 3) })
        for (int i = 0; i < n; i++)
            threads.Add(new Thread(() => { for (int r = 0; r < 15; r++) try { svc.Read(name, q); } catch { Interlocked.Increment(ref errors); } }));
    threads.ForEach(t => t.Start());
    threads.ForEach(t => t.Join());
    var s = m.Stats().Single(p => p.BaseName == server.Name);
    var f = m.Stats().Single(p => p.BaseName == file.Name);
    if (errors != 0) return $"{errors} read errors";
    if (s.Live is < 1 or > 4) return $"server live {s.Live}, cap 4";
    if (f.Live is < 1 or > 2) return $"file live {f.Live}, file cap is 2";
    return m.BudgetUsed <= 6 ? null : $"budget used {m.BudgetUsed} > 6";
}

// Under a tight working-set ceiling a busy base must evict the idle sessions of a quiet base, and
// never its own. Whether the second file reader then gets its own session (≤ the file cap of 2) or
// waits for the first one's depends on the working set after the eviction, which no test controls:
// the 2026-10-01 gate run saw 2 — so the check is the rule, not that count.
string? PressureEvicts()
{
    if (server is null || file is null) return "needs a server and a file base";
    long ws = WorkingSetMb();
    using var m = new SessionManager(comcntr, new PoolOptions
    {
        MaxWorkingSetMb = (int)ws + 150, IdleTimeout = TimeSpan.FromHours(1), SweepInterval = TimeSpan.FromHours(1)
    });
    m.Register(server); m.Register(file);
    var svc = new ReadService(m);
    int errors = 0;
    var warm = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
    {
        for (int r = 0; r < 10; r++) try { svc.Read(server.Name, q); } catch { Interlocked.Increment(ref errors); }
    })).ToList();
    warm.ForEach(t => t.Start()); warm.ForEach(t => t.Join());
    var fileThreads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
    {
        for (int r = 0; r < 20; r++) try { svc.Read(file.Name, q); } catch { Interlocked.Increment(ref errors); }
    })).ToList();
    fileThreads.ForEach(t => t.Start()); fileThreads.ForEach(t => t.Join());
    var s = m.Stats().Single(p => p.BaseName == server.Name);
    var f = m.Stats().Single(p => p.BaseName == file.Name);
    if (errors != 0) return $"{errors} read errors (a starved waiter times out)";
    if (s.Evicted < 1) return $"quiet server base was not evicted: {s}";
    if (f.Evicted != 0) return $"the file base evicted its own session: {f}";
    return f.Created is >= 1 and <= 2 ? null : $"file sessions created {f.Created}, cap 2";
}

// The rule, not a moment: the quiet base's idle session retires (idle timeout + sweep) while the
// busy base keeps reading — no read errors, the busy base never retires or evicts its own, every
// count within its cap and the budget. The old check took one snapshot 4.5 s after a clock that
// also counted KAN's cold Connect (6–8 s), so the server session was sometimes still in its idle
// window ("server live 1 (want 0)", 2026-10-02 gate run 3); the deadline below starts after it.
string? QuietBase()
{
    if (server is null || file is null) return "needs a server and a file base";
    var opt = new PoolOptions { IdleTimeout = TimeSpan.FromSeconds(2), SweepInterval = TimeSpan.FromSeconds(1) };
    using var m = new SessionManager(comcntr, opt);
    m.Register(server); m.Register(file);
    var svc = new ReadService(m);
    svc.Read(server.Name, q);
    svc.Read(file.Name, q);
    int errors = 0;
    PoolStats S() => m.Stats().Single(p => p.BaseName == server.Name);
    PoolStats F() => m.Stats().Single(p => p.BaseName == file.Name);
    var sw = Stopwatch.StartNew();
    while (S().Live > 0 && sw.Elapsed < TimeSpan.FromSeconds(30))      // ≫ idle 2 s + sweep 1 s
    {
        try { svc.Read(file.Name, q); } catch { errors++; }
        if (m.BudgetUsed > opt.GlobalMaxSessions) return $"budget used {m.BudgetUsed} > {opt.GlobalMaxSessions}";
        if (F().Live is < 1 or > 2) return $"file live {F().Live} while reading, file cap is 2";
    }
    var s = S(); var f = F();
    if (errors != 0) return $"{errors} read errors on the busy base";
    if (s.Live != 0 || s.Retired + s.Evicted < 1) return $"the quiet base kept its session {sw.Elapsed.TotalSeconds:F0} s past its idle timeout: {s}";
    if (f.Live is < 1 or > 2 || f.Retired + f.Evicted != 0) return $"the busy base lost or exceeded its sessions: {f}";
    return null;
}

// A session that died while idle is detected on borrow and replaced — the request succeeds.
string? DeadIdleSession()
{
    var b = server ?? file!;
    using var m = new SessionManager(comcntr, new PoolOptions { ValidateIdleAfter = TimeSpan.Zero });
    m.Register(b);
    var svc = new ReadService(m);
    if (svc.Read(b.Name, q).Rows.Count == 0) return "no rows";
    int killed = m.KillIdleConnectionsForTest(b.Name);
    if (killed != 1) return $"killed {killed} idle connections, want 1";
    if (svc.Read(b.Name, q).Rows.Count == 0) return "not served after the connection died";
    if (m.ProbeFailures(b.Name) != 1) return $"probe failures {m.ProbeFailures(b.Name)}, want 1";
    var s = m.Stats().Single();
    return s.Created == 2 && s.Live == 1 ? null : $"created {s.Created} (want 2), live {s.Live} (want 1)";
}

// The machine-wide queue is real: while another holder has the lock, a file-base Connect waits.
string? FileConnectLockWaits()
{
    if (file is null) return "needs a file base";
    using var m = new SessionManager(comcntr, new PoolOptions());
    m.Register(file);
    var holder = FileConnectLock.Acquire(FileConnectLock.LockPath, TimeSpan.FromSeconds(30));
    if (holder is null) return "could not take the lock";
    var release = Task.Run(async () => { await Task.Delay(2000); holder.Dispose(); });
    var sw = Stopwatch.StartNew();
    var rows = new ReadService(m).Read(file.Name, q).Rows;
    long waited = sw.ElapsedMilliseconds;
    release.Wait();
    if (rows.Count == 0) return "no rows";
    return waited >= 1500 ? null : $"file connect did not wait for the lock ({waited} ms)";
}

static long WorkingSetMb()
{
    using var p = Process.GetCurrentProcess();
    p.Refresh();
    return p.WorkingSet64 / 1024 / 1024;
}
