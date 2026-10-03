using OneC.Sync.Scheduling;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S4: global scheduler, admission, leases, fairness (§14–15). Fake clock, no 1C.</summary>
public sealed class SyncSchedulerTests
{
    private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeStat : IFeedStat
    {
        public readonly Dictionary<string, long> Sizes = new();
        public int Calls;
        public LogStat? Stat(string logDir)
        {
            Calls++;
            return Sizes.TryGetValue(logDir, out var n) ? new LogStat("20260901000000.lgp", n, DateTime.UnixEpoch) : null;
        }
    }

    private sealed class FakeDemand : IBaseDemand
    {
        public readonly Dictionary<string, BaseDemand> Pending = new();
        public BaseDemand? Get(string baseId, DateTimeOffset now) => Pending.TryGetValue(baseId, out var d) ? d : null;
    }

    /// <summary>Runs until told to finish or until the scheduler stops it; records what it saw.</summary>
    private sealed class FakeAgent : IBaseAgent
    {
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile BaseActivation? Seen;
        public volatile bool Stopped, Done;
        public async Task RunAsync(BaseActivation a)
        {
            Seen = a;
            using var reg = a.Stop.Register(() => { Stopped = true; Finish.TrySetResult(); });
            await Finish.Task;
            Done = true;
        }

        /// <summary>Started, and — if told to finish — finished.</summary>
        public bool Settled => Seen is not null && (!Finish.Task.IsCompleted || Done);
    }

    private (SyncScheduler S, FakeStat Stat, FakeDemand Demand, Dictionary<string, List<FakeAgent>> Agents) Make(SyncBudgets? budgets = null, bool finishAtOnce = false)
    {
        var stat = new FakeStat();
        var demand = new FakeDemand();
        var agents = new Dictionary<string, List<FakeAgent>>();
        var s = new SyncScheduler(budgets ?? new SyncBudgets(), stat, demand, id =>
        {
            var a = new FakeAgent();
            if (finishAtOnce) a.Finish.TrySetResult();
            lock (_all) _all.Add(a);
            (agents.TryGetValue(id, out var l) ? l : agents[id] = new()).Add(a);
            return a;
        }, () => _now);
        return (s, stat, demand, agents);
    }

    private readonly List<FakeAgent> _all = new();

