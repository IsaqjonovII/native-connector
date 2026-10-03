using System.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace OneC.Tests;

/// <summary>The child-process harness itself (no 1C): every way a child can end is surfaced, and none survives.</summary>
public class LiveChildTests
{
    private static void Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // GetProcessById can return an exited process whose handle is still open: signalled = gone.
            Assert.True(p.WaitForExit(TimeSpan.FromSeconds(10)), $"child {pid} still running");
        }
        catch (ArgumentException) { }                       // no such process
    }

    [Fact]
    public void APassingChildPasses()
    {
        LiveChild.Run("selftest-pass", TimeSpan.FromSeconds(60));
        Gone(LiveChild.LastPid);
    }

    [Fact]
    public void AReportedFailureFailsTheTestWithTheChildsWords()
    {
        var e = Assert.ThrowsAny<XunitException>(() => LiveChild.Run("selftest-fail", TimeSpan.FromSeconds(60)));
        Assert.Contains("FAIL: as asked", e.Message);
        Assert.Contains("exit 0x1", e.Message);
        Gone(LiveChild.LastPid);
    }

    [Fact]
    public void ANativeCrashFailsTheTestWithItsExitCodeAndLeavesThisProcessRunning()
    {
        var e = Assert.ThrowsAny<XunitException>(() => LiveChild.Run("selftest-crash", TimeSpan.FromSeconds(60)));
        Assert.DoesNotContain("exit 0x0\n", e.Message);
        Assert.Matches("exit 0x[0-9A-F]+", e.Message);
        Gone(LiveChild.LastPid);
    }

    /// <summary>The fallback cleanup deletes one run's documents only: a broad prefix is refused before any 1C call.</summary>
    [Fact]
    public void TheFallbackCleanupRefusesABroadPrefix()
    {
        if (Gate.Skip(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONEC_TEST_BASES")), "ONEC_TEST_BASES is not set")) return;
        foreach (var prefix in new[] { "AIBA_", "AIBA_REWRITE_", "AIBA_REWRITE_S8_", "AIBA_REWRITE_S8_x" })
        {
            string report = LiveChild.CleanupOwned("ПоступлениеТоваровУслуг", prefix);
            Assert.Contains("FAILED", report);
            Assert.Contains("refused", report);
            Gone(LiveChild.LastPid);
        }
    }

    [Fact]
    public void AHangingChildIsKilledAtTheLimit()
    {
        var sw = Stopwatch.StartNew();
        var e = Assert.ThrowsAny<XunitException>(() => LiveChild.Run("selftest-hang", TimeSpan.FromSeconds(3)));
        Assert.Contains("no answer in 3 s, killed", e.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45), $"took {sw.Elapsed}");
        Gone(LiveChild.LastPid);
    }
}
