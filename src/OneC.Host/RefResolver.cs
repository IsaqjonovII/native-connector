using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>What the resolver needs to know about a referenced table; cached per base.</summary>
internal sealed record RefTable(
    string Kind,
    string Name,
    bool HasName,
    bool HasCode,
    bool HasNumber,
    bool HasOwner,
    bool HasFolders,
    IReadOnlySet<string> Attributes,
    IReadOnlyDictionary<string, string>? EnumValues)
{
    public string Table => Kind + "." + Name;

    public string Managers => Kind switch
    {
        "Справочник" => "Справочники",
        "Документ" => "Документы",
        "ПланСчетов" => "ПланыСчетов",
        "ПланВидовХарактеристик" => "ПланыВидовХарактеристик",
        "ПланВидовРасчета" => "ПланыВидовРасчета",
        "Перечисление" => "Перечисления",
        _ => throw new InvalidOperationException(Kind)
    };

    private static readonly (string Xml, string Kind)[] Kinds =
    {
        ("CatalogRef.", "Справочник"), ("DocumentRef.", "Документ"), ("ChartOfAccountsRef.", "ПланСчетов"),
        ("ChartOfCharacteristicTypesRef.", "ПланВидовХарактеристик"), ("ChartOfCalculationTypesRef.", "ПланВидовРасчета"),
        ("EnumRef.", "Перечисление")
    };

    /// <summary>(Справочник, Контрагенты) for <c>CatalogRef.Контрагенты</c>; null for a type writes do not resolve.</summary>
    public static (string Kind, string Name)? Parse(string xmlType)
    {
        foreach (var (xml, kind) in Kinds)
            if (xmlType.StartsWith(xml, StringComparison.Ordinal) && xmlType.Length > xml.Length)
                return (kind, xmlType[xml.Length..]);
        return null;
    }
}

/// <summary>
/// Turns a payload value into a 1C reference for one write (D38). The old adapter's
/// НайтиСправочникПоРеквизитам (main.os:9460) searched by name prefix, took the first hit
/// whatever its owner or deletion mark, and created a catalog item on a miss. Here:
/// <list type="bullet">
/// <item>an <c>id</c> is authoritative when the object exists (a dead one is reported, then the other keys are tried);</item>
/// <item>the strongest key the payload gives — ИНН, account number, code, document number — decides; a miss on it is a miss, never a fall back to the name;</item>
/// <item>matches are exact, active (no deletion mark), not folders, and scoped to the owner when there is one;</item>
/// <item>two matches are an error, not a pick;</item>
/// <item>nothing is created unless the payload asks for it (<c>СоздатьНовый</c> / <c>forceCreate</c>, or <c>allowCreate</c> for a bank account).</item>
/// </list>
/// Every failure becomes an ERROR diagnostic; the writer refuses the document when there is one.
/// References returned are owned by the writer's scope. Session thread only.
/// </summary>
internal sealed class RefResolver
{
    /// <summary>Never created, whatever the payload says (the old list, main.os:338).</summary>
    private static readonly string[] NeverCreate = { "Справочник.Организации", "ПланСчетов.", "Документ.", "ПланВидовРасчета." };

    private readonly SessionContext _ctx;
    private readonly ComScope _scope;
    private readonly List<WriteDiagnostic> _diagnostics;
    private readonly Dictionary<string, object> _cache = new(StringComparer.Ordinal);

    public RefResolver(SessionContext ctx, ComScope scope, List<WriteDiagnostic> diagnostics)
    {
        _ctx = ctx;
        _scope = scope;
        _diagnostics = diagnostics;
    }

