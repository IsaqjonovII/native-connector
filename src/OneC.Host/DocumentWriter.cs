using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>The old create response (main.os:16994) plus the session that did it.</summary>
public sealed record DocumentCreated(
    string Document,
    string? RequestedDocument,
    string Id,
    string? Number,
    DateTime Date,
    bool Posted,
    bool Created,
    bool Idempotent,
    IReadOnlyList<WriteDiagnostic> Diagnostics,
    int SessionId);

/// <summary>The old PUT response (main.os:17392): posted is read from what was written, not intended.</summary>
public sealed record DocumentUpdated(string Id, bool Posted, bool WasPosted, IReadOnlyList<WriteDiagnostic> Diagnostics, int SessionId);

/// <summary>1C refused the write itself (Записать raised). Nothing was written: a posting write is one transaction.</summary>
public sealed class DocumentWriteFailed : Exception
{
    public OneCException OneC { get; }
    public string Document { get; }
    public string WriteMode { get; }
    public IReadOnlyList<WriteDiagnostic> Diagnostics { get; }

    public DocumentWriteFailed(OneCException oneC, string document, string writeMode, string userMessages,
                               IReadOnlyList<WriteDiagnostic> diagnostics)
        : base(userMessages.Length == 0 ? oneC.Message : $"{oneC.Message} | [1С] {userMessages}", oneC)
    {
        OneC = oneC;
        Document = document;
        WriteMode = writeMode;
        Diagnostics = diagnostics;
    }
}

/// <summary>
/// Creates a document from the old adapter's body (D38): same keys, same markers, same
/// response — without the old write quirks. In order:
/// <list type="number">
/// <item>parse and check the body before any COM call (<see cref="DocumentWriteBody"/>);</item>
/// <item>idempotency: an active document whose Комментарий carries <c>&lt;marker&gt;:</c> is returned as is — deletion-marked ones never count (Q5);</item>
/// <item>convert every value by the field's type: exact references (<see cref="RefResolver"/>), strict dates and numbers, strings never guessed to be dates (Q12), too-long strings refused instead of cut;</item>
/// <item>fill only what the payload left empty: ВидОперации of a sales/purchase document from its rows, the organisation's / counterparty's bank account when there is a single answer — never a value the caller sent (Q4);</item>
/// <item>refuse the document when any value could not be converted, before creating it;</item>
/// <item>one <c>Записать</c>: posting or not, no exchange mode (Q1), no draft left behind by a failed post (Q7), no second post attempt against a document found by number (Q8);</item>
/// <item>answer from the written object itself, not a lookup by number (Q9).</item>
/// </list>
/// </summary>
public sealed class DocumentWriter
{
    /// <summary>
    /// Two creates with the same marker must not both miss the lookup and both write. Striped
    /// by (base, marker): bounded, and a marker always maps to the same stripe in this host.
    /// </summary>
    internal static readonly SemaphoreSlim[] MarkerStripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static readonly object Invalid = new();

    private readonly WriteService _writes;

    public DocumentWriter(WriteService writes) => _writes = writes;

    public DocumentCreated Create(string baseName, string docType, JsonObject body, bool post, CancellationToken ct = default)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        var parsed = DocumentWriteBody.Parse(body, _writes.CommentPrefix);
        var stripe = parsed.IdempotencyMarker is { } m
            ? MarkerStripes[(uint)StringComparer.Ordinal.GetHashCode(baseName.ToLowerInvariant() + "|" + m) % (uint)MarkerStripes.Length]
            : null;

