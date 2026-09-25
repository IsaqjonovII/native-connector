using System.Globalization;

namespace OneC.Host;

/// <summary>
/// The old adapter's document filter rules (ПолучитьДокументы, main.os:7623–7697), turned into
/// query terms with positional parameters plus the two filters it applied in memory.
///
/// Keys: <c>contract_number</c> / <c>contract_date</c> / <c>ДоговорКонтрагента</c> — matched
/// after reading, against the rendered ДоговорКонтрагента and НомерВходящегоДокумента /
/// ДатаВходящегоДокумента (1C's ПОДОБНО on presentations failed on some configurations);
/// <c>date</c>/<c>Дата</c> — that whole day (a timesheet matches by its period);
/// <c>period_start</c>/<c>period_end</c> — on the period attributes the document has; the
/// response aliases <c>posted</c>/<c>deletionMark</c>/<c>number</c>; anything else is a field.
/// </summary>
public sealed class DocumentFilters
{
    public List<(string Term, string Param, object? Value)> Terms { get; } = new();
    public string? ContractNumber { get; private set; }
    public string? ContractDate { get; private set; }

    public bool HasPostFilter => !string.IsNullOrEmpty(ContractNumber) || !string.IsNullOrEmpty(ContractDate);

    private static readonly HashSet<string> Timesheets = new(StringComparer.Ordinal)
    {
        "ТабельУчетаРабочегоВремени", "ТабельУчетаРабочегоВремениОрганизации"
    };

    public static DocumentFilters Parse(IReadOnlyDictionary<string, object?>? filters, DocumentSchema s)
    {
        var f = new DocumentFilters();
        if (filters is null) return f;
        DateTime? day = null, periodStart = null, periodEnd = null;
        int n = 0;

        foreach (var (key, value) in filters)
        {
            string text = (Text(value) ?? "").Trim();
            switch (key)
            {
                case "contract_number": f.ContractNumber = NormalizeContract(text); continue;
                case "contract_date": f.ContractDate = text; continue;
                case "date" or "Дата": day = ParseDate(value, key); continue;
                case "period_start" or "ДатаНачалаПериода": periodStart = ParseDate(value, key); continue;
                case "period_end" or "ДатаОкончанияПериода": periodEnd = ParseDate(value, key); continue;
                case "ДоговорКонтрагента":
                    if (string.IsNullOrEmpty(f.ContractNumber)) f.ContractNumber = NormalizeContract(text);
                    continue;
            }

            string field = key switch
            {
                "posted" => "Проведен",
                "deletionMark" => "ПометкаУдаления",
                "number" => "Номер",
                _ => key
            };
            ReadService.ValidateIdentifier(field, "filter");
            object? v = value;
            if (field is "Проведен" or "ПометкаУдаления" && v is string sb) v = ParseBool(sb);
            if (v is string sd && LooksLikeIsoDate(sd) && TryParseIsoDate(sd) is { } parsed) v = parsed;
            f.Terms.Add(($"{field} = &f{n}", "f" + n, v));
            n++;
        }

        if (day is { } d)
        {
            var from = d.Date;
            var to = d.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            string term = Timesheets.Contains(s.Name) && s.Has("ДатаНачалаПериода") && s.Has("ДатаОкончанияПериода")
                ? "(ДатаНачалаПериода <= &dayTo И ДатаОкончанияПериода >= &dayFrom)"
                : Timesheets.Contains(s.Name) && s.Has("ПериодРегистрации")
                    ? "НАЧАЛОПЕРИОДА(ПериодРегистрации, МЕСЯЦ) = НАЧАЛОПЕРИОДА(&dayFrom, МЕСЯЦ)"
                    : "(Дата >= &dayFrom И Дата <= &dayTo)";
            f.Terms.Add((term, "dayFrom", from));
            if (term.Contains("&dayTo")) f.Terms.Add(("", "dayTo", to));
        }
        // A period bound on a document with neither period attribute is ignored, as before.
        if (periodStart is { } ps)
        {
            string? field = s.Has("ДатаНачалаПериода") ? "ДатаНачалаПериода" : s.Has("ПериодРегистрации") ? "ПериодРегистрации" : null;
            if (field is not null) f.Terms.Add(($"{field} >= &periodStart", "periodStart", ps));
        }
        if (periodEnd is { } pe)
        {
            string? field = s.Has("ДатаОкончанияПериода") ? "ДатаОкончанияПериода" : s.Has("ПериодРегистрации") ? "ПериодРегистрации" : null;
            if (field is not null) f.Terms.Add(($"{field} <= &periodEnd", "periodEnd", pe));
        }
        return f;
    }

    /// <summary>The query terms joined with И, or null. Parameter-only entries carry no term.</summary>
    public string? Where => Terms.Any(t => t.Term.Length > 0)
        ? string.Join(" И ", Terms.Where(t => t.Term.Length > 0).Select(t => t.Term))
        : null;

