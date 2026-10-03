using System.Diagnostics;
using OneC.SyncState;

// StoreBench [dir] — the dir defaults to %LOCALAPPDATA%\AIBA\Connector\sync-bench (the real store's disk).
string dir = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIBA", "Connector", "sync-bench");
Directory.CreateDirectory(dir);
string path = Path.Combine(dir, $"bench-{Guid.NewGuid():N}.db");
try
{
    using var db = SyncDb.Open(path);

    // 1. Small commits (one cursor + one item): the latency the feed and executor pay per transaction.
    var lat = new List<double>();
    for (int i = 0; i < 500; i++)
    {
        var sw = Stopwatch.StartNew();
        db.Write(tx => { tx.UpsertWork(new WorkRequest("b", "T", "k" + i, WorkFlags.Changed, 1)); tx.SetCursor("b", "c" + i, null); });
        lat.Add(sw.Elapsed.TotalMilliseconds);
    }
    lat.Sort();
    Console.WriteLine($"small commit (synchronous=FULL, WAL): p50 {lat[250]:F2} ms, p95 {lat[475]:F2} ms, p99 {lat[495]:F2} ms, max {lat[^1]:F2} ms");

    // 2. Batches of 500 upserts + cursor.
    var sw2 = Stopwatch.StartNew();
    for (int b = 0; b < 20; b++)
        db.Write(tx => { for (int i = 0; i < 500; i++) tx.UpsertWork(new WorkRequest("b", "U", $"k{b}-{i}", WorkFlags.Changed, 1)); tx.SetCursor("b", "x" + b, null); });
    Console.WriteLine($"10 000 upserts in 20 batches: {sw2.ElapsedMilliseconds} ms");

    // 3. 1 M object_versions, 5 000 per transaction (what a snapshot of a 1 M-row table writes).
    var sw3 = Stopwatch.StartNew();
    for (int b = 0; b < 200; b++)
        db.Write(tx => { for (int i = 0; i < 5000; i++) tx.SetVersion("b", "Документ.РеализацияТоваровУслуг", Guid.NewGuid().ToString("D"), "AAAAAAAAAAE="); });
    long ms = sw3.ElapsedMilliseconds;
    db.Checkpoint();
    long bytes = new FileInfo(path).Length;
    Console.WriteLine($"1 000 000 object_versions: {ms} ms ({1_000_000L * 1000 / Math.Max(ms, 1)} rows/s), file {bytes / 1048576.0:F0} MB = {bytes / 1_000_000.0:F0} bytes/object");

    // 4. A verify page against 1 M stored versions: 5 000 lookups + stamps.
    var keys = db.Read(tx => tx.UnseenKeys("b", "Документ.РеализацияТоваровУслуг", 1, 5000));
    var sw4 = Stopwatch.StartNew();
    db.Write(tx => tx.MarkSeen("b", "Документ.РеализацияТоваровУслуг", 1, keys.Select(k => (k, (string?)"AAAAAAAAAAE=")).ToList()));
    Console.WriteLine($"verify page of 5 000 against 1 M: {sw4.ElapsedMilliseconds} ms");
    var p = Process.GetCurrentProcess();
    Console.WriteLine($"process: ws {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + "*")) File.Delete(f);
}
