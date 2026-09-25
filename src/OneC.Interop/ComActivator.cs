using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneC.Interop;

/// <summary>
/// Creates V83.ComConnector from an explicit comcntr.dll path. No registry, no regsvr32,
/// no admin, no PATH change, no colocated files — LOAD_WITH_ALTERED_SEARCH_PATH already
/// searches the DLL's own directory, where its 69 sibling modules live.
/// One 1C version per process is a hard Windows loader constraint: a second comcntr
/// resolves its imports against the first version's already-mapped, unversioned siblings
/// and fails with win32 127. This class enforces that instead of discovering it at runtime.
/// </summary>
public static class ComActivator
{
    public static readonly Guid ClsidV83ComConnector = new("181E893D-73A4-4722-B61D-D604B3D67D47");
    private static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private const uint LoadWithAlteredSearchPath = 0x00000008;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectFn(ref Guid clsid, ref Guid iid, out IntPtr ppv);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
    }

    private static readonly object Gate = new();
    private static string? _boundPath;
    private static IntPtr _module;

    /// <summary>The comcntr.dll this process is bound to, once anything has been created.</summary>
    public static string? BoundPath { get { lock (Gate) return _boundPath; } }

    /// <summary>
    /// Bind the process to one comcntr.dll. Safe to call repeatedly with the same path;
    /// a different path throws rather than half-loading a second 1C version.
    /// </summary>
    public static void Bind(string comcntrPath)
    {
        comcntrPath = Path.GetFullPath(comcntrPath);
        lock (Gate)
        {
            if (_boundPath is not null)
            {
                if (string.Equals(_boundPath, comcntrPath, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException(
                    $"This process is already bound to {_boundPath}. A process can host exactly " +
                    $"one 1C version; start another OneC.Host for {comcntrPath}.");
            }

            if (!File.Exists(comcntrPath))
                throw new FileNotFoundException(
                    $"comcntr.dll not found — 1C version not installed: {comcntrPath}", comcntrPath);

            NativeProcess.CaptureCrashFilter();          // before any 1C module can replace it
            IntPtr module = LoadLibraryExW(comcntrPath, IntPtr.Zero, LoadWithAlteredSearchPath);
            int err = Marshal.GetLastWin32Error();
            if (module == IntPtr.Zero)
                throw new COMException(
                    $"LoadLibraryEx failed for {comcntrPath}: win32 {err} " +
                    $"({new System.ComponentModel.Win32Exception(err).Message})",
                    unchecked((int)(0x80070000 | (uint)err)));

            if (GetProcAddress(module, "DllGetClassObject") == IntPtr.Zero)
                throw new EntryPointNotFoundException(
                    $"{comcntrPath} has no DllGetClassObject — not a COM server");

            _module = module;
            _boundPath = comcntrPath;
        }
    }

    private static object? _shared;

    /// <summary>
    /// The process's one connector, created on first use and never released.
    ///
    /// Releasing the last V83.ComConnector in a process and creating another one later
    /// leaves comcntr broken: the next query on the new connector died with 0xC0000005 in
    /// IDispatch::Invoke (milestone 2.2, reproduced; the same scenario in a fresh process runs
    /// clean). It is the same teardown landmine research hit as TYPE_E_CANTLOADLIBRARY. So the
    /// connector lives for the process: SessionManagers come and go, the connector does not.
    /// It holds ~5 MB; sessions, which hold the real memory, are still released normally.
    /// </summary>
    public static object SharedConnector()
    {
        // Held across creation: a lost race would leave a second connector that could only
        // be released — the one thing this method exists to avoid. Monitor is re-entrant,
        // so CreateConnector taking the same gate is fine.
        lock (Gate) return _shared ??= CreateConnector();
    }

    /// <summary>
    /// Creates a new V83.ComConnector from the bound DLL. Production code uses
    /// <see cref="SharedConnector"/>; this exists for probes that need a connector with
    /// different settings. Never release the last one in the process.
    /// </summary>
    public static object CreateConnector()
    {
        IntPtr module;
        lock (Gate)
        {
            if (_boundPath is null)
                throw new InvalidOperationException("ComActivator.Bind was never called");
            module = _module;
        }

        var p = GetProcAddress(module, "DllGetClassObject");
        var fn = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectFn>(p);

        Guid clsid = ClsidV83ComConnector, iidCf = IidClassFactory, iidUnk = IidUnknown;
        int hr = fn(ref clsid, ref iidCf, out IntPtr pcf);
        if (hr < 0) throw new COMException($"DllGetClassObject failed 0x{hr:X8}", hr);

        var factory = (IClassFactory)Marshal.GetObjectForIUnknown(pcf);
        Marshal.Release(pcf);
        try
        {
            hr = factory.CreateInstance(IntPtr.Zero, ref iidUnk, out IntPtr instance);
            if (hr < 0) throw new COMException($"IClassFactory::CreateInstance failed 0x{hr:X8}", hr);
            var o = Marshal.GetObjectForIUnknown(instance);
            Marshal.Release(instance);
            return o;
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    }

    /// <summary>Every comcntr.dll mapped into this process. Should always be exactly one.</summary>
    public static IReadOnlyList<(string Path, string Version)> LoadedComcntrModules()
    {
        var list = new List<(string, string)>();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        foreach (ProcessModule m in p.Modules)
        {
            if (!m.ModuleName.Equals("comcntr.dll", StringComparison.OrdinalIgnoreCase)) continue;
            string v;
            try { v = FileVersionInfo.GetVersionInfo(m.FileName).FileVersion ?? "?"; }
            catch { v = "?"; }
            list.Add((m.FileName, v));
        }
        return list;
    }
}
