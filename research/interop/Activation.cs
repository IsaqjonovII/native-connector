using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneC.Interop;

/// <summary>
/// Three ways to get a V83.ComConnector instance, so they can be compared head to head:
///   registry — CoCreateInstance via ProgID. Whatever regsvr32 last pointed at. Today's Connector.
///   direct   — LoadLibrary an explicit comcntr.dll path, then DllGetClassObject. No registry at all.
///   sxs      — a registration-free COM activation context (manifest) around CoCreateInstance.
/// </summary>
public static class Activation
{
    public static readonly Guid CLSID_V83ComConnector = new("181E893D-73A4-4722-B61D-D604B3D67D47");
    private static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr h, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr mod, string name);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectFn(ref Guid clsid, ref Guid iid, out IntPtr ppv);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr obj);
        [PreserveSig] int LockServer(bool l);
    }

    /// <summary>Keeps every comcntr.dll this process pulled in, keyed by full path.</summary>
    private static readonly Dictionary<string, IntPtr> _loaded = new(StringComparer.OrdinalIgnoreCase);

    // ---------- registry ----------

    public static object CreateViaRegistry()
    {
        var t = Type.GetTypeFromProgID("V83.COMConnector")
                ?? throw new InvalidOperationException("V83.COMConnector is not registered");
        return Activator.CreateInstance(t)!;
    }

    // ---------- direct path ----------

    /// <summary>
    /// Bind to one exact comcntr.dll. The DLL's own bin directory goes on the module search
    /// path first — comcntr pulls a dozen siblings (backbas, core83, etc.) out of it.
    /// </summary>
    public static object CreateFromPath(string dllPath)
    {
        dllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath)) throw new FileNotFoundException("comcntr.dll not found", dllPath);

        IntPtr mod;
        lock (_loaded)
        {
            if (!_loaded.TryGetValue(dllPath, out mod))
            {
                // Control flag: prove that naming the bin directory is what makes this work,
                // rather than assuming it.
                bool useDllDir = Environment.GetEnvironmentVariable("ONEC_NO_DLLDIR") != "1";
                if (useDllDir) SetDllDirectoryW(Path.GetDirectoryName(dllPath)!);
                mod = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                int err = Marshal.GetLastWin32Error();   // before SetDllDirectory clobbers it
                SetDllDirectoryW(null);
                if (mod == IntPtr.Zero)
                    throw new COMException(
                        $"LoadLibraryEx failed for {dllPath}: win32 {err} ({new System.ComponentModel.Win32Exception(err).Message})",
                        unchecked((int)(0x80070000 | (uint)err)));
                _loaded[dllPath] = mod;
            }
        }

        var p = GetProcAddress(mod, "DllGetClassObject");
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException("DllGetClassObject");
        var fn = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectFn>(p);

        Guid clsid = CLSID_V83ComConnector, iidCf = IID_IClassFactory, iidUnk = IID_IUnknown;
        int hr = fn(ref clsid, ref iidCf, out IntPtr pcf);
        if (hr < 0) throw new COMException($"DllGetClassObject 0x{hr:X8}", hr);

        var cf = (IClassFactory)Marshal.GetObjectForIUnknown(pcf);
        Marshal.Release(pcf);
        hr = cf.CreateInstance(IntPtr.Zero, ref iidUnk, out IntPtr pobj);
        Marshal.FinalReleaseComObject(cf);
        if (hr < 0) throw new COMException($"IClassFactory::CreateInstance 0x{hr:X8}", hr);

        var o = Marshal.GetObjectForIUnknown(pobj);
        Marshal.Release(pobj);
        return o;
    }

    // ---------- SxS / registration-free ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ACTCTXW
    {
        public int cbSize;
        public uint dwFlags;
        public string lpSource;
        public ushort wProcessorArchitecture;
        public ushort wLangId;
        public string lpAssemblyDirectory;
        public string lpResourceName;
        public string lpApplicationName;
        public IntPtr hModule;
    }

    private const uint ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x004;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateActCtxW(ref ACTCTXW p);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool ActivateActCtx(IntPtr h, out IntPtr cookie);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeactivateActCtx(uint flags, IntPtr cookie);
    [DllImport("ole32")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx,
                                               ref Guid iid, out IntPtr obj);
    private const uint CLSCTX_INPROC_SERVER = 1;

    /// <summary>
    /// Writes an SxS manifest that declares the comcntr coclass, activates it, and calls a
    /// plain CoCreateInstance inside it. `assemblyDir` is where SxS looks for the file named
    /// by the manifest — point it at the version's bin directory.
    /// </summary>
    public static object CreateViaSxS(string manifestPath, string assemblyDir, out string note)
    {
        var a = new ACTCTXW
        {
            cbSize = Marshal.SizeOf<ACTCTXW>(),
            dwFlags = ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID,
            lpSource = manifestPath,
            lpAssemblyDirectory = assemblyDir
        };
        IntPtr hctx = CreateActCtxW(ref a);
        if (hctx == IntPtr.Zero || hctx == new IntPtr(-1))
            throw new COMException("CreateActCtx failed", Marshal.GetLastWin32Error());

        if (!ActivateActCtx(hctx, out IntPtr cookie))
            throw new COMException("ActivateActCtx failed", Marshal.GetLastWin32Error());
        try
        {
            Guid clsid = CLSID_V83ComConnector, iid = IID_IUnknown;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr p);
            if (hr < 0) throw new COMException($"CoCreateInstance 0x{hr:X8}", hr);
            var o = Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            note = "activated inside SxS context";
            return o;
        }
        finally { DeactivateActCtx(0, cookie); }
    }

    // ---------- proof of what actually loaded ----------

    /// <summary>Every comcntr.dll currently mapped into this process, with its file version.</summary>
    public static List<(string path, string version)> LoadedComcntrModules()
    {
        var r = new List<(string, string)>();
        var p = Process.GetCurrentProcess();
        p.Refresh();
        foreach (ProcessModule m in p.Modules)
        {
            if (!m.ModuleName.Equals("comcntr.dll", StringComparison.OrdinalIgnoreCase)) continue;
            string ver;
            try { ver = FileVersionInfo.GetVersionInfo(m.FileName).FileVersion ?? "?"; }
            catch { ver = "?"; }
            r.Add((m.FileName, ver));
        }
        return r;
    }

    /// <summary>Ask 1C itself which platform it is. The only answer that is not inference.</summary>
    public static string PlatformVersionFromConnection(object connection)
    {
        dynamic c = connection;
        dynamic si = c.NewObject("СистемнаяИнформация");
        try { return (string)si.ВерсияПриложения; }
        finally { if (si != null && Marshal.IsComObject(si)) Marshal.FinalReleaseComObject(si); }
    }
}
