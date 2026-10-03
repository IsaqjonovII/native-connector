using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OneC.Desktop.Services;

public enum SupervisorState { Stopped, Starting, Running, Failed }

/// <summary>
/// Owns the <c>OneC.Supervisor run --bases-stdin --port 0</c> child process. Bases (with
/// credentials) go over its stdin once; its stdout ready line gives the port and token for
/// the HTTP edge. The app never touches 1C itself (DECISIONS D28).
///
/// Lifetime: the supervisor exits when its stdin closes, and hosts exit when the supervisor's
/// pipes close — so if this app dies, the whole stack goes with it. No orphan processes.
/// </summary>
public sealed class SupervisorProcess : IDisposable
{
    private Process? _proc;
    private readonly object _lock = new();

    public SupervisorState State { get; private set; } = SupervisorState.Stopped;
    public string? LastError { get; private set; }
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public int? Pid => _proc is { HasExited: false } ? _proc.Id : null;
    public DateTime? StartedUtc { get; private set; }
    public List<(string Name, string Reason)> Unplaceable { get; } = new();
    public EdgeClient? Client { get; private set; }

    public event EventHandler? StateChanged;

    public string? RuntimeDir { get; }

    public SupervisorProcess() => RuntimeDir = FindRuntime();

    /// <summary>
    /// Where OneC.Supervisor.exe and OneC.Host.exe live. AIBA_ONEC_RUNTIME wins; otherwise the
    /// app's own "runtime" folder; otherwise (development) the Release outputs in the repo.
    /// </summary>
    private static string? FindRuntime()
    {
        string? env = Environment.GetEnvironmentVariable("AIBA_ONEC_RUNTIME");
        if (env is not null && File.Exists(Path.Combine(env, "OneC.Supervisor.exe"))) return env;

        string local = Path.Combine(AppContext.BaseDirectory, "runtime");
        if (File.Exists(Path.Combine(local, "OneC.Supervisor.exe"))) return local;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string sup = Path.Combine(d.FullName, "src", "OneC.Supervisor", "bin", "Release", "net9.0");
            if (File.Exists(Path.Combine(sup, "OneC.Supervisor.exe"))) return sup;
        }
        return null;
    }

    private string? HostExe()
    {
        if (RuntimeDir is null) return null;
        string sibling = Path.Combine(RuntimeDir, "OneC.Host.exe");
        if (File.Exists(sibling)) return sibling;
        // Development layout: src/OneC.Supervisor/bin/Release/net9.0 → src/OneC.Host/bin/Release/net9.0
        string dev = Path.GetFullPath(Path.Combine(RuntimeDir, "..", "..", "..", "..", "OneC.Host", "bin", "Release", "net9.0", "OneC.Host.exe"));
        return File.Exists(dev) ? dev : null;
    }

    /// <summary>
    /// Checks a connection before it is saved: a one-shot <c>OneC.Host probe</c> child (connect
    /// once, read the configuration, exit), like the old adapter's --test-connections. The
    /// connection string, password included, goes over stdin only. Returns the host's answer
    /// line: {ok, configuration, synonym, configurationVersion, platformVersion} or {ok:false, code, message}.
    /// </summary>
    public async Task<JsonElement> ProbeAsync(string name, string connectionString, string platformVersion, CancellationToken ct)
    {
        string hostExe = HostExe() ?? throw new InvalidOperationException("OneC.Host.exe not found.");
        var psi = new ProcessStartInfo(hostExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("probe");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the 1C check did not start");
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Name = name, ConnectionString = connectionString, PlatformVersion = platformVersion
        }));
        p.StandardInput.Close();
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
                if (line.StartsWith('{')) return JsonDocument.Parse(line).RootElement.Clone();
            string err = (await stderr).Trim();
            throw new InvalidOperationException("The 1C check ended without an answer" + (err.Length > 0 ? ": " + err : "."));
        }
        catch (OperationCanceledException)
        {
            // A stalled file base can hold the connect for minutes; Kill() works where exit hangs.
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <param name="syncConfig">Sync (preview, behind a setting that is off by default until S15): its config file.</param>
    public async Task StartAsync(string basesPayload, CancellationToken ct = default, string? syncConfig = null)
    {
        Stop();
        Set(SupervisorState.Starting, null);

        string? hostExe = HostExe();
        if (RuntimeDir is null || hostExe is null)
        {
            Set(SupervisorState.Failed, "OneC.Supervisor.exe / OneC.Host.exe not found. Set AIBA_ONEC_RUNTIME " +
                                        "or build src/OneC.Supervisor and src/OneC.Host in Release.");
            return;
        }

        var psi = new ProcessStartInfo(Path.Combine(RuntimeDir, "OneC.Supervisor.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "run", "--bases-stdin", "--port", "0", "--host", hostExe }) psi.ArgumentList.Add(a);
        if (syncConfig is not null) { psi.ArgumentList.Add("--sync-config"); psi.ArgumentList.Add(syncConfig); }

        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("process did not start"); }
        catch (Exception ex) { Set(SupervisorState.Failed, "could not start the supervisor: " + ex.Message); return; }

        lock (_lock) _proc = p;
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            if (State == SupervisorState.Running || State == SupervisorState.Starting)
                Set(SupervisorState.Failed, $"supervisor exited (code {SafeExit(p)})");
        };
        _ = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) is not null) LastError = l;
        });

        await p.StandardInput.WriteLineAsync(basesPayload);
        await p.StandardInput.FlushAsync();
        // stdin stays open: closing it is how the supervisor is told to stop.

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            while (true)
            {
                string? line = await p.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) { Set(SupervisorState.Failed, "supervisor ended before it was ready: " + LastError); return; }
                if (!line.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.TryGetProperty("event", out var ev) && ev.GetString() == "ready")
                {
                    Port = r.GetProperty("port").GetInt32();
                    Token = r.GetProperty("token").GetString() ?? "";
                    Unplaceable.Clear();
                    if (r.TryGetProperty("unplaceable", out var up))
                        foreach (var u in up.EnumerateArray())
                            Unplaceable.Add((u.GetProperty("name").GetString() ?? "", u.GetProperty("reason").GetString() ?? ""));
                    Client?.Dispose();
                    Client = new EdgeClient(Port, Token);
                    StartedUtc = DateTime.UtcNow;
                    Set(SupervisorState.Running, null);
                    // Keep draining stdout so the child never blocks on a full pipe.
                    _ = Task.Run(async () => { while (await p.StandardOutput.ReadLineAsync() is not null) { } });
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Set(SupervisorState.Failed, "supervisor did not report ready within 90 s");
            Kill(p);
        }
    }

    public void Stop()
    {
        Process? p;
        lock (_lock) { p = _proc; _proc = null; }
        Client?.Dispose();
        Client = null;
        if (p is null) { if (State != SupervisorState.Stopped) Set(SupervisorState.Stopped, null); return; }
        State = SupervisorState.Stopped;                      // Exited handler must not flag a failure
        try { p.StandardInput.Close(); } catch { }
        if (!p.WaitForExit(10_000)) Kill(p);
        p.Dispose();
        Set(SupervisorState.Stopped, null);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    private static int? SafeExit(Process p) { try { return p.ExitCode; } catch { return null; } }

    private void Set(SupervisorState s, string? error)
    {
        State = s;
        if (error is not null) LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
