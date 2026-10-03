using System.Diagnostics;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Runs one <c>OneCLiveChild</c> scenario in a process of its own and fails the calling test with
/// the child's own words — or its exit code, when 1C crashed it (0xC0000374: the heap corruption
/// MIGRATION_STATUS lists under known limits, seen only in processes that open a second
/// SessionManager — a process arrangement production never has: one host, one manager). The crash
/// is still a failure; it just no longer takes the whole run with it (DECISIONS D45).
///  - one process per scenario; the base list reaches it through the inherited ONEC_TEST_BASES
///    path, never the command line (it holds 1C passwords);
///  - stdout/stderr are read asynchronously, so a child stuck in 1C's exit (its crash filter
///    spinning with the pipe still open, D37) cannot hang the test past the timeout;
///  - on timeout, failure or success the child is gone before the test returns: "gone" = its
///    process handle signalled (AGENT.md), after a kill when it did not end by itself.
/// The read scenarios leave no test data behind; the write scenarios (SyncLiveTests) delete their
/// own document and, when the child fails or dies, the calling test runs <see cref="CleanupOwned"/>.
/// The OS releases what a dead child held (the machine-wide file-connect lock is an open handle);
/// the OneC.Host processes a child's Supervisor started end when its stdin closes, and
/// <see cref="Run(string, TimeSpan, string[])"/> waits for them — it never kills one (a 1C process
/// may be stalled in the file base, AGENT.md), it fails the test if one stays.
/// </summary>
internal static class LiveChild
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(4);

    /// <summary>The last child's process id (the harness's own tests check it is gone).</summary>
    internal static int LastPid { get; private set; }

    public static void Run(string scenario) => Run(scenario, Limit);

    /// <summary>
    /// Runs <paramref name="scenario"/> with non-secret <paramref name="args"/> (a run tag, a cleanup
    /// prefix) and returns its output for the test log; fails the test as <see cref="Run(string)"/> does.
    /// </summary>
    /// <param name="limit">Only the harness's own tests pass a shorter one.</param>
    internal static string Run(string scenario, TimeSpan limit, params string[] args)
    {
        string child = Path.Combine(AppContext.BaseDirectory, "OneCLiveChild.dll");
        Assert.True(File.Exists(child), child);
        if (!scenario.StartsWith("selftest-", StringComparison.Ordinal))
            Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONEC_TEST_BASES")), "ONEC_TEST_BASES is not set");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add(child);
        psi.ArgumentList.Add(scenario);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var started = DateTime.Now.AddSeconds(-1);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        bool ended = false;
        try
        {
            ended = p.WaitForExit(limit);
        }
        finally
        {
            if (!ended)
            {
                // The child only: its OneC.Host processes end when its pipes close (WaitForChildren).
                try { p.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
                p.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }
        string Text(Task<string> t) => t.Wait(TimeSpan.FromSeconds(10)) ? t.Result : "(output not closed)";
        string output = Text(stdout), errors = Text(stderr);
        LastPid = p.Id;
        string orphans = WaitForChildren(p.Id, started, TimeSpan.FromSeconds(60));
        Assert.True(ended, $"{scenario}: no answer in {limit.TotalSeconds:0} s, killed\n{output}\n{errors}{orphans}");
        Assert.True(p.ExitCode == 0 && output.Contains("PASS", StringComparison.Ordinal),
                    $"{scenario}: exit 0x{p.ExitCode:X}\n{output}\n{errors}{orphans}");
        Assert.True(orphans.Length == 0, $"{scenario}: passed, but{orphans}");
        return output;
    }

    /// <summary>
    /// Runs the guarded fallback cleanup in a child of its own (the parent still holds no 1C):
    /// deletes <paramref name="docType"/> documents whose comment starts with the run-specific
    /// <paramref name="prefix"/> — the child refuses anything but <c>AIBA_REWRITE_S&lt;n&gt;_&lt;tag&gt;</c>.
    /// Returns what it did, or why it failed, for the failing test's message.
    /// </summary>
    internal static string CleanupOwned(string docType, string prefix)
    {
        try { return "\nfallback cleanup: " + Run("cleanup-owned", TimeSpan.FromMinutes(10), docType, prefix).Trim(); }
        catch (Exception e) { return "\nfallback cleanup FAILED: " + e.Message; }
    }

    /// <summary>
    /// Waits for the processes the child started (OneC.Host under its Supervisor) to end: they end
    /// when the child's pipe to them closes. Never kills one; names those still running.
    /// </summary>
    private static string WaitForChildren(int parentPid, DateTime parentStart, TimeSpan limit)
    {
        var left = new List<string>();
        foreach (var pid in Descendants(parentPid))
        {
            try
            {
                using var c = Process.GetProcessById(pid);
                if (c.StartTime < parentStart) continue;        // an older process whose dead parent had this pid
                string name = c.ProcessName;
                if (!c.WaitForExit(limit)) left.Add($"{name} {pid}");
            }
            catch (ArgumentException) { }                   // already gone
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return left.Count == 0 ? "" : $"\nstill running after the child ended (not killed): {string.Join(", ", left)}";
    }

    /// <summary>Processes whose recorded parent is <paramref name="pid"/>, and theirs (the parent may already be gone).</summary>
    private static List<int> Descendants(int pid)
    {
        var all = new List<(int Pid, int Parent)>();
        IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return new List<int>();
        try
        {
            var e = new ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                all.Add(((int)e.ProcessId, (int)e.ParentProcessId));
        }
        finally { CloseHandle(snap); }
        var found = new List<int>();
        var queue = new Queue<int>(new[] { pid });
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            foreach (var (c, _) in all.Where(x => x.Parent == parent && x.Pid != pid && !found.Contains(x.Pid)))
            {
                found.Add(c);
                queue.Enqueue(c);
            }
        }
        return found;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
