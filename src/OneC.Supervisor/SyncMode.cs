using System.Diagnostics;
using OneC.Sessions;
using OneC.Sync;

namespace OneC.Supervisor;

/// <summary>
/// <c>OneC.Supervisor sync</c>: hosts + a local stub backend (D39) + sync passes, then 1C's row
/// counts next to the stub's. Nothing leaves the machine.
/// </summary>
internal static class SyncMode
{
    public static async Task<int> Run(List<OneCBase> bases, SupervisorOptions opt, string baseName, string[] tableNames,
                                      DateTime? from, string statePath, int passes)
    {
        if (tableNames.Length == 0) { Console.Error.WriteLine("--tables is required"); return 1; }
        var tables = tableNames.Select(n => SyncTable.Parse(n.Trim(), from)).ToList();
        string oneCId = "stub-" + baseName;

        using var sup = new Supervisor(opt);
        sup.Start(bases.Where(b => b.Name == baseName).ToList());
        await using var stub = new StubBackend();
        await stub.StartAsync();
        using var http = new HttpClient { BaseAddress = stub.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };
        var engine = new SyncEngine(new SupervisorSource(sup), new HttpUploadTarget(http, stub.Authorize));
        var source = new SupervisorSource(sup);

        if (File.Exists(statePath)) File.Delete(statePath);             // the stub starts empty, so does the state
        var state = BaseState.Load(statePath);
        for (int pass = 1; pass <= passes; pass++)
        {
            var sw = Stopwatch.StartNew();
            var report = await engine.RunOnceAsync(baseName, oneCId, tables, state, statePath);
            Console.WriteLine($"pass {pass}: {sw.Elapsed.TotalSeconds:F1} s, feed events {report.FeedEvents}{(report.FeedReset ? " (reset)" : "")}");
            foreach (var (t, (u, p, r)) in report.Tables) Console.WriteLine($"  {t}: uploaded {u}, pruned {p}, reconciled {r}");
            foreach (var w in report.Warnings) Console.WriteLine($"  ! {w}");
        }

        int mismatches = 0;
        foreach (var t in tables.Where(t => t.Kind != TableKind.ChartOfAccounts))
        {
            long inOneC = await source.CountAsync(baseName, t, CancellationToken.None);
            int inStub = stub.Count(oneCId, t.Name);
            bool registerWindow = t.IsRegister && from is not null;      // register counts are for the whole register
            bool same = registerWindow || inOneC == inStub;
            if (!same) mismatches++;
            Console.WriteLine($"{t.Name}: 1C {inOneC}{(registerWindow ? " (whole register)" : "")}, stub {inStub}{(same ? "" : "  MISMATCH")}");
        }
        Console.WriteLine($"stub uploads: {stub.Uploads.Count}, bytes {stub.Uploads.Sum(u => (long)u.Bytes)}");
        return mismatches == 0 ? 0 : 2;
    }
}
