using System.Diagnostics;
using OneC.Desktop.Services;
using OneC.EventLog;
using OneC.Sync.Scheduling;

// IdleBench [minutes=10] — every launcher base with a readable log, scheduler ticking once a second.
// IdleBench churn N      — N activations of one always-growing fake base: do handles grow per activation?
if (args.Length > 1 && args[0] == "churn")
{
    int n = int.Parse(args[1]);
    long size = 0;
    var s = new SyncScheduler(new SyncBudgets { FeedPollActive = TimeSpan.Zero }, new GrowingStat(() => ++size), new NoDemand(),
                              _ => new CountingAgent(() => { }), () => DateTimeOffset.UtcNow);
    s.AddBase("b", false, "log");
    var proc = Process.GetCurrentProcess();
    for (int round = 0; round < 5; round++)
    {
        for (int i = 0; i < n / 5; i++) { s.Tick(); await Task.Delay(1); }
        GC.Collect(); GC.WaitForPendingFinalizers();
        proc.Refresh();
        Console.WriteLine($"activations {s.Activations,6}: handles {proc.HandleCount}, threads {proc.Threads.Count}, private {proc.PrivateMemorySize64 / 1048576} MB");
    }
    return;
}
if (args.Length > 1 && args[0] == "statchurn")
{
    // FileFeedStat over real log dirs, N times each: do handles grow per stat?
    var st = new FileFeedStat();
    var dirs = new[] { @"D:\1C\bilim\1Cv8Log", @"C:\Program Files\1cv8\srvinfo\reg_1541\71990e64-b651-4a71-9b88-04242b40e214\1Cv8Log" };
    var proc = Process.GetCurrentProcess();
    for (int round = 0; round < 5; round++)
    {
        for (int i = 0; i < int.Parse(args[1]) / 5; i++) foreach (var d in dirs) st.Stat(d);
        GC.Collect(); GC.WaitForPendingFinalizers();
        proc.Refresh();
        Console.WriteLine($"round {round}: handles {proc.HandleCount}, private {proc.PrivateMemorySize64 / 1048576} MB");
    }
    return;
}
int minutes = args.Length > 0 ? int.Parse(args[0]) : 10;
var locator = new LogLocator();
var bases = new List<(string Name, bool IsFile, string Dir)>();
foreach (var b in LauncherBases.Read())
{
    string cs = b.Kind switch
    {
        LauncherKind.File => $"File=\"{b.FilePath}\";",
        LauncherKind.Server => $"Srvr=\"{b.Server}\";Ref=\"{b.Ref}\";",
        _ => ""
    };
    if (cs.Length == 0) continue;
    var (dir, err) = locator.Resolve(cs);
    Console.WriteLine($"  {b.Name,-40} {(dir ?? err)}");
    if (dir is not null && bases.All(x => !string.Equals(x.Dir, dir, StringComparison.OrdinalIgnoreCase)))
        bases.Add((b.Name, b.Kind == LauncherKind.File, dir));
}
Console.WriteLine($"{bases.Count} bases with a readable log");

long agentRuns = 0;
var activatedBy = new Dictionary<string, int>();
var scheduler = new SyncScheduler(new SyncBudgets(), new FileFeedStat(), new NoDemand(),
    id => new CountingAgent(() => { Interlocked.Increment(ref agentRuns); lock (activatedBy) activatedBy[id] = activatedBy.GetValueOrDefault(id) + 1; }),
    () => DateTimeOffset.UtcNow);
foreach (var (name, isFile, dir) in bases) scheduler.AddBase(name, isFile, dir);

var p = Process.GetCurrentProcess();
GC.Collect();
p.Refresh();
var cpu0 = p.TotalProcessorTime;
var sw = Stopwatch.StartNew();
Console.WriteLine($"start: ws {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB, handles {p.HandleCount}, threads {p.Threads.Count}");
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
var loop = scheduler.RunAsync(TimeSpan.FromSeconds(1), cts.Token);
for (int m = 1; m <= minutes; m++)
{
    try { await Task.Delay(TimeSpan.FromMinutes(1), cts.Token); } catch (OperationCanceledException) { }
    p.Refresh();
    double cpuPct = (p.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
    Console.WriteLine($"min {m,2}: cpu {cpuPct:F3} % of the machine, ws {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB, " +
                      $"handles {p.HandleCount}, threads {p.Threads.Count}, stats {scheduler.StatCalls}, activations {scheduler.Activations}, sync leases {scheduler.Leases.Total}");
}
await loop;
Console.WriteLine("activations by base: " + string.Join(", ", activatedBy.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));

sealed class GrowingStat(Func<long> next) : IFeedStat
{
    public LogStat? Stat(string logDir) => new LogStat("x.lgp", next(), DateTime.UnixEpoch);
}

sealed class NoDemand : IBaseDemand
{
    public BaseDemand? Get(string baseId, DateTimeOffset now) => null;
}

sealed class CountingAgent(Action onRun) : IBaseAgent
{
    public Task RunAsync(BaseActivation a) { onRun(); return Task.CompletedTask; }
}
