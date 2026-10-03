using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Sync (S8): which document types write movements to a register (their <c>Движения</c> contain
/// it), and which of them a recorder GUID belongs to. The event log names a recorder only by GUID
/// and a per-base type code (S0), so a movement event whose document event is not in the same read
/// is resolved here. Types are cached per base and register like other schemas.
/// </summary>
public sealed class RecorderMetadata(SessionManager sessions)
{
    public IReadOnlyList<string> RecorderTypes(string baseName, RegisterKind kind, string register, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(register, "register");
        return QueryKit.Healing(baseName, () => sessions.Use(baseName, ctx => Types(ctx, kind, register), ct));
    }

    /// <summary>The document type the GUID belongs to, among the given registers' recorder types; null when none has it.</summary>
    public string? RecorderOf(string baseName, IReadOnlyList<(RegisterKind Kind, string Register)> registers, string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException($"'{id}' is not a GUID");
        foreach (var (_, r) in registers) ReadService.ValidateIdentifier(r, "register");
        return QueryKit.Healing(baseName, () => sessions.Use(baseName, ctx =>
        {
            foreach (var type in registers.SelectMany(r => Types(ctx, r.Kind, r.Register)).Distinct())
            {
                using var s = new ComScope();
                var q = QueryKit.NewQuery(ctx, s, $"ВЫБРАТЬ ПЕРВЫЕ 1 1 КАК x ИЗ Документ.{type} КАК Т ГДЕ Т.Ссылка = &r");
                QueryKit.SetParameter(ctx, q, "r", QueryKit.RefByGuid(ctx, s, "Документы", type, id));
                if (QueryKit.Execute(ctx, s, q).CallBool("Следующий", ctx.Error)) return type;
            }
            return null;
        }, ct));
    }

    private static List<string> Types(SessionContext ctx, RegisterKind kind, string register) =>
        SchemaCache.Get(ctx, "recorders:" + kind, register, (c, name) =>
        {
            using var s = new ComScope();
            var md = s.Track(Dispatch.Get(c.Connection, "Метаданные", c.Error), "Метаданные");
            string collection = kind switch
            {
                RegisterKind.Information => "РегистрыСведений",
                RegisterKind.Accumulation => "РегистрыНакопления",
                _ => "РегистрыБухгалтерии"
            };
            var reg = MetadataShapes.Find(c, s, md, collection, name, "register");
            var docs = s.Track(Dispatch.Get(md, "Документы", c.Error), "Документы");
            int n = Dispatch.CallInt(docs, "Количество", c.Error);
            var list = new List<string>();
            for (int i = 0; i < n; i++)
            {
                using var ds = new ComScope();
                var doc = ds.Track(Dispatch.Call(docs, "Получить", c.Error, i), "Документ");
                var moves = ds.Track(Dispatch.Get(doc, "Движения", c.Error), "Движения");
                if (Convert.ToBoolean(Dispatch.Call(moves, "Содержит", c.Error, reg)))
                    list.Add(Dispatch.GetString(doc, "Имя", c.Error)!);
            }
            return list;
        });
}
