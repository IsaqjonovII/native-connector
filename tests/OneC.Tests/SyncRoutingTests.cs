using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Routing;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S10: organisation routing (§21, D-4).</summary>
public sealed class SyncRoutingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-route-").FullName;
    private readonly SyncDb _db;
    private static readonly string OrgA = FakeOneC.Guid(9_000_000), OrgB = FakeOneC.Guid(9_000_001);
    private static readonly TablePlan Doc = new("Document_Doc", "Doc", Families.Document, true);
    private static readonly TablePlan Cat = new("Catalog_Cat", "Cat", Families.Catalog, false);

    private static readonly SyncConfig Config = new("conn", "unisoft", "conn",
        new[] { new SyncPartition("conn:A", OrgA.ToUpperInvariant(), "501") },
        new[] { new SyncTableConfig(Doc.Table, "Документ.Doc", Families.Document, true), new SyncTableConfig(Cat.Table, "Справочник.Cat", Families.Catalog, false) });

    public SyncRoutingTests() => _db = SyncDb.Open(Path.Combine(_dir, "sync.db"));

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static MappedRow Row(TablePlan t, string? org, int i = 1)
    {
        var r = t == Doc ? FakeOneC.DocumentRow(i, DateTime.Today) : FakeOneC.CatalogRow(i);
        if (org is null) r.Remove("orgRef"); else r["orgRef"] = org;
        return CanonicalMapper.Map(t, r);
    }

    [Fact]
    public void ReferenceRowsAreSharedMovementRowsFollowTheirOrganisation()
    {
        var router = new OrgRouter(Config);
        Assert.True(router.MultiOrg);
        Assert.Equal("conn", router.PartitionOf(Cat, Row(Cat, OrgA)));
        Assert.Equal("conn:A", router.PartitionOf(Doc, Row(Doc, OrgA)));              // GUID case does not matter
        Assert.Null(router.PartitionOf(Doc, Row(Doc, OrgB)));                          // not bound yet
        Assert.Throws<RowMappingException>(() => router.PartitionOf(Doc, Row(Doc, null)));   // never guessed into shared
    }

    [Fact]
    public void ASingleOrganisationBaseSendsEverythingToTheConnection()
    {
        var router = new OrgRouter(Config with { Partitions = Array.Empty<SyncPartition>() });
        Assert.False(router.MultiOrg);
        Assert.Equal("conn", router.PartitionOf(Doc, Row(Doc, OrgB)));
        Assert.Equal("conn", router.PartitionOf(Doc, Row(Doc, null)));
    }

    [Fact]
    public void AClassificationMismatchRefusesToStart()
    {
        OrgRouter.Validate(Config, new[] { Doc, Cat });
        var e = Assert.Throws<RoutingConfigException>(() => OrgRouter.Validate(Config, new[] { Doc with { IsMovement = false }, Cat }));
        Assert.Contains("backend says movement", e.Message);
        Assert.Throws<RoutingConfigException>(() => OrgRouter.Validate(Config, new[] { new TablePlan("Document_Other", "Other", Families.Document, true) }));
    }

    [Fact]
    public async Task UnmappedRowsAreCountedNotSentAndNewBindingsAreFound()
    {
        var onec = new FakeOneC();
        onec.Tables["Doc"] = Enumerable.Range(1, 30).Select(i =>
        {
            var r = FakeOneC.DocumentRow(i, new DateTime(2026, 9, 1).AddHours(i));
            r["orgRef"] = i % 3 == 0 ? OrgB : OrgA;
            return r;
        }).ToList();
        var target = new StubSyncTarget(StubMode.V3);
        var b = new SyncBudgets();
        var gate = new UploadGate(b);
        var router = new OrgRouter(Config);
        var snap = new SnapshotRunner(_db, onec, new Uploader(target, gate), gate, target.Capabilities, router, new SnapshotOptions { PageSize = 7 });
        var r = await snap.RunTableAsync(new BaseActivation("b", false, SyncPriority.Snapshot, false, new SyncLeases(b, () => DateTimeOffset.UtcNow), b, CancellationToken.None), Doc, 1);
        Assert.Equal(SnapshotOutcome.Complete, r.Outcome);
        Assert.Equal(20, target.Count("conn:A", Doc.Table));
        Assert.Equal(0, target.Count("conn", Doc.Table));                              // nothing leaked into shared
        var unmapped = _db.Read(tx => tx.UnmappedOrgs("b")).Single();
        Assert.Equal((OrgB, Doc.Table, 10L), unmapped);

        var bound = new OrgRouter(Config with { Partitions = Config.Partitions.Append(new SyncPartition("conn:B", OrgB, "502")).ToList() });
        Assert.Equal(new[] { OrgB }, OrgRouter.NewlyBound(_db.Read(tx => tx.UnmappedOrgs("b")).Select(u => u.OrgRef), bound));
    }
}
