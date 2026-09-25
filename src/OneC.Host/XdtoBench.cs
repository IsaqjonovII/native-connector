using System.Diagnostics;
using System.Xml;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// <c>OneC.Host xdtobench</c>: is the old adapter's bulk trick (whole page → ТаблицаЗначений →
/// XDTO XML in one crossing) still worth it here? Same accounting page, three ways: execute
/// only; execute + the per-cell row walk this host uses; execute + Выгрузить + XDTO +
/// parsing the XML in C#.
/// </summary>
internal static class XdtoBench
{
    public static int Run(SessionManager m, string b, string register, DateTime from, int rows, int reps)
    {
        m.Use(b, ctx =>
        {
            var s = RegisterSchemas.Get(ctx, RegisterKind.Accounting, register);
            // Bound the virtual table to about one page, as the service's window does — an open
            // end makes Выполнить itself cost the whole tail of the register.
            DateTime edge;
            using (var es = new ComScope())
            {
                var eq = QueryKit.NewQuery(ctx, es, $"ВЫБРАТЬ ПЕРВЫЕ {rows} Период КАК p ИЗ {s.BaseTable} ГДЕ Период >= &from УПОРЯДОЧИТЬ ПО Период");
                QueryKit.SetParameter(ctx, eq, "from", from);
                var ec = QueryKit.Execute(ctx, es, eq);
                edge = from;
                while (ec.CallBool("Следующий", ctx.Error)) edge = (DateTime)ec.Get("p", ctx.Error)!;
            }
            string sql = RegisterReadService.Select(s, rows, $"{s.Source}(&from, &edge)", null, "Период ВОЗР, Регистратор ВОЗР, НомерСтроки ВОЗР");
            long Exec(Action<object> after)
            {
                using var scope = new ComScope();
                var q = QueryKit.NewQuery(ctx, scope, sql);
                QueryKit.SetParameter(ctx, q, "from", from);
                QueryKit.SetParameter(ctx, q, "edge", edge);
                var sw = Stopwatch.StartNew();
                var result = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
                after(result);
                return sw.ElapsedMilliseconds;
            }

            for (int rep = 0; rep < reps; rep++)
            {
                long exec = Exec(_ => { });
                int n = 0;
                long walk = Exec(result =>
                {
                    using var sc = new ComScope();
                    var cursor = new DispatchMemo(sc.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка"));
                    using var batch = new RefBatch(ctx);
                    var list = new List<Dictionary<string, object?>>();
                    while (cursor.CallBool("Следующий", ctx.Error))
                    {
                        var row = new Dictionary<string, object?>();
                        for (int i = 0; i < s.Columns.Count; i++) row[s.Columns[i].Name] = LegacyValue.Read(cursor, "c" + i, s.Columns[i], ctx, batch);
                        list.Add(row); n++;
                    }
                    batch.Patch(list);
                });
                long xmlChars = 0, values = 0, xdtoOnly = 0;
                long xdto = Exec(result =>
                {
                    using var sc = new ComScope();
                    var t0 = Stopwatch.StartNew();
                    var table = sc.Track(Dispatch.Call(result, "Выгрузить", ctx.Error), "ТаблицаЗначений");
                    var writer = sc.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "ЗаписьXML"), "ЗаписьXML");
                    Dispatch.Call(writer, "УстановитьСтроку", ctx.Error);
                    var ser = sc.Track(Dispatch.Get(ctx.Connection, "СериализаторXDTO", ctx.Error), "СериализаторXDTO");
                    Dispatch.Call(ser, "ЗаписатьXML", ctx.Error, writer, table);
                    string xml = (string)Dispatch.Call(writer, "Закрыть", ctx.Error)!;
                    xdtoOnly = t0.ElapsedMilliseconds;
                    xmlChars = xml.Length;
                    using var reader = XmlReader.Create(new StringReader(xml));
                    while (reader.Read()) if (reader.NodeType == XmlNodeType.Text) values++;
                });
                Console.WriteLine($"{rows} rows × {s.Columns.Count} cols: execute {exec} ms | + row walk {walk} ms ({n} rows) | " +
                                  $"+ Выгрузить+XDTO {xdtoOnly} ms + parse = {xdto} ms ({xmlChars / 1024} KB XML, {values} values)");
            }
            return 0;
        });
        return 0;
    }
}
