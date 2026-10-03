using System.Diagnostics;
using System.Globalization;
using OneC.Sessions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Scheduling;
using OneC.Sync.Snapshot;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Supervisor;

/// <summary>
/// <c>OneC.Supervisor sync-snapshot</c> — sync block S5 live check: one table's cold snapshot
/// from a real base into the in-memory stub target (v2 emulation), with K sync sessions. Prints
/// rows, rows/s and the peak memory of this process and the host. Nothing leaves the machine;
/// the state database is a temp file.
///   --bases f.json --base N --table Family:Name [--k 1] [--from yyyy-MM-dd] [--page 500]
/// Family: catalog | document | chart | reg_accounting | reg_accumulation | reg_info_recorded | reg_info_independent
/// </summary>
internal static class SyncBench
{
    public static async Task<int> Run(List<OneCBase> bases, SupervisorOptions opt, string baseName, string table, int k, DateTime? from, int page)
    {
        var parts = table.Split(':', 2);
        var plan = new TablePlan($"{parts[0]}_{parts[1]}", parts[1], parts[0], parts[0] is not (Families.Catalog or Families.Chart), from);
        var b = bases.First(x => x.Name == baseName);
        using var sup = new Supervisor(opt);
        sup.Start(new[] { b });
        var host = sup.HostFor(baseName);
        if (!host.WaitReady(TimeSpan.FromMinutes(3))) { Console.Error.WriteLine("host not ready: " + host.LastError); return 1; }

        string dbPath = Path.Combine(Path.GetTempPath(), $"aiba-sync-bench-{Guid.NewGuid():N}.db");
        try
        {
            using var db = SyncDb.Open(dbPath);
            var budgets = new SyncBudgets { SyncSessionsPerServerBase = k, SyncSessionsPerFileBase = Math.Min(k, 1), SyncSessionsGlobal = Math.Max(6, k) };
            // The in-memory stub, or — with AIBA_SYNC_BACKEND set — a real backend/1c: only ever the
            // isolated local instance started for S12 (its own port and database), never a shared server.
            var stub = new StubSyncTarget(StubMode.V2) { KeepJson = false };
            OneC.Sync.Abstractions.IBackendSyncTarget target = stub;
            string partition = "p";
            OneC.Sync.Targets.Python.PythonMongoSyncTarget? python = null;
            if (Environment.GetEnvironmentVariable("AIBA_SYNC_BACKEND") is { } url)
            {
                if (!new Uri(url).IsLoopback) { Console.Error.WriteLine("AIBA_SYNC_BACKEND must be a loopback address (S12: local instance only)"); return 1; }
                (python, partition) = await LocalBackend.ConnectAsync(url, Environment.GetEnvironmentVariable("AIBA_SYNC_SECRETS")!, baseName);
                target = python;
            }
            var caps = await target.GetCapabilitiesAsync(CancellationToken.None);
            var gate = new UploadGate(budgets);
            var up = new Uploader(target, gate);
            var reader = new SupervisorReader(sup);
            var runner = new SnapshotRunner(db, reader, up, gate, caps, new OneC.Sync.Incremental.SinglePartition(partition), new SnapshotOptions { PageSize = page });
            var leases = new SyncLeases(budgets, () => DateTimeOffset.UtcNow);
            var activation = new BaseActivation(baseName, b.IsFile, SyncPriority.Snapshot, false, leases, budgets, CancellationToken.None);

            long inOneC = await reader.CountAsync(baseName, plan, CancellationToken.None);
            Console.WriteLine($"{baseName} {plan.Family} {plan.Name}{(from is null ? "" : $" from {from:yyyy-MM-dd}")}: 1C count {inOneC}, K={k}, page {page}");
            if (plan.Family == Families.Catalog && Environment.GetEnvironmentVariable("AIBA_BENCH_READONLY") == "1")
            {
                // The same pages over the same pipe, nothing else: the floor the pipeline is compared with.
                var rw = Stopwatch.StartNew();
                long n = 0;
                string? after = null;
                while (true)
                {
                    var p = await reader.CatalogPageAsync(baseName, plan.Name, after, page, CancellationToken.None);
                    n += p.Rows.Count;
                    if (!p.HasMore) break;
                    after = p.NextAfter;
                }
                Console.WriteLine($"read-only over the pipe: {n} rows, {rw.Elapsed.TotalSeconds:F1} s = {n / rw.Elapsed.TotalSeconds:F0} rows/s");
            }
            var me = Process.GetCurrentProcess();
            long peakPrivate = 0, peakHostWs = 0, peakHostPriv = 0;
            using var cts = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    me.Refresh();
                    peakPrivate = Math.Max(peakPrivate, me.PrivateMemorySize64);
                    var (ws, pr) = host.Memory();
                    peakHostWs = Math.Max(peakHostWs, ws);
                    peakHostPriv = Math.Max(peakHostPriv, pr);
                    try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { }
                }
            });
            var sw = Stopwatch.StartNew();
            var r = await runner.RunTableAsync(activation, plan, 1);
            sw.Stop();
            cts.Cancel();
            await sampler;
            var slices = db.Read(tx => tx.GetSlices(baseName, plan.Table, 1));
            // Read back what the target holds (a reported count is not proof, §18).
            long stored = python is null ? stub.Count("p", plan.Table)
                                         : (await python.CountsAsync(partition, CancellationToken.None)).GetValueOrDefault(plan.Table);
            Console.WriteLine($"outcome {r.Outcome}: read {r.RowsRead}, accepted {r.RowsAccepted}, {(python is null ? "stub" : "backend")} {stored} rows, " +
                              $"{sw.Elapsed.TotalSeconds:F1} s = {r.RowsRead / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} rows/s; slices {slices.Count}");
            Console.WriteLine($"uploaded {up.RowsSent} rows / {up.BytesSent / 1048576.0:F1} MB, v2 count mismatches {up.CountMismatches}, " +
                              $"byte budget peak {gate.Bytes.Peak / 1048576.0:F1} MB, uploads in flight peak {gate.PeakInFlight}");
            Console.WriteLine($"peak memory: supervisor private {peakPrivate / 1048576} MB; host ws {peakHostWs} MB, private {peakHostPriv} MB");
            bool windowed = from is not null && Families.IsRegister(plan.Family);   // register counts are for the whole register
            bool ok = r.Outcome == SnapshotOutcome.Complete && (windowed || stored == inOneC);
            if (!ok) Console.WriteLine("MISMATCH");
            return ok ? 0 : 2;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*")) File.Delete(f);
        }
    }

    public static DateTime? Date(string? s) => s is null ? null : DateTime.Parse(s, CultureInfo.InvariantCulture);
}
