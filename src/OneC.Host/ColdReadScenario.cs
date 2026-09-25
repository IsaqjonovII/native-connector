using System.Collections.Concurrent;
using System.Diagnostics;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host coldread</c>: a register range read by K concurrent cursor walks over balanced
/// period slices (<see cref="SlicePlanner"/>), one pool session each, for K = 1, 2, 4 … — wall
/// time, rows/s, resources, and a check that every K returns exactly the rows K = 1 returned.
/// </summary>
internal static class ColdReadScenario
{
    public static int Run(SessionManager m, string b, RegisterKind kind, string name, DateTime from, DateTime to, int page, int[] workers)
    {
        var planner = new SlicePlanner(m);
        var svc = new RegisterReadService(m);
        string table = $"{RegisterSchemas.Prefix(kind)}.{name}";
        HashSet<string>? reference = null;
        int failures = 0;

        foreach (int k in workers)
        {
            var sw = Stopwatch.StartNew();
            var slices = planner.Plan(b, table, "Период", k, from, to);
            long planMs = sw.ElapsedMilliseconds;
            var keys = new ConcurrentBag<string>();
            sw.Restart();
            Parallel.ForEach(slices, new ParallelOptions { MaxDegreeOfParallelism = k }, s =>
            {
                DateTime cursor = s.From; int skip = 0;
                while (true)
                {
                    var p = svc.List(b, new RegisterQuery
                    {
                        Kind = kind, Register = name, Limit = page, Order = "asc", CursorDate = cursor, Offset = skip,
                        To = s.To ?? to, SkipTotal = true
                    });
                    foreach (var r in p.Rows) keys.Add(Key(r));
                    if (!p.HasMore || p.NextCursorDate is null) break;
                    cursor = DateTime.Parse(p.NextCursorDate); skip = p.NextCursorSkip;
                }
            });
            long ms = sw.ElapsedMilliseconds;
            var set = keys.ToHashSet();
            string check;
            if (reference is null) { reference = set; check = "reference"; }
            else check = set.SetEquals(reference) && keys.Count == reference.Count ? "same rows as K=1" : $"DIFFERENT: {keys.Count} rows, {set.Count} distinct vs {reference.Count}";
            if (check.StartsWith("DIFFERENT")) failures++;
            Console.WriteLine($"K={k}: slices [{string.Join(", ", slices.Select(s => s.Rows))}] planned in {planMs} ms; " +
                              $"{keys.Count} rows in {ms} ms = {(ms == 0 ? 0 : keys.Count * 1000L / ms)} rows/s; {check}");
            Console.WriteLine("  " + ResourceSampler.Take($"after K={k}", m.BudgetUsed).Line());
        }
        return failures;
    }

    /// <summary>A register row's identity: recorder GUID + line, or the whole row for independent registers.</summary>
    private static string Key(Dictionary<string, object?> r) =>
        r.TryGetValue("recorderRef", out var rr) ? $"{rr}#{r["lineNo"]}"
        : r.TryGetValue("Регистратор", out var reg) ? $"{reg}#{r.GetValueOrDefault("НомерСтроки")}#{r.GetValueOrDefault("Период")}"
        : System.Text.Json.JsonSerializer.Serialize(r);
}
