using System.Diagnostics;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// NativeProcess and the supervisor's reaper, driven through OneC.Host probe modes in child
/// processes — a test must not crash or hang its own test host.
/// </summary>
public class NativeProcessTests
{
    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe");

    private static Process Host(params string[] args)
    {
        var psi = new ProcessStartInfo(HostExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static bool InProcessList(int pid) => Process.GetProcesses().Any(p => p.Id == pid);

    /// <summary>
    /// A process stuck inside its own exit already reports HasExited — the supervisor used to
    /// take that as "gone" and never killed it. It must be detected and a kill must finish it.
    /// </summary>
    [Fact]
    public void AHostStuckInItsOwnExitIsDetectedAndKillFinishesIt()
    {
        using var p = Host("exittrap", "--hang");
        var sw = Stopwatch.StartNew();
        while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        Assert.True(p.HasExited, "probe never reached its exit");
        Assert.False(p.WaitForExit(1000), $"probe finished by itself (code {p.ExitCode}); nothing was stuck");
        Assert.True(InProcessList(p.Id), "stuck probe missing from the process list");
        Assert.True(HostProcess.IsStuckInExit(p));

        p.Kill();
        Assert.True(p.WaitForExit(5000), "kill did not finish the stuck probe");
        Assert.False(InProcessList(p.Id));
        Assert.False(HostProcess.IsStuckInExit(p));
    }

    [Fact]
    public void ARunningHostIsNotStuckInExit()
    {
        // `serve` blocks reading its config line from stdin: a live process with no exit code.
        var psi = new ProcessStartInfo(HostExe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add("aiba-reaper-test");
        using var p = Process.Start(psi)!;
        try
        {
            Thread.Sleep(500);
            Assert.False(HostProcess.IsStuckInExit(p));
            Assert.False(p.HasExited);
        }
        finally { p.Kill(); p.WaitForExit(5000); }
    }
}

[Collection("onec-live")]
public class NativeProcessLiveTests
{
    private readonly LiveFixture _f;
    public NativeProcessLiveTests(LiveFixture f) => _f = f;

    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe");

    private string[] BaseArgs() => new[] { "--bases", Environment.GetEnvironmentVariable("ONEC_TEST_BASES")!, "--base", _f.Server!.Name };

    private static (int Code, string Out, bool Finished) Run(string mode, string[] args, TimeSpan within)
    {
        var psi = new ProcessStartInfo(HostExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(mode);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        bool finished = p.WaitForExit(within);
        if (!finished) { p.Kill(); p.WaitForExit(5000); }
        return (finished ? p.ExitCode : -1, output.Wait(5000) ? output.Result : "", finished);
    }

    /// <summary>
    /// After a base is opened again (which arms 1C's looping crash filter), a crash on a native
    /// thread must end the host — before the fix it spun one core and the host lived on.
    /// </summary>
    [Fact]
    public void ANativeCrashAfterReconnectEndsTheHost()
    {
        if (!_f.Available || _f.Server is null) return;
        var r = Run("nativecrash", BaseArgs().Append("--wait").Append("20").ToArray(), TimeSpan.FromSeconds(60));
        Assert.True(r.Finished, "host still running a minute after the crash");
        Assert.Contains("crash filter after reconnect: coreclr.dll", r.Out);
        Assert.DoesNotContain("survived the crash", r.Out);
        Assert.Equal(unchecked((int)0xC0000005), r.Code);
    }

    /// <summary>A host that loaded 1C leaves without running DLL detach code: a crash there never fires.</summary>
    [Fact]
    public void AHostThatLoaded1CExitsWithoutRunningExitCode()
    {
        if (!_f.Available || _f.Server is null) return;
        var r = Run("exittrap", BaseArgs(), TimeSpan.FromSeconds(60));
        Assert.True(r.Finished, "host did not finish");
        Assert.Contains("trap set", r.Out);
        Assert.Equal(0, r.Code);
    }
}
