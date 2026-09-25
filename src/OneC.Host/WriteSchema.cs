using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// What one attribute or column accepts, from its ОписаниеТипов: reference types by XML name
/// (<c>CatalogRef.Контрагенты</c>, <c>EnumRef.…</c>, <c>ChartOfAccountsRef.…</c>,
/// <c>DocumentRef.…</c>) and the primitive kinds. A wide set (субконто: dozens of types) is
/// marked, not listed — a value for it must name its type.
/// </summary>
public sealed record FieldType(
    IReadOnlyList<string> Refs,
    bool String,
    int StringLength,
    bool Number,
    bool Date,
    bool Boolean,
    bool Wide)
{
    public bool HasRefs => Refs.Count > 0 || Wide;

    /// <summary>The only reference type, when there is exactly one and no primitive.</summary>
    public string? SingleRef => !Wide && Refs.Count == 1 && !String && !Number && !Date && !Boolean ? Refs[0] : null;

    public static readonly FieldType Unknown = new(Array.Empty<string>(), false, 0, false, false, false, false);
}

public sealed record WriteTabular(string Name, IReadOnlyDictionary<string, FieldType> Columns);

/// <summary>A document type as the write path needs it; cached per base (<see cref="SchemaCache"/>).</summary>
public sealed record WriteSchema(
    string Document,
    bool HasNumber,
    IReadOnlyDictionary<string, FieldType> Attributes,
    IReadOnlyDictionary<string, WriteTabular> Tabular)
{
    public FieldType? Attribute(string name) => Attributes.GetValueOrDefault(name);
}

public static class WriteSchemas
{
    public const string Kind = "document-write";

    /// <summary>More reference types than this and the set is "wide": listed nowhere, the value names its type.</summary>
    public const int MaxListedTypes = 16;

    public static WriteSchema Get(SessionContext ctx, string document) => SchemaCache.Get(ctx, Kind, document, Load);

    internal static WriteSchema Load(SessionContext ctx, string document)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var meta = MetadataShapes.Find(ctx, scope, md, "Документы", document, "document");
        var attributes = Fields(ctx, scope, meta);
        var tabular = new Dictionary<string, WriteTabular>(StringComparer.Ordinal);
        var sections = scope.Track(Dispatch.Get(meta, "ТабличныеЧасти", ctx.Error), "ТабличныеЧасти");
        int ns = Dispatch.CallInt(sections, "Количество", ctx.Error);
        for (int i = 0; i < ns; i++)
        {
            using var s = new ComScope();
            var ts = s.Track(Dispatch.Call(sections, "Получить", ctx.Error, i), "ТЧ");
            string name = Dispatch.GetString(ts, "Имя", ctx.Error)!;
            tabular[name] = new WriteTabular(name, Fields(ctx, s, ts));
        }
        return new WriteSchema(document, MetadataShapes.Length(ctx, meta, "ДлинаНомера") > 0, attributes, tabular);
    }

    private static Dictionary<string, FieldType> Fields(SessionContext ctx, ComScope scope, object owner)
    {
        var map = new Dictionary<string, FieldType>(StringComparer.Ordinal);
        var attrs = scope.Track(Dispatch.Get(owner, "Реквизиты", ctx.Error), "Реквизиты");
        int n = Dispatch.CallInt(attrs, "Количество", ctx.Error);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            var a = s.Track(Dispatch.Call(attrs, "Получить", ctx.Error, i), "Реквизит");
            string name = Dispatch.GetString(a, "Имя", ctx.Error)!;
            map[name] = TypeOf(ctx, s.Track(Dispatch.Get(a, "Тип", ctx.Error), "ОписаниеТипов"));
        }
        return map;
    }

    /// <summary>
    /// Every type through XMLТип(Тип).ИмяТипа: <c>string</c>, <c>decimal</c>, <c>dateTime</c>,
    /// <c>boolean</c>, or a reference's XML name. Each Тип is released before the next one — it
    /// is a shared 1C identity (D06 note).
    /// </summary>
    internal static FieldType TypeOf(SessionContext ctx, object typeDesc)
    {
        using var s = new ComScope();
        var types = s.Track(Dispatch.Call(typeDesc, "Типы", ctx.Error), "Типы");
        int n = Dispatch.CallInt(types, "Количество", ctx.Error);
        var refs = new List<string>();
        bool str = false, num = false, date = false, boolean = false;
        // Every type, even for a субконто set of dozens: two COM calls each, once per schema load.
        for (int i = 0; i < n; i++)
        {
            using var t = new ComScope();
            var type = t.Track(Dispatch.Call(types, "Получить", ctx.Error, i), "Тип");
            var x = Dispatch.Call(ctx.Connection, "XMLТип", ctx.Error, type);
            if (!OneCValue.IsCom(x)) continue;                  // no XML mapping (ХранилищеЗначения …)
            t.Add(x, "ТипДанныхXML");
            switch (Dispatch.GetString(x!, "ИмяТипа", ctx.Error))
            {
                case "string": str = true; break;
                case "decimal": num = true; break;
                case "dateTime": date = true; break;
                case "boolean": boolean = true; break;
                case { } name when name.Contains('.'): refs.Add(name); break;
            }
        }
        int length = 0;
        if (str)
        {
            var q = s.Track(Dispatch.Get(typeDesc, "КвалификаторыСтроки", ctx.Error), "КвалификаторыСтроки");
            length = Convert.ToInt32(Dispatch.Get(q, "Длина", ctx.Error));
        }
        bool wide = refs.Count > MaxListedTypes;
        return new FieldType(wide ? Array.Empty<string>() : refs, str, length, num, date, boolean, wide);
    }
}
