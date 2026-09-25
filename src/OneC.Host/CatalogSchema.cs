using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record CatalogSchema(
    string Name,
    bool HasCode,
    bool HasName,
    bool Hierarchical,
    bool HasFolders,
    bool HasOwner,
    string? OrgAttribute,
    IReadOnlyList<AttributeShape> Attributes,
    IReadOnlyList<TabularShape> Tabular)
{
    public AttributeShape? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);

    /// <summary>The parent is a reference to this same catalog.</summary>
    public AttributeShape ParentShape => new("Родитель", HasName, HasCode, false, false, false)
    {
        EmptyRefOf = HasName || HasCode ? "Справочник." + Name : null
    };
}

/// <summary>Catalog metadata, read once per base and catalog (<see cref="SchemaCache"/>).</summary>
public static class CatalogSchemas
{
    public const string Kind = "catalog";

    public static CatalogSchema Get(SessionContext ctx, string catalog) => SchemaCache.Get(ctx, Kind, catalog, Load);

    internal static bool IsCached(string baseName, string catalog) => SchemaCache.IsCached(baseName, Kind, catalog);

    public static void Forget(string baseName) => SchemaCache.Forget(baseName);

    internal static CatalogSchema Load(SessionContext ctx, string catalog)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var meta = MetadataShapes.Find(ctx, scope, md, "Справочники", catalog, "catalog");
        var (attributes, tabular) = MetadataShapes.Load(ctx, scope, md, meta);

        bool hierarchical = Dispatch.GetBool(meta, "Иерархический", ctx.Error);
        var owners = scope.Track(Dispatch.Get(meta, "Владельцы", ctx.Error), "Владельцы");

        return new CatalogSchema(
            Name: catalog,
            HasCode: MetadataShapes.Length(ctx, meta, "ДлинаКода") > 0,
            HasName: MetadataShapes.Length(ctx, meta, "ДлинаНаименования") > 0,
            Hierarchical: hierarchical,
            HasFolders: hierarchical && SupportsFolders(ctx, catalog),
            HasOwner: Dispatch.CallInt(owners, "Количество", ctx.Error) > 0,
            OrgAttribute: MetadataShapes.OrgAttribute(attributes.Select(a => a.Name)),
            Attributes: attributes,
            Tabular: tabular);
    }

    /// <summary>
    /// ЭтоГруппа exists only for ИерархияГруппИЭлементов. The old adapter probes with a query
    /// because ВидИерархии over COM does not compare to anything (main.os:8697); so do we.
    /// </summary>
    private static bool SupportsFolders(SessionContext ctx, string catalog)
    {
        using var scope = new ComScope();
        var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
        Dispatch.Set(q, "Текст", $"ВЫБРАТЬ ПЕРВЫЕ 1 ЭтоГруппа КАК g ИЗ Справочник.{catalog}", ctx.Error);
        try
        {
            scope.Add(Dispatch.Call(q, "Выполнить", ctx.Error), "РезультатЗапроса");
            return true;
        }
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime)
        {
            return false;
        }
    }
}
