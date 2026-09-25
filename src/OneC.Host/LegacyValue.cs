using System.Globalization;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Values exactly as the old adapter shipped them to the cloud (ПреобразоватьЗначение,
/// main.os:2871), so the backend sees the same rows when this host replaces the adapter.
///
/// A reference becomes, in order: null when empty; its Наименование; its trimmed Код; its
/// Номер; its GUID; its XML form (enums). The old code asked the connection's global
/// <c>Строка()</c> first — that member does not exist over COM (D30), so the step always
/// failed and is left out here.
///
/// The old code read those fields one COM call at a time per value. Here the query
/// dereferences them (<see cref="Select"/>), and only the leftovers — empty references, enums —
/// cost a call. <see cref="Slow"/> is the literal per-value algorithm: the parity tests use it
/// as the oracle, and attributes whose type set includes УникальныйИдентификатор use it for
/// real, because there a zero GUID is a value, not an empty reference.
/// </summary>
public static class LegacyValue
{
    public const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Query-text columns for one field: the raw value under <paramref name="alias"/>, plus the
    /// dereferenced Наименование / Код / Номер under alias + n / c / r when a type has them.
    /// </summary>
    public static string Select(string field, string alias, AttributeShape s)
    {
        var parts = new List<string>(4) { $"{field} КАК {alias}" };
        if (s.DerefName) parts.Add($"{Flagged(field, "Наименование", s)} КАК {alias}n");
        if (s.DerefCode) parts.Add($"{(s.DerefName ? $"{field}.Код" : Flagged(field, "Код", s))} КАК {alias}c");
        if (s.DerefNumber) parts.Add($"{field}.Номер КАК {alias}r");
        if (s.EnumNames is not null) parts.Add($"{field}.Порядок КАК {alias}o");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The first dereferenced column of a single-type reference answers "empty" as ЛОЖЬ, so an
    /// empty value costs one read. Composite types cannot name one empty value and skip this.
    /// </summary>
    private static string Flagged(string field, string member, AttributeShape s) =>
        s.EmptyRefOf is { } t
            ? $"ВЫБОР КОГДА {field} = ЗНАЧЕНИЕ({t}.ПустаяСсылка) ТОГДА ЛОЖЬ ИНАЧЕ {field}.{member} КОНЕЦ"
            : $"{field}.{member}";

    /// <summary>
    /// Reads the columns <see cref="Select"/> produced for the current row. Per-value references
    /// go to <paramref name="batch"/> (a <see cref="DeferredRef"/> until the page is patched).
    /// </summary>
    public static object? Read(DispatchMemo cursor, string alias, AttributeShape s, SessionContext ctx, RefBatch? batch = null)
    {
        // An enum-only attribute: the ordinal names the value; NULL is the empty value.
        if (s.EnumNames is { } names)
        {
            var o = cursor.Get(alias + "o", ctx.Error);
            if (o is null or DBNull)
            {
                // No ordinal: the empty enum value (null) or 1C NULL (""): the raw column tells.
                var rv = cursor.Get(alias, ctx.Error);
                if (!OneCValue.IsCom(rv)) return Scalar(rv);
                using (var s0 = new ComScope()) s0.Add(rv, "value");
                return null;
            }
            if (Convert.ToInt32(o) is int i && i >= 0 && i < names.Count) return names[i];
            // An ordinal the cached schema does not know (enum changed): ask 1C below.
        }

        object? raw = null;
        if (!s.PureRef)
        {
            raw = cursor.Get(alias, ctx.Error);
            if (!OneCValue.IsCom(raw)) return Scalar(raw);
        }

        using var scope = new ComScope();
        if (raw is not null) scope.Add(raw, "value");

        if (s.PerValue)
        {
            var v = raw ?? cursor.Get(alias, ctx.Error);
            if (!OneCValue.IsCom(v)) return Scalar(v);
            if (raw is null) scope.Add(v, "value");
            // Never read a reference's fields over COM in a row loop: 1C caches every object it
            // loads that way (RefBatch). Without a batch (diagnostics), the literal algorithm.
            return batch is not null ? batch.Defer(v) : Slow(v, ctx);
        }

        // NULL here means the reference is empty, broken, or of a type without the field;
        // ЛОЖЬ in the first column means empty (see Flagged).
        if (s.DerefName)
        {
            var n = cursor.Get(alias + "n", ctx.Error);
            if (n is false) return null;
            if (n is string ns && Filled(ns)) return ns;
        }
        if (s.DerefCode)
        {
            var c = cursor.Get(alias + "c", ctx.Error);
            if (c is false) return null;
            if (CodeText(c) is { Length: > 0 } ct) return ct;
        }
        if (s.DerefNumber && cursor.Get(alias + "r", ctx.Error) is { } r and not DBNull && Filled(r)) return r;

        // Left: an empty reference, an enum, or an object with none of the three filled.
        if (raw is null)
        {
            raw = cursor.Get(alias, ctx.Error);
            if (!OneCValue.IsCom(raw)) return Scalar(raw);
            scope.Add(raw, "value");
        }
        string? xml = Xml(raw!, ctx);
        if (!string.IsNullOrEmpty(xml) && xml != EmptyGuid) return xml;
        // Blank: for a reference or an enum that means empty (null). A non-reference COM value
        // (value storage) can also render blank and the old code kept its "" — so when the
        // type set has non-reference types, let the literal algorithm decide.
        return s.HasPrimitive ? Slow(raw, ctx) : null;
    }

    /// <summary>
    /// Scalars as the old adapter wrote them: dates as ISO text, the empty date as "".
    /// 1C's NULL (an attribute that does not apply to the row, e.g. on a group) is "" too: the
    /// old chain ends in <c>XMLСтрока(Null)</c> = "" (main.os:2953). Неопределено (COM
    /// VT_EMPTY → null) stays null. Both rules were found against the live old adapter
    /// (2026-09-25) — the oracle shares this function, so the sweeps could not see them.
    /// </summary>
    public static object? Scalar(object? v) => v switch
    {
        null => null,
        DBNull => "",
        DateTime d => IsEmptyDate(d) ? "" : d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        _ => v
    };

    /// <summary>
    /// 1C's empty date is 0001-01-01; over COM it arrives as the earliest OLE date, 0100-01-01
    /// (seen live on every empty date attribute of KAN), and oscript formatted it as "".
    /// </summary>
    public static bool IsEmptyDate(DateTime d) => d == DateTime.MinValue || d == OleFloor;

    private static readonly DateTime OleFloor = new(100, 1, 1);

    /// <summary>The old algorithm, one COM call per step, for one value (session thread only).</summary>
    public static object? Slow(object? v, SessionContext ctx)
    {
        if (!OneCValue.IsCom(v)) return Scalar(v);
        var o = v!;
        if (TryScalar(o, "Пустая", ctx, method: true) is bool empty && empty) return null;
        if (TryScalar(o, "Наименование", ctx) is { } name && Filled(name)) return name;
        if (CodeText(TryScalar(o, "Код", ctx)) is { Length: > 0 } code) return code;
        if (TryScalar(o, "Номер", ctx) is { } number && Filled(number)) return number;

        using (var s = new ComScope())
        {
            var uuid = TryGet(o, "УникальныйИдентификатор", ctx, method: true);
            if (OneCValue.IsCom(uuid))
            {
                s.Add(uuid, "УникальныйИдентификатор");
                if (Xml(uuid!, ctx) is { Length: > 0 } g) return g;
            }
        }
        return Xml(o, ctx);
    }

    /// <summary>
    /// ПреобразоватьСсылкуВОбъект (main.os:2960), used where the old code wanted more than a
    /// name but the element could not be read: <c>{ id, type, name }</c>. Over COM,
    /// Строка(ТипЗнч(…)) of a reference is "COMОбъект" in oscript, so that is the type it sent.
    /// </summary>
    public static object? RefObject(object? v, SessionContext ctx)
    {
        if (!OneCValue.IsCom(v)) return Scalar(v);
        var o = v!;
        if (TryScalar(o, "Пустая", ctx, method: true) is bool empty && empty) return null;
        string id = "";
        using (var s = new ComScope())
        {
            var uuid = TryGet(o, "УникальныйИдентификатор", ctx, method: true);
            if (OneCValue.IsCom(uuid)) { s.Add(uuid, "UUID"); id = Xml(uuid!, ctx) ?? ""; }
        }
        if (id.Length == 0) return Slow(o, ctx);
        string? name = TryScalar(o, "Наименование", ctx) as string;
        if (string.IsNullOrWhiteSpace(name)) name = CodeText(TryScalar(o, "Код", ctx)) ?? name ?? "";
        return new Dictionary<string, object?> { ["id"] = id, ["type"] = "COMОбъект", ["name"] = name };
    }

    /// <summary>ЗначениеЗаполнено for the scalar kinds these steps can see.</summary>
    public static bool Filled(object? v) => v switch
    {
        null or DBNull => false,
        string s => !string.IsNullOrWhiteSpace(s),
        bool b => b,
        DateTime d => !IsEmptyDate(d),
        IConvertible c when IsNumber(v) => c.ToDecimal(CultureInfo.InvariantCulture) != 0m,
        _ => true
    };

    /// <summary>СокрЛП(Строка(Код)) — 1C pads string codes to the field length.</summary>
    internal static string? CodeText(object? code) => code switch
    {
        null or DBNull => null,
        string s => s.Trim(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Trim(),
        _ => code.ToString()?.Trim()
    };

    private static bool IsNumber(object v) =>
        v is byte or short or int or long or float or double or decimal;

    private static string? Xml(object com, SessionContext ctx)
    {
        try { return Dispatch.Call(ctx.Connection, "XMLСтрока", ctx.Error, com) as string; }
        catch (OneCException) { return null; }
    }

    /// <summary>
    /// A step of the old <c>Попытка … Исключение</c> chain: a missing member or a 1C error is
    /// "no value", never a failure of the read. A COM result is released immediately unless
    /// the caller takes it (УникальныйИдентификатор).
    /// </summary>
    private static object? TryGet(object o, string member, SessionContext ctx, bool method = false)
    {
        if (!Dispatch.HasMember(o, member)) return null;
        try
        {
            return method ? Dispatch.Call(o, member, ctx.Error) : Dispatch.Get(o, member, ctx.Error);
        }
        catch (OneCException) { return null; }
    }

    /// <summary><see cref="TryGet"/> for steps that only use a scalar; a COM result is released.</summary>
    private static object? TryScalar(object o, string member, SessionContext ctx, bool method = false)
    {
        var r = TryGet(o, member, ctx, method);
        if (!OneCValue.IsCom(r)) return r;
        using var s = new ComScope();
        s.Add(r, member);
        return null;
    }
}
