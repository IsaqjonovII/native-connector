using System.Xml.Linq;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Diagnostic only — 1C's own event-log export (<c>ВыгрузитьЖурналРегистрации</c>) as the oracle
/// for the file reader (OneC.EventLog): the platform reads its log, we compare. Committed data
/// events in [from, to], as (ts, kind, metadata, ref).
/// </summary>
internal static class EventLogExport
{
    /// <summary>
    /// <c>OneC.Host logparity</c>: the file reader against 1C's own export over the same window —
    /// tail cursor now, wait, read every record after it (no transaction filter), export
    /// [start, end] through 1C, compare as multisets and tabulate tx letter vs 1C's status.
    /// </summary>
    public static int Parity(SessionManager m, OneCBase b, int waitSeconds, bool write = false, bool rollback = false)
    {
        var (dir, err) = new OneC.EventLog.LogLocator().Resolve(b.ConnectionString);
        Console.WriteLine($"{b.Name}: log dir {dir ?? "-"} {err}");
        if (dir is null) return 1;
        m.Use(b.Name, ctx => ctx.SessionId);                                  // connect first: its own events come before T0
        var reader = new OneC.EventLog.EventLogReader();
        var cursor = reader.Read(dir, null).Cursor;
        var t0 = DateTime.Now;
        Thread.Sleep(TimeSpan.FromSeconds(2));
        string? created = null;
        if (write)
        {
            // A test-owned document through the real write path: created, posted on a file base,
            // deleted — its GUID must come back out of the log with each step.
            var w = new WriteService(m, "AIBA_REWRITE_");
            const string Doc = "ПоступлениеТоваровУслуг";
            var c = w.CreateByClone(b.Name, Doc, $"AIBA_REWRITE_LOGPARITY_{DateTime.Now:MMddHHmmss}");
            created = c.Ref;
            if (b.IsFile) w.Post(b.Name, Doc, c.Ref);
            w.Delete(b.Name, Doc, c.Ref);
            Console.WriteLine($"wrote and deleted {Doc} {created}");
        }
        if (rollback)
        {
            // A write inside a transaction that is then cancelled: nothing persists, but 1C logs
            // the record — which tx letter does it get? (connector: "R" = rolled back).
            created = m.Use(b.Name, ctx =>
            {
                using var s = new ComScope();
                QueryKit.Release(Dispatch.Call(ctx.Connection, "НачатьТранзакцию", ctx.Error));
                try
                {
                    var mgr = QueryKit.Manager(ctx, s, "Документы", "ПоступлениеТоваровУслуг");
                    var doc = s.Track(Dispatch.Call(mgr, "СоздатьДокумент", ctx.Error), "Документ");
                    Dispatch.Set(doc, "Дата", DateTime.Now, ctx.Error);
                    Dispatch.Set(doc, "Комментарий", $"AIBA_REWRITE_ROLLBACK_{DateTime.Now:MMddHHmmss}", ctx.Error);
                    QueryKit.Release(Dispatch.Call(doc, "Записать", ctx.Error));
                    var r = s.Track(Dispatch.Get(doc, "Ссылка", ctx.Error), "Ссылка");
                    return OneCValue.RefGuid(r, ctx);
                }
                finally { QueryKit.Release(Dispatch.Call(ctx.Connection, "ОтменитьТранзакцию", ctx.Error)); }
            });
            Console.WriteLine($"wrote ПоступлениеТоваровУслуг {created} inside a transaction, then cancelled it");
        }
        Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
        var t1 = DateTime.Now;
        var ours = new List<OneC.EventLog.ChangeEvent>();
        OneC.EventLog.ChangeBatch batch;
        do { batch = reader.Read(dir, cursor); cursor = batch.Cursor; ours.AddRange(batch.Events); }
        while (batch.More);
        var (all, _) = m.Use(b.Name, ctx => Read(ctx, t0, t1));
        // The feed drops rolled-back transactions; so does this side of the comparison.
        var theirs = all.Where(e => e.TxStatus != "RolledBack").ToList();
        Console.WriteLine($"1C export: {all.Count} data events, {all.Count - theirs.Count} of them rolled back");

        string lo = t0.AddSeconds(1).ToString("yyyy-MM-dd'T'HH:mm:ss"), hi = t1.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss");
        bool In(string ts) => string.CompareOrdinal(ts, lo) >= 0 && string.CompareOrdinal(ts, hi) <= 0;
        var a = ours.Where(e => In(e.Ts)).ToList();
        var o = theirs.Where(e => In(e.Ts)).ToList();
        string Key(string ts, string k, string md, string? r) => $"{ts}|{k}|{md}|{r}";
        var pool = o.GroupBy(e => Key(e.Ts, e.Kind, e.Metadata, e.Ref)).ToDictionary(g => g.Key, g => g.ToList());
        var onlyOurs = new List<string>();
        foreach (var e in a)
        {
            string k = Key(e.Ts, e.Kind, e.Metadata, e.Ref);
            if (pool.TryGetValue(k, out var l) && l.Count > 0) l.RemoveAt(0);
            else onlyOurs.Add(k);
        }
        var onlyTheirs = pool.Values.SelectMany(l => l).Select(e => Key(e.Ts, e.Kind, e.Metadata, e.Ref) + "|" + e.TxStatus).ToList();
        Console.WriteLine($"window {lo} .. {hi}: file reader {a.Count} data events, 1C export {o.Count} (committed / not in a transaction); " +
                          $"only in reader {onlyOurs.Count}, only in export {onlyTheirs.Count}");
        foreach (var s in onlyOurs.Take(5)) Console.WriteLine("  reader only: " + s);
        foreach (var s in onlyTheirs.Take(5)) Console.WriteLine("  export only: " + s);
        if (created is not null)
            Console.WriteLine($"our document in the feed: [{string.Join(", ", a.Where(e => e.Ref == created).Select(e => e.Kind).Distinct())}]");
        return onlyOurs.Count + onlyTheirs.Count == 0 ? 0 : 1;
    }

