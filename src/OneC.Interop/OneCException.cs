using System.Runtime.InteropServices;
using CT = System.Runtime.InteropServices.ComTypes;

namespace OneC.Interop;

/// <summary>Which layer produced the failure. Taken from EXCEPINFO.bstrSource, not guessed.</summary>
public enum OneCLayer
{
    Unknown = 0,
    /// <summary>No EXCEPINFO — IDispatch itself refused (unknown name, bad arg count).</summary>
    Dispatch,
    /// <summary>bstrSource = "V83.COMConnector.1" — connect, credentials, infobase, versions.</summary>
    Connector,
    /// <summary>bstrSource = "1C:Enterprise x.y.z" — query, Записать, business logic.</summary>
    Runtime,
    /// <summary>Raised on our side of the boundary (released RCW, wrong thread, load failure).</summary>
    Host
}

/// <summary>
/// One error type for everything that comes out of the 1C boundary. Every field here was
/// verified populatable in DOTNET_ONEC_INTEROP_RESEARCH.md §6 — nothing is aspirational.
/// </summary>
public sealed class OneCException : Exception
{
    public int Hr { get; init; }
    public OneCLayer Layer { get; init; }
    /// <summary>EXCEPINFO.wCode. 1001 for every 1C runtime error observed.</summary>
    public int OneCCode { get; init; }
    /// <summary>EXCEPINFO.scode. Zero for runtime errors, E_FAIL for connector errors.</summary>
    public int SCode { get; init; }
    public string? OneCSource { get; init; }
    public string Operation { get; init; } = "";
    public string Member { get; init; } = "";
    public string? BaseName { get; init; }
    public string? PlatformVersion { get; init; }
    /// <summary>puArgErr, when IDispatch blamed a specific argument. -1 when not set.</summary>
    public int ArgErrIndex { get; init; } = -1;
    /// <summary>
    /// Stable UI code (auth/license/path/version/permission/network/unknown), the same codes
    /// the old adapter produced. Derived from the text — see <see cref="ErrorCategory"/>.
    /// </summary>
    public string Category { get; init; } = ErrorCategory.Unknown;
    public Exception? OriginalException { get; init; }

    public bool IsRetryable { get; init; }
    public bool IsSessionFatal { get; init; }
    public bool IsHostFatal { get; init; }

    public OneCException(string message) : base(message) { }

    public string Describe() =>
        $"[{Layer}] {Operation}/{Member} hr=0x{Hr:X8} code={OneCCode} scode=0x{SCode:X8} " +
        $"base={BaseName ?? "-"} ver={PlatformVersion ?? "-"} " +
        $"retry={IsRetryable} sessionFatal={IsSessionFatal} hostFatal={IsHostFatal} :: {Message}";

    // ---- construction ----

    internal static OneCException FromInvoke(int hr, bool hasExcepInfo, CT.EXCEPINFO ei,
                                             int argErr, ErrorContext ctx, string op, string member)
    {
        string desc = hasExcepInfo ? (ei.bstrDescription ?? "") : "";
        string? src = hasExcepInfo ? ei.bstrSource : null;
        int wcode = hasExcepInfo ? ei.wCode : 0;
        int scode = hasExcepInfo ? ei.scode : 0;

        var layer = src switch
        {
            null => OneCLayer.Dispatch,
            var s when s.StartsWith("V83.", StringComparison.OrdinalIgnoreCase) => OneCLayer.Connector,
            var s when s.StartsWith("1C:", StringComparison.OrdinalIgnoreCase) => OneCLayer.Runtime,
            _ => OneCLayer.Unknown
        };

        string msg = !string.IsNullOrWhiteSpace(desc)
            ? Flatten(desc)
            : hasExcepInfo
                ? $"1C returned an empty EXCEPINFO (wCode={wcode} scode=0x{scode:X8})"
                : global::OneC.Interop.Hr.Name(hr);

        return new OneCException(msg)
        {
            Hr = hr,
            Layer = layer,
            OneCCode = wcode,
            SCode = scode,
            OneCSource = src,
            Operation = op,
            Member = member,
            BaseName = ctx.BaseName,
            PlatformVersion = ctx.PlatformVersion,
            ArgErrIndex = argErr,
            IsRetryable = Retry.IsRetryable(hr, layer, msg),
            // Connection errors only, like the old adapter: its "not found" → network rule would
            // mislabel a query's "Table not found" as a network fault.
            Category = layer == OneCLayer.Connector ? ErrorCategory.Classify(msg) : ErrorCategory.Unknown,
            IsSessionFatal = global::OneC.Interop.Hr.IsSessionFatal(hr),
            IsHostFatal = global::OneC.Interop.Hr.IsHostFatal(hr)
        };
    }

    public static OneCException Host(string message, ErrorContext ctx, string op,
                                     string member = "", Exception? inner = null, int hr = 0,
                                     string category = ErrorCategory.Unknown, bool retryable = false)
        => new(message)
        {
            Hr = hr != 0 ? hr : (inner?.HResult ?? 0),
            Layer = OneCLayer.Host,
            Operation = op,
            Member = member,
            BaseName = ctx.BaseName,
            PlatformVersion = ctx.PlatformVersion,
            OriginalException = inner,
            Category = category,
            IsRetryable = retryable,
            IsSessionFatal = inner is InvalidComObjectException,
            IsHostFatal = hr != 0 && global::OneC.Interop.Hr.IsHostFatal(hr)
        };

    private static string Flatten(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        return s;
    }
}

/// <summary>Per-session facts stamped onto every error raised from that session.</summary>
public readonly record struct ErrorContext(string? BaseName, string? PlatformVersion)
{
    public static readonly ErrorContext None = new(null, null);
}

/// <summary>
/// Retry classification. HRESULT first; message text is only consulted for the lock/timeout
/// family, which 1C reports as an ordinary runtime error with no distinguishing code.
/// Research §6 flagged substring matching as fragile — it stays, contained, here alone.
/// </summary>
public static class Retry
{
    // Both languages: a base may be localized either way and both resolve to the same object.
    private static readonly string[] RetryableFragments =
    {
        "заблокирован", "блокировк", "конфликт блокировок",
        "lock conflict", "is locked", "deadlock",
        "таймаут", "timeout", "истекло время",
        "превышено количество попыток"
    };

    // Transient connector failures. Concurrent Connects to one FILE base occasionally lose a
    // race on its temp database, in two wordings seen so far:
    //   "Database sharing violation '…/1Cv8tmp.1CD'"            (milestone 2.4 connstress)
    //   "Error creating database file '…/1Cv8tmp.1CD'"           (milestone 4.4 badconnect)
    // Retrying the Connect succeeds. Anything naming the temp database is treated as this race.
    private static readonly string[] RetryableConnectorFragments =
    {
        "sharing violation", "нарушение совместного доступа", "1Cv8tmp.1CD"
    };

    public static bool IsRetryable(int hr, OneCLayer layer, string message)
    {
        if (Hr.IsSessionFatal(hr)) return true;          // a fresh session will do
        if (Hr.IsHostFatal(hr)) return false;            // needs a new process, not a retry
        if (layer == OneCLayer.Connector)
            return RetryableConnectorFragments.Any(f => message.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (layer != OneCLayer.Runtime) return false;    // dispatch errors are deterministic
        foreach (var f in RetryableFragments)
            if (message.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
