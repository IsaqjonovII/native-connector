using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

public sealed record DocumentSchema(
    string Name,
    bool HasNumber,
    string? OrgAttribute,
    IReadOnlyList<AttributeShape> Attributes,
    IReadOnlyList<TabularShape> Tabular)
{
    public AttributeShape? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    public bool Has(string attribute) => Find(attribute) is not null;
}

/// <summary>Document metadata, read once per base and document type (<see cref="SchemaCache"/>).</summary>
public static class DocumentSchemas
{
    public const string Kind = "document";

    public static DocumentSchema Get(SessionContext ctx, string document) => SchemaCache.Get(ctx, Kind, document, Load);

    internal static bool IsCached(string baseName, string document) => SchemaCache.IsCached(baseName, Kind, document);

    internal static DocumentSchema Load(SessionContext ctx, string document)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var meta = MetadataShapes.Find(ctx, scope, md, "Документы", document, "document");
        var (attributes, tabular) = MetadataShapes.Load(ctx, scope, md, meta);
        return new DocumentSchema(
            Name: document,
            HasNumber: MetadataShapes.Length(ctx, meta, "ДлинаНомера") > 0,
            OrgAttribute: MetadataShapes.OrgAttribute(attributes.Select(a => a.Name)),
            Attributes: attributes,
            Tabular: tabular);
    }

    /// <summary>
    /// The old adapter's bank-document alias (РазрешитьИмяБанковскогоДокумента, main.os:583):
    /// callers use either configuration's name for the same bank document (BP 3.0
    /// ПоступлениеНаРасчетныйСчет / СписаниеСРасчетногоСчета, BP 1.3 ПлатежноеПоручениеВходящее /
    /// ПлатежноеПоручениеИсходящее); the name this base does not have maps to the one it has.
    /// </summary>
    public static string ResolveBankAlias(SessionContext ctx, string document)
    {
        string? other = document switch
        {
            "ПоступлениеНаРасчетныйСчет" => "ПлатежноеПоручениеВходящее",
            "ПлатежноеПоручениеВходящее" => "ПоступлениеНаРасчетныйСчет",
            "СписаниеСРасчетногоСчета" => "ПлатежноеПоручениеИсходящее",
            "ПлатежноеПоручениеИсходящее" => "СписаниеСРасчетногоСчета",
            _ => null
        };
        if (other is null || Exists(ctx, document)) return document;
        return Exists(ctx, other) ? other : document;
    }

    private static bool Exists(SessionContext ctx, string document)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var docs = scope.Track(Dispatch.Get(md, "Документы", ctx.Error), "Документы");
        var found = Dispatch.Call(docs, "Найти", ctx.Error, document);
        if (!OneCValue.IsCom(found)) return false;
        scope.Add(found, document);
        return true;
    }
}
