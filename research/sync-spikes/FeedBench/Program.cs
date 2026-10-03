using System.Diagnostics;
using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Incremental;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;

// FeedBench <logDir> <file.lgp> — replays that file from offset 0 into a temp sync.db.
string logDir = args[0], file = args[1];
string dbPath = Path.Combine(Path.GetTempPath(), $"aiba-feedbench-{Guid.NewGuid():N}.db");
try
{
    using var db = SyncDb.Open(dbPath);
    var reader = new EventLogReader();
    var guid = reader.Handshake(logDir).LgfGuid;
    db.Write(tx => tx.SetCursor("kan", new LogCursor(guid, file, 0).ToString(), logDir));
    // A realistic KAN sync config: the documents, catalogs and registers the old Connector syncs most.
    var tables = new List<TablePlan>();
    foreach (var d in new[] { "ПоступлениеТоваровУслуг", "РеализацияТоваровУслуг", "ПлатежноеПоручениеВходящее", "ПлатежноеПоручениеИсходящее",
                              "СчетФактураВыданный", "СчетФактураПолученный", "ОперацияБух", "ЗаказПокупателя", "АвансовыйОтчет" })
        tables.Add(new TablePlan("Document_" + d, d, Families.Document, true));
    foreach (var c in new[] { "Номенклатура", "Контрагенты", "ДоговорыКонтрагентов", "БанковскиеСчета", "НомераГТД" })
        tables.Add(new TablePlan("Catalog_" + c, c, Families.Catalog, false));
    tables.Add(new TablePlan("AccountingRegister_Хозрасчетный", "Хозрасчетный", Families.AccountingRegister, true));
    foreach (var r in new[] { "НДСПродажи", "ДенежныеСредства", "ВзаиморасчетыСРаботникамиОрганизаций" })
        tables.Add(new TablePlan("AccumulationRegister_" + r, r, Families.AccumulationRegister, true));
    tables.Add(new TablePlan("InformationRegister_ЦеныНоменклатуры", "ЦеныНоменклатуры", Families.RecordedInfoRegister, true));
    tables.Add(new TablePlan("InformationRegister_СтатусыДокументов", "СтатусыДокументов", Families.IndependentInfoRegister, false));
    var map = new TableMap(tables);

    var feed = new FeedService(db, reader, new SyncBudgets { PendingWorkHighWater = int.MaxValue, PendingWorkLowWater = int.MaxValue - 1 });
    var p = Process.GetCurrentProcess();
    long peak = 0;
    var sw = Stopwatch.StartNew();
    int reads = 0, events = 0, items = 0;
    while (true)
    {
        var d = feed.Drain("kan", logDir, map);
        reads += d.Reads; events += d.Events; items += d.Items;
        p.Refresh(); peak = Math.Max(peak, p.PrivateMemorySize64);
        var c = LogCursor.Parse(db.Read(tx => tx.GetCursor("kan"))!.Cursor!);
        if (c.File != file || d.Reset) { Console.WriteLine($"stopped at {c} reset={d.Reset} {d.ResetReason}"); break; }
        if (d.Reads <= 1 && d.Events == 0) break;
    }
    long len = new FileInfo(Path.Combine(logDir, file)).Length;
    var byKind = db.Read(tx => tx.PeekWork("kan", 1_000_000)).GroupBy(w => w.Kind).Select(g => $"{g.Key} {g.Count()}");
    Console.WriteLine($"{file}: {len / 1048576} MB in {sw.Elapsed.TotalSeconds:F1} s = {len / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MB/s; " +
                      $"{reads} reads, {events} data events → {items} upserts, pending items {db.Read(tx => tx.CountWork("kan"))} ({string.Join(", ", byKind)})");
    Console.WriteLine($"peak private {peak / 1048576} MB; reader unknown ids {reader.UnknownIds}; sync.db {new FileInfo(dbPath).Length / 1048576.0:F1} MB");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*")) File.Delete(f);
}
