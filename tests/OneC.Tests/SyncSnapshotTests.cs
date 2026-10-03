using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S5: the cold snapshot pipeline (§4) against a fake 1C and the stub target.</summary>
public sealed class SyncSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiba-snap-" + Guid.NewGuid().ToString("N"));
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();

    public SyncSnapshotTests()
    {
        Directory.CreateDirectory(_dir);
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Reg = new("AccumulationRegister_Reg", "Reg", Families.AccumulationRegister, true);

    private (SnapshotRunner Runner, UploadGate Gate, Uploader Up) Runner(StubSyncTarget target, SyncBudgets budgets, SnapshotOptions? o = null,
                                                                         OneC.Sync.Incremental.IPartitioner? partitions = null)
    {
        var gate = new UploadGate(budgets);
        var up = new Uploader(target, gate) { Backoff = _ => TimeSpan.FromMilliseconds(10) };
        return (new SnapshotRunner(_db, _onec, up, gate, target.Capabilities, partitions ?? new OneC.Sync.Incremental.SinglePartition("p"), o), gate, up);
    }

    private static BaseActivation Activation(SyncBudgets b, bool isFile = false, CancellationToken stop = default) =>
        new("base", isFile, SyncPriority.Snapshot, false, new SyncLeases(b, () => DateTimeOffset.UtcNow), b, stop);

    private void Seed(int catalogs = 0, int documents = 0, int recorders = 0, int lines = 3)
    {
        _onec.Tables["Cat"] = Enumerable.Range(1, catalogs).Select(FakeOneC.CatalogRow).ToList();
        var d0 = new DateTime(2025, 1, 1);
        // Several documents per timestamp: page boundaries fall inside a run of one date.
        _onec.Tables["Doc"] = Enumerable.Range(1, documents).Select(i => FakeOneC.DocumentRow(i, d0.AddHours(i / 7))).ToList();
        _onec.Tables["Reg"] = Enumerable.Range(1, recorders).SelectMany(r => Enumerable.Range(1, lines).Select(l => FakeOneC.MovementRow(r, l, d0.AddHours(r / 5)))).ToList();
    }

    /// <summary>
    /// One mapper or uploader failing must end the whole run at once (2026-10-01 review): the
    /// downstream wait completed only when every mapper AND uploader ended, so a failed mapper left
    /// the readers blocked on a full channel forever — leases and the base's slot never came back.
    /// </summary>
    [Fact]
    public async Task AMappingFailureEndsTheRunInsteadOfHanging()
    {
        Seed(catalogs: 3000);
        _onec.Tables["Cat"][700].Remove("id");                                         // one row the mapper cannot key
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var run = Runner(t, b, new SnapshotOptions { PageSize = 50 }).Runner.RunTableAsync(Activation(b), Cat, 1);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        await Assert.ThrowsAsync<RowMappingException>(() => run);
        Assert.True(t.Count("p", Cat.Table) < 3000);
    }

    [Fact]
    public async Task AnUploadFailureEndsTheRunInsteadOfHangingAndGivesItsLeasesBack()
    {
        Seed(catalogs: 3000);
        var t = new StubSyncTarget(StubMode.V3);
        t.FailNext(new TargetResult(Outcome.Transient, 503, "down"), times: 10_000, op: "upload");
        var b = new SyncBudgets();
        var act = Activation(b);
        var run = Runner(t, b, new SnapshotOptions { PageSize = 50 }).Runner.RunTableAsync(act, Cat, 1);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.NotNull(act.Leases.TryAcquire("base", false));                          // the run's leases were released
    }

    [Fact]
    public async Task ACatalogArrivesWholeWithItsVersionsAndACompletedSlice()
    {
        Seed(catalogs: 1234);
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var r = await Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b), Cat, 1);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(1234, t.Count("p", Cat.Table));
        var row = JsonNode.Parse(t.Rows("p", Cat.Table)[FakeOneC.Guid(5)])!;
        Assert.Equal(FakeOneC.Guid(5), (string)row["__rowKey"]!);
        Assert.Null(row["dataVersion"]);                                               // sync metadata is not uploaded
        Assert.Equal(1234, _db.Read(tx => tx.CountVersions("base", Cat.Table)));
        Assert.Equal(FakeOneC.Version(1), _db.Read(tx => tx.GetVersion("base", Cat.Table, FakeOneC.Guid(5))));
        var slice = Assert.Single(_db.Read(tx => tx.GetSlices("base", Cat.Table, 1)));
        Assert.True(slice.Done);
        Assert.Equal(1234, slice.RowsDone);
    }

    [Fact]
    public async Task ABigServerTableIsReadInParallelSlicesExactlyOnce()
    {
        Seed(documents: 3000);
        _onec.ReadDelayMs = 5;
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var r = await Runner(t, b, new SnapshotOptions { PageSize = 97, SliceThresholdRows = 100 }).Runner.RunTableAsync(Activation(b), Doc, 1);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(3, r.Slices);                                                       // server base: 3 sync sessions
        Assert.Equal(3000, t.Count("p", Doc.Table));
        Assert.Equal(3000, r.RowsRead);                                                  // nothing read twice across slice edges
        Assert.True(_onec.PeakConcurrentReads >= 2, $"peak parallel reads {_onec.PeakConcurrentReads}");
    }

    [Fact]
    public async Task AFileBaseIsNeverSliced()
    {
        Seed(documents: 500);
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var r = await Runner(t, b, new SnapshotOptions { PageSize = 50, SliceThresholdRows = 10 }).Runner.RunTableAsync(Activation(b, isFile: true), Doc, 1);
        Assert.Equal(1, r.Slices);
        Assert.Equal(1, _onec.PeakConcurrentReads);
        Assert.Equal(500, t.Count("p", Doc.Table));
    }

    [Fact]
    public async Task RegisterRowsAreKeyedByRecorderAndLine()
    {
        Seed(recorders: 400, lines: 4);
        var t = new StubSyncTarget(StubMode.V2);
        var b = new SyncBudgets();
        var (runner, _, up) = Runner(t, b, new SnapshotOptions { PageSize = 123 });
        await runner.RunTableAsync(Activation(b), Reg, 1);
        Assert.Equal(1600, t.Count("p", Reg.Table));
        Assert.Contains(FakeOneC.Guid(17) + "#3", t.Rows("p", Reg.Table).Keys);
        Assert.Equal(0, up.CountMismatches);                                            // batches never carry a key twice
    }

    [Fact]
    public async Task MemoryStaysInsideTheByteBudgetWithASlowBackend()
    {
        Seed(catalogs: 3000);
        var t = new StubSyncTarget(StubMode.V3) { RowsPerSecond = 20_000 };
        var b = new SyncBudgets { BufferedBytesMax = 64 * 1024 };
        var (runner, gate, _) = Runner(t, b, new SnapshotOptions { PageSize = 100 });
        var r = await runner.RunTableAsync(Activation(b), Cat, 1);
        Assert.Equal(3000, t.Count("p", Cat.Table));
        long page = 100 * 150;                                                           // one page may pass alone when the budget is empty
        Assert.True(gate.Bytes.Peak <= b.BufferedBytesMax + page, $"peak {gate.Bytes.Peak} bytes");
        Assert.Equal(0, gate.Bytes.Used);
        Assert.True(gate.PeakInFlight <= b.UploadConcurrencyPerBase);
    }

    [Fact]
    public async Task ACrashMidTableResumesFromTheLastAcceptedPage()
    {
        Seed(catalogs: 1000);
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        int commits = 0;
        _db.FaultHook = p => { if (p == "checkpoint" && ++commits == 4) throw new IOException("power cut"); };
        var (runner, gate, _) = Runner(t, b, new SnapshotOptions { PageSize = 100 });
        await Assert.ThrowsAsync<IOException>(() => runner.RunTableAsync(Activation(b), Cat, 7));
        Assert.Equal(0, gate.Bytes.Used);
        _db.FaultHook = null;
        var slice = _db.Read(tx => tx.GetSlices("base", Cat.Table, 7)).Single();
        Assert.Equal(300, slice.RowsDone);                                               // three pages checkpointed
        int readsBefore = _onec.Reads;
        var r = await Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b), Cat, 7);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(1000, t.Count("p", Cat.Table));
        Assert.Equal(1000, _db.Read(tx => tx.GetSlices("base", Cat.Table, 7)).Single().RowsDone);
        Assert.InRange(_onec.Reads - readsBefore, 7, 8);                                 // pages 4..10 (+ the empty end page)
    }

    [Fact]
    public async Task AStopEndsReadingAtAPageBoundaryAndLosesNothing()
    {
        Seed(catalogs: 2000);
        _onec.ReadDelayMs = 20;
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        using var stop = new CancellationTokenSource();
        var run = Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b, stop: stop.Token), Cat, 3);
        await Task.Delay(150);
        stop.Cancel();
        var r = await run;
        Assert.Equal(SnapshotOutcome.Stopped, r.Outcome);
        var done = _db.Read(tx => tx.GetSlices("base", Cat.Table, 3)).Single().RowsDone;
        Assert.Equal(done, t.Count("p", Cat.Table));                                     // what was read was uploaded and checkpointed
        Assert.InRange(done, 100, 1900);
        var again = await Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b), Cat, 3);
        Assert.Equal(SnapshotOutcome.Complete, again.Outcome);
        Assert.Equal(2000, t.Count("p", Cat.Table));
    }

    [Fact]
    public async Task TransientFailuresRetryButLostAuthPausesWithoutAdvancing()
    {
        Seed(catalogs: 300);
        var t = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        t.FailNext(new TargetResult(Outcome.Transient, 503, "busy"), times: 2, op: "upload");
        var r = await Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b), Cat, 1);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(300, t.Count("p", Cat.Table));

        Seed(catalogs: 300);
        t.FailNext(new TargetResult(Outcome.Auth, 401, "expired"), times: 100, op: "upload");
        var e = await Assert.ThrowsAsync<SyncPausedException>(() => Runner(t, b, new SnapshotOptions { PageSize = 100 }).Runner.RunTableAsync(Activation(b), Cat, 2));
        Assert.Equal(Outcome.Auth, e.Outcome);
        Assert.Equal(0, _db.Read(tx => tx.GetSlices("base", Cat.Table, 2)).Single().RowsDone);
    }

    [Fact]
    public async Task RowsTheTargetRejectsBecomeDeadLettersAndTheRestProceeds()
    {
        Seed(catalogs: 50);
        var t = new StubSyncTarget(StubMode.V3);
        t.RejectKeys.Add(FakeOneC.Guid(10));
        var b = new SyncBudgets();
        var r = await Runner(t, b).Runner.RunTableAsync(Activation(b), Cat, 1);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(49, t.Count("p", Cat.Table));
        var dl = Assert.Single(_db.Read(tx => tx.DeadLetters("base")));
        Assert.Equal((FakeOneC.Guid(10), DeadLetterCategories.Validation), (dl.ObjectKey, dl.Category));
    }

    [Fact]
    public async Task NoSessionMeansNoWork()
    {
        Seed(catalogs: 10);
        var t = new StubSyncTarget();
        var b = new SyncBudgets();
        var a = Activation(b);
        using var user = a.Leases.Foreground("base");
        var r = await Runner(t, b).Runner.RunTableAsync(a, Cat, 1);
        Assert.Equal(SnapshotOutcome.NoSession, r.Outcome);
        Assert.Equal(0, _onec.Reads);
    }

    [Fact]
    public void TheMapperKeysEveryFamilyAndRefusesARowWithoutAKey()
    {
        Assert.Equal(FakeOneC.Guid(1), CanonicalMapper.Map(Cat, FakeOneC.CatalogRow(1)).Key);
        Assert.Equal(FakeOneC.Guid(2) + "#5", CanonicalMapper.Map(Reg, FakeOneC.MovementRow(2, 5, DateTime.Today)).Key);
        var chart = new TablePlan("ChartOfAccounts_Х", "Х", Families.Chart, false);
        Assert.Equal("60.01", CanonicalMapper.Map(chart, new JsonObject { ["Код"] = "60.01" }).Key);
        var info = new TablePlan("InformationRegister_I", "I", Families.IndependentInfoRegister, false);
        Assert.Equal("d:2026|s:x", CanonicalMapper.Map(info, new JsonObject { ["naturalKey"] = "d:2026|s:x" }).Key);
        Assert.Throws<RowMappingException>(() => CanonicalMapper.Map(Reg, new JsonObject { ["Сумма"] = 1 }));
        Assert.Equal(3L, CanonicalMapper.VersionNumber(FakeOneC.Version(3)));
        Assert.True(CanonicalMapper.VersionNumber("AAACKAAAAAA=") > CanonicalMapper.VersionNumber("AAACJwAAAAA="));   // S0 bilim
        Assert.Equal("{\"n\":3790,\"s\":\"Кириллица\",\"__rowKey\":\"" + FakeOneC.Guid(1) + "\"}",
                     System.Text.Encoding.UTF8.GetString(CanonicalMapper.Map(Cat, new JsonObject { ["n"] = 3790.00m, ["s"] = "Кириллица", ["id"] = FakeOneC.Guid(1) }).Row.Json)
                         .Replace(",\"id\":\"" + FakeOneC.Guid(1) + "\"", ""));
    }

    /// <summary>§17: the engine branches on capabilities only, never on the target's Kind.</summary>
    [Fact]
    public void EngineCodeNeverBranchesOnTheTargetKind()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "OneC.Sync"));
        if (!Directory.Exists(root)) root = Path.Combine(FindRepo(), "src", "OneC.Sync");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.l, @"(?i)target\w*\??\.Kind\b|""python-v[23]""|""stub""|""rust"""))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.l.Trim()}").ToList();
        Assert.Empty(offenders);
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SYNC_ENGINE_ARCHITECTURE.md"))) d = d.Parent;
        return d?.FullName ?? @"D:\aiba\1c-arch";
    }
}
