using System.Text;
using Microsoft.Data.Sqlite;
using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using Xunit;

namespace OneC.Tests;

/// <summary>Sync block S6 against real bases: a test-owned document's events become one item.</summary>
[Collection("onec-live")]
public sealed class SyncFeedLiveTests
{
    private readonly LiveFixture _f;
    public SyncFeedLiveTests(LiveFixture f) => _f = f;

    [Fact]
    public void ATestDocumentsWritesBecomeOneWorkItem()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.Server, _f.File }.OfType<OneC.Sessions.OneCBase>())
        {
            var (dir, err) = new LogLocator().Resolve(b.ConnectionString);
            Assert.True(dir is not null, $"{b.Name}: {err}");
            string dbDir = Directory.CreateTempSubdirectory("aiba-feedlive-").FullName;
            try
            {
                using var db = SyncDb.Open(Path.Combine(dbDir, "sync.db"));
                var feed = new FeedService(db, new EventLogReader(), new SyncBudgets());
                var map = new TableMap(new[]
                {
                    new TablePlan("Document_ПоступлениеТоваровУслуг", "ПоступлениеТоваровУслуг", Families.Document, true),
                    new TablePlan("AccountingRegister_Хозрасчетный", "Хозрасчетный", Families.AccountingRegister, true)
                });
                feed.Handshake(b.Name, dir!);
                var w = new OneC.Host.WriteService(_f.Manager!, "AIBA_REWRITE_");
                const string doc = "ПоступлениеТоваровУслуг";
                string comment = $"AIBA_REWRITE_S6_{DateTime.Now:MMddHHmmssfff}";
                string id = w.CreateByClone(b.Name, doc, comment).Ref;
                try
                {
                    w.Update(b.Name, doc, id, new Dictionary<string, object?> { ["Комментарий"] = comment });
                    w.Post(b.Name, doc, id);
                    Thread.Sleep(3000);                                          // the server writes its log after commit
                    feed.Drain(b.Name, dir!, map);
                    var mine = db.Read(tx => tx.PeekWork(b.Name, 100_000)).Where(i => i.ObjectKey == id).ToList();
                    var one = Assert.Single(mine);                               // New + Update + Update + Post + movements: one item
                    Assert.Equal(WorkKinds.SyncRecorder, one.Kind);
                }
                finally { w.Delete(b.Name, doc, id); }
                Thread.Sleep(3000);
                feed.Drain(b.Name, dir!, map);
                var after = Assert.Single(db.Read(tx => tx.PeekWork(b.Name, 100_000)), i => i.ObjectKey == id);
                Assert.Equal(WorkKinds.DeleteObject, after.Kind);                // merged into the pending item, not a second one
                Assert.Empty(w.FindOwned(b.Name, doc, comment));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(dbDir, true); } catch (IOException) { }
            }
        }
    }
}

