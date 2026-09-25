using System.Diagnostics;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host writeparity</c>: the write path's round-trip sweep (D38). For every document type
/// with data, the newest documents are turned into old-style bodies (<see cref="WritePayload"/>),
/// written back through <see cref="DocumentWriter"/> as new <c>AIBA_REWRITE_</c> documents, read
/// again and compared field by field; each copy is then deleted. Local copies only — never a
/// client's base (user rule, 2026-09-24).
/// </summary>
internal static class WriteParityScenario
{
    public const string Prefix = "AIBA_REWRITE_";

    private static bool Trace => Environment.GetEnvironmentVariable("WRITEPARITY_TRACE") == "1";

    public static int Run(SessionManager m, IEnumerable<string> baseNames, int perType, string? only, bool post)
    {
        var writes = new WriteService(m, Prefix);
        var writer = new DocumentWriter(writes);
        string marker = Prefix + "WP_" + DateTime.Now.ToString("MMddHHmmss");
        int identical = 0, differ = 0, refused = 0, oneCRefused = 0, leftBehind = 0;

        foreach (var b in baseNames)
        {
            var names = m.Use(b, ctx => DocumentParityScenario.DocumentNames(ctx));
            if (only is not null) names = names.Where(n => n == only).ToList();
            int types = 0;
            var sw = Stopwatch.StartNew();
            foreach (var name in names)
            {
                List<string> sources;
                try { sources = m.Use(b, ctx => Sources(ctx, name, perType, post)); }
                catch (OneCException e) { Console.WriteLine($"  {name}: cannot list ({One(e.Message)})"); continue; }
                if (sources.Count == 0) continue;
                types++;

                foreach (var source in sources)
                {
                    if (Trace) Console.WriteLine($"  … {name} {source}: read");
                    JsonObject original = m.Use(b, ctx => WritePayload.FromRef(ctx, name, source));
                    if (Trace) Console.WriteLine($"  … {name} {source}: write");
                    var body = (JsonObject)original.DeepClone();
                    body[DocumentWriteBody.CommentField] = $"{marker}: copy of {source}";
                    string? copy = null;
                    try
                    {
                        var created = writer.Create(b, name, body, post);
                        copy = created.Id;
                        if (Trace) Console.WriteLine($"  … {name} {source}: read copy {copy}");
                        var written = m.Use(b, ctx => WritePayload.FromRef(ctx, name, copy));
                        var diffs = WritePayload.Diff(original, written);
                        if (diffs.Count == 0) identical++;
                        else
                        {
                            differ++;
                            Console.WriteLine($"  {name} {source}: {diffs.Count} difference(s): {string.Join("; ", diffs.Take(6))}");
                        }
                        foreach (var d in created.Diagnostics.Where(d => d.Level != WriteDiagnostic.Info))
                            Console.WriteLine($"  {name} {source}: {d.Level} {d.Code} {d.Field}: {One(d.Message)}");
                    }
                    catch (WriteRejected e)
                    {
                        refused++;
                        Console.WriteLine($"  {name} {source}: refused — {One(e.Message)}");
                    }
                    catch (DocumentWriteFailed e)
                    {
                        oneCRefused++;
                        Console.WriteLine($"  {name} {source}: 1C refused ({e.WriteMode}) — {One(e.Message)}");
                    }
                    finally
                    {
                        if (Trace && copy is not null) Console.WriteLine($"  … {name} {source}: delete copy");
                        if (copy is not null)
                            try { writes.Delete(b, name, copy); }
                            catch (Exception e)
                            {
                                // A configuration may deny direct deletion; the copy is ours, so at least mark it.
                                leftBehind++;
                                string marked;
                                try { writes.MarkForDeletion(b, name, copy); marked = "marked for deletion instead"; }
                                catch (Exception e2) { marked = "not marked either: " + One(e2.Message); }
                                Console.WriteLine($"  {name} {copy}: NOT DELETED — {One(e.Message)}; {marked}");
                            }
                    }
                }
            }
            Console.WriteLine($"{b}: {types} document types with data, {sw.Elapsed.TotalSeconds:F0} s");
        }
        Console.WriteLine($"identical {identical}, differ {differ}, refused {refused}, 1C refused {oneCRefused}, left behind {leftBehind}");
        return differ + leftBehind == 0 ? 0 : 1;
    }

    /// <summary>The newest documents to copy: active, not AIBA's own, posted when posting is tested.</summary>
    internal static List<string> Sources(SessionContext ctx, string name, int count, bool posted)
    {
        var schema = WriteSchemas.Get(ctx, name);
        if (!schema.Attributes.ContainsKey(DocumentWriteBody.CommentField)) return new();
        using var scope = new ComScope();
        var q = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ {count} Т.Ссылка КАК r ИЗ Документ.{name} КАК Т " +
            $"ГДЕ НЕ Т.ПометкаУдаления И НЕ Т.Комментарий ПОДОБНО \"AIBA%\"{(posted ? " И Т.Проведен" : "")} " +
            "УПОРЯДОЧИТЬ ПО Т.Дата УБЫВ");
        var cur = QueryKit.Execute(ctx, scope, q);
        var list = new List<string>();
        while (cur.CallBool("Следующий", ctx.Error))
        {
            using var rs = new ComScope();
            if (OneCValue.RefGuid(rs.Track(cur.Get("r", ctx.Error), "Ссылка"), ctx) is { } g) list.Add(g);
        }
        return list;
    }

    private static string One(string? s) => (s ?? "").ReplaceLineEndings(" ");
}
