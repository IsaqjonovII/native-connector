using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OneC.Desktop.Services;

/// <summary>
/// "Start with Windows" (the per-user Run key, the old Connector's mechanism) and "keep this PC
/// awake" (SetThreadExecutionState). Both are per-user and need no administrator rights.
/// </summary>
internal static class StartupAndPower
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AIBA Connector";
    /// <summary>The flag the Run entry passes: start in the notification area, no window.</summary>
    public const string BackgroundArg = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArg}";

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
    }

    public static void SetStartsWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, Command);
        else k.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>
    /// On start: if the entry exists but points at another exe (the app folder moved or was
    /// updated in place under a new path), point it at this one so the toggle stays truthful.
    /// </summary>
    public static void RepairStartEntry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k?.GetValue(RunValue) is string cur && !string.Equals(cur, Command, StringComparison.OrdinalIgnoreCase))
            k.SetValue(RunValue, Command);
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    /// <summary>
    /// Keeps Windows from sleeping while the app runs (the screen may still turn off). The request
    /// belongs to the calling thread, so it is made on the UI thread, which lives as long as the app.
    /// </summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
