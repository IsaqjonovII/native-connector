using System.Runtime.InteropServices;
using OneC.Interop;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>How reference-typed columns come back from a read.</summary>
public enum RefMode
{
    /// <summary>1C's own presentation text, computed inside the query (ПРЕДСТАВЛЕНИЕ).</summary>
    Text,
    /// <summary>The reference's GUID (XMLСтрока) — what a sync or a write needs.</summary>
    Guid,
    /// <summary>Both, as <c>{ "ref": guid, "text": presentation }</c>.</summary>
    Both
}

/// <summary>
/// CLR values out of 1C. Scalars pass through; references are never turned into text here.
///
/// History (milestone 4.0): this used to call the global <c>Строка()</c> on the connection
/// for every reference. <c>Строка</c> does not exist on the COM connection
/// (DISP_E_UNKNOWNNAME), so every call failed and fell back to reading <c>Номер</c> /
/// <c>Наименование</c> — a register's Регистратор came back as "00000000106" instead of
/// "Отчет о розничных продажах 00000000106 dated 08.02.17", at ~0.9–1.9 ms per row. The read
/// path now asks 1C for ПРЕДСТАВЛЕНИЕ() inside the query (7–15× faster, correct text).
/// </summary>
public static class OneCValue
{
    public static bool IsScalar(object? v) =>
        v is null or DBNull or string or bool or byte or short or int or long or float or double or decimal or DateTime;

    public static object? Scalar(object? v) => v is DBNull ? null : v;

    /// <summary>The GUID behind a reference, via XMLСтрока. Null when the value is not a ref.</summary>
    public static string? RefGuid(object com, SessionContext ctx)
    {
        try { return Dispatch.Call(ctx.Connection, "XMLСтрока", ctx.Error, com) as string; }
        catch (OneCException) { return null; }
    }

    public static bool IsCom(object? v) => v is not null && Marshal.IsComObject(v);
}
