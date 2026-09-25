using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OneC.Host;
using OneC.Ipc;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>Milestone 5.5 — cold-read slices, without 1C.</summary>
public class SlicePlannerUnitTests
{
    private static List<(DateTime, long)> Days(params long[] counts) =>
        counts.Select((n, i) => (new DateTime(2025, 1, 1).AddDays(i * 2), n)).ToList();

    [Fact]
    public void SlicesAreBalancedAndCoverTheRangeWithoutGaps()
    {
        var days = Days(10, 10, 10, 10, 10, 10, 10, 10);
        var from = new DateTime(2024, 12, 1);
        var to = new DateTime(2025, 2, 1);
        var s = SlicePlanner.Cut(days, 4, from, to);

        Assert.Equal(new long[] { 20, 20, 20, 20 }, s.Select(x => x.Rows));
        Assert.Equal(from, s[0].From);
        Assert.Equal(to, s[^1].To);
        for (int i = 1; i < s.Count; i++) Assert.Equal(s[i - 1].To, s[i].From);   // no gap, no overlap
    }

    [Fact]
    public void AHeavyDayIsNeverSplitAndSparseRangesGiveFewerSlices()
    {
        var s = SlicePlanner.Cut(Days(1, 1000, 1), 4, null, null);
        Assert.Equal(1002, s.Sum(x => x.Rows));
        Assert.True(s.Count <= 3);
        Assert.Contains(s, x => x.Rows >= 1000);
        Assert.Null(s[^1].To);                                                   // open end stays open

        Assert.Single(SlicePlanner.Cut(Days(5), 8, null, null));
        Assert.Empty(SlicePlanner.Cut(new List<(DateTime, long)>(), 4, null, null));
    }
}

[Collection("onec-live")]
public class ColdReadLiveTests
{
    private readonly LiveFixture _f;
    public ColdReadLiveTests(LiveFixture f) => _f = f;
    private SessionManager M => _f.Manager!;

    /// <summary>
    /// Slices walked on concurrent sessions return exactly the rows of one walk over the whole
    /// range — the exclusive upper bound leaves no second in two slices (D34 #6).
    /// </summary>
    [Fact]
    public void RegisterSlicesReadConcurrentlyEqualOneWalk()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        var svc = new RegisterReadService(M);
        var anchor = svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 1, Offset = 1500, SkipTotal = true });
        var from = DateTime.Parse((string)anchor.Rows[0]["Период"]!).Date;
        var to = from.AddDays(2);

        var slices = new SlicePlanner(M).Plan(b, "РегистрБухгалтерии.Хозрасчетный", "Период", 3, from, to);
        var parts = new ConcurrentBag<string>();
        Parallel.ForEach(slices, new ParallelOptions { MaxDegreeOfParallelism = 2 }, s =>
        {
            foreach (var r in Walk(svc, b, s.From, s.To!.Value)) parts.Add($"{r["recorderRef"]}#{r["lineNo"]}");
        });
        var whole = Walk(svc, b, from, to).Select(r => $"{r["recorderRef"]}#{r["lineNo"]}").ToList();

        Assert.True(whole.Count >= 50, $"range too small ({whole.Count})");
        Assert.Equal(whole.Count, parts.Count);
        Assert.Equal(whole.Order(), parts.Order());
        Assert.Equal(whole.Count, slices.Sum(s => s.Rows));                      // the plan's counts are exact
    }

    [Fact]
    public void DocumentSlicesReadConcurrentlyEqualOneWalk()
    {
        if (!_f.Available || _f.Server is null) return;
        string b = _f.Server.Name;
        const string Doc = "РеализацияТоваровУслуг";
        var svc = new DocumentReadService(M);
        var anchor = svc.List(b, new DocumentQuery { Document = Doc, Limit = 1, Offset = 300, SkipTotal = true, Tabular = false });
        var to = DateTime.Parse((string)anchor.Rows[0]["date"]!).Date.AddDays(1);
        var from = to.AddDays(-20);

        var slices = new SlicePlanner(M).Plan(b, "Документ." + Doc, "Дата", 3, from, to);
        var parts = new ConcurrentBag<string>();
        Parallel.ForEach(slices, new ParallelOptions { MaxDegreeOfParallelism = 2 }, s =>
        {
            foreach (var id in Ids(svc, b, Doc, s.From, s.To)) parts.Add(id);
        });
        var whole = Ids(svc, b, Doc, from, to);

        Assert.True(whole.Count >= 30, $"range too small ({whole.Count})");
        Assert.Equal(whole.Order(), parts.Order());
        Assert.Equal(whole.Count, slices.Sum(s => s.Rows));
    }

    [Fact]
    public void SlicesOpAnswersThroughOperations()
    {
        if (!_f.Available || _f.Server is null) return;
        var ops = new Operations(M);
        var resp = ops.Execute(new IpcRequest
        {
            Op = Ops.Slices, Base = _f.Server.Name,
            Args = new JsonObject { ["kind"] = "accounting", ["name"] = "Хозрасчетный", ["parts"] = 3, ["from"] = "2025-01-01", ["to"] = "2025-02-01" }
        }, CancellationToken.None);
        Assert.True(resp.Ok, resp.Error?.Message);
        var slices = resp.Result!["slices"]!.AsArray();
        Assert.InRange(slices.Count, 1, 3);
        Assert.StartsWith("2025-01-01", slices[0]!["from"]!.GetValue<string>());

        var bad = ops.Execute(new IpcRequest { Op = Ops.Slices, Base = _f.Server.Name, Args = new JsonObject { ["kind"] = "x", ["name"] = "y" } }, CancellationToken.None);
        Assert.Equal(Layers.Validation, bad.Error!.Layer);
    }

    private static List<Dictionary<string, object?>> Walk(RegisterReadService svc, string b, DateTime from, DateTime to)
    {
        var rows = new List<Dictionary<string, object?>>();
        DateTime cursor = from; int skip = 0;
        while (true)
        {
            var p = svc.List(b, new RegisterQuery { Kind = RegisterKind.Accounting, Register = "Хозрасчетный", Limit = 250, Order = "asc", CursorDate = cursor, Offset = skip, To = to, SkipTotal = true });
            rows.AddRange(p.Rows);
            if (!p.HasMore || p.NextCursorDate is null) return rows;
            cursor = DateTime.Parse(p.NextCursorDate); skip = p.NextCursorSkip;
        }
    }

    private static List<string> Ids(DocumentReadService svc, string b, string doc, DateTime from, DateTime? to)
    {
        var ids = new List<string>();
        string? after = OneC.Host.LegacyValue.EmptyGuid;
        while (after is not null)
        {
            var p = svc.List(b, new DocumentQuery { Document = doc, Limit = 200, After = after, From = from, To = to, SkipTotal = true, Tabular = false, Fields = Array.Empty<string>() });
            ids.AddRange(p.Rows.Select(r => (string)r["id"]!));
            after = p.Next;
        }
        return ids;
    }
}
