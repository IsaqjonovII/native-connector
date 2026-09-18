using System.Diagnostics;

namespace OneC.Research;

public sealed class OpResult
{
    public long Ms;
    public bool Ok;
    public string ErrKind;      // com | onec | lock | number | other
    public string ErrText;
    public string Number;       // document number, when the op created one
}

/// <summary>Latency percentiles, throughput, failure breakdown, plus process RSS/CPU.</summary>
public sealed class Run
{
    private readonly List<OpResult> _ops = new();
    private readonly object _lock = new();

    public string Label { get; init; } = "";
    public int K { get; init; }
    public long WallMs { get; set; }
    public long PeakRssMb { get; set; }
    public double CpuPercent { get; set; }
    public int SessionCount { get; set; }
    public long ConnectTotalMs { get; set; }

    public void Add(OpResult r) { lock (_lock) _ops.Add(r); }

    public int Total { get { lock (_lock) return _ops.Count; } }
    public int Failures { get { lock (_lock) return _ops.Count(o => !o.Ok); } }
    public int Ok => Total - Failures;

    public double OpsPerSec => WallMs <= 0 ? 0 : Ok * 1000.0 / WallMs;

    private long Pct(double p)
    {
        lock (_lock)
        {
            var good = _ops.Where(o => o.Ok).Select(o => o.Ms).OrderBy(x => x).ToList();
            if (good.Count == 0) return -1;
            int i = (int)Math.Ceiling(p / 100.0 * good.Count) - 1;
            return good[Math.Clamp(i, 0, good.Count - 1)];
        }
    }

    public long P50 => Pct(50);
    public long P95 => Pct(95);
    public long Max { get { lock (_lock) { var g = _ops.Where(o => o.Ok).Select(o => o.Ms).ToList(); return g.Count == 0 ? -1 : g.Max(); } } }

    public Dictionary<string, int> ErrorKinds()
    {
        lock (_lock)
            return _ops.Where(o => !o.Ok)
                       .GroupBy(o => o.ErrKind ?? "other")
                       .ToDictionary(g => g.Key, g => g.Count());
    }

    public List<string> SampleErrors(int n = 3)
    {
        lock (_lock)
            return _ops.Where(o => !o.Ok && o.ErrText != null)
                       .Select(o => o.ErrText).Distinct().Take(n).ToList();
    }

    public string Line()
    {
        var errs = Failures == 0 ? "" :
            "  ERR " + string.Join(",", ErrorKinds().Select(kv => $"{kv.Key}={kv.Value}"));
        return $"  K={K,-2} ops={Ok,-5} fail={Failures,-3} wall={WallMs,6}ms  " +
               $"{OpsPerSec,7:F1} op/s  p50={P50,-5} p95={P95,-6} max={Max,-6} " +
               $"rss={PeakRssMb}MB cpu={CpuPercent:F0}%{errs}";
    }
}

public static class Proc
{
    public static (long rssMb, TimeSpan cpu) Snapshot()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return (p.WorkingSet64 / 1024 / 1024, p.TotalProcessorTime);
    }

    /// <summary>Classify a failure so contention shows up separately from plumbing bugs.</summary>
    public static (string kind, string text) Classify(Exception ex)
    {
        string msg = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        string low = msg.ToLowerInvariant();
        string kind =
            low.Contains("заблокирован") || low.Contains("блокировк") || low.Contains("lock") ? "lock"
            : low.Contains("номер") || low.Contains("number") ? "number"
            : low.Contains("timeout") || low.Contains("таймаут") ? "timeout"
            : ex is System.Runtime.InteropServices.COMException ? "com"
            : low.Contains("1с") || low.Contains("1c") ? "onec"
            : "other";
        if (msg.Length > 180) msg = msg[..180];
        // A .NET-side exception is a harness bug, not 1C contention — keep the first
        // frame so the two are never confused in the results.
        string frame = "";
        if (ex is NullReferenceException or InvalidOperationException or ArgumentException)
        {
            var f = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(f)) frame = "  @ " + (f.Length > 120 ? f[..120] : f);
        }
        return (kind, ex.GetType().Name + ": " + msg + frame);
    }
}
