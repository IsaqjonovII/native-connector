using System.Runtime.InteropServices;

namespace OneC.Interop;

/// <summary>
/// The connector's own dual interfaces, called through the vtable.
///
/// Why not IDispatch: V83.ComConnector implements its IDispatch with the REGISTERED type
/// library (LoadRegTypeLib). With the machine registration gone, GetIDsOfNames("Connect")
/// fails with TYPE_E_LIBNOTREGISTERED (0x8002801D) even though explicit-path activation still
/// creates the object — observed live on 2026-09-23 when something removed the comcntr
/// registration mid-run. Vtable calls need no type library, so the host no longer depends on
/// any registry state at all. Layouts read from the typelib embedded in comcntr.dll
/// (research/typelib-probe), identical in 8.3.15 and 8.3.18.
///
/// Objects past Connect (the connection, queries, documents) publish no type library and
/// implement IDispatch themselves, so <see cref="Dispatch"/> keeps working for them.
/// </summary>
public static class ConnectorApi
{
    // The result slot is a raw, zero-initialised pointer, not `out object`: with PreserveSig
    // the marshaller converts an `out object` even when Connect FAILED, and a callee that
    // leaves junk in the slot on failure would have .NET AddRef a garbage pointer. We only
    // turn the pointer into an object after a success HRESULT.
    [ComImport, Guid("ba4e52bd-dcb2-4bf7-bb29-84c1ca456a8f"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IV8COMConnector
    {
        [PreserveSig]
        int Connect([MarshalAs(UnmanagedType.BStr)] string connectString, ref IntPtr conn);
    }

    [ComImport, Guid("687cb41e-3fbc-4096-9baa-9065f2546d8f"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IV8COMConnector2
    {
        [PreserveSig] int Connect([MarshalAs(UnmanagedType.BStr)] string connectString, ref IntPtr conn);
        [PreserveSig] int GetPoolCapacity(out int v);
        [PreserveSig] int SetPoolCapacity(int v);
        [PreserveSig] int GetPoolTimeout(out int v);
        [PreserveSig] int SetPoolTimeout(int v);
    }

    [DllImport("oleaut32")]
    private static extern int GetErrorInfo(int reserved, out IntPtr errorInfo);

    [ComImport, Guid("1CF2B120-547D-101B-8E65-08002B2BD119"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IErrorInfo
    {
        [PreserveSig] int GetGUID(out Guid g);
        [PreserveSig] int GetSource([MarshalAs(UnmanagedType.BStr)] out string? source);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string? description);
        [PreserveSig] int GetHelpFile([MarshalAs(UnmanagedType.BStr)] out string? helpFile);
        [PreserveSig] int GetHelpContext(out int ctx);
    }

    /// <summary>Opens a 1C session. Caller owns the returned connection object.</summary>
    public static object Connect(object connector, string connectionString, ErrorContext ctx)
    {
        if (connector is not IV8COMConnector c)
            throw OneCException.Host("object is not a V83.ComConnector (IV8COMConnector missing)", ctx, "Connect");

        IntPtr p = IntPtr.Zero;
        int hr = c.Connect(connectionString, ref p);
        // Opening a base again after its last session closed re-initialises 1C's ImageMagick,
        // which leaves a crash filter that loops forever (NativeProcess).
        NativeProcess.RestoreCrashFilter();
        if (hr < 0 || p == IntPtr.Zero)
            throw Failure(hr < 0 ? hr : Hr.E_FAIL, ctx, "Connect");
        try { return Marshal.GetObjectForIUnknown(p); }
        finally { Marshal.Release(p); }                 // the RCW holds its own reference
    }

    public static void SetPool(object connector, int capacity, int timeoutSeconds, ErrorContext ctx)
    {
        if (connector is not IV8COMConnector2 c)
            throw OneCException.Host("connector does not implement IV8COMConnector2", ctx, "SetPool");
        int hr = c.SetPoolCapacity(capacity);
        if (hr >= 0) hr = c.SetPoolTimeout(timeoutSeconds);
        if (hr < 0) throw Failure(hr, ctx, "SetPool");
    }

    public static (int Capacity, int Timeout) GetPool(object connector, ErrorContext ctx)
    {
        if (connector is not IV8COMConnector2 c)
            throw OneCException.Host("connector does not implement IV8COMConnector2", ctx, "GetPool");
        int to = 0;
        int hr = c.GetPoolCapacity(out int cap);
        if (hr >= 0) hr = c.GetPoolTimeout(out to);
        if (hr < 0) throw Failure(hr, ctx, "GetPool");
        return (cap, to);
    }

    /// <summary>
    /// A vtable call reports its text through IErrorInfo (SetErrorInfo), not EXCEPINFO.
    /// Same layer rules as the IDispatch path: the connector's source is "V83.COMConnector*".
    /// </summary>
    private static OneCException Failure(int hr, ErrorContext ctx, string op)
    {
        string? desc = null, src = null;
        if (GetErrorInfo(0, out IntPtr pei) == 0 && pei != IntPtr.Zero)
        {
            try
            {
                var ei = (IErrorInfo)Marshal.GetObjectForIUnknown(pei);
                ei.GetDescription(out desc);
                ei.GetSource(out src);
                Marshal.FinalReleaseComObject(ei);
            }
            finally { Marshal.Release(pei); }
        }

        var ex = new System.Runtime.InteropServices.ComTypes.EXCEPINFO
        {
            bstrDescription = desc ?? "",
            bstrSource = src ?? "V83.COMConnector",
            scode = hr
        };
        // Reuse the one classifier: a Connector-layer error with the connector's own text.
        return OneCException.FromInvoke(Hr.DISP_E_EXCEPTION, hasExcepInfo: true, ex, -1, ctx, op, "Connect");
    }
}
