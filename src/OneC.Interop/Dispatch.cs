
using System.Runtime.InteropServices;
using CT = System.Runtime.InteropServices.ComTypes;

namespace OneC.Interop;

[ComImport, Guid("00020400-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatchRaw
{
    [PreserveSig] int GetTypeInfoCount(out int count);
    [PreserveSig] int GetTypeInfo(int index, int lcid, out IntPtr typeInfo);
    [PreserveSig] int GetIDsOfNames(ref Guid riid,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        int nameCount, int lcid, [MarshalAs(UnmanagedType.LPArray)] int[] dispIds);
    [PreserveSig] int Invoke(int dispId, ref Guid riid, int lcid, ushort flags,
        ref CT.DISPPARAMS args, IntPtr result, IntPtr excepInfo, IntPtr argErr);
}

/// <summary>
/// Controlled late binding over IDispatch. Replaces C# <c>dynamic</c> everywhere in the
/// production path: the runtime binder discards the error text whenever
/// EXCEPINFO.scode == 0 &amp;&amp; wCode != 0, which is every genuine 1C runtime error
/// (DOTNET_ONEC_INTEROP_RESEARCH.md §5). Typed interop is not an option — only
/// V83.ComConnector publishes a type library; everything past Connect reports
/// GetTypeInfoCount = 0.
/// </summary>
public static class Dispatch
{
    private static Guid _iidNull = Guid.Empty;
    private const int Lcid = 0x0800;               // LOCALE_SYSTEM_DEFAULT
    private const int DispIdPropertyPut = -3;

    private const ushort FlagMethod = 1;
    private const ushort FlagPropertyGet = 2;
    private const ushort FlagPropertyPut = 4;

    private static readonly int VariantSize = IntPtr.Size == 8 ? 24 : 16;
    private static readonly int ExcepInfoSize = Marshal.SizeOf<CT.EXCEPINFO>();

    [DllImport("oleaut32")]
    private static extern int VariantClear(IntPtr variant);

    // ---- public surface ----

    /// <summary>Method call, or a parameterless property read when 1C models it as both.</summary>
    public static object? Call(object target, string member, ErrorContext ctx, params object?[] args)
        => Invoke(target, member, FlagMethod | FlagPropertyGet, ctx, args);

    public static object? Get(object target, string member, ErrorContext ctx, params object?[] args)
        => Invoke(target, member, FlagPropertyGet, ctx, args);

    public static void Set(object target, string member, object? value, ErrorContext ctx)
        => Invoke(target, member, FlagPropertyPut, ctx, new[] { value });

    public static string? GetString(object target, string member, ErrorContext ctx)
        => Get(target, member, ctx)?.ToString();

    public static bool GetBool(object target, string member, ErrorContext ctx)
        => Convert.ToBoolean(Get(target, member, ctx));

    public static int CallInt(object target, string member, ErrorContext ctx, params object?[] args)
        => Convert.ToInt32(Call(target, member, ctx, args));

    public static bool CallBool(object target, string member, ErrorContext ctx, params object?[] args)
        => Convert.ToBoolean(Call(target, member, ctx, args));

    /// <summary>True when the member exists on this object. Costs one GetIDsOfNames.</summary>
    public static bool HasMember(object target, string member)
    {
        var d = AsDispatch(target, ErrorContext.None, "HasMember", member);
        var ids = new int[1];
        return d.GetIDsOfNames(ref _iidNull, new[] { member }, 1, Lcid, ids) >= 0;
    }

    public static int DispIdOf(object target, string member, ErrorContext ctx)
    {
        var d = AsDispatch(target, ctx, "GetIDsOfNames", member);
        return ResolveDispId(d, member, ctx);
    }

    /// <summary>
    /// Number of type-info blocks the object publishes. Used by diagnostics to confirm the
    /// finding that 1C business objects publish none.
    /// </summary>
    public static int TypeInfoCount(object target)
    {
        var d = AsDispatch(target, ErrorContext.None, "GetTypeInfoCount", "");
        return d.GetTypeInfoCount(out int c) >= 0 ? c : -1;
    }

    // ---- DISPID resolution ----
    //
    // There is deliberately no DISPID cache. It was built, then measured away: the host's
    // `probe-dispid` mode resolves the same member names against unrelated 1C types and
    // they collide —
    //     Вставить -> 3 on Массив, 2 on Структура, 4 on ТаблицаЗначений
    //     Добавить -> 4 on Массив, 3 on ТаблицаЗначений
    //     Очистить -> 6 on Массив, 4 on Структура
    // 1C numbers members per type, and the objects carry no type information we could key
    // a cache on (GetTypeInfoCount = 0 everywhere past Connect). A process-wide name->DISPID
    // cache would silently call the wrong member. GetIDsOfNames is sub-millisecond; correct
    // beats cached.

    private static long _nameLookups;
    public static long NameLookups => Volatile.Read(ref _nameLookups);

    private static int ResolveDispId(IDispatchRaw d, string member, ErrorContext ctx)
    {
        var ids = new int[1];
        int hr = d.GetIDsOfNames(ref _iidNull, new[] { member }, 1, Lcid, ids);
        Interlocked.Increment(ref _nameLookups);
        if (hr < 0)
            throw OneCException.FromInvoke(hr, false, default, -1, ctx, "GetIDsOfNames", member);
        return ids[0];
    }

    // ---- the call ----

    private static IDispatchRaw AsDispatch(object target, ErrorContext ctx, string op, string member)
    {
        if (target is IDispatchRaw d) return d;
        throw OneCException.Host("object does not expose IDispatch", ctx, op, member);
    }

    private static object? Invoke(object target, string member, ushort flags,
                                  ErrorContext ctx, object?[] args)
    {
        var d = AsDispatch(target, ctx, "Invoke", member);
        return Invoke(d, ResolveDispId(d, member, ctx), member, flags, ctx, args);
    }

    internal static int Resolve(object target, string member, ErrorContext ctx) =>
        ResolveDispId(AsDispatch(target, ctx, "GetIDsOfNames", member), member, ctx);

    internal static object? InvokeResolved(object target, int dispId, string member, ushort flags,
                                           ErrorContext ctx, object?[] args) =>
        Invoke(AsDispatch(target, ctx, "Invoke", member), dispId, member, flags, ctx, args);

    internal const ushort MethodOrGet = FlagMethod | FlagPropertyGet;
    internal const ushort PropertyGet = FlagPropertyGet;

    private static object? Invoke(IDispatchRaw d, int dispId, string member, ushort flags,
                                  ErrorContext ctx, object?[] args)
    {
        args ??= Array.Empty<object?>();

        IntPtr argv = IntPtr.Zero, named = IntPtr.Zero,
               result = IntPtr.Zero, excep = IntPtr.Zero, argErr = IntPtr.Zero;
        try
        {
            if (args.Length > 0)
            {
                argv = Marshal.AllocCoTaskMem(VariantSize * args.Length);
                Zero(argv, VariantSize * args.Length);
                // DISPPARAMS takes arguments in reverse order.
                for (int i = 0; i < args.Length; i++)
                    Marshal.GetNativeVariantForObject(args[args.Length - 1 - i], argv + VariantSize * i);
            }

            var dp = new CT.DISPPARAMS
            {
                rgvarg = argv,
                cArgs = args.Length,
                rgdispidNamedArgs = IntPtr.Zero,
                cNamedArgs = 0
            };

            if ((flags & FlagPropertyPut) != 0)
            {
                named = Marshal.AllocCoTaskMem(sizeof(int));
                Marshal.WriteInt32(named, DispIdPropertyPut);
                dp.rgdispidNamedArgs = named;
                dp.cNamedArgs = 1;
            }

            result = Marshal.AllocCoTaskMem(VariantSize); Zero(result, VariantSize);
            excep = Marshal.AllocCoTaskMem(ExcepInfoSize); Zero(excep, ExcepInfoSize);
            argErr = Marshal.AllocCoTaskMem(sizeof(int)); Marshal.WriteInt32(argErr, -1);

            int hr = d.Invoke(dispId, ref _iidNull, Lcid, flags, ref dp, result, excep, argErr);

            if (hr < 0)
            {
                bool hasEi = hr == Hr.DISP_E_EXCEPTION;
                var ei = hasEi ? Marshal.PtrToStructure<CT.EXCEPINFO>(excep) : default;
                throw OneCException.FromInvoke(hr, hasEi, ei, Marshal.ReadInt32(argErr),
                                               ctx, "Invoke", member);
            }

            return (flags & FlagPropertyPut) != 0 ? null : Marshal.GetObjectForNativeVariant(result);
        }
        finally
        {
            if (argv != IntPtr.Zero)
            {
                for (int i = 0; i < args.Length; i++) VariantClear(argv + VariantSize * i);
                Marshal.FreeCoTaskMem(argv);
            }
            if (result != IntPtr.Zero) { VariantClear(result); Marshal.FreeCoTaskMem(result); }
            if (excep != IntPtr.Zero) { FreeExcepInfo(excep); Marshal.FreeCoTaskMem(excep); }
            if (named != IntPtr.Zero) Marshal.FreeCoTaskMem(named);
            if (argErr != IntPtr.Zero) Marshal.FreeCoTaskMem(argErr);
        }
    }

    /// <summary>EXCEPINFO owns three BSTRs. Nobody frees them for us.</summary>
    private static void FreeExcepInfo(IntPtr p)
    {
        var ei = Marshal.PtrToStructure<CT.EXCEPINFO>(p);
        // Offsets: wCode(2) wReserved(2) [pad 4] bstrSource bstrDescription bstrHelpFile
        int off = IntPtr.Size == 8 ? 8 : 4;
        for (int i = 0; i < 3; i++)
        {
            IntPtr b = Marshal.ReadIntPtr(p, off + i * IntPtr.Size);
            if (b != IntPtr.Zero) Marshal.FreeBSTR(b);
        }
        _ = ei;
    }

    private static void Zero(IntPtr p, int bytes)
    {
        int i = 0;
        for (; i + 8 <= bytes; i += 8) Marshal.WriteInt64(p, i, 0);
        for (; i < bytes; i++) Marshal.WriteByte(p, i, 0);
    }
}
