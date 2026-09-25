using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// What a reference-typed column can be dereferenced to inside the query. The flags mirror the
/// fallback order of the old adapter's ПреобразоватьЗначение (main.os:2871): Наименование,
/// then Код, then Номер, then the GUID. A flag is set when at least one type in the
/// attribute's type set has that field; 1C yields NULL for the others.
/// </summary>
public sealed record AttributeShape(
    string Name,
    bool DerefName,
    bool DerefCode,
    bool DerefNumber,
    bool HasPrimitive,
    bool MayBeUuid)
{
    public bool HasRefTypes => DerefName || DerefCode || DerefNumber || RefWithoutDeref;

    /// <summary>A reference type with none of the three fields (enums).</summary>
    public bool RefWithoutDeref { get; init; }

    /// <summary>
    /// Full name (<c>Справочник.Валюты</c>) when the attribute has exactly one type and it is a
    /// reference with a dereferenceable field. Lets the query flag the empty reference in the
    /// first dereferenced column, so an empty value costs one COM call instead of four.
    /// </summary>
    public string? EmptyRefOf { get; init; }

    /// <summary>
    /// Value names of the attribute's only type when that type is an enum, by ordinal. The old
    /// adapter shipped an enum as XMLСтрока — its value name; the query selects the ordinal
    /// (<c>f.Порядок</c>) and the name comes from here, with no COM call per value.
    /// </summary>
    public IReadOnlyList<string>? EnumNames { get; init; }

    /// <summary>Every value is a reference (or empty) — no need to read the raw column first.</summary>
    public bool PureRef => !HasPrimitive && (DerefName || DerefCode || DerefNumber);

    /// <summary>
    /// Render each value with the literal old algorithm instead of query columns: when a zero
    /// GUID can be a value (УникальныйИдентификатор), or when the type set is so wide that
    /// dereferencing it joins half the configuration (see <see cref="MetadataShapes.MaxDerefTypes"/>).
    /// </summary>
    public bool PerValue { get; init; }

    public static AttributeShape Primitive(string name) => new(name, false, false, false, true, false);
}

public sealed record TabularShape(string Name, IReadOnlyList<AttributeShape> Attributes);

/// <summary>
/// The metadata walk catalogs and documents share: attribute and tabular-section shapes of one
/// metadata object. A few COM calls per attribute and type, so callers cache the result
/// (<see cref="SchemaCache"/>).
/// </summary>
public static class MetadataShapes
{
    /// <summary>
    /// Most reference types an attribute may have and still be dereferenced in the query. Each
    /// type is a LEFT JOIN; «any reference» attributes (ИдентификаторыОбъектовМетаданных.
    /// ЗначениеПустойСсылки, file owners) made a 20-row page take 25 s on KAN. Wider ones go
    /// per value, which is what the old adapter did for every attribute.
    /// </summary>
    public const int MaxDerefTypes = 4;

