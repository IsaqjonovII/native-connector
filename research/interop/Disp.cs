using System.Runtime.InteropServices;
using CT = System.Runtime.InteropServices.ComTypes;

namespace OneC.Interop;

/// <summary>
/// Hand-rolled IDispatch. Everything is PreserveSig so the raw HRESULT and the EXCEPINFO
/// survive instead of being turned into whatever the C# runtime binder feels like — which
/// for a textless 1C error is a NullReferenceException thrown from inside the binder itself.
/// </summary>
[ComImport, Guid("00020400-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDispatchRaw
{
    [PreserveSig] int GetTypeInfoCount(out int c);
    [PreserveSig] int GetTypeInfo(int i, int lcid, out IntPtr ti);
    [PreserveSig] int GetIDsOfNames(ref Guid riid,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        int cNames, int lcid,
        [MarshalAs(UnmanagedType.LPArray)] int[] dispIds);
    [PreserveSig] int Invoke(int dispId, ref Guid riid, int lcid, ushort flags,
        ref CT.DISPPARAMS p, IntPtr varResult, IntPtr excepInfo, IntPtr argErr);
}

public sealed class OneCException : Exception
{
    public int HResultCode { get; init; }
    public string OneCCode { get; init; }          // EXCEPINFO.wCode / scode, when 1C fills it
    public string Operation { get; init; }
    public string Member { get; init; }
    public string BaseName { get; init; }
    public string PlatformVersion { get; init; }
    public new string Source { get; init; }        // EXCEPINFO.bstrSource
    public bool ExcepInfoPresent { get; init; }
    public bool ExcepInfoTextless { get; init; }   // EXCEPINFO returned, description empty
    public int ArgErrIndex { get; init; } = -1;
    public Exception OriginalException { get; init; }

    public bool IsRetryable { get; init; }
    public bool IsSessionFatal { get; init; }
    public bool IsHostFatal { get; init; }

    public OneCException(string msg) : base(msg) { }

    public string Describe() =>
        $"hr=0x{HResultCode:X8} code={OneCCode ?? "-"} op={Operation}/{Member} " +
        $"src={Source ?? "-"} excep={(ExcepInfoPresent ? (ExcepInfoTextless ? "TEXTLESS" : "text") : "none")} " +
        $"retry={IsRetryable} sessionFatal={IsSessionFatal} hostFatal={IsHostFatal} :: {Message}";
}

/// <summary>Controlled late binding: the "option C" wrapper.</summary>
public static class Disp
{
    private static Guid IID_NULL = Guid.Empty;
    private const int LOCALE_SYSTEM_DEFAULT = 0x0800;
    private const int DISPID_PROPERTYPUT = -3;

    private const ushort DISPATCH_METHOD = 1;
    private const ushort DISPATCH_PROPERTYGET = 2;
    private const ushort DISPATCH_PROPERTYPUT = 4;
    private const ushort DISPATCH_PROPERTYPUTREF = 8;

    private static readonly int VariantSize = IntPtr.Size == 8 ? 24 : 16;

    private const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    private const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);
    private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);
    private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    private const int RPC_E_SERVERFAULT = unchecked((int)0x80010105);
    private const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);
    private const int TYPE_E_CANTLOADLIBRARY = unchecked((int)0x80029C4A);

    public static string Context = "";        // base name, for error enrichment
    public static string Version = "";

    private static IDispatchRaw AsDisp(object o) =>
        o as IDispatchRaw ?? throw new InvalidOperationException("object does not expose IDispatch");

    public static int DispIdOf(object o, string name)
    {
        var d = AsDisp(o);
        var ids = new int[1];
        int hr = d.GetIDsOfNames(ref IID_NULL, new[] { name }, 1, LOCALE_SYSTEM_DEFAULT, ids);
        if (hr < 0)
            throw Build(hr, false, default, "GetIDsOfNames", name, -1, null);
        return ids[0];
    }

    public static bool TryDispId(object o, string name, out int id)
    {
        var d = AsDisp(o);
        var ids = new int[1];
        int hr = d.GetIDsOfNames(ref IID_NULL, new[] { name }, 1, LOCALE_SYSTEM_DEFAULT, ids);
        id = ids[0];
        return hr >= 0;
    }

    public static object Call(object o, string name, params object[] args) =>
        InvokeCore(o, name, DISPATCH_METHOD | DISPATCH_PROPERTYGET, args);

    public static object Get(object o, string name, params object[] args) =>
        InvokeCore(o, name, DISPATCH_PROPERTYGET, args);

    public static void Set(object o, string name, object value) =>
        InvokeCore(o, name, DISPATCH_PROPERTYPUT, new[] { value });

    private static object InvokeCore(object o, string name, ushort flags, object[] args)
    {
        var d = AsDisp(o);
        int dispId = DispIdOf(o, name);
        return InvokeById(d, dispId, name, flags, args);
    }

    private static object InvokeById(IDispatchRaw d, int dispId, string name, ushort flags, object[] args)
    {
        args ??= Array.Empty<object>();
        IntPtr rgvarg = IntPtr.Zero, named = IntPtr.Zero, result = IntPtr.Zero,
               excep = IntPtr.Zero, argErr = IntPtr.Zero;
        try
        {
            if (args.Length > 0)
            {
                rgvarg = Marshal.AllocCoTaskMem(VariantSize * args.Length);
                for (int i = 0; i < args.Length; i++)
                {
                    // DISPPARAMS holds arguments in reverse order.
                    IntPtr slot = rgvarg + VariantSize * i;
                    for (int b = 0; b < VariantSize; b++) Marshal.WriteByte(slot, b, 0);
                    Marshal.GetNativeVariantForObject(args[args.Length - 1 - i], slot);
                }
            }

            var dp = new CT.DISPPARAMS { rgvarg = rgvarg, cArgs = args.Length, cNamedArgs = 0, rgdispidNamedArgs = IntPtr.Zero };
            if ((flags & DISPATCH_PROPERTYPUT) != 0)
            {
                named = Marshal.AllocCoTaskMem(sizeof(int));
                Marshal.WriteInt32(named, DISPID_PROPERTYPUT);
                dp.rgdispidNamedArgs = named;
                dp.cNamedArgs = 1;
            }

            result = Marshal.AllocCoTaskMem(VariantSize);
            for (int b = 0; b < VariantSize; b++) Marshal.WriteByte(result, b, 0);
            excep = Marshal.AllocCoTaskMem(Marshal.SizeOf<CT.EXCEPINFO>());
            for (int b = 0; b < Marshal.SizeOf<CT.EXCEPINFO>(); b++) Marshal.WriteByte(excep, b, 0);
            argErr = Marshal.AllocCoTaskMem(sizeof(uint));
            Marshal.WriteInt32(argErr, -1);

            int hr = d.Invoke(dispId, ref IID_NULL, LOCALE_SYSTEM_DEFAULT, flags, ref dp, result, excep, argErr);

            if (hr < 0)
            {
                var ei = Marshal.PtrToStructure<CT.EXCEPINFO>(excep);
                int ae = Marshal.ReadInt32(argErr);
                bool hasEi = hr == DISP_E_EXCEPTION;
                throw Build(hr, hasEi, ei, "Invoke", name, ae, null);
            }

            object rv = Marshal.GetObjectForNativeVariant(result);
            return rv;
        }
        finally
        {
            if (rgvarg != IntPtr.Zero)
            {
                for (int i = 0; i < args.Length; i++) VariantClear(rgvarg + VariantSize * i);
                Marshal.FreeCoTaskMem(rgvarg);
            }
            if (result != IntPtr.Zero) { VariantClear(result); Marshal.FreeCoTaskMem(result); }
            if (excep != IntPtr.Zero) Marshal.FreeCoTaskMem(excep);
            if (named != IntPtr.Zero) Marshal.FreeCoTaskMem(named);
            if (argErr != IntPtr.Zero) Marshal.FreeCoTaskMem(argErr);
        }
    }

    [DllImport("oleaut32")]
    private static extern int VariantClear(IntPtr v);

    /// <summary>Everything the COM layer actually gives us, turned into one typed error.</summary>
    public static OneCException Build(int hr, bool hasExcep, CT.EXCEPINFO ei,
                                      string op, string member, int argErr, Exception inner)
    {
        string desc = hasExcep ? (ei.bstrDescription ?? "") : "";
        string src = hasExcep ? ei.bstrSource : null;
        int scode = hasExcep ? ei.scode : 0;
        int wcode = hasExcep ? ei.wCode : 0;
        bool textless = hasExcep && string.IsNullOrWhiteSpace(desc);

        string msg = !string.IsNullOrWhiteSpace(desc)
            ? desc.Replace("\r", " ").Replace("\n", " ").Trim()
            : hasExcep
                ? $"1C raised a textless error (EXCEPINFO empty; scode=0x{scode:X8} wCode={wcode})"
                : HrName(hr);

        bool sessionFatal = hr is RPC_E_DISCONNECTED or RPC_E_SERVERFAULT or CO_E_OBJNOTCONNECTED;
        bool hostFatal = hr == TYPE_E_CANTLOADLIBRARY;
        bool retry = sessionFatal
                     || msg.Contains("заблокирован", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("блокировк", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase);

        return new OneCException(msg)
        {
            HResultCode = hr,
            OneCCode = hasExcep && (wcode != 0 || scode != 0) ? $"w{wcode}/0x{scode:X8}" : null,
            Operation = op,
            Member = member,
            BaseName = Context,
            PlatformVersion = Version,
            Source = src,
            ExcepInfoPresent = hasExcep,
            ExcepInfoTextless = textless,
            ArgErrIndex = argErr,
            OriginalException = inner,
            IsRetryable = retry,
            IsSessionFatal = sessionFatal,
            IsHostFatal = hostFatal
        };
    }

    public static string HrName(int hr) => hr switch
    {
        DISP_E_MEMBERNOTFOUND => "DISP_E_MEMBERNOTFOUND — no such property or method",
        DISP_E_UNKNOWNNAME => "DISP_E_UNKNOWNNAME — name not found on this object",
        DISP_E_TYPEMISMATCH => "DISP_E_TYPEMISMATCH — argument type wrong",
        DISP_E_BADPARAMCOUNT => "DISP_E_BADPARAMCOUNT — wrong number of arguments",
        RPC_E_DISCONNECTED => "RPC_E_DISCONNECTED — session is gone",
        CO_E_OBJNOTCONNECTED => "CO_E_OBJNOTCONNECTED — object no longer connected",
        TYPE_E_CANTLOADLIBRARY => "TYPE_E_CANTLOADLIBRARY — comcntr type library cannot load",
        _ => $"HRESULT 0x{hr:X8}"
    };

    /// <summary>
    /// Same classification for an exception the runtime binder produced, so dynamic and this
    /// wrapper can be compared on equal footing.
    /// </summary>
    public static OneCException FromClr(Exception ex, string op, string member)
    {
        int hr = ex is COMException ce ? ce.HResult : ex.HResult;
        bool binderCrash = ex is NullReferenceException &&
                           (ex.StackTrace ?? "").Contains("ExcepInfo", StringComparison.OrdinalIgnoreCase);
        string msg = binderCrash
            ? "binder crashed building the exception (textless 1C error)"
            : (ex.Message ?? ex.GetType().Name);
        return new OneCException(msg)
        {
            HResultCode = hr,
            Operation = op,
            Member = member,
            BaseName = Context,
            PlatformVersion = Version,
            ExcepInfoPresent = false,
            ExcepInfoTextless = binderCrash,
            OriginalException = ex,
            IsSessionFatal = hr is RPC_E_DISCONNECTED or CO_E_OBJNOTCONNECTED,
            IsHostFatal = hr == TYPE_E_CANTLOADLIBRARY
        };
    }
}