/// <summary>
/// Sync block S6: feed → coalescer → durable work queue (§5, §6, §11), on synthetic log files
/// in 1C's format. No 1C.
/// </summary>
public sealed class SyncFeedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-feed-").FullName;
    private readonly string _log;
    private readonly SyncDb _db;
    private readonly SyncBudgets _budgets = new();

    private static readonly TablePlan Doc = new("Document_Реализация", "Реализация", Families.Document, true);
    private static readonly TablePlan Cat = new("Catalog_Контрагенты", "Контрагенты", Families.Catalog, false);
    private static readonly TablePlan Acc = new("AccountingRegister_Хозрасчетный", "Хозрасчетный", Families.AccountingRegister, true);
    private static readonly TablePlan Rates = new("InformationRegister_Курсы", "Курсы", Families.IndependentInfoRegister, false);
    private static readonly TableMap Map = new(new[] { Doc, Cat, Acc, Rates });

    // Event ids and metadata ids of the synthetic dictionary.
    private static readonly string[] EventNames = { "_$Data$_.New", "_$Data$_.Update", "_$Data$_.Post", "_$Data$_.Unpost", "_$Data$_.Delete", "_$InfoBase$_.RestoreFinish" };
    private static readonly string[] Metas = { "Документ.Реализация", "Справочник.Контрагенты", "РегистрБухгалтерии.Хозрасчетный",
                                               "РегистрСведений.Курсы", "Документ.НеНаш", "Справочник.НеНаш", "РегистрНакопления.НеНаш" };

    public SyncFeedTests()
    {
        _log = Path.Combine(_dir, "1Cv8Log");
        Directory.CreateDirectory(_log);
        var lgf = new StringBuilder("﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n");
        for (int i = 0; i < EventNames.Length; i++) lgf.Append($"{{4,\"{EventNames[i]}\",{i + 1}}},\r\n");
        for (int i = 0; i < Metas.Length; i++) lgf.Append($"{{5,{Guid.NewGuid()},\"{Metas[i]}\",{i + 1}}},\r\n");
        File.WriteAllText(Path.Combine(_log, "1Cv8.lgf"), lgf.ToString(), new UTF8Encoding(false));
        _db = SyncDb.Open(Path.Combine(_dir, "sync.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string G(int i) => new Guid(i, 0, 0, new byte[8]).ToString("D");

    /// <summary>lgp stores a ref as d4a+d4b+d3+d2+d1 (LogFormat.HexToUuid).</summary>
    private static string Hex(string uuid)
    {
        var p = uuid.Replace("-", "");
        return p[16..20] + p[20..32] + p[12..16] + p[8..12] + p[0..8];
    }

    private static string Record(string ev, string meta, string? guid, string tx = "U") =>
        $"{{20260930120000,{tx},\r\n{{0,0}},1,1,1,1,{Array.IndexOf(EventNames, ev) + 1},I,\"\",{Array.IndexOf(Metas, meta) + 1},\r\n" +
        (guid is null ? "{\"U\"}" : $"{{\"R\",214:{Hex(guid)}}}") + ",\"\",0,0,0,2,0,\r\n{0}\r\n},\r\n";

    private void Append(params string[] records)
    {
        string file = Path.Combine(_log, "20260930000000.lgp");
        if (!File.Exists(file)) File.WriteAllText(file, "﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n", new UTF8Encoding(false));
        File.AppendAllText(file, string.Concat(records), new UTF8Encoding(false));
    }

    private FeedService Feed() => new(_db, new EventLogReader(), _budgets);
    private List<WorkItem> Pending() => _db.Read(tx => tx.PeekWork("b", 10_000));

    [Fact]
    public void TheCoalescingTableOfSection6()
    {
        var events = new List<ChangeEvent>
        {
            new("t", "Update", "Справочник.Контрагенты", G(1)),                    // catalog changed
            new("t", "Update", "Справочник.Контрагенты", G(1)),                    // …twice: one item
            new("t", "New", "Документ.Реализация", G(2)),
            new("t", "Update", "Документ.Реализация", G(2)),                       // document changed only
            new("t", "Post", "Документ.Реализация", G(3)),                         // posted: recorder sync
            new("t", "Update", "РегистрБухгалтерии.Хозрасчетный", G(3)),
            new("t", "Delete", "Документ.Реализация", G(4)),                       // deleted
            new("t", "Update", "РегистрБухгалтерии.Хозрасчетный", G(5)),           // movements of a document type we do not sync (R-9)…
            new("t", "Update", "Документ.НеНаш", G(5)),                            // …its own event names its type
            new("t", "Update", "РегистрБухгалтерии.Хозрасчетный", G(6)),           // recorder type unknown in this batch
            new("t", "Update", "РегистрСведений.Курсы", null),                     // independent register: table refresh
            new("t", "Update", "РегистрСведений.Курсы", null),
            new("t", "Update", "Справочник.НеНаш", G(7)),                          // not configured: nothing
            new("t", "Update", "РегистрНакопления.НеНаш", G(8)),                   // register not configured, recorder unknown: nothing
        };
        var items = EventCoalescer.Coalesce("b", events, Map).ToDictionary(w => (w.Table, w.ObjectKey));
        Assert.Equal(7, items.Count);
        Assert.Equal(WorkFlags.Changed, items[(Cat.Table, G(1))].Flags);
        Assert.Equal(ItemPriority.Catalog, items[(Cat.Table, G(1))].Priority);
        Assert.Equal(WorkKinds.SyncObject, WorkKinds.For(items[(Doc.Table, G(2))].Flags));
        Assert.Equal(WorkKinds.SyncRecorder, WorkKinds.For(items[(Doc.Table, G(3))].Flags));
        Assert.Equal(ItemPriority.Destructive, items[(Doc.Table, G(3))].Priority);
        Assert.Equal(WorkKinds.DeleteObject, WorkKinds.For(items[(Doc.Table, G(4))].Flags));
        Assert.Equal(WorkFlags.Movements, items[("?Документ.НеНаш", G(5))].Flags);
        Assert.Equal(WorkFlags.Movements, items[(EventCoalescer.UnknownRecorderTable, G(6))].Flags);
        Assert.Equal(WorkFlags.Refresh, items[(Rates.Table, Rates.Table)].Flags);
    }

    [Fact]
    public void EventsWrittenAfterTheHandshakeBecomeOneItemPerObject()
    {
        Append(Record("_$Data$_.Update", "Справочник.Контрагенты", G(99)));        // before the handshake: history, not work
        var feed = Feed();
        feed.Handshake("b", _log);
        Append(Record("_$Data$_.New", "Документ.Реализация", G(1)), Record("_$Data$_.Post", "Документ.Реализация", G(1)),
               Record("_$Data$_.Update", "РегистрБухгалтерии.Хозрасчетный", G(1)), Record("_$Data$_.Update", "Справочник.Контрагенты", G(2)),
               Record("_$Data$_.Update", "Справочник.Контрагенты", G(2)));
        var d = feed.Drain("b", _log, Map);
        Assert.Equal((5, 2), (d.Events, d.Items));
        var p = Pending();
        Assert.Equal(new[] { G(1), G(2) }, p.Select(w => w.ObjectKey));             // recorder first (priority 0), then the catalog
        Assert.Equal(WorkKinds.SyncRecorder, p[0].Kind);
        Assert.Equal(0, Feed().Drain("b", _log, Map).Items);                        // nothing new: nothing added, idempotent
    }

    [Fact]
    public void ABaseWithNoLogFileYetStartsFromTheFirstFile()
    {
        var feed = Feed();
        string c = feed.Handshake("b", _log);
        Assert.EndsWith("|*|0", c);
        Assert.Equal(0, feed.Drain("b", _log, Map).Events);                         // still no file: nothing, no error
        Append(Record("_$Data$_.Update", "Справочник.Контрагенты", G(1)));          // the first file appears later
        Assert.Equal(1, feed.Drain("b", _log, Map).Items);
        Assert.EndsWith(".lgp", _db.Read(tx => tx.GetCursor("b"))!.Cursor!.Split('|')[1]);
    }

    [Fact]
    public void TheCursorNeverRunsAheadOfTheItems()
    {
        var feed = Feed();
        string before = feed.Handshake("b", _log);
        Append(Record("_$Data$_.Update", "Справочник.Контрагенты", G(1)));
        _db.FaultHook = p => { if (p == "cursor") throw new IOException("crash between the items and the cursor"); };
        Assert.Throws<IOException>(() => feed.Drain("b", _log, Map));
        _db.FaultHook = null;
        Assert.Empty(Pending());
        Assert.Equal(before, _db.Read(tx => tx.GetCursor("b"))!.Cursor);
        Assert.Equal(1, feed.Drain("b", _log, Map).Items);                          // the same events again, nothing lost
        Assert.Single(Pending());
    }

    [Fact]
    public void TooMuchPendingWorkStopsTheFeedUntilItDrains()
    {
        var budgets = _budgets with { PendingWorkHighWater = 10, PendingWorkLowWater = 4 };
        var feed = new FeedService(_db, new EventLogReader(), budgets) { MaxBytesPerRead = 2_000 };
        feed.Handshake("b", _log);
        Append(Enumerable.Range(1, 40).Select(i => Record("_$Data$_.Update", "Справочник.Контрагенты", G(i))).ToArray());
        var done = new HashSet<string>();
        void Finish(int n)
        {
            for (int i = 0; i < n; i++)
                _db.Write(tx => { var w = tx.ClaimWork("b", 1, TimeSpan.FromMinutes(1)).Single(); done.Add(w.ObjectKey); tx.CompleteWork(w); });
        }

        var d = feed.Drain("b", _log, Map);
        Assert.True(d.Backpressured);
        int held = Pending().Count;
        Assert.InRange(held, 10, 20);                                               // stopped at the high water (one read may overshoot)
        Finish(held - 5);
        Assert.True(feed.Drain("b", _log, Map).Backpressured);                      // 5 left: above the low water, still holding
        Finish(1);
        Assert.True(feed.Drain("b", _log, Map).Items > 0);                          // 4: resumes
        for (int round = 0; round < 100; round++)
        {
            Finish(Pending().Count);
            var r = feed.Drain("b", _log, Map);
            if (!r.Backpressured && Pending().Count == 0) break;
        }
        Assert.Equal(40, done.Count);                                               // every object became work exactly once
    }

    [Fact]
    public void AResetOrARestoreSendsTheBaseToRecoveryAndKeepsTheOldCursor()
    {
        _db.Write(tx => tx.UpsertBase(new SyncBaseRow("b", "stub", "c", null, SyncModes.Incremental, null, false, "h", tx.Now)));
        var feed = Feed();
        Append(Record("_$Data$_.Update", "Справочник.Контрагенты", G(1)));
        string start = feed.Handshake("b", _log);
        Append(Record("_$InfoBase$_.RestoreFinish", "Справочник.Контрагенты", null));
        var d = feed.Drain("b", _log, Map);
        Assert.True(d.Reset);
        Assert.Equal("infobase_restored", d.ResetReason);
        Assert.Equal(SyncModes.Recovery, _db.Read(tx => tx.GetBase("b"))!.Mode);
        Assert.Equal(start, _db.Read(tx => tx.GetCursor("b"))!.Cursor);            // never silently jumps to the tail
        Assert.NotNull(_db.Read(tx => tx.GetMeta(FeedService.RecoveryCursorKey("b"))));

        // The log recreated (new instance GUID): same.
        var lgf = Path.Combine(_log, "1Cv8.lgf");
        File.WriteAllText(lgf, File.ReadAllText(lgf).Replace("11111111-2222", "99999999-2222"), new UTF8Encoding(false));
        Assert.Equal("log_recreated", Feed().Drain("b", _log, Map).ResetReason);
    }
}
