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

/// <summary>Sync block S8: SyncRecorder and movement reconciliation (§9), both target modes.</summary>
public sealed class SyncRecorderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-rec-").FullName;
    private readonly SyncDb _db;
    private readonly FakeOneC _onec = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Acc = new("AccountingRegister_Acc", "Acc", Families.AccountingRegister, true);
    private static readonly TablePlan Vat = new("AccumulationRegister_Vat", "Vat", Families.AccumulationRegister, true);
    private static readonly string D1 = FakeOneC.Guid(1);

    /// <summary>Routes by the row's organisation (the S10 rule in miniature): org X → "px", else "shared".</summary>
    private sealed class ByOrg : IPartitioner
    {
        private readonly HashSet<string> _seen = new() { "shared" };
        public string Shared => "shared";
        /// <summary>The bindings a real config would list: every partition handed out so far.</summary>
        public IReadOnlyList<string> All => _seen.ToList();
        public string? PartitionOf(TablePlan t, MappedRow row)
        {
            string p = row.OrgRef is { } o ? "p" + o[..8] : Shared;
            _seen.Add(p);
            return p;
        }
    }

    public SyncRecorderTests()
    {
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"), () => _now);
        _onec.Tables["Doc"] = new() { FakeOneC.DocumentRow(1, new DateTime(2026, 9, 1)) };
        _onec.Tables["Acc"] = new();
        _onec.Tables["Vat"] = new();
        _onec.RecorderTypes["Acc"] = new() { "Doc" };
        _onec.RecorderTypes["Vat"] = new() { "Doc", "Other" };
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Lines(string table, params (int Line, decimal Sum)[] lines)
    {
        _onec.Tables[table] = lines.Select(l =>
        {
            var r = FakeOneC.MovementRow(1, l.Line, new DateTime(2026, 9, 1));
            r["Сумма"] = l.Sum;
            return r;
        }).ToList();
    }

    private (WorkExecutor Exec, StubSyncTarget Target, SyncRecorderHandler Rec) Make(StubMode mode, IPartitioner? parts = null)
    {
        var t = new StubSyncTarget(mode);
        var up = new Uploader(t, new UploadGate(new SyncBudgets()));
        var tables = new Dictionary<string, TablePlan> { [Doc.Table] = Doc, [Acc.Table] = Acc, [Vat.Table] = Vat };
        parts ??= new ByOrg();
        // A realistic table: v2 refuses to prune more than 5 % of it, and one of one is 100 %.
        for (int i = 0; i < 100; i++) t.Seed(P, Doc.Table, FakeOneC.Guid(500_000 + i), "{}");
        var deletes = new DeleteObjectHandler(_db, t, tables, parts);
        var rec = new SyncRecorderHandler(_db, _onec, t, up, t.Capabilities, tables, parts, deletes) { PageSize = 2 };
        deletes.ClearMovements = rec.ClearAsync;
        var handlers = new Dictionary<string, IWorkHandler>
        {
            [WorkKinds.SyncRecorder] = rec, [WorkKinds.DeleteObject] = deletes,
            [WorkKinds.SyncObject] = new SyncObjectHandler(_db, _onec, up, tables, parts, deletes)
        };
        return (new WorkExecutor(_db, handlers), t, rec);
    }

    private static BaseActivation Act() =>
        new("b", false, SyncPriority.Incremental, true, new SyncLeases(new SyncBudgets(), () => DateTimeOffset.UtcNow), new SyncBudgets(), CancellationToken.None);

    private async Task Run(WorkExecutor exec, WorkFlags f, string table = "Document_Doc")
    {
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", table, D1, f, 0)));
        var s = await exec.RunAsync(Act());
        Assert.True(s.Done == 1, $"done {s.Done}, failed {s.Failed}, dead {s.DeadLettered}: {_db.Read(tx => tx.DeadLetters("b")).FirstOrDefault()?.Error}");
    }

    private static Dictionary<string, decimal> Cloud(StubSyncTarget t, string partition, TablePlan reg) =>
        t.Rows(partition, reg.Table).ToDictionary(kv => kv.Key, kv => (decimal)JsonNode.Parse(kv.Value)!["Сумма"]!);

    private static string P => "p" + FakeOneC.Guid(9_000_000)[..8];     // the organisation of the fake rows

    [Theory, InlineData(StubMode.V2), InlineData(StubMode.V3)]
    public async Task PostRepostUnpostDeleteLeaveExactlyOnesMovements(StubMode mode)
    {
        var (exec, t, _) = Make(mode);
        // Post: 3 lines in Acc, 1 in Vat.
        Lines("Acc", (1, 10), (2, 20), (3, 30));
        Lines("Vat", (1, 5));
        await Run(exec, WorkFlags.Changed | WorkFlags.Movements);
        Assert.Equal(new Dictionary<string, decimal> { [D1 + "#1"] = 10, [D1 + "#2"] = 20, [D1 + "#3"] = 30 }, Cloud(t, P, Acc));
        Assert.Single(Cloud(t, P, Vat));
        Assert.True(t.Rows(P, Doc.Table).ContainsKey(D1));

        // Repost with fewer lines AND changed amounts on the same keys (F1).
        Lines("Acc", (1, 11), (2, 22));
        await Run(exec, WorkFlags.Movements);
        Assert.Equal(new Dictionary<string, decimal> { [D1 + "#1"] = 11, [D1 + "#2"] = 22 }, Cloud(t, P, Acc));

        // Unpost: no movements anywhere.
        Lines("Acc");
        Lines("Vat");
        await Run(exec, WorkFlags.Changed | WorkFlags.Movements);
        Assert.Empty(Cloud(t, P, Acc));
        Assert.Empty(Cloud(t, P, Vat));
        Assert.True(t.Rows(P, Doc.Table).ContainsKey(D1));                           // the document stays

        // Post again, then delete the document in 1C.
        Lines("Acc", (1, 1));
        await Run(exec, WorkFlags.Movements);
        _onec.Tables["Doc"].Clear();
        Lines("Acc");
        await Run(exec, WorkFlags.Deleted);
        Assert.Empty(Cloud(t, P, Acc));
        Assert.False(t.Rows(P, Doc.Table).ContainsKey(D1));
    }

    [Theory, InlineData(StubMode.V2), InlineData(StubMode.V3)]
    public async Task ARecorderThatMovesToAnotherOrganisationLeavesNothingInTheOldPartition(StubMode mode)
    {
        var (exec, t, _) = Make(mode);
        Lines("Acc", (1, 10), (2, 20));
        await Run(exec, WorkFlags.Movements);
        Assert.Equal(2, Cloud(t, P, Acc).Count);
        // The document's organisation changes: rows and document go to "pNEW…", nothing may stay behind.
        string org2 = FakeOneC.Guid(8_000_000);
        _onec.Tables["Doc"][0]["orgRef"] = org2;
        foreach (var r in _onec.Tables["Acc"]) r["orgRef"] = org2;
        await Run(exec, WorkFlags.Changed | WorkFlags.Movements);
        Assert.Empty(Cloud(t, P, Acc));
        Assert.False(t.Rows(P, Doc.Table).ContainsKey(D1));
        Assert.Equal(2, Cloud(t, "p" + org2[..8], Acc).Count);
        Assert.Equal(new[] { "p" + org2[..8] }, _db.Read(tx => tx.GetPartitions("b", D1)));
    }

    [Fact]
    public async Task MovementsOfADocumentTypeThatIsNotSyncedAreStillReconciled()
    {
        // R-9 / F2: a register event names a recorder of type "Other" (not a configured table).
        var (exec, t, _) = Make(StubMode.V2);
        _onec.Tables["Vat"] = new() { FakeOneC.MovementRow(1, 1, DateTime.Today), FakeOneC.MovementRow(1, 2, DateTime.Today) };
        _onec.RecorderKinds[D1] = "Other";
        await Run(exec, WorkFlags.Movements, EventCoalescer.UnknownRecorderTable);
        Assert.Equal(2, Cloud(t, P, Vat).Count);
        Assert.False(t.Rows(P, Doc.Table).ContainsKey(D1));                          // no document row: its table is not synced
        _onec.Tables["Vat"].RemoveAt(1);
        await Run(exec, WorkFlags.Movements, "?Документ.Other");
        Assert.Single(Cloud(t, P, Vat));
    }

    [Fact]
    public async Task ACrashBetweenUploadAndReconcileIsRepairedByTheRetry()
    {
        var (exec, t, _) = Make(StubMode.V2);
        Lines("Acc", (1, 10), (2, 20), (3, 30));
        await Run(exec, WorkFlags.Movements);
        Lines("Acc", (1, 10));
        t.FailNext(new OneC.Sync.Abstractions.TargetResult(OneC.Sync.Abstractions.Outcome.Transient, 503, "down"), times: 1, op: "reconcile");
        _db.Write(tx => tx.UpsertWork(new WorkRequest("b", Doc.Table, D1, WorkFlags.Movements, 0)));
        var s = await exec.RunAsync(Act());
        Assert.Equal(1, s.Failed);
        Assert.Equal(3, Cloud(t, P, Acc).Count);                                     // extra old rows for now, never missing ones
        _now += TimeSpan.FromMinutes(10);
        Assert.Equal(1, (await exec.RunAsync(Act())).Done);
        Assert.Equal(new[] { D1 + "#1" }, Cloud(t, P, Acc).Keys);
    }

    [Fact]
    public async Task APostCostsOneDocumentReadAndOneMovementReadPerRegisterPage()
    {
        var (exec, t, rec) = Make(StubMode.V2);
        Lines("Acc", (1, 1), (2, 2), (3, 3), (4, 4), (5, 5));                         // 3 pages of 2
        Lines("Vat", (1, 1));
        int reads = _onec.Reads;
        await Run(exec, WorkFlags.Movements);
        Assert.Equal(1 + 3 + 1, _onec.Reads - reads);                                 // doc + Acc pages + Vat page; no table scans
        Assert.Equal(5, Cloud(t, P, Acc).Count);
    }
}
