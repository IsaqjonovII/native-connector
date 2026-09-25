namespace OneC.Interop;

/// <summary>HRESULTs we actually branch on. Everything else stays a raw number.</summary>
public static class Hr
{
    public const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    public const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    public const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);
    public const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);
    public const int DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);

    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    public const int RPC_E_SERVERFAULT = unchecked((int)0x80010105);
    public const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
    public const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);

    public const int TYPE_E_CANTLOADLIBRARY = unchecked((int)0x80029C4A);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    /// <summary>The session is gone; the caller must get a new one.</summary>
    public static bool IsSessionFatal(int hr) =>
        hr is RPC_E_DISCONNECTED or RPC_E_SERVERFAULT or CO_E_OBJNOTCONNECTED;

    /// <summary>The comcntr in this process is unusable; the whole host must be replaced.</summary>
    public static bool IsHostFatal(int hr) => hr == TYPE_E_CANTLOADLIBRARY;

    public static string Name(int hr) => hr switch
    {
        DISP_E_UNKNOWNNAME => "DISP_E_UNKNOWNNAME (no such member)",
        DISP_E_MEMBERNOTFOUND => "DISP_E_MEMBERNOTFOUND",
        DISP_E_EXCEPTION => "DISP_E_EXCEPTION",
        DISP_E_TYPEMISMATCH => "DISP_E_TYPEMISMATCH",
        DISP_E_BADPARAMCOUNT => "DISP_E_BADPARAMCOUNT",
        DISP_E_PARAMNOTFOUND => "DISP_E_PARAMNOTFOUND",
        RPC_E_DISCONNECTED => "RPC_E_DISCONNECTED (session gone)",
        RPC_E_SERVERFAULT => "RPC_E_SERVERFAULT",
        RPC_E_WRONG_THREAD => "RPC_E_WRONG_THREAD",
        CO_E_OBJNOTCONNECTED => "CO_E_OBJNOTCONNECTED",
        TYPE_E_CANTLOADLIBRARY => "TYPE_E_CANTLOADLIBRARY (comcntr typelib)",
        E_FAIL => "E_FAIL",
        E_NOTIMPL => "E_NOTIMPL",
        _ => $"HRESULT 0x{hr:X8}"
    };
}
