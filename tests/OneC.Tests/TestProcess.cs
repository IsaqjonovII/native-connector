using System.Runtime.CompilerServices;
using OneC.Interop;

namespace OneC.Tests;

internal static class TestProcess
{
    /// <summary>
    /// The test host loads 1C as OneC.Host does, so it has to leave the same way
    /// (NativeProcess): 1C's own exit code can crash and then spin in its ImageMagick crash
    /// filter, which left a test host per run burning a core and holding the file base open
    /// (2026-09-24).
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => NativeProcess.ExitHardAtProcessExit();
}
