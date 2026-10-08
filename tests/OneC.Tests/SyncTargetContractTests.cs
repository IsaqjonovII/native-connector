using System.Text;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Sync block S2: the canonical target contract (§17), run against the stub in both emulation
/// modes. The Python v2 target (S12) must pass the same suite against a local backend.
/// </summary>
public sealed class SyncTargetContractTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static SyncRow Row(string key, string json, long? v = null) => new(key, v, Encoding.UTF8.GetBytes(json));
    private static string B() => Guid.NewGuid().ToString("N");

    [Theory, InlineData(StubMode.V2), InlineData(StubMode.V3)]
    public async Task ReUploadingTheSameRowsIsIdempotent(StubMode mode)
    {
        var t = new StubSyncTarget(mode);
        var rows = new[] { Row("a", "{\"n\":1}"), Row("b", "{\"n\":2}") };
        string id = B();
        var first = await t.UploadRowsAsync(new UploadBatch(id, "p", "T", rows), Ct);
        var replay = await t.UploadRowsAsync(new UploadBatch(id, "p", "T", rows), Ct);
        var again = await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", rows), Ct);
        Assert.True(first.Ok && replay.Ok && again.Ok);
        Assert.Equal(2, t.Count("p", "T"));
        Assert.Equal("{\"n\":2}", t.Rows("p", "T")["b"]);
    }

    [Fact]
    public async Task V2KeepsTheFirstDuplicateSilentlyAndOverReports()
    {
        var t = new StubSyncTarget(StubMode.V2);
        var r = await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", new[] { Row("a", "{\"v\":1}"), Row("a", "{\"v\":2}") }), Ct);
        Assert.True(r.Ok);
        Assert.Equal(2, r.ReportedCount);          // says two were stored…
        Assert.Equal(1, t.Count("p", "T"));        // …one is, and it is the first
        Assert.Equal("{\"v\":1}", t.Rows("p", "T")["a"]);
        Assert.False(t.Capabilities.ReportsTrustworthyCounts);
        Assert.Equal(DuplicateKeyPolicy.KeepFirstSilently, t.Capabilities.DuplicateKeys);
    }

    [Fact]
    public async Task V3RejectsDuplicatesAndNamesThem()
    {
        var t = new StubSyncTarget(StubMode.V3);
        var r = await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", new[] { Row("a", "{}"), Row("a", "{}") }), Ct);
        Assert.Equal(Outcome.Validation, r.Outcome);
        Assert.Contains("a", r.Message);
        Assert.Equal(0, t.Count("p", "T"));
    }

    [Fact]
    public async Task V3RefusesAStaleSourceVersionAndReportsRejectedRows()
    {
        var t = new StubSyncTarget(StubMode.V3);
        await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", new[] { Row("a", "{\"v\":5}", 5) }), Ct);
        t.RejectKeys.Add("bad");
        var r = await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", new[] { Row("a", "{\"v\":4}", 4), Row("bad", "{}", 1), Row("c", "{}", 1) }), Ct);
        Assert.True(r.Ok);
        Assert.Equal(new[] { "a" }, r.Stale);
        Assert.Equal("bad", Assert.Single(r.Rejected).Key);
        Assert.Equal(1, r.Applied);
        Assert.Equal("{\"v\":5}", t.Rows("p", "T")["a"]);
    }

    [Theory, InlineData(StubMode.V2), InlineData(StubMode.V3)]
    public async Task ReconcileRemovesOnlyTheRecordersRowsMissingFromTheLiveSet(StubMode mode)
    {
        var t = new StubSyncTarget(mode);
        t.Seed("p", "R", "doc1#1", "{}"); t.Seed("p", "R", "doc1#2", "{}"); t.Seed("p", "R", "doc1#3", "{}");
        t.Seed("p", "R", "doc10#1", "{}");     // another recorder whose key starts the same way
        t.Seed("q", "R", "doc1#9", "{}");      // same recorder, other partition
        var partial = await t.ReconcileRecorderAsync(new RecorderReconcile(B(), "p", "R", "doc1", new[] { "doc1#1" }), Ct);
        Assert.Equal(2, partial.Removed);
        var empty = await t.ReconcileRecorderAsync(new RecorderReconcile(B(), "p", "R", "doc1", Array.Empty<string>()), Ct);
        Assert.Equal(1, empty.Removed);
        Assert.Equal(new[] { "doc10#1" }, t.Rows("p", "R").Keys);
        Assert.Equal(1, t.Count("q", "R"));
    }

    [Fact]
    public async Task V2RefusesDeletesOverTheCapOrOnAnEmptyTableUntilApproved()
    {
        var t = new StubSyncTarget(StubMode.V2);
        var onEmpty = await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", new[] { "x" }, "deleted in 1C"), Ct);
        Assert.Equal((Outcome.Ok, 0), (onEmpty.Outcome, onEmpty.Deleted));        // nothing stored = already gone
        for (int i = 0; i < 100; i++) t.Seed("p", "T", "k" + i, "{}");
        var within = await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", new[] { "k0", "k1", "k2", "k3", "k4" }, "deleted in 1C"), Ct);
        Assert.Equal(5, within.Deleted);
        var over = await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", Enumerable.Range(10, 20).Select(i => "k" + i).ToList(), "deleted in 1C"), Ct);
        Assert.Equal(Outcome.Policy, over.Outcome);
        Assert.Equal(95, t.Count("p", "T"));
        var approved = await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", Enumerable.Range(10, 20).Select(i => "k" + i).ToList(), "deleted in 1C", Approved: true), Ct);
        Assert.Equal(20, approved.Deleted);
    }

    [Fact]
    public async Task V3HasNoDeleteCap()
    {
        var t = new StubSyncTarget(StubMode.V3);
        t.Seed("p", "T", "a", "{}");
        Assert.Equal(1, (await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", new[] { "a" }, "r"), Ct)).Deleted);
    }

    [Fact]
    public async Task V3AtomicRecorderReplacesMovementsAndRefusesAStaleRetry()
    {
        var t = new StubSyncTarget(StubMode.V3);
        t.Seed("p", "Acc", "d#1", "{\"sum\":10}", 1); t.Seed("p", "Acc", "d#2", "{\"sum\":20}", 1);
        var repost = new RecorderSync(B(), "d", 7, "Doc", "p", Row("d", "{\"posted\":true}"), new[] { "p" }, new[] { "Acc", "Acc2" },
            new[] { new PartitionMovements("p", "Acc", new[] { Row("d#1", "{\"sum\":15}") }) });
        var r = await t.SyncRecorderAtomicAsync(repost, Ct);
        Assert.Equal((1, 1), (r.MovementsWritten, r.MovementsRemoved));
        Assert.Equal("{\"sum\":15}", t.Rows("p", "Acc")["d#1"]);          // same key, changed amount (F1)
        Assert.False(t.Rows("p", "Acc").ContainsKey("d#2"));
        var stale = await t.SyncRecorderAtomicAsync(repost with { BatchId = B(), SourceVersion = 6, Document = Row("d", "{\"posted\":false}") }, Ct);
        Assert.True(stale.StaleDocument);
        Assert.Equal("{\"posted\":true}", t.Rows("p", "Doc")["d"]);
        var gone = await t.SyncRecorderAtomicAsync(new RecorderSync(B(), "d", 8, "Doc", null, null, new[] { "p" }, new[] { "Acc" },
            Array.Empty<PartitionMovements>()), Ct);
        Assert.Equal(1, gone.MovementsRemoved);
        Assert.Equal(0, t.Count("p", "Doc") + t.Count("p", "Acc"));
    }

    [Fact]
    public async Task V3AtomicRecorderMovesADocumentBetweenOrganisationsInOneCall()
    {
        var t = new StubSyncTarget(StubMode.V3);
        t.Seed("orgA", "Doc", "d", "{\"org\":\"a\"}", 1); t.Seed("orgA", "Acc", "d#1", "{}", 1);
        t.Seed("orgA", "Acc", "x#1", "{}", 1);                                 // another recorder: never touched
        var moved = new RecorderSync(B(), "d", 2, "Doc", "orgB", Row("d", "{\"org\":\"b\"}"), new[] { "orgA", "orgB", "shared" }, new[] { "Acc" },
            new[] { new PartitionMovements("orgB", "Acc", new[] { Row("d#1", "{}"), Row("d#2", "{}") }) });
        var r = await t.SyncRecorderAtomicAsync(moved, Ct);
        Assert.True(r.Ok);
        Assert.Equal(new[] { "x#1" }, t.Rows("orgA", "Acc").Keys);
        Assert.False(t.Rows("orgA", "Doc").ContainsKey("d"));
        Assert.Equal(2, t.Count("orgB", "Acc"));
        Assert.Equal("{\"org\":\"b\"}", t.Rows("orgB", "Doc")["d"]);
    }

    [Fact]
    public async Task V3AtomicRecorderRefusesRowsOutsideItsScope()
    {
        var t = new StubSyncTarget(StubMode.V3);
        var outside = new RecorderSync(B(), "d", 1, "Doc", "p", null, new[] { "p" }, new[] { "Acc" },
            new[] { new PartitionMovements("q", "Acc", new[] { Row("d#1", "{}") }) });
        Assert.Equal(Outcome.Validation, (await t.SyncRecorderAtomicAsync(outside, Ct)).Outcome);
        var foreignKey = outside with { Movements = new[] { new PartitionMovements("p", "Acc", new[] { Row("e#1", "{}") }) } };
        Assert.Equal(Outcome.Validation, (await t.SyncRecorderAtomicAsync(foreignKey, Ct)).Outcome);
        Assert.Equal(0, t.Count("q", "Acc") + t.Count("p", "Acc"));
    }

    [Fact]
    public async Task V2HasNoAtomicRecorder()
    {
        var t = new StubSyncTarget(StubMode.V2);
        Assert.False(t.Capabilities.AtomicRecorder);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            t.SyncRecorderAtomicAsync(new RecorderSync(B(), "d", 1, "Doc", null, null, new[] { "p" }, Array.Empty<string>(), Array.Empty<PartitionMovements>()), Ct));
    }

    [Theory, InlineData(StubMode.V2), InlineData(StubMode.V3)]
    public async Task FailuresAreCanonical(StubMode mode)
    {
        var t = new StubSyncTarget(mode);
        t.FailNext(new TargetResult(Outcome.RateLimited, 429, "slow down", TimeSpan.FromSeconds(3)), op: "upload");
        var limited = await t.UploadRowsAsync(new UploadBatch(B(), "p", "T", new[] { Row("a", "{}") }), Ct);
        Assert.Equal(Outcome.RateLimited, limited.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(3), limited.RetryAfter);
        t.Offline = true;
        Assert.Equal(Outcome.Transient, (await t.DeleteRowsAsync(new DeleteBatch(B(), "p", "T", new[] { "a" }, "r"), Ct)).Outcome);
        t.Offline = false;
        t.Deleting = true;
        Assert.Equal(Outcome.Gone, (await t.ReportStatusAsync(new BaseStatus("c", "active", 1, 100, null), Ct)).Outcome);
        Assert.Equal(0, t.Count("p", "T"));
    }

    [Fact]
    public async Task ConfigComesBackAsSet()
    {
        var t = new StubSyncTarget();
        var cfg = new SyncConfig("c1", "unisoft", "c1", new[] { new SyncPartition("c1:org1", "org1", "501") },
                                 new[] { new SyncTableConfig("Document_X", "Документ.X", "document", true) });
        t.SetConfig(cfg);
        Assert.Same(cfg, await t.GetSyncConfigAsync("c1", Ct));
        Assert.Empty((await t.GetSyncConfigAsync("other", Ct)).Tables);
    }
}