        if (stripe is not null && !stripe.Wait(_writes.GateTimeout, ct))
            throw WriteRejected.MarkerInFlight(parsed.IdempotencyMarker!);
        try
        {
            return _writes.RunGated(baseName, post ? WriteKind.Post : WriteKind.Create, ct,
                                    ctx => CreateIn(ctx, docType, parsed, post));
        }
        finally { stripe?.Release(); }
    }

    private DocumentCreated CreateIn(SessionContext ctx, string requested, DocumentWriteBody body, bool post)
    {
        string name = DocumentSchemas.ResolveBankAlias(ctx, requested);
        string? requestedName = name == requested ? null : requested;
        var schema = WriteSchemas.Get(ctx, name);
        if (!schema.Attributes.ContainsKey(DocumentWriteBody.CommentField))
            throw WriteRejected.Bad($"{name} has no '{DocumentWriteBody.CommentField}' attribute to carry the AIBA marker");

        var diagnostics = new List<WriteDiagnostic>(body.Diagnostics);
        using var scope = new ComScope();

        if (body.IdempotencyMarker is { } marker && FindByMarker(ctx, scope, name, marker, diagnostics) is { } existing)
            return existing with { RequestedDocument = requestedName };

        var resolver = new RefResolver(ctx, scope, diagnostics);
        var header = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in body.Header.OrderBy(h => Order(h.Key)))
        {
            if (schema.Attribute(field) is not { } type)
            {
                diagnostics.Add(new WriteDiagnostic("unknown_attribute", WriteDiagnostic.Warn, field, $"{name} has no attribute '{field}'; ignored"));
                continue;
            }
            header[field] = Convert(field, type, value, OwnerHint(field, null, header), resolver, diagnostics);
        }

        var sections = new List<(string Name, List<Dictionary<string, object?>> Rows)>();
        foreach (var (section, rows) in body.Tabular)
        {
            if (!schema.Tabular.TryGetValue(section, out var ts))
            {
                diagnostics.Add(new WriteDiagnostic("unknown_tabular_section", WriteDiagnostic.Warn, section, $"{name} has no tabular section '{section}'; ignored"));
                continue;
            }
            var converted = new List<Dictionary<string, object?>>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (column, value) in rows[i].OrderBy(c => Order(c.Key)))
                {
                    if (value is null || column.Equals("LineNumber", StringComparison.OrdinalIgnoreCase)) continue;
                    string where = $"{section}[{i}].{column}";
                    if (!ts.Columns.TryGetValue(column, out var type))
                    {
                        diagnostics.Add(new WriteDiagnostic("unknown_tabular_column", WriteDiagnostic.Warn, where, $"no column '{column}'; ignored"));
                        continue;
                    }
                    row[column] = Convert(where, type, value, OwnerHint(column, row, header), resolver, diagnostics);
                }
                converted.Add(row);
            }
            sections.Add((section, converted));
        }

        FillEmpty(name, schema, header, sections, resolver, diagnostics);

        Reject(diagnostics, "could not be used");

        var obj = scope.Track(Dispatch.Call(QueryKit.Manager(ctx, scope, "Документы", name), "СоздатьДокумент", ctx.Error), "new " + name);
        Dispatch.Set(obj, "Дата", body.Date, ctx.Error);
        if (body.Number is { } number) Dispatch.Set(obj, "Номер", number, ctx.Error);
        Dispatch.Set(obj, DocumentWriteBody.CommentField, body.Comment, ctx.Error);
        foreach (var (field, value) in header) Set(ctx, obj, field, value, field, diagnostics);
        foreach (var (section, rows) in sections)
        {
            var table = scope.Track(Dispatch.Get(obj, section, ctx.Error), section);
            for (int i = 0; i < rows.Count; i++)
            {
                using var rs = new ComScope();
                var line = rs.Track(Dispatch.Call(table, "Добавить", ctx.Error), section + " row");
                foreach (var (column, value) in rows[i]) Set(ctx, line, column, value, $"{section}[{i}].{column}", diagnostics);
            }
        }
        Reject(diagnostics, "were changed by 1C on the way in");

        WriteObject(ctx, obj, name, post ? "Проведение" : "Запись", diagnostics);

        var r = scope.Track(Dispatch.Get(obj, "Ссылка", ctx.Error), "Ссылка");
        string id = OneCValue.RefGuid(r, ctx)
            ?? throw OneCException.Host("written document has no readable reference", ctx.Error, "Create", name);
        string? written = Dispatch.GetString(obj, "Номер", ctx.Error)?.Trim();
        if (body.Number is { } asked && !string.Equals(asked.Trim(), written, StringComparison.Ordinal))
            diagnostics.Add(new WriteDiagnostic("number_changed_by_1c", WriteDiagnostic.Warn, "Номер", $"asked '{asked}', 1C wrote '{written}'"));
        return new DocumentCreated(name, requestedName, id, written, System.Convert.ToDateTime(Dispatch.Get(obj, "Дата", ctx.Error)),
                                   Dispatch.GetBool(obj, "Проведен", ctx.Error), Created: true, Idempotent: false,
                                   diagnostics, ctx.SessionId);
    }

    // ---------------- update ----------------

    /// <summary>
    /// The old PUT (ОбновитьДокумент, main.os:17093), the typed update contract: header fields by the
    /// same conversion as create, references included. Changed (D38): tabular sections are refused
    /// instead of silently ignored (Q25); a value that fails refuses the update instead of a 200 with
    /// <c>updateErrors</c>; a posted document is changed and posted again in ONE write, so a failed
    /// post leaves it exactly as it was (the old unpost-then-post could leave it unposted); no fill
    /// steps run. Ownership (D53): an AIBA-created document may be updated as before; a customer-created
    /// one only by compare-and-set — <paramref name="expected"/> field values and/or
    /// <paramref name="expectedVersion"/> (ВерсияДанных) the caller read — and its Комментарий can never
    /// take the AIBA marker.
    /// </summary>
    /// <param name="post">Post after the change; null keeps the document's posted state.</param>
    /// <param name="autoUnpost">A posted document may be changed only when true (the old guard).</param>
    public DocumentUpdated Update(string baseName, string docType, string id, JsonObject fields, bool? post, bool autoUnpost,
                                  CancellationToken ct = default, JsonObject? expected = null, string? expectedVersion = null)
    {
        ReadService.ValidateIdentifier(docType, nameof(docType));
        if (!Guid.TryParse(id, out _)) throw WriteRejected.Bad($"'{id}' is not a GUID");
        var given = new List<KeyValuePair<string, JsonNode>>();
        string? newComment = null;
        foreach (var (k, v) in fields)
        {
            if (k.StartsWith('_') || v is null) continue;
            if (k == "tabularSections" || v is JsonArray)
                throw WriteRejected.Bad($"'{k}': tabular sections cannot be changed by an update; create a new document (D38)");
            if (k == DocumentWriteBody.CommentField)
                newComment = v is JsonValue cv && cv.TryGetValue(out string? c) ? c : throw WriteRejected.Bad($"'{k}' must be text");
            given.Add(new(k is "Date" or "date" ? "Дата"
                          : k.Equals("Номер", StringComparison.OrdinalIgnoreCase) || k.Equals("number", StringComparison.OrdinalIgnoreCase) ? "Номер"
                          : k, v));
        }

        return _writes.RunGated(baseName, WriteKind.Update, ct, ctx =>
        {
            using var scope = new ComScope();
            var obj = _writes.LoadByRef(ctx, scope, docType, id, "Update");
            bool owned = _writes.IsOwned(ctx, obj);
            bool marked = newComment?.StartsWith(_writes.CommentPrefix, StringComparison.Ordinal) == true;
            if (owned && newComment is not null && !marked)
                throw WriteRejected.Bad($"'{DocumentWriteBody.CommentField}' must keep its AIBA marker ('{_writes.CommentPrefix}…')");
            if (!owned && marked)
                throw WriteRejected.Bad($"'{DocumentWriteBody.CommentField}' must not take an AIBA marker: {docType} {id} was not created by AIBA");
            if (!owned && (expected is null || expected.Count == 0) && expectedVersion is null)
                throw new WriteRejected($"{docType} {id} was not created by AIBA: an update needs 'expected' values or 'expectedVersion' (compare-and-set)",
                                        unprocessable: true,
                                        new[] { new WriteDiagnostic("expected_state_required", WriteDiagnostic.Error, null, "customer-created document") });
            var schema = WriteSchemas.Get(ctx, docType);
            var conflicts = ExpectedState.Conflicts(ctx, obj, expected, expectedVersion,
                                                    f => schema.Attribute(f) is not null || f is "Дата" or "Номер" or "Проведен" or "ПометкаУдаления",
                                                    $"Документ.{docType}");
            if (conflicts.Count > 0) throw ExpectedState.Refusal(conflicts);
            bool wasPosted = Dispatch.GetBool(obj, "Проведен", ctx.Error);
            if (wasPosted && !autoUnpost)
                throw new WriteRejected($"{docType} {id} is posted; send autoUnpost=true to change it", unprocessable: true,
                                        new[] { new WriteDiagnostic("requires_unpost", WriteDiagnostic.Error, null, "the document is posted") });

            var diagnostics = new List<WriteDiagnostic>();
            var resolver = new RefResolver(ctx, scope, diagnostics);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (field, value) in given.OrderBy(g => Order(g.Key)))
            {
                FieldType? type = field switch
                {
                    "Дата" => new FieldType(Array.Empty<string>(), false, 0, false, true, false, false),
                    "Номер" => new FieldType(Array.Empty<string>(), true, 0, false, false, false, false),
                    _ => schema.Attribute(field)
                };
                if (type is null)
                {
                    diagnostics.Add(new WriteDiagnostic("unknown_attribute", WriteDiagnostic.Warn, field, $"{docType} has no attribute '{field}'; ignored"));
                    continue;
                }
                values[field] = Convert(field, type, value, OwnerHint(field, null, values), resolver, diagnostics);
            }
            Reject(diagnostics, "could not be used");
            foreach (var (field, value) in values) Set(ctx, obj, field, value, field, diagnostics);
            Reject(diagnostics, "were changed by 1C on the way in");

            bool postAfter = post ?? wasPosted;
            WriteObject(ctx, obj, docType, postAfter ? "Проведение" : wasPosted ? "ОтменаПроведения" : "Запись", diagnostics);
            return new DocumentUpdated(id, postAfter, wasPosted, diagnostics, ctx.SessionId);
        });
    }

    internal static void Reject(List<WriteDiagnostic> diagnostics, string what)
    {
        var errors = diagnostics.Where(d => d.Level == WriteDiagnostic.Error).ToList();
        if (errors.Count > 0)
            throw new WriteRejected($"{errors.Count} value(s) {what}: " +
                                    string.Join("; ", errors.Take(5).Select(e => $"{e.Field}: {e.Message}")),
                                    unprocessable: true, diagnostics);
    }

    // ---------------- idempotency ----------------

    /// <summary>
    /// The newest ACTIVE document whose Комментарий carries <c>&lt;marker&gt;:</c> (the old
    /// НайтиДокументПоМаркеру, main.os:12374, minus its blind spot for deletion-marked ones).
    /// Returned as found: a draft stays a draft — posting it is the caller's call, as before.
    /// </summary>
    private static DocumentCreated? FindByMarker(SessionContext ctx, ComScope scope, string name, string marker,
                                                 List<WriteDiagnostic> diagnostics)
    {
        var q = QueryKit.NewQuery(ctx, scope,
            $"ВЫБРАТЬ ПЕРВЫЕ 5 Т.Ссылка КАК r, Т.Номер КАК n, Т.Дата КАК d, Т.Проведен КАК p, Т.Комментарий КАК c " +
            $"ИЗ Документ.{name} КАК Т ГДЕ НЕ Т.ПометкаУдаления И Т.Комментарий ПОДОБНО &m СПЕЦСИМВОЛ \"~\" " +
            "УПОРЯДОЧИТЬ ПО Т.Дата УБЫВ");
        QueryKit.SetParameter(ctx, q, "m", "%" + WriteService.EscapeLike(marker + ":") + "%");
        var cur = QueryKit.Execute(ctx, scope, q);
        DocumentCreated? found = null;
        int matches = 0;
        while (cur.CallBool("Следующий", ctx.Error))
        {
            // ПОДОБНО is case-insensitive; the marker must also start the token (not "XAIBA_…").
            string comment = cur.Get("c", ctx.Error) as string ?? "";
            int at = comment.IndexOf(marker + ":", StringComparison.Ordinal);
            if (at < 0 || (at > 0 && (char.IsLetterOrDigit(comment[at - 1]) || comment[at - 1] == '_'))) continue;
            if (++matches > 1) continue;
            using var rs = new ComScope();
            var r = rs.Track(cur.Get("r", ctx.Error), "Ссылка");
            found = new DocumentCreated(name, null, OneCValue.RefGuid(r, ctx)!, (cur.Get("n", ctx.Error) as string)?.Trim(),
                                        System.Convert.ToDateTime(cur.Get("d", ctx.Error)), System.Convert.ToBoolean(cur.Get("p", ctx.Error)),
                                        Created: false, Idempotent: true, diagnostics, ctx.SessionId);
        }
        if (matches > 1)
            diagnostics.Add(new WriteDiagnostic("duplicate_marker", WriteDiagnostic.Warn, DocumentWriteBody.CommentField,
                                                $"{matches} active documents carry '{marker}:'; returned the newest"));
        return found;
    }

    // ---------------- values ----------------

    /// <summary>Owners first: a bank account or a contract is looked up within its owner.</summary>
    private static int Order(string field) => field switch
    {
        "Организация" => 0,
        "Контрагент" => 1,
        _ => 2
    };

    private static object? OwnerHint(string field, Dictionary<string, object?>? row, Dictionary<string, object?> header)
    {
        string? owner = field switch
        {
            "СчетОрганизации" => "Организация",
            "СчетКонтрагента" or "ДоговорКонтрагента" => "Контрагент",
            _ => null
        };
        if (owner is null) return null;
        object? v = row?.GetValueOrDefault(owner) ?? header.GetValueOrDefault(owner);
        return OneCValue.IsCom(v) ? v : null;
    }

    /// <summary>
    /// A payload value as the field's type takes it, or <see cref="Invalid"/> with an ERROR
    /// diagnostic. A string goes to a string-typed field as text — even when it looks like a
    /// date — then to a date, number or boolean field by strict parsing, then to references.
    /// </summary>
    internal static object? Convert(string field, FieldType type, JsonNode value, object? ownerHint,
                                   RefResolver resolver, List<WriteDiagnostic> diagnostics)
    {
        object? Fail(string code, string message)
        {
            diagnostics.Add(new WriteDiagnostic(code, WriteDiagnostic.Error, field, message));
            return Invalid;
        }

        switch (value)
        {
            case JsonObject:
                return type.HasRefs
                    ? resolver.Resolve(field, type, value, ownerHint) ?? Invalid
                    : Fail("value_type_mismatch", "this field takes no reference");
            case JsonArray:
                return Fail("value_type_mismatch", "an array is only allowed as a tabular section");
            case JsonValue v when v.TryGetValue(out string? s):
                if (type.String)
                    return type.StringLength > 0 && s.Length > type.StringLength
                        ? Fail("value_too_long", $"{s.Length} characters; the field holds {type.StringLength}")
                        : s;
                if (type.Date)
                    return DocumentWriteBody.ParseDate(s) is { } d ? d : Fail("invalid_date", $"'{s}' is not yyyy-MM-dd[THH:mm:ss]");
                if (type.Number)
                    return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                        ? n : Fail("invalid_number", $"'{s}' is not a number like 1234.56");
                if (type.Boolean)
                    return s.Trim().ToLowerInvariant() switch
                    {
                        "true" or "1" or "да" or "истина" => true,
                        "false" or "0" or "нет" or "ложь" => false,
                        _ => Fail("invalid_boolean", $"'{s}' is not true/false")
                    };
                return type.HasRefs ? resolver.Resolve(field, type, value, ownerHint) ?? Invalid
                                    : Fail("value_type_mismatch", "this field takes no text");
            case JsonValue v when v.TryGetValue(out bool b):
                return type.Boolean ? b : Fail("value_type_mismatch", "this field takes no true/false");
            case JsonValue v when v.TryGetValue(out decimal d):
                if (type.Number) return d;
                if (type.String) return d.ToString(CultureInfo.InvariantCulture);
                return Fail("value_type_mismatch", "this field takes no number");
            default:
                return Fail("value_type_mismatch", $"unsupported value {value.ToJsonString()}");
        }
    }

    /// <summary>
    /// Sets one value; a number is read back, and a value 1C changed (rounded to the field's
    /// precision) is an ERROR — the old code set values and moved on (quirk Q13).
    /// </summary>
    internal static void Set(SessionContext ctx, object target, string member, object? value, string where,
                            List<WriteDiagnostic> diagnostics)
    {
        if (ReferenceEquals(value, Invalid)) return;
        try { Dispatch.Set(target, member, value, ctx.Error); }
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime)
        {
            diagnostics.Add(new WriteDiagnostic("attribute_set_failed", WriteDiagnostic.Error, where, e.Message));
            return;
        }
        if (value is decimal d && Dispatch.Get(target, member, ctx.Error) is { } back &&
            System.Convert.ToDecimal(back, CultureInfo.InvariantCulture) != d)
            diagnostics.Add(new WriteDiagnostic("value_changed_by_1c", WriteDiagnostic.Error, where,
                                                $"sent {d.ToString(CultureInfo.InvariantCulture)}, the field holds {System.Convert.ToDecimal(back, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}"));
    }

    // ---------------- fill-empty ----------------

    private static readonly HashSet<string> TradeDocuments = new(StringComparer.Ordinal) { "ПоступлениеТоваровУслуг", "РеализацияТоваровУслуг" };

    private static readonly HashSet<string> BankDocuments = new(StringComparer.Ordinal)
    {
        "ПоступлениеНаРасчетныйСчет", "СписаниеСРасчетногоСчета", "ПлатежноеПоручениеВходящее", "ПлатежноеПоручениеИсходящее"
    };

    /// <summary>
    /// Fills only fields the payload left out, only with a single deterministic answer
    /// (D38). Not ported (the payload must carry them): the old ВидОперации guesses for bank
    /// documents, the VAT rate from company history, accounts and subconto from registers and
    /// name similarity, default Склад / Подразделение, Заполнить(Основание).
    /// </summary>
    private static void FillEmpty(string name, WriteSchema schema, Dictionary<string, object?> header,
                                  List<(string Name, List<Dictionary<string, object?>> Rows)> sections,
                                  RefResolver resolver, List<WriteDiagnostic> diagnostics)
    {
        if (TradeDocuments.Contains(name) && schema.Attribute("ВидОперации") is { } opType && !header.ContainsKey("ВидОперации"))
        {
            int goods = sections.Where(s => s.Name == "Товары").Sum(s => s.Rows.Count);
            int services = sections.Where(s => s.Name == "Услуги").Sum(s => s.Rows.Count);
            string? op = goods > 0 && services == 0 ? "Товары" : services > 0 && goods == 0 ? "Услуги" : goods > 0 ? "ТоварыУслуги" : null;
            if (op is not null && resolver.TryEnum(opType, op) is { } opRef)
            {
                header["ВидОперации"] = opRef;
                diagnostics.Add(new WriteDiagnostic("operation_autofilled", WriteDiagnostic.Info, "ВидОперации",
                                                    $"{op} ({goods} goods rows, {services} service rows)"));
            }
        }

        // Bank documents only, as before (main.os:3930): elsewhere an empty account is the
        // document's own business (the write sweep caught it filling an АвансовыйОтчет).
        if (!BankDocuments.Contains(name)) return;
        foreach (var (field, ownerField, ownerTable) in new[]
                 {
                     ("СчетОрганизации", "Организация", "CatalogRef.Организации"),
                     ("СчетКонтрагента", "Контрагент", "CatalogRef.Контрагенты")
                 })
        {
            if (header.ContainsKey(field) || schema.Attribute(field)?.SingleRef is not { } accountXml) continue;
            if (!OneCValue.IsCom(header.GetValueOrDefault(ownerField))) continue;
            var (account, active) = resolver.DefaultAccount(accountXml, ownerTable, header[ownerField]!);
            if (account is not null)
            {
                header[field] = account;
                diagnostics.Add(new WriteDiagnostic("bank_account_autofilled", WriteDiagnostic.Info, field, $"the {ownerField}'s only / main account"));
            }
            else
                // The code the connector's bank_account_missing flow looks for.
                diagnostics.Add(new WriteDiagnostic("unresolved_reference", WriteDiagnostic.Warn, field,
                                                    $"no account given and the {ownerField} has {active} active accounts to choose from"));
        }
    }

    /// <summary>
    /// One <c>Записать</c> in <paramref name="mode"/> (Запись / Проведение / ОтменаПроведения);
    /// posting is Неоперативный, which every document accepts whatever its date. A refusal
    /// carries 1C's own messages (the old error text did too, main.os:2485) and has written
    /// nothing: the write is one transaction.
    /// </summary>
    internal static void WriteObject(SessionContext ctx, object obj, string document, string mode, List<WriteDiagnostic> diagnostics)
    {
        UserMessages(ctx);                                          // drop anything left from before
        try
        {
            using var ms = new ComScope();
            var writeModes = ms.Track(Dispatch.Get(ctx.Connection, "РежимЗаписиДокумента", ctx.Error), "РежимЗаписиДокумента");
            var writeMode = ms.Track(Dispatch.Get(writeModes, mode, ctx.Error), mode);
            if (mode == "Проведение")
            {
                var postModes = ms.Track(Dispatch.Get(ctx.Connection, "РежимПроведенияДокумента", ctx.Error), "РежимПроведенияДокумента");
                var nonOperative = ms.Track(Dispatch.Get(postModes, "Неоперативный", ctx.Error), "Неоперативный");
                Dispatch.Call(obj, "Записать", ctx.Error, writeMode, nonOperative);
            }
            else Dispatch.Call(obj, "Записать", ctx.Error, writeMode);
        }
        catch (OneCException e) when (e.Layer == OneCLayer.Runtime)
        {
            string messages = UserMessages(ctx);
            diagnostics.Add(new WriteDiagnostic("document_write_failed", WriteDiagnostic.Error, null, e.Message));
            if (KnownBlocker(e.Message + " " + messages) is { } blocker) diagnostics.Add(blocker);
            throw new DocumentWriteFailed(e, document, mode, messages, diagnostics);
        }
    }

    /// <summary>
    /// Refusals that come from the configuration, not the data, told apart so the caller can
    /// say what to fix (plan 5.8). The KAN one (solved 2026-07-16): an event subscription on
    /// ОбработкаПроведения whose handler module is «Вызов сервера», so a COM connection cannot
    /// pass it the document object — every post over COM fails, the thick client works.
    /// </summary>
    internal static WriteDiagnostic? KnownBlocker(string text) =>
        text.Contains("mutable value", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("изменяемого значения", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("изменяемое значение", StringComparison.OrdinalIgnoreCase)
            ? new WriteDiagnostic("posting_blocked_by_configuration", WriteDiagnostic.Error, null,
                "a posting event subscription's handler is a «Вызов сервера» module, which a COM connection cannot pass " +
                "the document to; set that module to server + «Внешнее соединение» without «Вызов сервера» (KAN: " +
                "ПривилегированныйМодульВызовСервера.ПроведениеРеализацииПакетДокументов). Exchange mode is NOT a fix: it posts without movements.")
            : null;

    /// <summary>ПолучитьСообщенияПользователю(Истина): 1C's own explanation of a refused post, cleared as read.</summary>
    private static string UserMessages(SessionContext ctx)
    {
        try
        {
            using var s = new ComScope();
            var list = Dispatch.Call(ctx.Connection, "ПолучитьСообщенияПользователю", ctx.Error, true);
            if (!OneCValue.IsCom(list)) return "";
            s.Add(list, "СообщенияПользователю");
            int n = Dispatch.CallInt(list!, "Количество", ctx.Error);
            var texts = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                using var ms = new ComScope();
                var m = ms.Track(Dispatch.Call(list!, "Получить", ctx.Error, i), "СообщениеПользователю");
                if (Dispatch.GetString(m, "Текст", ctx.Error) is { Length: > 0 } t) texts.Add(t.Trim());
            }
            return string.Join(" | ", texts);
        }
        catch (OneCException) { return ""; }
    }
}