    public sealed record Exported(string Ts, string Kind, string Metadata, string? Ref, string TxStatus);

    /// <summary>The export names metadata in English (InformationRegister.X); the log's dictionary in the configuration's language.</summary>
    private static string Russian(string fullName)
    {
        int dot = fullName.IndexOf('.');
        if (dot < 0) return fullName;
        string kind = fullName[..dot] switch
        {
            "Catalog" => "Справочник", "Document" => "Документ", "InformationRegister" => "РегистрСведений",
            "AccumulationRegister" => "РегистрНакопления", "AccountingRegister" => "РегистрБухгалтерии",
            "CalculationRegister" => "РегистрРасчета", "ChartOfAccounts" => "ПланСчетов",
            "ChartOfCharacteristicTypes" => "ПланВидовХарактеристик", "ChartOfCalculationTypes" => "ПланВидовРасчета",
            "ExchangePlan" => "ПланОбмена", "BusinessProcess" => "БизнесПроцесс", "Task" => "Задача",
            "Constant" => "Константа", "Sequence" => "Последовательность", "DocumentJournal" => "ЖурналДокументов",
            var other => other
        };
        return kind + fullName[dot..];
    }

    /// <summary><c>OneC.Host logscan</c>: catch-up cost — the newest .lgp read from its start in capped steps.</summary>
    public static int Scan(OneCBase b, long maxBytes)
    {
        var (dir, err) = new OneC.EventLog.LogLocator().Resolve(b.ConnectionString);
        if (dir is null) { Console.WriteLine(err); return 1; }
        var reader = new OneC.EventLog.EventLogReader();
        var tail = reader.Read(dir, null).Cursor!;
        var cursor = tail with { Offset = 0 };
        Console.WriteLine($"{b.Name}: {tail.File}, {tail.Offset / 1048576} MB");
        Console.WriteLine(ResourceSampler.Take("before").Line());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long events = 0, records = 0; int calls = 0;
        OneC.EventLog.ChangeBatch batch;
        var kinds = new Dictionary<string, long>();
        do
        {
            batch = reader.Read(dir, cursor, maxBytes);
            cursor = batch.Cursor!; calls++; records += batch.Records; events += batch.Events.Count;
            foreach (var e in batch.Events) kinds[e.Metadata.Split('.')[0]] = kinds.GetValueOrDefault(e.Metadata.Split('.')[0]) + 1;
        } while (batch.More && cursor.File == tail.File);
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine(ResourceSampler.Take("after").Line());
        Console.WriteLine($"{records} records, {events} data events in {calls} reads, {ms} ms = {(ms == 0 ? 0 : cursor.Offset / 1048576.0 / (ms / 1000.0)):F0} MB/s; " +
                          string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value}")));
        return 0;
    }

    public static (List<Exported> Events, string RawSample) Read(SessionContext ctx, DateTime from, DateTime to)
    {
        string file = Path.Combine(Path.GetTempPath(), $"aiba-eventlog-{Guid.NewGuid():N}.xml");
        try
        {
            using (var scope = new ComScope())
            {
                var filter = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Структура"), "Структура");
                QueryKit.Release(Dispatch.Call(filter, "Вставить", ctx.Error, "ДатаНачала", from));
                QueryKit.Release(Dispatch.Call(filter, "Вставить", ctx.Error, "ДатаОкончания", to));
                QueryKit.Release(Dispatch.Call(ctx.Connection, "ВыгрузитьЖурналРегистрации", ctx.Error, file, filter));
            }
            var doc = XDocument.Load(file);
            string sample = doc.ToString().Length > 3000 ? doc.ToString()[..3000] : doc.ToString();
            var list = new List<Exported>();
            foreach (var e in doc.Descendants().Where(x => x.Name.LocalName == "Event" && x.Elements().Any()))
            {
                string? V(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value;
                string name = V("Event") ?? "";
                string? kind = OneC.EventLog.LogFormat.DataEventKind(name);
                if (kind is null) continue;
                string tx = V("TransactionStatus") ?? "";
                string? data = V("Data");
                string? guid = data is not null && Guid.TryParse(data.Trim(), out var g) ? g.ToString("D") : null;
                var date = DateTime.Parse(V("Date")!);
                list.Add(new Exported(date.ToString("yyyy-MM-dd'T'HH:mm:ss"), kind, Russian(V("Metadata") ?? ""), guid, tx));
            }
            return (list, sample);
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