    /// <summary>The in-memory contract filters, on a row already rendered by LegacyValue.</summary>
    public bool Matches(IReadOnlyDictionary<string, object?> row)
    {
        if (!string.IsNullOrEmpty(ContractNumber) &&
            !ContainsIgnoreCase(NormalizeContract(Text(row.GetValueOrDefault("ДоговорКонтрагента"))), ContractNumber) &&
            !ContainsIgnoreCase(NormalizeContract(Text(row.GetValueOrDefault("НомерВходящегоДокумента"))), ContractNumber))
            return false;
        if (!string.IsNullOrEmpty(ContractDate) &&
            !ContractDateMatches(row.GetValueOrDefault("ДоговорКонтрагента"), ContractDate) &&
            !ContractDateMatches(row.GetValueOrDefault("ДатаВходящегоДокумента"), ContractDate))
            return false;
        return true;
    }

    // ---------------- the old helpers, one to one ----------------

    /// <summary>НормализоватьЗначениеДоговора (:7330): trim, drop "№", collapse double spaces.</summary>
    internal static string NormalizeContract(string? s)
    {
        string v = (s ?? "").Trim().Replace("№", "");
        while (v.Contains("  ")) v = v.Replace("  ", " ");
        return v.Trim();
    }

    /// <summary>СтрокаСодержитРегНезависимо (:7339).</summary>
    internal static bool ContainsIgnoreCase(string? source, string? pattern)
    {
        string a = (source ?? "").Trim(), b = (pattern ?? "").Trim();
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.ToLowerInvariant().Contains(b.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>СовпадаетДатаДоговора (:7348): ISO prefix, or dd.MM.yyyy anywhere.</summary>
    internal static bool ContractDateMatches(object? value, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        string doc = (Text(value) ?? "").Trim();
        if (string.IsNullOrWhiteSpace(doc)) return false;

        string iso, local;
        if (filter.Length >= 10 && filter[4] == '-' && filter[7] == '-')
        {
            iso = filter[..10];
            local = $"{filter.Substring(8, 2)}.{filter.Substring(5, 2)}.{filter[..4]}";
        }
        else if (filter.Length >= 10 && filter[2] == '.' && filter[5] == '.')
        {
            local = filter[..10];
            iso = $"{filter.Substring(6, 4)}-{filter.Substring(3, 2)}-{filter[..2]}";
        }
        else iso = local = filter;

        if (iso.Length > 0 && doc.StartsWith(iso, StringComparison.Ordinal)) return true;
        return ContainsIgnoreCase(doc, local);
    }

    /// <summary>ЗначениеФильтраВБулево (:7415).</summary>
    internal static bool ParseBool(string s) =>
        s.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "да" or "истина";

    internal static bool LooksLikeIsoDate(string s) => s.Length >= 10 && s.IndexOf('-') == 4;

    /// <summary>The old auto-cast (:7795): yyyy-MM-dd, optionally THH:mm:ss; else not a date.</summary>
    internal static DateTime? TryParseIsoDate(string s)
    {
        try
        {
            int y = int.Parse(s[..4], CultureInfo.InvariantCulture), mo = int.Parse(s.Substring(5, 2), CultureInfo.InvariantCulture),
                d = int.Parse(s.Substring(8, 2), CultureInfo.InvariantCulture);
            if (s.Length >= 19 && s.IndexOf('T') == 10)
                return new DateTime(y, mo, d, int.Parse(s.Substring(11, 2), CultureInfo.InvariantCulture),
                                    int.Parse(s.Substring(14, 2), CultureInfo.InvariantCulture),
                                    int.Parse(s.Substring(17, 2), CultureInfo.InvariantCulture));
            return new DateTime(y, mo, d);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or OverflowException) { return null; }
    }

    /// <summary>
    /// НормализоватьДатуИзJSON (:3149) — ISO or dd.MM.yyyy. The old code fell back to TODAY for
    /// anything else, so a typo filtered on the current date; here it is a validation error.
    /// </summary>
    internal static DateTime ParseDate(object? value, string key)
    {
        if (value is DateTime dt) return dt;
        string s = (Text(value) ?? "").Trim();
        if (LooksLikeIsoDate(s) && TryParseIsoDate(s) is { } iso) return iso;
        if (s.Length >= 10 && s.IndexOf('.') == 2 &&
            DateTime.TryParseExact(s[..10], "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return local;
        throw new ArgumentException($"filter '{key}': '{s}' is not a date (yyyy-MM-dd[THH:mm:ss] or dd.MM.yyyy)");
    }

    /// <summary>Строка() of a rendered value: null is "", booleans as 1C writes them.</summary>
    private static string? Text(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "Да" : "Нет",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };
}
