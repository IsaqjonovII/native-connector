using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S7: the incremental executor for catalog and document objects (§5, §6, §10, §22).</summary>
public sealed class SyncExecutorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-exec-").FullName;
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);

    public SyncExecutorTests()
    {
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"), () => _now);
        _onec.Tables["Cat"] = Enumerable.Range(1, 30).Select(FakeOneC.CatalogRow).ToList();
        _onec.Tables["Doc"] = Enumerable.Range(1, 5).Select(i => FakeOneC.DocumentRow(i, new DateTime(2026, 9, 1))).ToList();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (WorkExecutor Exec, StubSyncTarget Target) Make(StubSyncTarget? target = null)
    {
        var t = target ?? new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var up = new Uploader(t, new UploadGate(b)) { Backoff = _ => TimeSpan.FromMilliseconds(5), MaxAttempts = 2 };
        var tables = new Dictionary<string, TablePlan> { [Cat.Table] = Cat, [Doc.Table] = Doc };
        var parts = new SinglePartition("p");
        var deletes = new DeleteObjectHandler(_db, t, tables, parts);
        var handlers = new Dictionary<string, IWorkHandler>
        {
            [WorkKinds.SyncObject] = new SyncObjectHandler(_db, _onec, up, tables, parts, deletes),
            [WorkKinds.DeleteObject] = deletes
        };
        return (new WorkExecutor(_db, handlers) { Backoff = _ => TimeSpan.FromSeconds(10) }, t);
    }

    private static BaseActivation Act(CancellationToken stop = default) =>
        new("b", false, SyncPriority.Incremental, true, new SyncLeases(new SyncBudgets(), () => DateTimeOffset.UtcNow), new SyncBudgets(), stop);

    private void Queue(TablePlan t, int i, WorkFlags f) => _db.Write(tx => tx.UpsertWork(new WorkRequest("b", t.Table, FakeOneC.Guid(i), f, 1)));

    [Fact]
    public async Task AChangedObjectIsReadAgainUploadedAndRemembered()
    {
        var (exec, target) = Make();
        Queue(Cat, 3, WorkFlags.Changed);
        var s = await exec.RunAsync(Act());
        Assert.Equal(1, s.Done);
        Assert.Contains("item 3", target.Rows("p", Cat.Table)[FakeOneC.Guid(3)]);
        Assert.Equal(FakeOneC.Version(1), _db.Read(tx => tx.GetVersion("b", Cat.Table, FakeOneC.Guid(3))));
        Assert.Equal(new[] { "p" }, _db.Read(tx => tx.GetPartitions("b", FakeOneC.Guid(3))));
        Assert.Equal(0, _db.Read(tx => tx.CountWork("b")));

        // The same version again (an Update that changed nothing we sync): nothing is sent.
        Queue(Cat, 3, WorkFlags.Changed);
        int uploads = target.Calls.Count(c => c.StartsWith("upload"));
        Assert.Equal(1, (await exec.RunAsync(Act())).Skipped);
        Assert.Equal(uploads, target.Calls.Count(c => c.StartsWith("upload")));

        // A real change: new version, sent.
        _onec.Tables["Cat"][2]["name"] = "renamed";
        _onec.Tables["Cat"][2]["dataVersion"] = FakeOneC.Version(2);
        Queue(Cat, 3, WorkFlags.Changed);
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.Contains("renamed", target.Rows("p", Cat.Table)[FakeOneC.Guid(3)]);
    }

    [Fact]
    public async Task AnObjectGoneFromOneCIsDeletedEvenIfTheEventSaidChanged()
    {
        var (exec, target) = Make();
        Queue(Cat, 7, WorkFlags.Changed);
        await exec.RunAsync(Act());
        _onec.Tables["Cat"].RemoveAll(r => (string)r["id"]! == FakeOneC.Guid(7));   // deleted in 1C before the item ran again
        Queue(Cat, 7, WorkFlags.Changed);
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.False(target.Rows("p", Cat.Table).ContainsKey(FakeOneC.Guid(7)));
        Assert.Null(_db.Read(tx => tx.GetVersion("b", Cat.Table, FakeOneC.Guid(7))));
    }

    [Fact]
    public async Task AMissingTableIsADeadLetterNotADeleteAndTheRestProceeds()
    {
        var (exec, target) = Make();
        target.Seed("p", Doc.Table, FakeOneC.Guid(1), "{}");
        _onec.MissingTables.Add("Doc");
        Queue(Doc, 1, WorkFlags.Changed);
        Queue(Cat, 1, WorkFlags.Changed);
        var s = await exec.RunAsync(Act());
        Assert.Equal((1, 1), (s.Done, s.DeadLettered));
        Assert.True(target.Rows("p", Doc.Table).ContainsKey(FakeOneC.Guid(1)));   // "unknown" never deletes
        Assert.Equal(DeadLetterCategories.MissingConfig, _db.Read(tx => tx.DeadLetters("b")).Single().Category);
    }

    [Fact]
    public async Task ATransientFailureBacksOffInTheQueueThenSucceeds()
    {
        var (exec, target) = Make();
        target.FailNext(new TargetResult(Outcome.Transient, 503, "busy"), times: 2, op: "upload");   // beats the uploader's 2 attempts
        Queue(Cat, 4, WorkFlags.Changed);
        var s = await exec.RunAsync(Act());
        Assert.Equal(1, s.Failed);
        var item = _db.Read(tx => tx.PeekWork("b")).Single();
        Assert.Contains("503", item.LastError);
        Assert.Empty(await Task.FromResult(_db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(1)))));   // backing off
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.True(target.Rows("p", Cat.Table).ContainsKey(FakeOneC.Guid(4)));
    }

    [Fact]
    public async Task LostAuthStopsTheRunAndKeepsTheWork()
    {
        var (exec, target) = Make();
        target.FailNext(new TargetResult(Outcome.Auth, 401, "expired"), times: 10, op: "upload");
        foreach (var i in new[] { 1, 2, 3, 4 }) Queue(Cat, i, WorkFlags.Changed);
        var s = await exec.RunAsync(Act());
        Assert.Equal(0, s.Done);
        Assert.Equal(4, _db.Read(tx => tx.CountWork("b")));                     // nothing lost, nothing dead-lettered
        Assert.Empty(_db.Read(tx => tx.DeadLetters("b")));
    }

    [Fact]
    public async Task ADeleteTheBackendRefusesWaitsForApproval()
    {
        var target = new StubSyncTarget(StubMode.V2);
        var (exec, _) = Make(target);
        target.Seed("p", Cat.Table, FakeOneC.Guid(101), "{}");
        target.FailNext(new TargetResult(Outcome.Policy, 409, "over the 5% cap"), op: "delete");
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Cat.Table, FakeOneC.Guid(101), WorkFlags.Deleted, 0)));
        var s = await exec.RunAsync(Act());
        Assert.Equal(1, s.DeadLettered);
        var dl = _db.Read(tx => tx.DeadLetters("b")).Single();
        Assert.Equal((DeadLetterCategories.NeedsApproval, WorkKinds.DeleteObject), (dl.Category, dl.WorkKind));
        Assert.Null(dl.NextRetryAt);                                             // never retried or discarded on its own
    }

    [Fact]
    public async Task ADeleteIsSentEvenWithoutARecordOfSending()
    {
        // S15 kill test: the upload landed, the process died before the version was stored — the
        // row is in the cloud although the engine has no record of it. The delete must still go.
        var target = new StubSyncTarget(StubMode.V2);
        var (exec, _) = Make(target);
        for (int i = 0; i < 30; i++) target.Seed("p", Cat.Table, FakeOneC.Guid(900 + i), "{}");
        target.Seed("p", Cat.Table, FakeOneC.Guid(555), "{}");
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Cat.Table, FakeOneC.Guid(555), WorkFlags.Deleted, 0)));
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.False(target.Rows("p", Cat.Table).ContainsKey(FakeOneC.Guid(555)));
        // And for an object that really was never sent (created and deleted between syncs): harmless.
        var empty = new StubSyncTarget(StubMode.V2);
        var (exec2, _) = Make(empty);
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Cat.Table, FakeOneC.Guid(556), WorkFlags.Deleted, 0)));
        Assert.Equal(1, (await exec2.RunAsync(Act())).Done);
        Assert.Empty(_db.Read(tx => tx.DeadLetters("b")));
    }

    [Fact]
    public async Task ACrashAfterTheUploadRunsTheItemAgainHarmlessly()
    {
        var (exec, target) = Make();
        Queue(Cat, 5, WorkFlags.Changed);
        _db.FaultHook = p => { if (p == "complete") throw new IOException("power cut after the upload"); };
        await exec.RunAsync(Act());
        _db.FaultHook = null;
        Assert.Equal(1, _db.Read(tx => tx.CountWork("b")));                      // the item survived
        _now += TimeSpan.FromMinutes(6);                                         // lease / backoff over
        await exec.RunAsync(Act());
        Assert.Equal(0, _db.Read(tx => tx.CountWork("b")));
        Assert.Equal(1, target.Count("p", Cat.Table));                           // idempotent upsert: once
    }

    [Fact]
    public async Task OneItemPerObjectAtATime()
    {
        var (exec, _) = Make();
        Queue(Cat, 6, WorkFlags.Changed);
        var held = _db.Write(tx => tx.ClaimWork("b", 10, TimeSpan.FromMinutes(5)));   // another executor holds it
        Assert.Single(held);
        var s = await exec.RunAsync(Act());
        Assert.Equal(0, s.Done + s.Failed);
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Cat.Table, FakeOneC.Guid(6), WorkFlags.Deleted, 0)));   // arrives meanwhile
        Assert.False(_db.Write(tx => tx.CompleteWork(held[0])));                 // released for the delete, not dropped
        Assert.Equal(WorkKinds.DeleteObject, _db.Read(tx => tx.PeekWork("b")).Single().Kind);
    }
}
