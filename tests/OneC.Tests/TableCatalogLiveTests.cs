using System.Diagnostics;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>"Add table": the configuration's table list, read live (timed, since the desktop waits on it).</summary>
[Collection("onec-live")]
public class TableCatalogLiveTests
{
    private readonly LiveFixture _f;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public TableCatalogLiveTests(LiveFixture f, Xunit.Abstractions.ITestOutputHelper output) { _f = f; _out = output; }

    [Fact]
    public void ListHasEveryKindAndDetailsResolveFamilies()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.Server, _f.File }.OfType<OneCBase>())
        {
            SchemaCache.Forget(b.Name);
            var catalog = new TableCatalog(_f.Manager!);
            var sw = Stopwatch.StartNew();
            var list = catalog.List(b.Name);
            _out.WriteLine($"{b.Name}: {list.Count} tables in {sw.ElapsedMilliseconds} ms; " +
                           string.Join(", ", list.GroupBy(t => t.Family).Select(g => $"{g.Key} {g.Count()}")));
            Assert.Contains(list, t => t.Table == "Catalog_Контрагенты");
            Assert.Contains(list, t => t.Table == "AccountingRegister_Хозрасчетный");

            sw.Restart();
            var infoRegs = list.Where(t => t.Family == "reg_info").Take(40).Select(t => t.Table).ToList();
            var details = catalog.Details(b.Name, new[] { "Document_ПоступлениеТоваровУслуг", "Catalog_Контрагенты", "Catalog_НетТакого" }.Concat(infoRegs).ToList());
            _out.WriteLine($"  details in {sw.ElapsedMilliseconds} ms: " + string.Join("; ", details.Take(6).Select(d => $"{d.Table} {d.Family} {d.IsMovement}")));
            Assert.Equal(2 + infoRegs.Count, details.Count);
            Assert.True(details.Single(d => d.Family == "document").IsMovement);
            // Both ways an information register is written show up among the first 40 of a real configuration.
            Assert.Contains(details, d => d.Family == "reg_info_recorded" && d.IsMovement);
            Assert.Contains(details, d => d.Family == "reg_info_independent" && !d.IsMovement);
        }
    }

    /// <summary>Where the time of a full metadata walk goes, per collection and per COM read.</summary>
    [Fact]
    public void MetadataWalkTimings()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.Server, _f.File }.OfType<OneCBase>())
            _f.Manager!.Use(b.Name, c =>
            {
                using var s = new ComScope();
                var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
                foreach (var col in new[] { "Справочники", "Документы", "РегистрыСведений" })
                {
                    var items = s.Track(Dispatch.Get(md, col, c.Error), col);
                    int n = Dispatch.CallInt(items, "Количество", c.Error);
                    long get = 0, name = 0, syn = 0;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < n; i++)
                    {
                        using var es = new ComScope();
                        long t0 = sw.ElapsedTicks;
                        var o = es.Track(Dispatch.Call(items, "Получить", c.Error, i), col);
                        long t1 = sw.ElapsedTicks;
                        Dispatch.GetString(o, "Имя", c.Error);
                        long t2 = sw.ElapsedTicks;
                        Dispatch.GetString(o, "Синоним", c.Error);
                        get += t1 - t0; name += t2 - t1; syn += sw.ElapsedTicks - t2;
                    }
                    double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
                    _out.WriteLine($"{b.Name} {col}: {n} objects, {sw.ElapsedMilliseconds} ms — Получить {ms(get):0} ms, Имя {ms(name):0} ms, Синоним {ms(syn):0} ms");
                }
                return 0;
            });
    }
}
