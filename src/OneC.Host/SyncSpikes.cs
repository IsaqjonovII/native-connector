using System.Diagnostics;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Sync block S0 (measurement CLI, not production): facts the sync design depends on
/// (SYNC_IMPLEMENTATION_PLAN S0). Reads only, except <see cref="Lifecycle"/>, which works on
/// one test-owned <c>AIBA_REWRITE_S0_</c> document it creates and deletes itself.
/// </summary>
internal static class SyncSpikes
{
    /// <summary>Q1a: is ВерсияДанных selectable in a query, and what does it look like over COM?</summary>
    public static void Selectable(SessionManager m, string baseName, string table)
    {
        m.Use(baseName, ctx =>
        {
            using var s = new ComScope();
            var q = QueryKit.NewQuery(ctx, s, $"ВЫБРАТЬ ПЕРВЫЕ 3 Т.Ссылка КАК Ссылка, Т.ВерсияДанных КАК Версия ИЗ {table} КАК Т");
            var cur = QueryKit.Execute(ctx, s, q);
            while (cur.CallBool("Следующий", ctx.Error))
            {
                using var row = new ComScope();
                var r = row.Track(cur.Get("Ссылка", ctx.Error), "Ссылка");
                var v = cur.Get("Версия", ctx.Error);
                Console.WriteLine($"  {baseName} {table}: {OneCValue.RefGuid(r!, ctx)} version={v} ({v?.GetType().Name})");
            }
        });
    }

    /// <summary>Q2: a full keyset scan of (Ссылка, ВерсияДанных) — the version scan fallback/recovery rely on.</summary>
    public static void Scan(SessionManager m, string baseName, string table, int page)
    {
        string manager = table.StartsWith("Документ.") ? "Документы" : "Справочники";
        string name = table[(table.IndexOf('.') + 1)..];
        long rows = 0, pages = 0;
        string? last = null;
        var sw = Stopwatch.StartNew();
        Console.WriteLine("  " + ResourceSampler.Take("before").Line());
        while (true)
        {
            int got = m.Use(baseName, ctx =>
            {
                using var s = new ComScope();
                var q = QueryKit.NewQuery(ctx, s,
                    $"ВЫБРАТЬ ПЕРВЫЕ {page} Т.Ссылка КАК Ссылка, Т.ВерсияДанных КАК Версия ИЗ {table} КАК Т " +
                    (last is null ? "" : "ГДЕ Т.Ссылка > &Last ") + "УПОРЯДОЧИТЬ ПО Т.Ссылка");
                if (last is not null) QueryKit.SetParameter(ctx, q, "Last", QueryKit.RefByGuid(ctx, s, manager, name, last));
                var cur = QueryKit.Execute(ctx, s, q);
                int n = 0;
                while (cur.CallBool("Следующий", ctx.Error))
                {
                    using var row = new ComScope();
                    var r = row.Track(cur.Get("Ссылка", ctx.Error), "Ссылка");
                    _ = cur.Get("Версия", ctx.Error) as string;
                    last = OneCValue.RefGuid(r!, ctx);
                    n++;
                }
                return n;
            });
            rows += got; pages++;
            if (got < page) break;
        }
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine("  " + ResourceSampler.Take("after").Line());
        Console.WriteLine($"  {baseName} {table}: {rows} rows, {pages} pages of {page}, {ms} ms = {(ms == 0 ? 0 : rows * 1000 / ms)} rows/s");
    }

    public static long Count(SessionManager m, string baseName, string table) => m.Use(baseName, ctx =>
    {
        using var s = new ComScope();
        var cur = QueryKit.Execute(ctx, s, QueryKit.NewQuery(ctx, s, $"ВЫБРАТЬ КОЛИЧЕСТВО(*) КАК Н ИЗ {table} КАК Т"));
        return cur.CallBool("Следующий", ctx.Error) ? Convert.ToInt64(cur.Get("Н", ctx.Error)) : 0;
    });

    /// <summary>
    /// Q1b + Q4: which writes change ВерсияДанных, and which log events each write produces.
    /// One test-owned document: create → write unchanged → post → repost → unpost → mark →
    /// unmark → delete. Every step's version and log events are printed.
    /// </summary>
    public static int Lifecycle(SessionManager m, OneCBase b, string docType)
    {
        var (dir, err) = new OneC.EventLog.LogLocator().Resolve(b.ConnectionString);
        Console.WriteLine($"{b.Name}: log {dir ?? err}");
        var reader = new OneC.EventLog.EventLogReader();
        OneC.EventLog.LogCursor? cursor = dir is null ? null : reader.Read(dir, null).Cursor;
        var w = new WriteService(m, "AIBA_REWRITE_");
        string comment = $"AIBA_REWRITE_S0_{DateTime.Now:MMddHHmmss}";
        string? id = null;
        string? prev = null;

        void Step(string what, Func<string?> act)
        {
            string result;
            try { var r = act(); id ??= r; result = "ok"; }
            catch (Exception e) { result = "FAILED " + (e is OneCException oe ? oe.Describe() : e.Message); }
            string? v = id is null ? null : Version(m, b.Name, docType, id);
            Thread.Sleep(1500);                                   // the log is written after commit
            var events = new List<string>();
            if (dir is not null)
            {
                OneC.EventLog.ChangeBatch batch;
                do { batch = reader.Read(dir, cursor); cursor = batch.Cursor; events.AddRange(batch.Events.Where(e => e.Ref == id || e.Metadata.StartsWith("Регистр")).Select(e => $"{e.Kind} {e.Metadata}")); }
                while (batch.More);
            }
            string changed = prev is null || v is null ? "" : v == prev ? " (UNCHANGED)" : " (changed)";
            Console.WriteLine($"  {what,-22} {result,-6} version={v ?? "-"}{changed}");
            foreach (var g in events.GroupBy(x => x)) Console.WriteLine($"      log: {g.Key}{(g.Count() > 1 ? " ×" + g.Count() : "")}");
            prev = v ?? prev;
        }

        Step("create (clone)", () => w.CreateByClone(b.Name, docType, comment).Ref);
        if (id is null) return 1;
        Console.WriteLine($"  document {id} {comment}");
        Step("write, no change", () => w.Update(b.Name, docType, id, new Dictionary<string, object?> { ["Комментарий"] = comment }).Ref);
        Step("post", () => w.Post(b.Name, docType, id).Ref);
        Step("repost", () => w.Post(b.Name, docType, id).Ref);
        Step("unpost", () => w.Unpost(b.Name, docType, id).Ref);
        Step("mark for deletion", () => w.MarkForDeletion(b.Name, docType, id, true).Ref);
        Step("unmark", () => w.MarkForDeletion(b.Name, docType, id, false).Ref);
        Step("delete", () => w.Delete(b.Name, docType, id).Ref);
        long left = w.FindOwned(b.Name, docType, comment).Count;
        Console.WriteLine($"  test documents left with {comment}: {left}");
        return left == 0 ? 0 : 1;
    }

    private static string? Version(SessionManager m, string baseName, string docType, string id) => m.Use(baseName, ctx =>
    {
        using var s = new ComScope();
        var q = QueryKit.NewQuery(ctx, s, $"ВЫБРАТЬ Т.ВерсияДанных КАК Версия ИЗ Документ.{docType} КАК Т ГДЕ Т.Ссылка = &R");
        QueryKit.SetParameter(ctx, q, "R", QueryKit.RefByGuid(ctx, s, "Документы", docType, id));
        var cur = QueryKit.Execute(ctx, s, q);
        return cur.CallBool("Следующий", ctx.Error) ? cur.Get("Версия", ctx.Error) as string : null;
    });
}
