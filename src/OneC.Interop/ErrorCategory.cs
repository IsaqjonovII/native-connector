namespace OneC.Interop;

/// <summary>
/// Stable, UI-facing error codes — a port of the old adapter's КлассифицироватьОшибку1С
/// (connector main.os:1951), same order, same Russian + English phrases, so screens and the
/// cloud keep the codes they already understand. The platform answers in either language
/// depending on the machine, hence both.
/// </summary>
public static class ErrorCategory
{
    public const string Auth = "auth";
    public const string License = "license";
    public const string Path = "path";
    public const string Version = "version";
    public const string Permission = "permission";
    public const string Network = "network";
    public const string Unknown = "unknown";

    private static readonly (string Code, string[] Phrases)[] Rules =
    {
        (Auth, new[] { "идентификация пользователя", "имя пользователя или пароль",
                       "user identification failed", "incorrect user name or password" }),
        (License, new[] { "лицензи", "license", "ключ защиты", "protection key" }),
        (Path, new[] { "1cv8.1cd", "файл базы данных", "не обнаружен файл", "не найден файл",
                       "database file", "infobase file" }),
        (Version, new[] { "несовместим", "incompatible" }),
        (Permission, new[] { "доступ запрещ", "недостаточно прав", "access denied", "insufficient rights" }),
        (Network, new[] { "сервер", "кластер", "недоступ", "не найден",
                          "server", "cluster", "unavailable", "not found" })
    };

    public static string Classify(string? message)
    {
        if (string.IsNullOrEmpty(message)) return Unknown;
        string t = message.ToLowerInvariant();
        foreach (var (code, phrases) in Rules)
            foreach (var p in phrases)
                if (t.Contains(p, StringComparison.Ordinal)) return code;
        return Unknown;
    }
}
