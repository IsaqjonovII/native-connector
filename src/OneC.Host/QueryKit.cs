using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>Small query helpers the catalog and document reads share. Session thread only.</summary>
internal static class QueryKit
{
    /// <summary>
    /// The source alias every generated select list qualifies its fields with, so a field whose
    /// name is a query keyword (<c>end</c> in the МИКО extension) still parses.
    /// </summary>
    public const string Alias = "Т";

    public static string Field(string name) => Alias + "." + name;

    public static object NewQuery(SessionContext ctx, ComScope scope, string text)
    {
        var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", text, ctx.Error);
        return q;
    }

    public static void SetParameter(SessionContext ctx, object query, string name, object? value)
        => Release(Dispatch.Call(query, "УстановитьПараметр", ctx.Error, name, value));

    /// <summary>Runs the query; the cursor resolves its column DISPIDs once (DispatchMemo).</summary>
    public static DispatchMemo Execute(SessionContext ctx, ComScope scope, object query)
    {
        var result = scope.Track(Dispatch.Call(query, "Выполнить", ctx.Error), "РезультатЗапроса");
        return new DispatchMemo(scope.Track(Dispatch.Call(result, "Выбрать", ctx.Error), "Выборка"));
    }

    /// <summary>
    /// A reference from a GUID: <c>&lt;managers&gt;.&lt;name&gt;.ПолучитьСсылку(УникальныйИдентификатор)</c>,
    /// managers = Справочники or Документы. A GUID with no object gives a "broken" reference,
    /// which a query simply does not match.
    /// </summary>
    public static object RefByGuid(SessionContext ctx, ComScope scope, string managers, string name, string guid)
        => RefFrom(ctx, scope, Manager(ctx, scope, managers, name), guid);

    public static object Manager(SessionContext ctx, ComScope scope, string managers, string name)
    {
        var all = scope.Track(Dispatch.Get(ctx.Connection, managers, ctx.Error), managers);
        return scope.Track(Dispatch.Get(all, name, ctx.Error), name);
    }

    public static object RefFrom(SessionContext ctx, ComScope scope, object manager, string guid)
    {
        var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", guid), "UUID");
        return scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "Ссылка");
    }

    /// <summary>A 1C array (NewObject) — a CLR array does not marshal into a query parameter.</summary>
    public static object NewArray(SessionContext ctx, ComScope scope) =>
        scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Массив"), "Массив");

    public static void Add(SessionContext ctx, object array, object value) =>
        Release(Dispatch.Call(array, "Добавить", ctx.Error, value));

    /// <summary>
    /// orgRef: the organisation's GUID, next to the attribute's name (names are not identity —
    /// one base has both «MIREL» and «OOO "MIREL GROUP"»). Like the old code, an empty
    /// reference still yields the zero GUID; only a missing value yields nothing.
    /// </summary>
    public static string? OrgGuid(DispatchMemo cursor, string alias, SessionContext ctx)
    {
        var v = cursor.Get(alias, ctx.Error);
        if (!OneCValue.IsCom(v)) return null;
        using var s = new ComScope();
        s.Add(v, "Организация");
        return OneCValue.RefGuid(v!, ctx) is { Length: > 0 } g ? g : null;
    }

    public static void Release(object? maybeCom)
    {
        if (!OneCValue.IsCom(maybeCom)) return;
        using var s = new ComScope();
        s.Add(maybeCom, "result");
    }

    /// <summary>
    /// A 1C error on a read may mean the configuration changed under a cached schema (a field
    /// gone): drop the base's schemas so the next call reads metadata again.
    /// </summary>
    public static T Healing<T>(string baseName, Func<T> read)
    {
        try { return read(); }
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime)
        {
            SchemaCache.Forget(baseName);
            throw;
        }
    }

    public static string? Join(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrEmpty(x)).ToList();
        return p.Count == 0 ? null : string.Join(" И ", p);
    }
}

/// <summary>
/// Tabular sections for a page of objects: one query per section, limited to the page's refs
/// (main.os:6372 / :8007), rows under <c>tabularSections.&lt;name&gt;</c>.
/// </summary>
internal static class TabularSections
{
    /// <param name="table">Full table name, e.g. <c>Справочник.Контрагенты</c>.</param>
    /// <param name="includeEmpty">
    /// Every section present, empty ones as [] — the old by-id / batch document shape
    /// (main.os:8413). Otherwise a section key exists only when it has rows — the list shape.
    /// </param>
    public static void Load(SessionContext ctx, string table, IReadOnlyList<TabularShape> sections, object pageRefs,
                            IReadOnlyDictionary<string, Dictionary<string, object?>> byId, bool includeEmpty,
                            CancellationToken ct, RefBatch? batch = null)
    {
        if (includeEmpty && sections.Count > 0)
            foreach (var element in byId.Values)
                element["tabularSections"] = sections.ToDictionary(ts => ts.Name, _ => (object?)new List<Dictionary<string, object?>>());

        foreach (var ts in sections)
        {
            var cols = ts.Attributes.Select((a, i) => ", " + LegacyValue.Select(QueryKit.Field(a.Name), "a" + i, a));
            string sql = $"ВЫБРАТЬ {QueryKit.Field("Ссылка")} КАК id, {QueryKit.Field("НомерСтроки")} КАК ln{string.Concat(cols)} " +
                         $"ИЗ {table}.{ts.Name} КАК {QueryKit.Alias} ГДЕ Ссылка В (&refs) УПОРЯДОЧИТЬ ПО Ссылка, НомерСтроки";

            using var scope = new ComScope();
            var query = QueryKit.NewQuery(ctx, scope, sql);
            QueryKit.SetParameter(ctx, query, "refs", pageRefs);
            var cursor = QueryKit.Execute(ctx, scope, query);
            while (cursor.CallBool("Следующий", ctx.Error))
            {
                ct.ThrowIfCancellationRequested();
                using var rowScope = new ComScope();
                var owner = rowScope.Track(cursor.Get("id", ctx.Error), "Ссылка");
                if (OneCValue.RefGuid(owner, ctx) is not { } id || !byId.TryGetValue(id, out var element)) continue;

                var line = new Dictionary<string, object?>(ts.Attributes.Count + 1, StringComparer.Ordinal)
                {
                    ["lineNumber"] = LegacyValue.Scalar(cursor.Get("ln", ctx.Error))
                };
                for (int i = 0; i < ts.Attributes.Count; i++)
                    line[ts.Attributes[i].Name] = LegacyValue.Read(cursor, "a" + i, ts.Attributes[i], ctx, batch);

                if (element.GetValueOrDefault("tabularSections") is not Dictionary<string, object?> all)
                    element["tabularSections"] = all = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (all.GetValueOrDefault(ts.Name) is not List<Dictionary<string, object?>> list)
                    all[ts.Name] = list = new List<Dictionary<string, object?>>();
                list.Add(line);
            }
        }
    }
}
