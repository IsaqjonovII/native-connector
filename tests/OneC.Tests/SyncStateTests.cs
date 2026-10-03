using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OneC.SyncState;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S1: the durable store (SYNC_ENGINE_ARCHITECTURE §13). No 1C.</summary>
public sealed class SyncStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiba-syncstate-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public SyncStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_dir, "sync.db");
    private SyncDb Open() => SyncDb.Open(DbPath, () => _now);
    private static WorkRequest Req(string key, WorkFlags f, int p = 1, string table = "T") => new("b", table, key, f, p);

    [Fact]
    public void MigratesAnEmptyFileAndReopensAtTheSameVersion()
    {
        using (var db = Open())
        {
            Assert.Equal(SyncDb.SchemaVersion, db.CurrentSchemaVersion);
            db.Write(tx => tx.SetMeta("x", "1"));
        }
        using var again = Open();
        Assert.Equal(SyncDb.SchemaVersion, again.CurrentSchemaVersion);
        Assert.Equal("1", again.Read(tx => tx.GetMeta("x")));
        Assert.Null(again.QuarantinedTo);
        Assert.Null(again.RecoveryRequired);
    }

    [Fact]
    public void ANewerSchemaIsRefusedNotGuessed()
    {
        using (var db = Open()) db.Write(tx => tx.SetMeta("schema_version", "99"));
        var e = Assert.Throws<SyncDbTooNewException>(() => Open());
        Assert.Equal(99, e.Found);
    }

    [Fact]
    public void CursorAndWorkItemsCommitTogetherOrNotAtAll()
    {
        using var db = Open();
        db.Write(tx => { tx.UpsertWork(Req("a", WorkFlags.Changed)); tx.SetCursor("b", "c1", null); });
        db.FaultHook = p => { if (p == "cursor") throw new IOException("killed between the items and the cursor"); };
        Assert.Throws<IOException>(() => db.Write(tx => { tx.UpsertWork(Req("b", WorkFlags.Changed)); tx.SetCursor("b", "c2", null); }));
        db.FaultHook = p => { if (p == "commit") throw new IOException("killed before commit"); };
        Assert.Throws<IOException>(() => db.Write(tx => { tx.UpsertWork(Req("c", WorkFlags.Changed)); tx.SetCursor("b", "c3", null); }));
        db.FaultHook = null;
        Assert.Equal("c1", db.Read(tx => tx.GetCursor("b"))!.Cursor);
        Assert.Equal(new[] { "a" }, db.Read(tx => tx.PeekWork("b")).Select(w => w.ObjectKey));
    }

    [Theory]
    [InlineData(WorkFlags.Changed, WorkFlags.Changed, WorkFlags.Changed, WorkKinds.SyncObject)]
    [InlineData(WorkFlags.Changed, WorkFlags.Movements, WorkFlags.Changed | WorkFlags.Movements, WorkKinds.SyncRecorder)]
    [InlineData(WorkFlags.Movements, WorkFlags.Deleted, WorkFlags.Movements | WorkFlags.Deleted, WorkKinds.DeleteObject)]
    [InlineData(WorkFlags.Deleted, WorkFlags.Changed, WorkFlags.Deleted | WorkFlags.Changed, WorkKinds.DeleteObject)]
    [InlineData(WorkFlags.Deleted, WorkFlags.Recreated, WorkFlags.Changed, WorkKinds.SyncObject)]
    [InlineData(WorkFlags.Deleted | WorkFlags.Movements, WorkFlags.Recreated, WorkFlags.Changed | WorkFlags.Movements, WorkKinds.SyncRecorder)]
    // an approved delete still pending is cancelled by the object coming back (2026-10-01 review)
    [InlineData(WorkFlags.ApprovedDelete | WorkFlags.Deleted, WorkFlags.Recreated, WorkFlags.Changed, WorkKinds.SyncObject)]
    public void MergeFollowsTheCoalescingTable(WorkFlags pending, WorkFlags incoming, WorkFlags merged, string kind)
    {
        Assert.Equal(merged, WorkKinds.Merge(pending, incoming));
        Assert.Equal(kind, WorkKinds.For(merged));
    }

    [Fact]
    public void RepeatedEventsForOneObjectMakeOneItem()
    {
        using var db = Open();
        db.Write(tx =>
        {
            tx.UpsertWork(Req("doc", WorkFlags.Changed, 2));
            tx.UpsertWork(Req("doc", WorkFlags.Movements, 0));
            tx.UpsertWork(Req("doc", WorkFlags.Changed, 3));
        });
        var items = db.Read(tx => tx.PeekWork("b"));
        var only = Assert.Single(items);
        Assert.Equal(WorkKinds.SyncRecorder, only.Kind);
        Assert.Equal(0, only.Priority);
    }

    [Fact]
    public void ClaimTakesPriorityThenFeedOrderAndLeasesTheObject()
    {
        using var db = Open();
        db.Write(tx =>
        {
            tx.UpsertWork(Req("c1", WorkFlags.Changed, 2));
            tx.UpsertWork(Req("c2", WorkFlags.Changed, 2));
            tx.UpsertWork(Req("d1", WorkFlags.Deleted, 0));
        });
        var first = db.Write(tx => tx.ClaimWork("b", 2, TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { "d1", "c1" }, first.Select(i => i.ObjectKey));
        Assert.All(first, i => Assert.Equal(1, i.Attempts));
        var second = db.Write(tx => tx.ClaimWork("b", 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { "c2" }, second.Select(i => i.ObjectKey));   // leased ones are locked
        _now += TimeSpan.FromMinutes(6);                                    // leases expire: a crashed executor's items return
        var third = db.Write(tx => tx.ClaimWork("b", 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(3, third.Count);
        Assert.Equal(2, third.First(i => i.ObjectKey == "d1").Attempts);
    }

    [Fact]
    public void FailureBacksOffAndKeepsTheItem()
    {
        using var db = Open();
        db.Write(tx => tx.UpsertWork(Req("a", WorkFlags.Changed)));
        var item = db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))).Single();
        db.Write(tx => tx.FailWork(item, "503", _now + TimeSpan.FromSeconds(30)));
        Assert.Empty(db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))));
        Assert.Equal(_now + TimeSpan.FromSeconds(30), db.Read(tx => tx.NextWorkAt("b")));
        _now += TimeSpan.FromSeconds(31);
        var again = Assert.Single(db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))));
        Assert.Equal("503", again.LastError);
    }

    [Fact]
    public void AnIntentMergedWhileTheItemRunsIsNotLostOnCompletion()
    {
        using var db = Open();
        db.Write(tx => tx.UpsertWork(Req("doc", WorkFlags.Movements)));
        var running = db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))).Single();
        db.Write(tx => tx.UpsertWork(Req("doc", WorkFlags.Deleted)));      // deleted in 1C while the recorder sync ran
        Assert.False(db.Write(tx => tx.CompleteWork(running)));
        var next = Assert.Single(db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))));
        Assert.Equal(WorkKinds.DeleteObject, next.Kind);
        Assert.True(db.Write(tx => tx.CompleteWork(next)));
        Assert.Equal(0, db.Read(tx => tx.CountWork("b")));
    }

    [Fact]
    public void ADeadLetterNeverSwallowsANewerIntent()
    {
        using var db = Open();
        db.Write(tx => tx.UpsertWork(Req("doc", WorkFlags.Changed)));
        var bad = db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))).Single();
        db.Write(tx => tx.UpsertWork(Req("doc", WorkFlags.Deleted)));
        Assert.False(db.Write(tx => tx.DeadLetterWork(bad, DeadLetterCategories.Validation, "400")));
        Assert.Empty(db.Read(tx => tx.DeadLetters("b")));
        var del = db.Write(tx => tx.ClaimWork("b", 1, TimeSpan.FromMinutes(5))).Single();
        Assert.True(db.Write(tx => tx.DeadLetterWork(del, DeadLetterCategories.NeedsApproval, "over the 5% cap", 409, null, "12000 keys")));
        var dl = Assert.Single(db.Read(tx => tx.DeadLetters("b", DeadLetterCategories.NeedsApproval)));
        Assert.Equal(WorkKinds.DeleteObject, dl.WorkKind);
        Assert.Equal("12000 keys", dl.PayloadHint);
        Assert.Null(dl.NextRetryAt);
        Assert.Equal(0, db.Read(tx => tx.CountWork("b")));
    }

    [Fact]
    public void AGarbageFileIsQuarantinedAndRecoveryIsRequired()
    {
        File.WriteAllText(DbPath, "this is not a database, it is a corrupted sync.db with enough bytes to have a header");
        using var db = Open();
        Assert.NotNull(db.QuarantinedTo);
        Assert.True(File.Exists(db.QuarantinedTo));
        Assert.Equal(db.QuarantinedTo, db.RecoveryRequired);
        Assert.Equal(SyncDb.SchemaVersion, db.CurrentSchemaVersion);
    }

    [Fact]
    public void ADamagedPageFailsTheIntegrityCheckAndIsQuarantined()
    {
        using (var db = Open())
            db.Write(tx => { for (int i = 0; i < 3000; i++) tx.SetVersion("b", "T", $"key-{i:D6}", "AAAAAAAAAAE="); });
        SqliteConnection.ClearAllPools();
        using (var fs = new FileStream(DbPath, FileMode.Open, FileAccess.ReadWrite))
        {
            // Overwrite the middle of the file (object_versions' b-tree pages) with noise, keep the header.
            var noise = new byte[4096 * 4];
            new Random(1).NextBytes(noise);
            fs.Seek(fs.Length / 2 / 4096 * 4096, SeekOrigin.Begin);
            fs.Write(noise);
        }
        using var again = Open();
        Assert.NotNull(again.QuarantinedTo);
        Assert.NotNull(again.RecoveryRequired);
        Assert.Equal(0, again.Read(tx => tx.CountVersions("b", "T")));
    }

    [Fact]
    public void AVerifyPassFindsChangedNewAndGoneObjectsWithoutOrdering()
    {
        using var db = Open();
        db.Write(tx => { tx.SetVersion("b", "T", "a", "v1"); tx.SetVersion("b", "T", "b", "v1"); tx.SetVersion("b", "T", "gone", "v1"); });
        // 1C returns keys in its own ref order; pages in any order must give the same answer.
        var changed = db.Write(tx => tx.MarkSeen("b", "T", 7, new[] { ("new", (string?)"v1"), ("b", "v2") }));
        changed.AddRange(db.Write(tx => tx.MarkSeen("b", "T", 7, new[] { ("a", (string?)"v1") })));
        Assert.Equal(new[] { "new", "b" }, changed);
        Assert.Equal(new[] { "gone" }, db.Read(tx => tx.UnseenKeys("b", "T", 7, 100)));
    }

    [Fact]
    public void AnIndependentRegisterRefreshUploadsOnlyChangedRowsAndFindsRemovedOnes()
    {
        using var db = Open();
        db.Write(tx => { tx.SetRegisterRow("b", "R", "k1", "h1", 1); tx.SetRegisterRow("b", "R", "k2", "h2", 1); tx.SetRegisterRow("b", "R", "k3", "h3", 1); });
        var changed = db.Write(tx => tx.DiffRegisterRows("b", "R", 2, new[] { ("k1", "h1"), ("k2", "h2x"), ("k4", "h4") }));
        Assert.Equal(new[] { "k2", "k4" }, changed);
        db.Write(tx => { tx.SetRegisterRow("b", "R", "k2", "h2x", 2); tx.SetRegisterRow("b", "R", "k4", "h4", 2); });
        Assert.Equal(new[] { "k3" }, db.Read(tx => tx.UnseenRegisterRows("b", "R", 2, 100)));
    }

    [Fact]
    public void ASliceCheckpointMovesPositionAndCountTogether()
    {
        using var db = Open();
        db.Write(tx => tx.UpsertSlice(new SnapshotSlice("b", "T", 1, 0, "2020-01-01", "2021-01-01", null, null, 0, false)));
        db.Write(tx => tx.CheckpointSlice("b", "T", 1, 0, "ref-500", 0, 500, false));
        db.FaultHook = p => { if (p == "commit") throw new IOException("crash"); };
        Assert.Throws<IOException>(() => db.Write(tx => tx.CheckpointSlice("b", "T", 1, 0, "ref-1000", 0, 500, false)));
        db.FaultHook = null;
        var s = Assert.Single(db.Read(tx => tx.GetSlices("b", "T", 1)));
        Assert.Equal("ref-500", s.Cursor);
        Assert.Equal(500, s.RowsDone);
    }

    [Fact]
    public void ModesAndPartitionsAndRunsRoundTrip()
    {
        using var db = Open();
        db.Write(tx =>
        {
            tx.UpsertBase(new SyncBaseRow("b", "stub", "conn-1", "501", SyncModes.Snapshot, null, false, "h", _now));
            tx.SetMode("b", SyncModes.Incremental, "snapshot done");
            tx.AddPartition("b", "doc", "p-org1");
            tx.AddPartition("b", "doc", "p-org1");
            tx.AddPartition("b", "doc", "p-org2");
            tx.CountUnmappedOrg("b", "org-x", "T", 3);
            tx.CountUnmappedOrg("b", "org-x", "T", 4);
        });
        var b = db.Read(tx => tx.GetBase("b"))!;
        Assert.Equal(SyncModes.Incremental, b.Mode);
        Assert.Equal(new[] { "p-org1", "p-org2" }, db.Read(tx => tx.GetPartitions("b", "doc")));
        Assert.Equal(7, db.Read(tx => tx.UnmappedOrgs("b")).Single().Rows);
        Assert.Throws<InvalidOperationException>(() => db.Write(tx => tx.SetMode("nope", SyncModes.Paused, null)));

        long old = db.Write(tx => tx.StartRun("b", "snapshot"));
        db.Write(tx => tx.FinishRun(old, "ok", 10, 10, 1000));
        _now += TimeSpan.FromDays(40);
        long fresh = db.Write(tx => tx.StartRun("b", "incremental"));
        db.Write(tx => tx.FinishRun(fresh, "ok", 1, 1, 10));
        Assert.Equal(1, db.Write(tx => tx.PruneRuns(TimeSpan.FromDays(30))));
        Assert.Equal(fresh, db.Read(tx => tx.Runs("b")).Single().Id);
    }

    [Fact]
    public void TenThousandWorkItemsInBatchesOf500TakeUnderASecond()
    {
        using var db = Open();
        var sw = Stopwatch.StartNew();
        for (int batch = 0; batch < 20; batch++)
            db.Write(tx =>
            {
                for (int i = 0; i < 500; i++) tx.UpsertWork(Req($"k{batch * 500 + i}", WorkFlags.Changed));
                tx.SetCursor("b", "c" + batch, null);
            });
        sw.Stop();
        Assert.Equal(10_000, db.Read(tx => tx.CountWork("b")));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"10 000 upserts took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void CommittedWorkSurvivesAProcessKill()
    {
        string child = Path.Combine(AppContext.BaseDirectory, "SyncStateChild.dll");
        Assert.True(File.Exists(child), child);
        using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{child}\" \"{DbPath}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        })!;
        int last = 0;
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
                if (line.StartsWith("committed ") && (last = int.Parse(line[10..])) >= 200) break;
        }
        finally { p.Kill(); p.WaitForExit(10_000); }
        Assert.True(last >= 200);
        using var db = Open();
        Assert.Null(db.QuarantinedTo);
        long items = db.Read(tx => tx.CountWork("b"));
        string cursor = db.Read(tx => tx.GetCursor("b"))!.Cursor!;
        // Everything announced is there, and the cursor matches the items exactly (never ahead).
        Assert.True(items >= last, $"{items} items after {last} announced commits");
        Assert.Equal("cursor-" + items, cursor);
    }
}
