using System.Text;

namespace OneC.Desktop.Services;

/// <summary>
/// Search that does not care which alphabet the user typed in: "kontr", "контр" and "КОНТР" all find
/// Контрагенты; "xozraschet", "hozraschyot" find Хозрасчетный; "realizaciya", "realizatsiya" find
/// РеализацияТоваровУслуг. Both the text and the query are folded to one Latin spelling — Cyrillic
/// (Russian and Uzbek letters) transliterated, then the spellings people mix up made one: x/kh → h,
/// ts → c, zh → j, q → k, w → v, ий/ый → y, ё/э → e; spaces, punctuation and apostrophes dropped.
/// </summary>
public static class Translit
{
    private static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "j",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya", ['ў'] = "o", ['қ'] = "k", ['ғ'] = "g", ['ҳ'] = "h", ['і'] = "i"
    };

    /// <summary>The one spelling both sides are compared in.</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char ch in text.ToLowerInvariant())
        {
            if (Cyr.TryGetValue(ch, out var lat)) sb.Append(lat);
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            // anything else (spaces, _ . ' ʻ ‘ -) is dropped
        }
        return sb.ToString()
            .Replace("shch", "sh").Replace("kh", "h").Replace('x', 'h')
            .Replace("ts", "c").Replace("zh", "j").Replace('q', 'k').Replace('w', 'v')
            .Replace("yo", "e").Replace("iy", "y").Replace("yy", "y");
    }

    /// <summary>
    /// How well <paramref name="query"/> matches: 0 = no match. Every word of the query must occur in
    /// one of the texts; a text that starts with the query ranks above one that only contains it, and
    /// earlier texts (the name) above later ones (the synonym).
    /// </summary>
    public static int Score(string query, params string?[] texts)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return 1;
        var folded = texts.Select(t => Fold(t ?? "")).ToList();
        if (!words.All(w => folded.Any(f => f.Contains(w, StringComparison.Ordinal)))) return 0;
        string whole = string.Concat(words);
        int best = 1;
        for (int i = 0; i < folded.Count; i++)
        {
            int weight = 10 * (folded.Count - i);
            if (folded[i].StartsWith(whole, StringComparison.Ordinal)) best = Math.Max(best, weight + 5);
            else if (folded[i].Contains(whole, StringComparison.Ordinal)) best = Math.Max(best, weight);
        }
        return best;
    }
}
