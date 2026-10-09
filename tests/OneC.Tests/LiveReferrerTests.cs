using OneC.Host;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// ReferrerSearch against a real 1C (read-only): the counterparty of a real ПоступлениеТоваровУслуг must find
/// that document. The fake-based SyncRenameTests passed while the first live run failed on a COM member the
/// external connection does not expose (ТипЗнч, 2026-10-09) — so this runs in the gate on both live bases.
/// </summary>
[Collection("onec-live")]
public class LiveReferrerTests
{
    private readonly LiveFixture _f;
    public LiveReferrerTests(LiveFixture f) => _f = f;

    [Fact]
    public void ACounterpartyFindsTheDocumentsThatShowIt()
    {
        if (!_f.Available) { Assert.True(true, _f.Skip); return; }
        foreach (var b in new[] { _f.File, _f.Server }.OfType<OneCBase>())
        {
            var docs = new ReadService(_f.Manager!).Read(b.Name, new ReadQuery
            {
                Entity = "Документ.ПоступлениеТоваровУслуг", Fields = new[] { "Ссылка", "Контрагент" }, Limit = 50,
                OrderBy = "Дата", Descending = true, Refs = RefMode.Guid
            });
            var row = docs.Rows.FirstOrDefault(r => r["Контрагент"] is string g && g.Length == 36 && g != Guid.Empty.ToString());
            Assert.True(row is not null, $"{b.Name}: no ПоступлениеТоваровУслуг with a counterparty to search for");
            string doc = (string)row!["Ссылка"]!, cp = (string)row["Контрагент"]!;

            var (hits, truncated) = new ReferrerSearch(_f.Manager!).Find(b.Name, "Контрагенты", cp, new[]
            {
                new ReferrerSearch.Target("document", "ПоступлениеТоваровУслуг", null),
                new ReferrerSearch.Target("catalog", "Банки", null),                        // no column can hold a counterparty
                new ReferrerSearch.Target("accounting", "Хозрасчетный", DateTime.Today.AddDays(-7))
            }, 10_000);

            Assert.False(truncated);
            Assert.Contains(hits, h => h.Kind == "document" && h.Name == "ПоступлениеТоваровУслуг" &&
                                       string.Equals(h.Id, doc, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(hits, h => h.Name == "Банки");
            Assert.All(hits, h => Assert.True(Guid.TryParse(h.Id, out _), h.Id));
        }
    }
}
