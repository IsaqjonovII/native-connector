using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OneC.EventLog;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Mapping;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S9: independent information registers — natural keys, coalesced refresh, per-table interval (§8, D-6).</summary>
public sealed class SyncRegisterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-reg-").FullName;
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private static readonly TablePlan Rates = new("InformationRegister_Курсы", "Курсы", Families.IndependentInfoRegister, false);

    public SyncRegisterTests()
    {
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"), () => _now);
        _onec.Tables["Курсы"] = Enumerable.Range(1, 25).Select(i => Rate(i, 12000 + i)).ToList();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static JsonObject Rate(int day, decimal rate) => new()
    {
        ["Период"] = $"2026-09-{day:D2}T00:00:00", ["Валюта"] = "USD", ["Курс"] = rate, ["Кратность"] = 1,
        ["naturalKey"] = $"d:2026-09-{day:D2}T00:00:00|CatalogRef.Валюты:usd"
    };

    private (WorkExecutor Exec, StubSyncTarget Target) Make(TablePlan? plan = null, StubMode mode = StubMode.V3)
    {
        var t = new StubSyncTarget(mode);
        var up = new Uploader(t, new UploadGate(new SyncBudgets()));
        var tables = new Dictionary<string, TablePlan> { [Rates.Table] = plan ?? Rates };
        var h = new RefreshRegisterHandler(_db, _onec, t, up, tables, new SinglePartition("p")) { PageSize = 7 };
        return (new WorkExecutor(_db, new Dictionary<string, IWorkHandler> { [WorkKinds.RefreshRegister] = h }), t);
    }

    private static BaseActivation Act() =>
        new("b", false, SyncPriority.Incremental, true, new SyncLeases(new SyncBudgets(), () => DateTimeOffset.UtcNow), new SyncBudgets(), CancellationToken.None);

    private async Task<ExecutorStats> Refresh(WorkExecutor exec)
    {
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Rates.Table, Rates.Table, WorkFlags.Refresh, ItemPriority.Refresh)));
        return await exec.RunAsync(Act());
    }

    private int Uploaded(StubSyncTarget t) => t.Calls.Where(c => c.StartsWith("upload")).Sum(c => int.Parse(c.Split(' ')[^1]));

    [Fact]
    public async Task ARefreshSendsOnlyChangedAndNewRowsAndRemovesGoneOnes()
    {
        var (exec, t) = Make();
        Assert.Equal(1, (await Refresh(exec)).Done);
        Assert.Equal(25, t.Count("p", Rates.Table));
        int first = Uploaded(t);
        Assert.Equal(1, (await Refresh(exec)).Skipped);                           // nothing changed: nothing sent
        Assert.Equal(first, Uploaded(t));

        _onec.Tables["Курсы"][4]["Курс"] = 99999m;                               // one rate corrected
        _onec.Tables["Курсы"].Add(Rate(26, 12026));                              // one new day
        _onec.Tables["Курсы"].RemoveAt(0);                                       // one removed
        Assert.Equal(1, (await Refresh(exec)).Done);
        Assert.Equal(first + 2, Uploaded(t));
        Assert.Equal(25, t.Count("p", Rates.Table));
        Assert.False(t.Rows("p", Rates.Table).ContainsKey("d:2026-09-01T00:00:00|CatalogRef.Валюты:usd"));
        Assert.Contains("99999", t.Rows("p", Rates.Table)["d:2026-09-05T00:00:00|CatalogRef.Валюты:usd"]);
    }

    /// <summary>A chart of accounts is refreshed by code through the chart read (it threw on the register read before, 2026-10-01).</summary>
    [Fact]
    public async Task AChartOfAccountsIsRefreshedByCode()
    {
        var chart = new TablePlan("ChartOfAccounts_Хоз", "Хоз", Families.Chart, false);
        _onec.Tables["Хоз"] = Enumerable.Range(1, 30).Select(i => new JsonObject { ["Код"] = $"{i:D2}.0", ["Наименование"] = "account " + i }).ToList();
        var t = new StubSyncTarget(StubMode.V3);
        var h = new RefreshRegisterHandler(_db, _onec, t, new Uploader(t, new UploadGate(new SyncBudgets())), new Dictionary<string, TablePlan> { [chart.Table] = chart },
                                           new SinglePartition("p")) { PageSize = 7 };
        var exec = new WorkExecutor(_db, new Dictionary<string, IWorkHandler> { [WorkKinds.RefreshRegister] = h });
        async Task<ExecutorStats> Run()
        {
            _db.Write(tx => tx.UpsertWork(new WorkRequest("b", chart.Table, chart.Table, WorkFlags.Refresh, ItemPriority.Refresh)));
            return await exec.RunAsync(Act());
        }
        Assert.Equal(1, (await Run()).Done);
        Assert.Equal(30, t.Count("p", chart.Table));
        _onec.Tables["Хоз"][3]["Наименование"] = "renamed";
        _onec.Tables["Хоз"].RemoveAt(29);
        Assert.Equal(1, (await Run()).Done);
        Assert.Contains("renamed", t.Rows("p", chart.Table)["04.0"]);
        Assert.Equal(29, t.Count("p", chart.Table));
        Assert.Empty(_db.Read(tx => tx.DeadLetters("b")));
    }

    /// <summary>
    /// v2 refuses deleting most of a table (5 % cap): the refresh's delete waits for approval; approving
    /// it deletes those rows (it sent a delete keyed by the table name before, 2026-10-01).
    /// </summary>
    [Fact]
    public async Task ApprovingARefreshsRefusedDeleteDeletesTheGoneRows()
    {
        var (exec, t) = Make(mode: StubMode.V2);
        Assert.Equal(1, (await Refresh(exec)).Done);
        _onec.Tables["Курсы"].RemoveRange(0, 20);                                  // 20 of 25 gone: over the cap
        await Refresh(exec);
        var dl = Assert.Single(_db.Read(tx => tx.DeadLetters("b")));
        Assert.Equal(DeadLetterCategories.NeedsApproval, dl.Category);
        Assert.Equal(25, t.Count("p", Rates.Table));

        Assert.True(new OneC.Sync.Errors.DeadLetterService(_db).Approve(dl.Id));
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.Equal(5, t.Count("p", Rates.Table));
        Assert.Empty(_db.Read(tx => tx.DeadLetters("b")));
        Assert.Null(_db.Read(tx => tx.GetMeta(RefreshRegisterHandler.ApprovedDeleteKey("b", Rates.Table))));   // one approval, one delete
    }

    [Fact]
    public async Task ManyEventsMakeOneRefreshAndNeverTouchOtherTables()
    {
        // R-1 regression: an information-register event reset the whole table in the old engine.
        var events = Enumerable.Range(0, 500).Select(_ => new ChangeEvent("t", "Update", "РегистрСведений.Курсы", null)).ToList();
        var items = EventCoalescer.Coalesce("b", events, new TableMap(new[] { Rates, new TablePlan("Catalog_X", "X", Families.Catalog, false) }));
        var one = Assert.Single(items);
        Assert.Equal((Rates.Table, WorkFlags.Refresh), (one.Table, one.Flags));
        var (exec, _) = Make();
        foreach (var w in items) _db.Write(tx => tx.UpsertWork(w));
        _db.Write(tx => tx.UpsertWork(items[0]));
        Assert.Equal(1, _db.Read(tx => tx.CountWork("b")));
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.Empty(_db.Read(tx => tx.GetSlices("b", Rates.Table, 1)));          // no snapshot, no reset
    }

    [Fact]
    public async Task TheTablesIntervalHoldsARefreshBackWithoutAnError()
    {
        var (exec, t) = Make(Rates with { RefreshEvery = TimeSpan.FromMinutes(5) });
        await Refresh(exec);
        int after1 = Uploaded(t);
        _onec.Tables["Курсы"][2]["Курс"] = 1m;
        var s = await Refresh(exec);
        Assert.Equal((0, 0, 1), (s.Done, s.DeadLettered, s.Released));             // waits, no failure recorded as one
        Assert.Equal(after1, Uploaded(t));
        Assert.Empty(_db.Read(tx => tx.DeadLetters("b")));
        _now += TimeSpan.FromMinutes(6);
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);                        // the same item, now due
        Assert.Equal(after1 + 1, Uploaded(t));
    }

    [Fact]
    public async Task ASnapshotRemembersTheRowsSoTheFirstRefreshSendsNothing()
    {
        var target = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var gate = new UploadGate(b);
        var snap = new SnapshotRunner(_db, _onec, new Uploader(target, gate), gate, target.Capabilities, new SinglePartition("p"), new SnapshotOptions { PageSize = 10 });
        await snap.RunTableAsync(Act() with { Priority = SyncPriority.Snapshot }, Rates, 1);
        Assert.Equal(25, target.Count("p", Rates.Table));
        int sent = Uploaded(target);
        var up = new Uploader(target, gate);
        var h = new RefreshRegisterHandler(_db, _onec, target, up, new Dictionary<string, TablePlan> { [Rates.Table] = Rates }, new SinglePartition("p"));
        var exec = new WorkExecutor(_db, new Dictionary<string, IWorkHandler> { [WorkKinds.RefreshRegister] = h });
        Assert.Equal(1, (await Refresh(exec)).Skipped);
        Assert.Equal(sent, Uploaded(target));
    }

    [Fact]
    public void NaturalKeysAreTheRowsKeys()
    {
        var a = CanonicalMapper.Map(Rates, Rate(3, 1));
        var b = CanonicalMapper.Map(Rates, Rate(3, 2));
        Assert.Equal(a.Key, b.Key);                                                // same row, new value: same key
        Assert.NotEqual(RefreshRegisterHandler.Hash(a.Row), RefreshRegisterHandler.Hash(b.Row));
        Assert.NotEqual(a.Key, CanonicalMapper.Map(Rates, Rate(4, 1)).Key);
    }
}