    /// <summary>A reference for <paramref name="field"/>, or null with an ERROR diagnostic.</summary>
    /// <param name="ownerHint">The owner to scope by when the value names none (the document's Организация for СчетОрганизации, …).</param>
    public object? Resolve(string field, FieldType type, JsonNode value, object? ownerHint)
    {
        try
        {
            return value switch
            {
                JsonObject spec => FromSpec(field, type, spec, ownerHint),
                JsonValue v when v.TryGetValue(out string? s) => FromText(field, type, s, ownerHint),
                _ => Fail(field, "reference_value_invalid", $"expected an object {{\"type\": …}} or a string, got {value.ToJsonString()}")
            };
        }
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime)
        {
            return Fail(field, "reference_lookup_failed", e.Message);
        }
    }

    // ---------------- fill-empty helpers (no diagnostics of their own) ----------------

    /// <summary>The enum value named <paramref name="name"/> of the field's only enum type, or null.</summary>
    public object? TryEnum(FieldType type, string name)
    {
        var xml = type.Refs.Where(r => r.StartsWith("EnumRef.", StringComparison.Ordinal)).SingleOrDefaultIfMany();
        if (xml is null || RefTable.Parse(xml) is not { } kn) return null;
        var t = Table(kn.Kind, kn.Name);
        return EnumName(t, name) is { } n && n == name ? EnumRef(t, n) : null;
    }

    /// <summary>
    /// The bank account to use when the payload gave none: the owner's ОсновнойБанковскийСчет
    /// when it is set and active, else the owner's only active account. Null when there is no
    /// such single answer, with the number of active accounts found. The old fill took the
    /// owner's first account whatever its state (main.os:3930, quirk Q19).
    /// </summary>
    public (object? Account, int Active) DefaultAccount(string accountXml, string ownerTable, object owner)
    {
        if (RefTable.Parse(accountXml) is not { } kn) return (null, 0);
        var accounts = Table(kn.Kind, kn.Name);
        if (RefTable.Parse(ownerTable) is { } okn && Table(okn.Kind, okn.Name) is { } ot && ot.Attributes.Contains("ОсновнойБанковскийСчет"))
        {
            using var s = new ComScope();
            var q = QueryKit.NewQuery(_ctx, s,
                $"ВЫБРАТЬ Т.ОсновнойБанковскийСчет КАК a ИЗ {ot.Table} КАК Т " +
                "ГДЕ Т.Ссылка = &o И НЕ Т.ОсновнойБанковскийСчет.ПометкаУдаления");
            QueryKit.SetParameter(_ctx, q, "o", owner);
            var cur = QueryKit.Execute(_ctx, s, q);
            if (cur.CallBool("Следующий", _ctx.Error))
            {
                var a = _scope.Track(cur.Get("a", _ctx.Error), "ОсновнойБанковскийСчет");
                if (OneCValue.RefGuid(a, _ctx) is { } g && g != LegacyValue.EmptyGuid) return (a, 1);
            }
        }
        var hits = Search(accounts, owner);
        return hits.Count == 1 ? (hits[0], 1) : (null, hits.Count);
    }

    // ---------------- entry points ----------------

    private object? FromSpec(string field, FieldType type, JsonObject spec, object? ownerHint)
    {
        string? xml = Str(spec, "type");
        if (xml is null)
        {
            xml = type.SingleRef ?? type.Refs.Where(r => !r.StartsWith("EnumRef.", StringComparison.Ordinal)).SingleOrDefaultIfMany();
            if (xml is null)
                return Fail(field, "reference_type_missing",
                            $"the value must name its 'type'; the field accepts {Accepts(type)}");
        }
        else if (!type.Wide && !type.Refs.Contains(xml))
            return Fail(field, "reference_type_not_accepted", $"'{xml}' is not a type of this field; it accepts {Accepts(type)}");

        if (RefTable.Parse(xml) is not { } kn)
            return Fail(field, "reference_type_unsupported", $"'{xml}' cannot be resolved by the write path");
        var table = Table(kn.Kind, kn.Name);
        if (table.Kind == "Перечисление")
            return EnumValue(field, table, Str(spec, "value") ?? Str(spec, "name") ?? Str(spec, "Наименование") ?? Str(spec, "synonym") ?? Str(spec, "Синоним"));

        object? owner = ownerHint;
        if ((spec["Владелец"] ?? spec["owner"]) is { } ownerNode)
        {
            owner = OwnerOf(field, ownerNode);
            if (owner is null) return null;                      // already reported
        }
        return Lookup(field, table, spec, owner);
    }

    /// <summary>
    /// A bare string for a reference field: an enum value name/synonym, or the exact
    /// Наименование / Код of one active object across the field's reference types. The old code
    /// tried each type in turn and created an item named by the string in the first catalog
    /// that allowed it (quirk Q10); here more than one match across the types is an error.
    /// </summary>
    private object? FromText(string field, FieldType type, string text, object? ownerHint)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail(field, "reference_value_invalid", "empty string");
        if (type.Wide) return Fail(field, "reference_type_missing", "this field accepts many types; send {\"type\": …} with the value");

        var hits = new List<object>();
        foreach (var xml in type.Refs)
        {
            if (RefTable.Parse(xml) is not { } kn) continue;
            var table = Table(kn.Kind, kn.Name);
            if (table.Kind == "Перечисление")
            {
                if (EnumName(table, text) is { } name) hits.Add(EnumRef(table, name));
                continue;
            }
            if (table.Kind == "Документ") continue;              // a document is never named by text
            object? owner = table.HasOwner ? ownerHint : null;
            if (table.HasName) hits.AddRange(Search(table, owner, ("Наименование", text)));
            if (hits.Count == 0 && table.HasCode) hits.AddRange(Search(table, owner, ("Код", text)));
        }
        return hits.Count switch
        {
            1 => hits[0],
            0 => Fail(field, "unresolved_reference", $"no active {string.Join(" / ", type.Refs)} named or coded '{text}'"),
            _ => Fail(field, "ambiguous_reference", $"'{text}' matches {hits.Count} objects; send an id or a key that tells them apart")
        };
    }

    // ---------------- lookup ----------------

    private object? Lookup(string field, RefTable t, JsonObject spec, object? owner)
    {
        string? id = Str(spec, "id") ?? Str(spec, "uuid") ?? Str(spec, "uid");
        if (id is not null)
        {
            if (!Guid.TryParse(id, out _)) return Fail(field, "reference_value_invalid", $"'{id}' is not a GUID");
            if (ById(field, t, id) is { } byId) return byId;
            _diagnostics.Add(new WriteDiagnostic("stale_ref_id_ignored", WriteDiagnostic.Warn, field, $"{t.Table} {id} does not exist; trying the other keys"));
        }

        string? name = t.HasName ? Str(spec, "Наименование") ?? Str(spec, "name") : null;
        var key = PrimaryKey(t, spec);
        bool create = Flag(spec, "СоздатьНовый", "forceCreate");
        bool allowCreate = t.Table == "Справочник.БанковскиеСчета" && Flag(spec, "СоздатьЕслиНетоПусто", "allowCreate");

        if (create) return Create(field, t, spec, owner, key, name);

        List<object> hits;
        if (key is { } k)
        {
            hits = Search(t, owner, k);
            if (hits.Count > 1 && name is not null) hits = Search(t, owner, k, ("Наименование", name));
        }
        else if (name is not null) hits = Search(t, owner, ("Наименование", name));
        else if (id is not null) return Fail(field, "unresolved_reference", $"{t.Table} {id} does not exist and no other key was given");
        else return Fail(field, "reference_key_missing", $"no id, ИНН, code, number or name to find a {t.Table} by");

        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            return Fail(field, "ambiguous_reference", $"{hits.Count} active {t.Table} match {Describe(key, name)}; send an id");
        if (allowCreate) return Create(field, t, spec, owner, key, name);
        return Fail(field, "unresolved_reference", $"no active {t.Table} with {Describe(key, name)}{(owner is null ? "" : " for this owner")}");
    }

    /// <summary>The key that identifies the object when given — a miss on it is a miss.</summary>
    private static (string Field, object Value)? PrimaryKey(RefTable t, JsonObject spec)
    {
        if (t.Attributes.Contains("ИНН") && (Str(spec, "ИНН") ?? Str(spec, "tin")) is { } inn) return ("ИНН", inn);
        if (t.Attributes.Contains("НомерСчета") && (Str(spec, "НомерСчета") ?? Str(spec, "Номер") ?? Str(spec, "number")) is { } acc)
            return ("НомерСчета", acc);
        if (t.Kind == "Документ")
            return (Str(spec, "Номер") ?? Str(spec, "number") ?? Str(spec, "code")) is { } no ? ("Номер", no) : null;
        if (t.HasCode && (Str(spec, "Код") ?? Str(spec, "code") ?? (t.Kind == "ПланСчетов" ? Str(spec, "Номер") : null)) is { } code)
            return ("Код", code);
        if (t.Kind == "ПланСчетов" && (Str(spec, "КодБыстрогоВыбора") ?? Str(spec, "quickCode")) is { } quick)
            return ("КодБыстрогоВыбора", quick);
        return null;
    }

    private object? ById(string field, RefTable t, string id)
    {
        string cacheKey = t.Table + "#" + id;
        if (_cache.TryGetValue(cacheKey, out var hit)) return hit;
        var r = QueryKit.RefByGuid(_ctx, _scope, t.Managers, t.Name, id);
        using var s = new ComScope();
        var q = QueryKit.NewQuery(_ctx, s, $"ВЫБРАТЬ Т.ПометкаУдаления КАК d ИЗ {t.Table} КАК Т ГДЕ Т.Ссылка = &r");
        QueryKit.SetParameter(_ctx, q, "r", r);
        var cur = QueryKit.Execute(_ctx, s, q);
        if (!cur.CallBool("Следующий", _ctx.Error)) return null;
        if (Convert.ToBoolean(cur.Get("d", _ctx.Error)))
            _diagnostics.Add(new WriteDiagnostic("deleted_reference", WriteDiagnostic.Warn, field, $"{t.Table} {id} is marked for deletion"));
        return _cache[cacheKey] = r;
    }

    /// <summary>Active, non-folder objects of <paramref name="t"/> where every condition holds exactly (at most 3).</summary>
    private List<object> Search(RefTable t, object? owner, params (string Field, object Value)[] where)
    {
        var sql = new List<string> { "НЕ Т.ПометкаУдаления" };
        if (t.HasFolders) sql.Add("НЕ Т.ЭтоГруппа");
        if (owner is not null && t.HasOwner) sql.Add("Т.Владелец = &owner");
        for (int i = 0; i < where.Length; i++) sql.Add($"Т.{where[i].Field} = &p{i}");
        string text = $"ВЫБРАТЬ ПЕРВЫЕ 3 Т.Ссылка КАК r ИЗ {t.Table} КАК Т ГДЕ {string.Join(" И ", sql)}";

        List<object> Run(bool numericCode)
        {
            using var s = new ComScope();
            var q = QueryKit.NewQuery(_ctx, s, text);
            if (owner is not null && t.HasOwner) QueryKit.SetParameter(_ctx, q, "owner", owner);
            for (int i = 0; i < where.Length; i++)
            {
                object v = where[i].Value;
                if (numericCode && where[i].Field == "Код" && decimal.TryParse(v as string, System.Globalization.NumberStyles.Number,
                                                                               System.Globalization.CultureInfo.InvariantCulture, out var d))
                    v = d;
                QueryKit.SetParameter(_ctx, q, "p" + i, v);
            }
            var cur = QueryKit.Execute(_ctx, s, q);
            var list = new List<object>();
            while (cur.CallBool("Следующий", _ctx.Error)) list.Add(_scope.Track(cur.Get("r", _ctx.Error), t.Table));
            return list;
        }

        try { return Run(numericCode: false); }
        // A numeric code compared with a string parameter; ask again with a number.
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime && where.Any(w => w.Field == "Код")) { return Run(numericCode: true); }
    }

    private object? OwnerOf(string field, JsonNode ownerNode)
    {
        if (ownerNode is not JsonObject o || Str(o, "type") is not { } xml || RefTable.Parse(xml) is not { } kn)
            return Fail(field, "reference_value_invalid", "'Владелец' must be an object with its 'type'");
        return Lookup(field + ".Владелец", Table(kn.Kind, kn.Name), o, owner: null);
    }

    // ---------------- explicit create ----------------

    /// <summary>
    /// Creates the object only because the payload asked. Only what the payload gives is set:
    /// name, key, owner, and attributes of the table given as scalars or as references that
    /// resolve by lookup (never a nested create). The old universal mapper copied every key and
    /// created nested items recursively (main.os:10591).
    /// </summary>
    private object? Create(string field, RefTable t, JsonObject spec, object? owner, (string Field, object Value)? key, string? name)
    {
        if (t.Kind != "Справочник" || NeverCreate.Any(n => t.Table.StartsWith(n, StringComparison.Ordinal)))
            return Fail(field, "create_not_allowed", $"{t.Table} is never created by a write");
        if (t.HasOwner && owner is null)
            return Fail(field, "create_owner_missing", $"{t.Table} needs an owner ('Владелец') to be created");

        string description = name ?? (key is { } k ? k.Value.ToString()! : "");
        if (description.Length == 0) return Fail(field, "create_name_missing", $"no name to create a {t.Table} with");

        var item = _scope.Track(Dispatch.Call(QueryKit.Manager(_ctx, _scope, t.Managers, t.Name), "СоздатьЭлемент", _ctx.Error), t.Table + " (new)");
        if (t.HasOwner) Dispatch.Set(item, "Владелец", owner, _ctx.Error);
        if (t.HasName) Dispatch.Set(item, "Наименование", description, _ctx.Error);
        if (key is { } kk && kk.Field != "Код") Dispatch.Set(item, kk.Field, kk.Value, _ctx.Error);
        if (key is { Field: "Код" } kc) Dispatch.Set(item, "Код", kc.Value, _ctx.Error);
        else if (t.HasCode) Dispatch.Call(item, "УстановитьНовыйКод", _ctx.Error);

        foreach (var (k2, v) in spec)
        {
            if (v is null || !t.Attributes.Contains(k2) || k2 == key?.Field) continue;
            switch (v)
            {
                case JsonValue jv when jv.TryGetValue(out string? s): Dispatch.Set(item, k2, s, _ctx.Error); break;
                case JsonValue jv when jv.TryGetValue(out bool b): Dispatch.Set(item, k2, b, _ctx.Error); break;
                case JsonValue jv when jv.TryGetValue(out decimal d): Dispatch.Set(item, k2, d, _ctx.Error); break;
                case JsonObject o when Str(o, "type") is { } xml && RefTable.Parse(xml) is { } kn:
                    var nested = kn.Kind == "Перечисление"
                        ? EnumValue($"{field}.{k2}", Table(kn.Kind, kn.Name), Str(o, "value") ?? Str(o, "name") ?? Str(o, "Наименование"))
                        : Lookup($"{field}.{k2}", Table(kn.Kind, kn.Name), StripCreate(o), owner: null);
                    if (nested is null) return null;
                    Dispatch.Set(item, k2, nested, _ctx.Error);
                    break;
            }
        }
        Dispatch.Call(item, "Записать", _ctx.Error);
        var r = _scope.Track(Dispatch.Get(item, "Ссылка", _ctx.Error), t.Table);
        _diagnostics.Add(new WriteDiagnostic("explicit_create", WriteDiagnostic.Info, field,
                                             $"created {t.Table} '{description}' {OneCValue.RefGuid(r, _ctx)}"));
        return r;
    }

    private static JsonObject StripCreate(JsonObject o)
    {
        var c = (JsonObject)o.DeepClone();
        foreach (var k in new[] { "СоздатьНовый", "forceCreate", "СоздатьЕслиНетоПусто", "allowCreate" }) c.Remove(k);
        return c;
    }

    // ---------------- enums ----------------

    private object? EnumValue(string field, RefTable t, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Fail(field, "reference_value_invalid", $"no value for {t.Table}");
        return EnumName(t, value) is { } name
            ? EnumRef(t, name)
            : Fail(field, "unresolved_reference", $"{t.Table} has no value named '{value}' ({string.Join(", ", t.EnumValues!.Values.Distinct().Take(12))}…)");
    }

    /// <summary>The value's name by name or synonym, case-insensitive (НайтиЗначениеПеречисления, main.os:160).</summary>
    private static string? EnumName(RefTable t, string value) =>
        t.EnumValues!.TryGetValue(Normalize(value), out var name) ? name : null;

    private object EnumRef(RefTable t, string name)
    {
        string key = t.Table + "#" + name;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        return _cache[key] = _scope.Track(Dispatch.Get(QueryKit.Manager(_ctx, _scope, t.Managers, t.Name), name, _ctx.Error), key);
    }

    private static string Normalize(string s) => string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------- table metadata ----------------

    private RefTable Table(string kind, string name) =>
        SchemaCache.Get(_ctx, "reftable", kind + "." + name, (ctx, full) => LoadTable(ctx, kind, name, full));

    private static RefTable LoadTable(SessionContext ctx, string kind, string name, string full)
    {
        using var scope = new ComScope();
        var md = scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
        var meta = Dispatch.Call(md, "НайтиПоПолномуИмени", ctx.Error, full) is { } m && OneCValue.IsCom(m) ? m
            : throw OneCException.Host($"{full} not found in the configuration", ctx.Error, "Resolve", full);
        scope.Add(meta, full);

        var attributes = new HashSet<string>(StringComparer.Ordinal);
        if (Dispatch.HasMember(meta, "Реквизиты"))
        {
            var attrs = scope.Track(Dispatch.Get(meta, "Реквизиты", ctx.Error), "Реквизиты");
            int n = Dispatch.CallInt(attrs, "Количество", ctx.Error);
            for (int i = 0; i < n; i++)
            {
                using var s = new ComScope();
                attributes.Add(Dispatch.GetString(s.Track(Dispatch.Call(attrs, "Получить", ctx.Error, i), "Реквизит"), "Имя", ctx.Error)!);
            }
        }

        Dictionary<string, string>? values = null;
        if (kind == "Перечисление")
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            var vs = scope.Track(Dispatch.Get(meta, "ЗначенияПеречисления", ctx.Error), "ЗначенияПеречисления");
            int n = Dispatch.CallInt(vs, "Количество", ctx.Error);
            for (int i = 0; i < n; i++)
            {
                using var s = new ComScope();
                var v = s.Track(Dispatch.Call(vs, "Получить", ctx.Error, i), "ЗначениеПеречисления");
                string vn = Dispatch.GetString(v, "Имя", ctx.Error)!;
                values.TryAdd(Normalize(vn), vn);
                if (Dispatch.GetString(v, "Синоним", ctx.Error) is { Length: > 0 } syn) values.TryAdd(Normalize(syn), vn);
            }
        }

        bool hasOwner = false;
        if (Dispatch.HasMember(meta, "Владельцы"))
        {
            var owners = scope.Track(Dispatch.Get(meta, "Владельцы", ctx.Error), "Владельцы");
            hasOwner = Dispatch.CallInt(owners, "Количество", ctx.Error) > 0;
        }
        return new RefTable(
            kind, name,
            HasName: MetadataShapes.Length(ctx, meta, "ДлинаНаименования") > 0,
            HasCode: MetadataShapes.Length(ctx, meta, "ДлинаКода") > 0,
            HasNumber: MetadataShapes.Length(ctx, meta, "ДлинаНомера") > 0,
            HasOwner: hasOwner,
            HasFolders: kind == "Справочник" && CatalogSchemas.Get(ctx, name).HasFolders,
            Attributes: attributes,
            EnumValues: values);
    }

    // ---------------- helpers ----------------

    private object? Fail(string field, string code, string message)
    {
        _diagnostics.Add(new WriteDiagnostic(code, WriteDiagnostic.Error, field, message));
        return null;
    }

    private static string Accepts(FieldType t) =>
        t.Wide ? "many types" : string.Join(", ", t.Refs.Concat(new[] { t.String ? "string" : null, t.Number ? "number" : null,
                                                                   t.Date ? "date" : null, t.Boolean ? "boolean" : null }).OfType<string>());

    private static string Describe((string Field, object Value)? key, string? name) =>
        key is { } k ? $"{k.Field} = '{k.Value}'" + (name is null ? "" : $" and Наименование = '{name}'") : $"Наименование = '{name}'";

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v ? (v.TryGetValue(out string? s) ? (s.Length > 0 ? s : null)
                                 : v.TryGetValue(out long l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 : v.TryGetValue(out decimal d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : null)
        : null;

    private static bool Flag(JsonObject o, params string[] keys) =>
        keys.Any(k => o[k] is JsonValue v && (v.TryGetValue(out bool b) ? b : v.TryGetValue(out string? s) &&
                                                s.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "да" or "истина"));
}

internal static class EnumerableExtensions
{
    /// <summary>The single element, or null when there are none or several.</summary>
    public static T? SingleOrDefaultIfMany<T>(this IEnumerable<T> source) where T : class
    {
        T? found = null;
        foreach (var x in source)
        {
            if (found is not null) return null;
            found = x;
        }
        return found;
    }
}
