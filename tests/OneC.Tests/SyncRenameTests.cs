using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Mapping;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Rows carry references as names (the old adapter's shape). A catalog rename changes what other rows show
/// without changing them in 1C: only the rows that reference the renamed GUID are read again — found by a
/// referrer search, never a rescan (F-reference-presentation-staleness, 2026-10-09).
/// </summary>
public sealed class SyncRenameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-rename-").FullName;
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();

    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Contract = new("Catalog_Contract", "Contract", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Acc = new("AccountingRegister_Acc", "Acc", Families.AccountingRegister, true);
    private static readonly string Item = FakeOneC.Guid(7), Agreement = FakeOneC.Guid(8), D1 = FakeOneC.Guid(1), D2 = FakeOneC.Guid(2);
    private const string P = "p";

    public SyncRenameTests()
    {
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
        _onec.Tables["Cat"] = new() { new JsonObject { ["id"] = Item, ["name"] = "Old", ["ИНН"] = "1", ["dataVersion"] = FakeOneC.Version(1) } };
        _onec.Tables["Contract"] = new() { new JsonObject { ["id"] = Agreement, ["name"] = "c-1", ["Владелец"] = "Old", ["dataVersion"] = FakeOneC.Version(1) } };
        var d1 = FakeOneC.DocumentRow(1, new DateTime(2026, 9, 1)); d1["Контрагент"] = "Old";
        var d2 = FakeOneC.DocumentRow(2, new DateTime(2026, 9, 1)); d2["Контрагент"] = "Someone else";
        _onec.Tables["Doc"] = new() { d1, d2 };
        var line = FakeOneC.MovementRow(1, 1, new DateTime(2026, 9, 1)); line["СубконтоДт1"] = "Old";
        _onec.Tables["Acc"] = new() { line };
        _onec.RecorderTypes["Acc"] = new() { "Doc" };
        _onec.Referrers[Item] = new() { (Doc.Table, D1), (Contract.Table, Agreement), (EventCoalescer.UnknownRecorderTable, D1) };
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (WorkExecutor Exec, StubSyncTarget Target) Make()
    {
        var t = new StubSyncTarget(StubMode.V3);
        var up = new Uploader(t, new UploadGate(new SyncBudgets()));
        var tables = new Dictionary<string, TablePlan> { [Cat.Table] = Cat, [Contract.Table] = Contract, [Doc.Table] = Doc, [Acc.Table] = Acc };
        var parts = new SinglePartition(P);
        var deletes = new DeleteObjectHandler(_db, t, tables, parts);
        var rec = new SyncRecorderHandler(_db, _onec, t, up, t.Capabilities, tables, parts, deletes);
        deletes.ClearMovements = rec.ClearAsync;
        return (new WorkExecutor(_db, new Dictionary<string, IWorkHandler>
        {
            [WorkKinds.SyncRecorder] = rec, [WorkKinds.DeleteObject] = deletes,
            [WorkKinds.SyncObject] = new SyncObjectHandler(_db, _onec, up, tables, parts, deletes)
        }), t);
    }

    private static BaseActivation Act() =>
        new("b", false, SyncPriority.Incremental, true, new SyncLeases(new SyncBudgets(), () => DateTimeOffset.UtcNow), new SyncBudgets(), CancellationToken.None);

    /// <summary>Queues the requests and runs the executor until nothing is left.</summary>
    private async Task Run(WorkExecutor exec, params WorkRequest[] requests)
    {
        _db.Write(tx => { foreach (var r in requests) tx.UpsertWork(r); });
        for (int i = 0; i < 10 && _db.Read(tx => tx.CountWork("b")) > 0; i++)
        {
            var s = await exec.RunAsync(Act());
            Assert.True(s.Failed == 0 && s.DeadLettered == 0, _db.Read(tx => tx.DeadLetters("b")).FirstOrDefault()?.Error);
        }
        Assert.Equal(0, _db.Read(tx => tx.CountWork("b")));
    }

    private static WorkRequest Object(TablePlan t, string id) => new("b", t.Table, id, WorkFlags.Changed, ItemPriority.Catalog);

    private static string? Field(StubSyncTarget t, TablePlan table, string key, string field) =>
        t.Rows(P, table.Table).TryGetValue(key, out var json) ? (string?)JsonNode.Parse(json)![field] : null;

    private void Rename(string to, long version)
    {
        _onec.Tables["Cat"][0]["name"] = to;
        _onec.Tables["Cat"][0]["dataVersion"] = FakeOneC.Version(version);
        // What 1C now renders in every row that references the item, though none of them changed.
        _onec.Tables["Doc"][0]["Контрагент"] = to;
        _onec.Tables["Contract"][0]["Владелец"] = to;
        _onec.Tables["Acc"][0]["СубконтоДт1"] = to;
    }

    [Fact]
    public async Task ARenameReReadsExactlyTheRowsThatShowTheItem()
    {
        var (exec, t) = Make();
        await Run(exec, Object(Cat, Item), Object(Contract, Agreement),
                  new WorkRequest("b", Doc.Table, D1, WorkFlags.Changed | WorkFlags.Movements, ItemPriority.Document),
                  new WorkRequest("b", Doc.Table, D2, WorkFlags.Changed | WorkFlags.Movements, ItemPriority.Document));
        Assert.Equal("Old", Field(t, Doc, D1, "Контрагент"));
        Assert.Equal(0, _onec.ReferrerSearches);                                       // first sight of the item: nothing to follow

        // Another field changes, the name does not: no search.
        _onec.Tables["Cat"][0]["ИНН"] = "2";
        _onec.Tables["Cat"][0]["dataVersion"] = FakeOneC.Version(2);
        await Run(exec, Object(Cat, Item));
        Assert.Equal(0, _onec.ReferrerSearches);

        // The rename: one search, and the document, its movements and the contract show the new name.
        Rename("New", 3);
        int reads = _onec.Reads;
        await Run(exec, Object(Cat, Item));
        Assert.Equal(1, _onec.ReferrerSearches);
        Assert.Equal("New", Field(t, Cat, Item, "name"));
        Assert.Equal("New", Field(t, Doc, D1, "Контрагент"));
        Assert.Equal("New", Field(t, Acc, D1 + "#1", "СубконтоДт1"));
        Assert.Equal("New", Field(t, Contract, Agreement, "Владелец"));               // its 1C version did not change: forced
        Assert.Equal("Someone else", Field(t, Doc, D2, "Контрагент"));
        Assert.DoesNotContain(_db.Read(tx => tx.DeadLetters("b")), _ => true);
        Assert.True(_onec.Reads - reads < 12, $"{_onec.Reads - reads} reads for one rename");  // the referrers only, no table walk

        // The same name again (an unrelated edit) after the rename: no second search.
        _onec.Tables["Cat"][0]["dataVersion"] = FakeOneC.Version(4);
        await Run(exec, Object(Cat, Item));
        Assert.Equal(1, _onec.ReferrerSearches);
    }

    [Fact]
    public async Task ACatalogCopiedBeforeRenamesWereFollowedIsSeededOnceAndThenFollowsThem()
    {
        var (exec, t) = Make();
        await Run(exec, Object(Cat, Item), new WorkRequest("b", Doc.Table, D1, WorkFlags.Changed | WorkFlags.Movements, ItemPriority.Document));
        // State as an older Connector left it: versions stored, no shown names.
        _db.Write(tx => tx.DeleteMeta(CanonicalMapper.ShownKey("b", Cat.Table, Item)));
        Rename("Renamed before seeding", 2);
        await Run(exec, Object(Cat, Item));
        Assert.Equal(0, _onec.ReferrerSearches);                                       // unknown before: nothing to compare
        Assert.NotNull(_db.Read(tx => tx.GetMeta(CanonicalMapper.ShownKey("b", Cat.Table, Item))));
        Rename("New", 3);
        await Run(exec, Object(Cat, Item));
        Assert.Equal(1, _onec.ReferrerSearches);
        Assert.Equal("New", Field(t, Doc, D1, "Контрагент"));
    }

    [Fact]
    public void TheShownNameIsTheNameElseTheCode()
    {
        var a = CanonicalMapper.ShownOf(new JsonObject { ["name"] = "A", ["code"] = "1" });
        Assert.NotEqual(a, CanonicalMapper.ShownOf(new JsonObject { ["name"] = "B", ["code"] = "1" }));
        Assert.NotEqual(a, CanonicalMapper.ShownOf(new JsonObject { ["name"] = "A", ["code"] = "2" }));
        Assert.NotNull(CanonicalMapper.ShownOf(new JsonObject { ["code"] = "000001" }));
        Assert.Null(CanonicalMapper.ShownOf(new JsonObject { ["id"] = Item }));
        Assert.Null(CanonicalMapper.Map(Doc, FakeOneC.DocumentRow(1, DateTime.Today)).Shown);
    }
}
