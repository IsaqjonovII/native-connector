using System.Runtime.InteropServices;

namespace OneC.Interop;

/// <summary>
/// Process-level guards against native code the 1C platform brings into the process.
///
/// 1C 8.3.15 ships ImageMagick 6.9.3 (CORE_RL_magick_.dll). Its NTWindowsGenesis does
/// <c>prev = SetUnhandledExceptionFilter(NTUncaughtException)</c>, and 1C runs it twice during
/// the first Connect, so the saved "previous" filter is the filter itself. NTUncaughtException
/// ends with a tail jump to "previous": from then on any native exception nobody handles — a
/// crash on one of 1C's own threads, or during process exit — spins one core forever instead of
/// crashing (2026-09-24: ten test hosts at 83 % CPU, each stuck in that loop after an access
/// violation inside 1C's exit code). The same DLL is in the old oscript adapter.
///
/// Two guards: put back the filter the process had before 1C loaded after every Connect, and
/// end a process that has loaded 1C with TerminateProcess, which runs no DLL detach code at all.
/// </summary>
public static class NativeProcess
{
    [DllImport("kernel32")]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, char[] name, uint size);

    private static readonly object Gate = new();
    private static IntPtr _filter;
    private static bool _captured;

    /// <summary>Remembers the process's crash filter. Call before any 1C module is loaded.</summary>
    public static void CaptureCrashFilter()
    {
        lock (Gate)
        {
            if (_captured) return;
            _filter = SetUnhandledExceptionFilter(IntPtr.Zero);
            SetUnhandledExceptionFilter(_filter);
            _captured = true;
        }
    }

    /// <summary>Puts back the filter captured before 1C loaded. Call after every Connect.</summary>
    public static void RestoreCrashFilter()
    {
        lock (Gate) if (_captured) SetUnhandledExceptionFilter(_filter);
    }

    /// <summary>The module that owns the current crash filter ("" when none). Diagnostics only.</summary>
    public static string CrashFilterModule()
    {
        IntPtr current;
        lock (Gate)
        {
            current = SetUnhandledExceptionFilter(IntPtr.Zero);
            SetUnhandledExceptionFilter(current);
        }
        if (current == IntPtr.Zero) return "";
        // FROM_ADDRESS | UNCHANGED_REFCOUNT
        if (!GetModuleHandleExW(0x4 | 0x2, current, out IntPtr module)) return $"0x{current:X}";
        var name = new char[260];
        uint n = GetModuleFileNameW(module, name, (uint)name.Length);
        return Path.GetFileName(new string(name, 0, (int)n));
    }

    /// <summary>
    /// Ends this process now with <paramref name="code"/>, skipping every DLL's detach code.
    /// Only for a process whose own cleanup (sessions released, output written) is done.
    /// </summary>
    public static void Exit(int code)
    {
        try { Console.Out.Flush(); Console.Error.Flush(); } catch { /* nothing left to report to */ }
        TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
    }

    /// <summary>
    /// For a process whose Main is not ours (the test host): end it with <see cref="Exit"/> when
    /// .NET raises ProcessExit, if 1C was loaded. Handlers registered later do not run.
    /// </summary>
    public static void ExitHardAtProcessExit() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (ComActivator.BoundPath is not null) Exit(Environment.ExitCode);
        };
}