    /// <summary>
    /// The old adapter's ИмяРеквизитаОрганизацииОбъекта (main.os:8161): «Организация» wins,
    /// then the alternative names some catalogs use.
    /// </summary>
    public static string? OrgAttribute(IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.Ordinal);
        foreach (var n in new[] { "Организация", "ГоловнаяОрганизация", "УдалитьТекущаяОрганизация", "УдалитьОрганизация" })
            if (set.Contains(n)) return n;
        return null;
    }

    /// <summary>
    /// Finds a catalog or document by name (<paramref name="collection"/> = Справочники /
    /// Документы). The metadata object is tracked in <paramref name="scope"/>.
    /// </summary>
    public static object Find(SessionContext ctx, ComScope scope, object md, string collection, string name, string what)
    {
        var coll = scope.Track(Dispatch.Get(md, collection, ctx.Error), collection);
        var found = Dispatch.Call(coll, "Найти", ctx.Error, name);
        if (found is null || !Marshal.IsComObject(found))
            throw OneCException.Host($"{what} '{name}' not found", ctx.Error, what, name);
        return scope.Track(found, name);
    }

    /// <summary>Attribute shapes and tabular-section shapes of <paramref name="meta"/>.</summary>
    public static (List<AttributeShape> Attributes, List<TabularShape> Tabular) Load(
        SessionContext ctx, ComScope scope, object md, object meta)
    {
        var attributes = Shapes(ctx, scope, md, meta, meta);
        var tabular = new List<TabularShape>();
        var sections = scope.Track(Dispatch.Get(meta, "ТабличныеЧасти", ctx.Error), "ТабличныеЧасти");
        int ns = Dispatch.CallInt(sections, "Количество", ctx.Error);
        for (int i = 0; i < ns; i++)
        {
            var ts = scope.Track(Dispatch.Call(sections, "Получить", ctx.Error, i), "ТЧ");
            tabular.Add(new TabularShape(Dispatch.GetString(ts, "Имя", ctx.Error)!, Shapes(ctx, scope, md, ts, meta)));
        }
        return (attributes, tabular);
    }

    /// <summary>
    /// Shape of a query-result column from its ТипЗначения — for tables whose columns come from
    /// <c>ВЫБРАТЬ *</c> rather than from one metadata object (registers).
    /// </summary>
    public static AttributeShape OfColumn(SessionContext ctx, object md, string name, object typeDesc)
    {
        using var s = new ComScope();
        return Shape(ctx, s, md, name, typeDesc, ownMeta: null);
    }

    /// <summary>0 when the metadata object has no such property (an enum has no ДлинаКода).</summary>
    public static int Length(SessionContext ctx, object meta, string prop)
    {
        if (!Dispatch.HasMember(meta, prop)) return 0;
        try { return Convert.ToInt32(Dispatch.Get(meta, prop, ctx.Error)); }
        catch (OneCException) { return 0; }
    }

    /// <param name="ownMeta">
    /// The object's own metadata, owned by the caller. 1C hands out metadata objects and
    /// <c>Тип</c> values as shared identities, and .NET maps one identity to one RCW — so
    /// <c>НайтиПоТипу</c> on a self-referencing attribute returns the caller's RCW, and
    /// releasing it here would separate it for the caller (D06 note).
    /// </param>
    private static List<AttributeShape> Shapes(SessionContext ctx, ComScope scope, object md, object owner, object ownMeta)
    {
        var list = new List<AttributeShape>();
        var attrs = scope.Track(Dispatch.Get(owner, "Реквизиты", ctx.Error), "Реквизиты");
        int n = Dispatch.CallInt(attrs, "Количество", ctx.Error);
        for (int i = 0; i < n; i++)
        {
            using var s = new ComScope();
            var a = s.Track(Dispatch.Call(attrs, "Получить", ctx.Error, i), "Реквизит");
            string name = Dispatch.GetString(a, "Имя", ctx.Error)!;
            var typeDesc = s.Track(Dispatch.Get(a, "Тип", ctx.Error), "ОписаниеТипов");
            list.Add(Shape(ctx, s, md, name, typeDesc, ownMeta));
        }
        return list;
    }

    private static AttributeShape Shape(SessionContext ctx, ComScope s, object md, string name, object typeDesc, object? ownMeta)
    {
        bool dn = false, dc = false, dr = false, prim = false, uuid = false, refNoDeref = false;
        string? singleRef = null;
        List<string>? enumNames = null;
        int primTypes = 0;
        var types = s.Track(Dispatch.Call(typeDesc, "Типы", ctx.Error), "Типы");
        int n = Dispatch.CallInt(types, "Количество", ctx.Error);
        // There are fewer than 8 primitive types, so this many types is a wide reference set
        // whatever they are — no need to walk hundreds of them.
        if (n > MaxDerefTypes + 8)
            return new AttributeShape(name, false, false, false, true, false) { PerValue = true };
        for (int i = 0; i < n; i++)
        {
            // Everything taken here is released before the next type: a Тип is a shared
            // identity, so nothing may hold one across this scope.
            using var t = new ComScope();
            var type = t.Track(Dispatch.Call(types, "Получить", ctx.Error, i), "Тип");
            var m = Dispatch.Call(md, "НайтиПоТипу", ctx.Error, type);
            if (m is null || !Marshal.IsComObject(m))
            {
                prim = true;
                primTypes++;
                uuid |= XmlTypeName(ctx, t, type) == "UUID";
                continue;
            }
            if (!ReferenceEquals(m, ownMeta)) t.Add(m, "type metadata");
            // A business-process route point maps to its process's metadata, which has a Номер
            // the route point does not ("Field not found ТочкаМаршрута.Номер", KAN Рецензии).
            if (XmlTypeName(ctx, t, type)?.StartsWith("BusinessProcessRoutePointRef.", StringComparison.Ordinal) == true)
                return new AttributeShape(name, false, false, false, true, false) { PerValue = true };
            bool name1 = Length(ctx, m, "ДлинаНаименования") > 0;
            bool code1 = Length(ctx, m, "ДлинаКода") > 0;
            bool num1 = Length(ctx, m, "ДлинаНомера") > 0;
            dn |= name1; dc |= code1; dr |= num1;
            if (!name1 && !code1 && !num1) refNoDeref = true;
            if (n == 1) singleRef = Dispatch.Call(m, "ПолноеИмя", ctx.Error) as string;
            if (n == 1 && Dispatch.HasMember(m, "ЗначенияПеречисления")) enumNames = EnumValueNames(ctx, m);
        }
        int refTypes = n - primTypes;
        if (refTypes > MaxDerefTypes)
            return new AttributeShape(name, false, false, false, true, uuid) { PerValue = true };
        return new AttributeShape(name, dn, dc, dr, prim, uuid)
        {
            PerValue = uuid,
            RefWithoutDeref = refNoDeref,
            EmptyRefOf = singleRef is not null && !refNoDeref && IsRussianRefKind(singleRef) ? singleRef : null,
            EnumNames = enumNames
        };
    }

    private static List<string> EnumValueNames(SessionContext ctx, object enumMeta)
    {
        using var s = new ComScope();
        var values = s.Track(Dispatch.Get(enumMeta, "ЗначенияПеречисления", ctx.Error), "ЗначенияПеречисления");
        int n = Dispatch.CallInt(values, "Количество", ctx.Error);
        var names = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            using var v = new ComScope();
            names.Add(Dispatch.GetString(v.Track(Dispatch.Call(values, "Получить", ctx.Error, i), "ЗначениеПеречисления"), "Имя", ctx.Error)!);
        }
        return names;
    }

    /// <summary>
    /// ЗНАЧЕНИЕ(…ПустаяСсылка) is written with Russian names; a configuration whose script
    /// variant is English reports <c>Catalog.X</c> and simply does not get the shortcut.
    /// </summary>
    private static bool IsRussianRefKind(string fullName) =>
        fullName.StartsWith("Справочник.", StringComparison.Ordinal) ||
        fullName.StartsWith("Документ.", StringComparison.Ordinal) ||
        fullName.StartsWith("ПланСчетов.", StringComparison.Ordinal) ||
        fullName.StartsWith("ПланВидовХарактеристик.", StringComparison.Ordinal) ||
        fullName.StartsWith("ПланВидовРасчета.", StringComparison.Ordinal) ||
        fullName.StartsWith("ПланОбмена.", StringComparison.Ordinal);

    /// <summary>XMLТип(Тип).ИмяТипа — "UUID" for УникальныйИдентификатор, null when unmapped.</summary>
    private static string? XmlTypeName(SessionContext ctx, ComScope t, object type)
    {
        var x = Dispatch.Call(ctx.Connection, "XMLТип", ctx.Error, type);
        if (!OneCValue.IsCom(x)) return null;
        t.Add(x, "ТипДанныхXML");
        return Dispatch.GetString(x!, "ИмяТипа", ctx.Error);
    }
}

/// <summary>
/// Schemas per (base, kind, name), shared by catalog and document reads. A configuration
/// update while the host runs is picked up after <see cref="Ttl"/>, or at once after a 1C
/// error on that base (the read services call <see cref="Forget"/>).
/// </summary>
public static class SchemaCache
{
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(string Base, string Kind, string Name), (object Schema, DateTime At)> Cache = new();

    public static T Get<T>(SessionContext ctx, string kind, string name, Func<SessionContext, string, T> load) where T : class
    {
        var key = (ctx.Base.Name, kind, name);
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Ttl) return (T)hit.Schema;
        var s = load(ctx, name);
        Cache[key] = (s, DateTime.UtcNow);
        return s;
    }

    public static bool IsCached(string baseName, string kind, string name) => Cache.ContainsKey((baseName, kind, name));

    public static void Forget(string baseName)
    {
        foreach (var k in Cache.Keys.Where(k => k.Base == baseName).ToList()) Cache.TryRemove(k, out _);
    }
}
