using System.Globalization;
using System.Text.Json;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Independent information registers, sync (§8, S3): pages in natural-key order
/// (<c>Период</c> if periodic, then the dimensions), each page starting strictly after the previous
/// page's last key — so a page ends, and every row is read once (the old read of a non-periodic
/// register had no order and no end: R-2). Every row carries <c>naturalKey</c>, the same
/// type-tagged values joined, as its stable row key.
///
/// Key values travel as 1C's own XML form (<c>XMLТипЗнч</c> + <c>XMLСтрока</c>, back through
/// <c>ИзXMLТипа</c> + <c>XMLЗначение</c>): one rule for references, enums, dates, numbers and strings,
/// no per-type code. An unfilled composite dimension is compared as NULL.
/// </summary>
internal static class RegisterKeyset
{
    /// <summary>One key value: a 1C XML type (or s/n/b/d for CLR scalars) and its text; null = unfilled.</summary>
    internal sealed record KeyValue(string Type, string Uri, string Text);

    public static RegisterPage Page(SessionContext ctx, ComScope scope, RegisterSchema s, RegisterQuery q, long total, CancellationToken ct)
    {
        var keyCols = KeyColumns(s);
        var after = q.AfterKey is null ? null : Decode(q.AfterKey, keyCols.Count);
        var parameters = new List<(string, object?)>();
        string? where = null;
        if (after is not null)
        {
            var terms = new List<string>();
            for (int i = 0; i < keyCols.Count; i++)
            {
                var parts = new List<string>();
                for (int j = 0; j < i; j++) parts.Add(Eq(keyCols[j], after[j], j));
                parts.Add(Gt(keyCols[i], after[i], i));
                terms.Add("(" + string.Join(" И ", parts) + ")");
            }
            where = string.Join(" ИЛИ ", terms);
            for (int i = 0; i < keyCols.Count; i++)
                if (after[i] is { } v) parameters.Add(("k" + i, ToOneC(ctx, scope, v)));
        }
        string? order = keyCols.Count > 0 ? string.Join(", ", keyCols.Select(QueryKit.Field)) : null;
        string sql = RegisterReadService.Select(s, q.Limit, s.Source, where, order);
        var query = QueryKit.NewQuery(ctx, scope, sql);
        foreach (var (name, value) in parameters) QueryKit.SetParameter(ctx, query, name, value);

        var aliases = keyCols.Select(c => "c" + s.Columns.ToList().FindIndex(x => x.Name == c)).ToList();
        var keys = new List<string>();
        List<KeyValue?>? last = null;
        var rows = RegisterReadService.ReadRows(ctx, scope, query, s, 0, q.Limit, syncKeys: true, ct, out _, out _,
            (cursor, n) =>
            {
                var values = aliases.Select(a => FromOneC(ctx, cursor.Get(a, ctx.Error))).ToList();
                keys.Add(NaturalKey(values));
                last = values;
            });
        for (int i = 0; i < rows.Count; i++) rows[i]["naturalKey"] = keys[i];
        bool more = rows.Count >= q.Limit && last is not null;
        return new RegisterPage(rows, total, null, 0, more, q.Register, ctx.SessionId)
        {
            NextKey = more ? Encode(last!) : null
        };
    }

    public static IReadOnlyList<string> KeyColumns(RegisterSchema s) =>
        (s.Periodic ? new[] { "Период" } : Array.Empty<string>()).Concat(s.Dimensions).ToList();

    private static string Eq(string col, KeyValue? v, int i) =>
        v is null ? $"{QueryKit.Field(col)} ЕСТЬ NULL" : $"{QueryKit.Field(col)} = &k{i}";

    private static string Gt(string col, KeyValue? v, int i) =>
        v is null ? $"НЕ {QueryKit.Field(col)} ЕСТЬ NULL" : $"{QueryKit.Field(col)} > &k{i}";

    /// <summary>The stable row key: each value as type:text, joined — unique, deterministic, readable.</summary>
    internal static string NaturalKey(IReadOnlyList<KeyValue?> values) =>
        string.Join("|", values.Select(v => v is null ? "" : $"{v.Type}:{v.Text}"));

    internal static string Encode(IReadOnlyList<KeyValue?> values) =>
        JsonSerializer.Serialize(values.Select(v => v is null ? null : new[] { v.Type, v.Uri, v.Text }));

    internal static List<KeyValue?> Decode(string key, int count)
    {
        List<string[]?>? raw;
        try { raw = JsonSerializer.Deserialize<List<string[]?>>(key); }
        catch (JsonException) { throw new ArgumentException("afterKey is not a key this host returned"); }
        if (raw is null || raw.Count != count || raw.Any(r => r is not null && r.Length != 3))
            throw new ArgumentException("afterKey does not match the register's key columns");
        return raw.Select(r => r is null ? null : new KeyValue(r[0], r[1], r[2])).ToList();
    }

    internal static KeyValue? FromOneC(SessionContext ctx, object? v)
    {
        switch (v)
        {
            case null or DBNull: return null;
            case string str: return new KeyValue("s", "", str);
            case bool b: return new KeyValue("b", "", b ? "1" : "0");
            case DateTime d: return new KeyValue("d", "", d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            case decimal or double or float or int or long or short or byte:
                return new KeyValue("n", "", Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }
        if (!OneCValue.IsCom(v)) return new KeyValue("s", "", Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
        using var s = new ComScope();
        s.Add(v, "key value");
        var xt = Dispatch.Call(ctx.Connection, "XMLТипЗнч", ctx.Error, v);
        if (!OneCValue.IsCom(xt)) return null;                              // Неопределено
        s.Add(xt, "ТипДанныхXML");
        string name = Dispatch.GetString(xt!, "ИмяТипа", ctx.Error) ?? "";
        string uri = Dispatch.GetString(xt!, "URIПространстваИмен", ctx.Error) ?? "";
        string text = Dispatch.Call(ctx.Connection, "XMLСтрока", ctx.Error, v) as string ?? "";
        return new KeyValue(name, uri, text);
    }

    private static object? ToOneC(SessionContext ctx, ComScope scope, KeyValue v) => v.Type switch
    {
        "s" => v.Text,
        "b" => v.Text == "1",
        "d" => DateTime.ParseExact(v.Text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        "n" => decimal.Parse(v.Text, CultureInfo.InvariantCulture),
        _ => scope.Track(Dispatch.Call(ctx.Connection, "XMLЗначение", ctx.Error,
                 scope.Track(Dispatch.Call(ctx.Connection, "ИзXMLТипа", ctx.Error, v.Type, v.Uri), "Тип"), v.Text), "key value")
    };
}
