using Microsoft.Data.Sqlite;
using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Source;
using OneC.Sync.Verify;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S11: verify pass, recovery, fallback (§11, §12).</summary>
public sealed class SyncVerifyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-verify-").FullName;
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();
    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Acc = new("AccountingRegister_Acc", "Acc", Families.AccountingRegister, true);

    public SyncVerifyTests()
    {
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        _onec.Tables["Cat"] = Enumerable.Range(1, 20).Select(FakeOneC.CatalogRow).ToList();
        _onec.Tables["Doc"] = Enumerable.Range(1, 10).Select(i => FakeOneC.DocumentRow(i, DateTime.Today)).ToList();
        // What was synced before: every current version.
        _db.Write(tx =>
        {
            foreach (var r in _onec.Tables["Cat"]) tx.SetVersion("b", Cat.Table, (string)r["id"]!, (string)r["dataVersion"]!);
            foreach (var r in _onec.Tables["Doc"]) tx.SetVersion("b", Doc.Table, (string)r["id"]!, (string)r["dataVersion"]!);
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Dictionary<string, WorkItem> Work() => _db.Read(tx => tx.PeekWork("b", 10_000)).ToDictionary(w => w.ObjectKey);

    [Fact]
    public async Task OnlyChangedNewAndGoneObjectsBecomeWorkWhateverTheScanOrder()
    {
        _onec.Tables["Cat"][3]["dataVersion"] = FakeOneC.Version(2);          // edited
        _onec.Tables["Cat"][11]["dataVersion"] = FakeOneC.Version(5);         // edited
        _onec.Tables["Cat"].Add(FakeOneC.CatalogRow(21));                     // new
        _onec.Tables["Cat"].RemoveAt(0);                                      // gone
        var r = await new VerifyRunner(_db, _onec) { PageSize = 3 }.VerifyTableAsync("b", Cat, false, CancellationToken.None);
        Assert.Equal((20L, 3, 1), (r.Scanned, r.Changed, r.Gone));
        var w = Work();
        Assert.Equal(4, w.Count);
        Assert.Equal(WorkKinds.SyncObject, w[FakeOneC.Guid(4)].Kind);
        Assert.Equal(WorkKinds.SyncObject, w[FakeOneC.Guid(21)].Kind);
        Assert.Equal(WorkKinds.DeleteObject, w[FakeOneC.Guid(1)].Kind);

        // A second pass before the items ran finds the same differences and merges into the same items.
        var again = await new VerifyRunner(_db, _onec) { PageSize = 7 }.VerifyTableAsync("b", Cat, false, CancellationToken.None);
        Assert.Equal((3, 1), (again.Changed, again.Gone));
        Assert.Equal(4, Work().Count);
    }

    [Fact]
    public async Task AChangedPostedDocumentGetsARecorderSync()
    {
        _onec.Tables["Doc"][2]["dataVersion"] = FakeOneC.Version(9);          // reposted: its version moved (S0)
        await new VerifyRunner(_db, _onec).VerifyTableAsync("b", Doc, recordedDocuments: true, CancellationToken.None);
        Assert.Equal(WorkKinds.SyncRecorder, Work()[FakeOneC.Guid(3)].Kind);
    }

    [Fact]
    public async Task RecoveryAdoptsTheParkedCursorOnlyAfterEveryTableWasVerified()
    {
        _db.Write(tx =>
        {
            tx.UpsertBase(new SyncBaseRow("b", "stub", "c", null, SyncModes.Recovery, "feed: log_recreated", false, "h", tx.Now));
            tx.SetCursor("b", "11111111-2222-3333-4444-555555555555|a.lgp|10", null);
            tx.SetMeta(FeedService.RecoveryCursorKey("b"), "99999999-2222-3333-4444-555555555555|b.lgp|500");
        });
        _onec.Tables["Doc"][0]["dataVersion"] = FakeOneC.Version(7);
        _onec.FailRead = t => t == "Doc";                                      // the second table fails mid-recovery
        var rec = new Recovery(_db, new VerifyRunner(_db, _onec));
        await Assert.ThrowsAsync<IOException>(() => rec.RecoverAsync("b", new[] { Cat, Doc, Acc }, _dir, new EventLogReader(), CancellationToken.None));
        Assert.Equal("11111111-2222-3333-4444-555555555555|a.lgp|10", _db.Read(tx => tx.GetCursor("b"))!.Cursor);
        Assert.Equal(SyncModes.Recovery, _db.Read(tx => tx.GetBase("b"))!.Mode);
        _onec.FailRead = null;
        await rec.RecoverAsync("b", new[] { Cat, Doc, Acc }, _dir, new EventLogReader(), CancellationToken.None);
        Assert.Equal("99999999-2222-3333-4444-555555555555|b.lgp|500", _db.Read(tx => tx.GetCursor("b"))!.Cursor);
        Assert.Equal(SyncModes.Incremental, _db.Read(tx => tx.GetBase("b"))!.Mode);
        Assert.Equal(WorkKinds.SyncRecorder, Work()[FakeOneC.Guid(1)].Kind);
        Assert.Null(_db.Read(tx => tx.GetMeta(FeedService.RecoveryCursorKey("b"))));
    }

    [Fact]
    public void LostStateIsRecoveryNeverATailStart()
    {
        string path = Path.Combine(_dir, "lost.db");
        File.WriteAllText(path, "garbage that is not a database, long enough to have a header of sorts");
        using var lost = SyncDb.Open(path);
        Assert.Equal(SyncModes.Recovery, Recovery.StartMode(lost, "any", feedReadable: true));
        Assert.Equal(SyncModes.Snapshot, Recovery.StartMode(_db, "new-base", feedReadable: true));
        _db.Write(tx => tx.UpsertBase(new SyncBaseRow("b", "stub", "c", null, SyncModes.Incremental, null, false, "h", tx.Now)));
        Assert.Equal(SyncModes.Fallback, Recovery.StartMode(_db, "b", feedReadable: false));
        Assert.Equal(SyncModes.Incremental, Recovery.StartMode(_db, "b", feedReadable: true));
    }

    [Fact]
    public async Task FallbackVerifiesOneTableATurnRoundRobin()
    {
        var rec = new Recovery(_db, new VerifyRunner(_db, _onec));
        var tables = new[] { Cat, Doc, Acc };
        var seen = new List<long>();
        for (int i = 0; i < 4; i++) seen.Add((await rec.FallbackTurnAsync("b", tables, CancellationToken.None))!.Scanned);
        Assert.Equal(new long[] { 20, 10, 20, 10 }, seen);                    // Cat, Doc, Cat, Doc — registers follow their documents
    }
}