    /// <summary>Agents run on the pool: wait until each one started (and finished if told to), however busy the machine is.</summary>
    private void Settle()
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (_all) if (_all.All(a => a.Settled)) break;
            Thread.Sleep(5);
        }
        Thread.Sleep(10);                                                           // the task's own completion after Done
    }

    [Fact]
    public void ThirtyIdleBasesCostOnlyStatsAndNeverActivate()
    {
        var (s, stat, _, _) = Make(new SyncBudgets { MaxActiveBases = 30 }, finishAtOnce: true);
        for (int i = 0; i < 30; i++) { stat.Sizes["log" + i] = 1000; s.AddBase("b" + i, i % 2 == 0, "log" + i); }
        for (int t = 0; t < 20; t++) { s.Tick(); Settle(); _now += TimeSpan.FromSeconds(1); }
        // Each base drains its feed once at start (its stored cursor may be behind the log — S15)…
        Assert.Equal(30, s.Activations);
        for (int t = 0; t < 600; t++) { s.Tick(); _now += TimeSpan.FromSeconds(1); }   // …then 10 idle minutes, a tick per second
        Assert.Equal(30, s.Activations);
        Assert.Empty(s.ActiveBases);
        Assert.Equal(0, s.Leases.Total);
        // First stat of each, then one per base per 60 s (never grew): ≤ 30 + 30 × 10.
        Assert.InRange(stat.Calls, 30, 30 + 30 * 10 + 30);
    }

    [Fact]
    public void AGrowingLogActivatesItsBaseAndPollsItFaster()
    {
        var (s, stat, _, agents) = Make();
        stat.Sizes["log"] = 100;
        s.AddBase("b", false, "log");
        Assert.Equal(new[] { "b" }, s.Tick());                                        // the start-up drain
        Settle();
        agents["b"][0].Finish.SetResult();
        Settle();
        s.Tick();
        Assert.Single(agents["b"]);                                                   // nothing new: no more activations
        stat.Sizes["log"] = 200;
        _now += TimeSpan.FromSeconds(61);
        Assert.Equal(new[] { "b" }, s.Tick());
        Settle();
        Assert.True(agents["b"][1].Seen!.FeedGrew);
        int before = stat.Calls;
        agents["b"][1].Finish.SetResult();
        Settle();
        for (int i = 0; i < 12; i++) { _now += TimeSpan.FromSeconds(5); s.Tick(); }   // active interval: every 5 s
        Assert.InRange(stat.Calls - before, 11, 13);
    }

    [Fact]
    public void NeverMoreThanMaxActiveBases()
    {
        var (s, _, demand, agents) = Make();
        for (int i = 0; i < 30; i++) { s.AddBase("b" + i, false, null); demand.Pending["b" + i] = new BaseDemand(SyncPriority.Incremental, _now); }
        for (int t = 0; t < 20; t++)
        {
            s.Tick();
            Settle();
            Assert.True(s.ActiveBases.Count <= 3, $"{s.ActiveBases.Count} active");
            // Finish one running base per round: the next ready one takes the slot.
            var running = agents.Values.SelectMany(l => l).FirstOrDefault(a => !a.Finish.Task.IsCompleted);
            running?.Finish.TrySetResult();
            Settle();
        }
        Assert.True(s.Activations >= 20);
    }

    [Fact]
    public void RecorderWorkPreemptsASnapshotOnAnotherBase()
    {
        var (s, _, demand, agents) = Make(new SyncBudgets { MaxActiveBases = 1 });
        s.AddBase("cold", false, null);
        s.AddBase("hot", false, null);
        demand.Pending["cold"] = new BaseDemand(SyncPriority.Snapshot, _now);
        Assert.Equal(new[] { "cold" }, s.Tick());
        Settle();
        demand.Pending["hot"] = new BaseDemand(SyncPriority.Destructive, _now);
        Assert.Empty(s.Tick());                                  // no free slot yet…
        Settle();
        Assert.True(agents["cold"][0].Stopped);                  // …but the snapshot was told to stop at its next page
        Assert.Equal(new[] { "hot" }, s.Tick());
        Settle();
        Assert.Equal(SyncPriority.Destructive, agents["hot"][0].Seen!.Priority);
    }

    [Fact]
    public void ABaseYieldsAfterItsSliceWhenOthersWaitAtTheSamePriority()
    {
        var (s, _, demand, agents) = Make(new SyncBudgets { MaxActiveBases = 1, MaxSlice = TimeSpan.FromSeconds(30) });
        foreach (var b in new[] { "a", "b" }) { s.AddBase(b, false, null); demand.Pending[b] = new BaseDemand(SyncPriority.Snapshot, _now); }
        var first = s.Tick().Single();
        Settle();
        _now += TimeSpan.FromSeconds(10);
        s.Tick();
        Settle();
        Assert.False(agents[first][0].Stopped);                  // within its slice
        _now += TimeSpan.FromSeconds(25);
        s.Tick();
        Settle();
        Assert.True(agents[first][0].Stopped);
        var second = s.Tick().Single();
        Assert.NotEqual(first, second);                          // round-robin
        Settle();
        _now += TimeSpan.FromSeconds(31);
        s.Tick(); Settle();
        Assert.Equal(first, s.Tick().Single());                  // and back
    }

    [Fact]
    public void AnAgentThatStopsEarlyLeavesNoSlotBehind()
    {
        var (s, _, demand, agents) = Make(new SyncBudgets { MaxActiveBases = 1 });
        s.AddBase("a", false, null);
        demand.Pending["a"] = new BaseDemand(SyncPriority.Incremental, _now);
        s.Tick(); Settle();
        demand.Pending.Remove("a");
        agents["a"][0].Finish.SetResult(); Settle();
        s.Tick();
        Assert.Empty(s.ActiveBases);
        demand.Pending["a"] = new BaseDemand(SyncPriority.Incremental, _now + TimeSpan.FromMinutes(1));   // backoff: not yet due
        Assert.Empty(s.Tick());
        _now += TimeSpan.FromMinutes(2);
        Assert.Equal(new[] { "a" }, s.Tick());
    }

    [Fact]
    public void LeasesCapPerBaseAndGloballyAndYieldToTheUser()
    {
        var leases = new SyncLeases(new SyncBudgets(), () => _now);
        var file = leases.TryAcquire("file", isFile: true);
        Assert.NotNull(file);
        Assert.Null(leases.TryAcquire("file", isFile: true));                     // a file base gets one
        var server = Enumerable.Range(0, 5).Select(_ => leases.TryAcquire("srv1", false)).ToList();
        Assert.Equal(3, server.Count(l => l is not null));                        // MaxConcurrency − 1
        var more = Enumerable.Range(0, 5).Select(_ => leases.TryAcquire("srv2", false)).ToList();
        Assert.Equal(2, more.Count(l => l is not null));                          // global 6: 1 + 3 + 2
        Assert.Equal(6, leases.Total);
        file!.Dispose(); file.Dispose();                                          // double dispose is harmless
        Assert.Equal(5, leases.Total);

        using (leases.Foreground("srv3"))
        {
            Assert.True(leases.ShouldYield("srv3"));
            Assert.Null(leases.TryAcquire("srv3", false));
        }
        Assert.False(leases.ShouldYield("srv3"));
        leases.NoteWrite("srv3");
        Assert.True(leases.ShouldYield("srv3"));
        _now += TimeSpan.FromSeconds(6);
        Assert.False(leases.ShouldYield("srv3"));
    }

    [Fact]
    public async Task StoppingTheLoopStopsEveryAgent()
    {
        var (s, _, demand, agents) = Make();
        s.AddBase("a", false, null);
        demand.Pending["a"] = new BaseDemand(SyncPriority.Incremental, _now);
        using var cts = new CancellationTokenSource();
        var loop = s.RunAsync(TimeSpan.FromMilliseconds(20), cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(agents["a"].All(a => a.Stopped || a.Finish.Task.IsCompleted));
    }
}
