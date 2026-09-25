using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Ipc;
using OneC.Sessions;

namespace OneC.Supervisor;

public enum HostState { Starting, Ready, Exited, Failed }

/// <summary>
/// One running <c>OneC.Host serve --pipe …</c> child (IPC_CONTRACT.md §1). stdin carries the
/// config line and then nothing — closing it tells the host to exit. Requests and responses
/// go over the named pipe, multiplexed by id.
/// </summary>
public sealed class HostProcess : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _supervisorPid = Environment.ProcessId;
    private Process? _proc;
    private NamedPipeClientStream? _pipe;
    private long _nextId;

    public HostAssignment Assignment { get; }
    public string Key => Assignment.Key;
    public string PipeName { get; }
    public HostState State { get; private set; } = HostState.Starting;
    public int? Pid => _proc is { HasExited: false } ? _proc.Id : null;
    public JsonObject? ReadyInfo { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public string? LastError { get; private set; }
    public int? ExitCode { get; private set; }

    public HostProcess(HostAssignment assignment)
    {
        Assignment = assignment;
        PipeName = PipeNames.For(assignment.Key, _supervisorPid);
    }

    public void Start(string hostExe, int idleTimeoutSeconds, int maxWorkingSetMb,
                      int globalMaxSessions, TimeSpan startTimeout)
    {
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
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(PipeName);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("host did not start");
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => OnExited();
        StartedUtc = DateTime.UtcNow;

        new Thread(StdoutLoop) { IsBackground = true, Name = $"host-out-{Key}" }.Start();
        new Thread(() =>
        {
            string? l;
            while ((l = _proc.StandardError.ReadLine()) is not null) LastError = l;
        }) { IsBackground = true, Name = $"host-err-{Key}" }.Start();

        // Credentials go over stdin once, never to disk, never over the pipe.
        _proc.StandardInput.WriteLine(JsonSerializer.Serialize(new
        {
            Comcntr = Assignment.Install.ComcntrPath,
            Bases = Assignment.Bases,
            IdleTimeoutSeconds = idleTimeoutSeconds,
            MaxWorkingSetMb = maxWorkingSetMb,
            GlobalMaxSessions = globalMaxSessions
        }));
        _proc.StandardInput.Flush();

        // Connect to the pipe the host creates. Connect waits for the server to appear.
        var sw = Stopwatch.StartNew();
        while (State == HostState.Starting && sw.Elapsed < startTimeout)
        {
            var p = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                p.Connect((int)Math.Min(1000, Math.Max(1, (startTimeout - sw.Elapsed).TotalMilliseconds)));
                _pipe = p;
                new Thread(PipeLoop) { IsBackground = true, Name = $"host-pipe-{Key}" }.Start();
                break;
            }
            catch (TimeoutException) { p.Dispose(); }
            catch (IOException) { p.Dispose(); Thread.Sleep(50); }
        }
    }

    public bool WaitReady(TimeSpan timeout) => _ready.Task.Wait(timeout) && State == HostState.Ready;

    /// <summary>
    /// Sends one request and waits for its response. Never throws for host-side failures:
    /// a dead pipe or a silent host becomes a Transport error response (IPC_CONTRACT.md §5).
    /// </summary>
    public async Task<IpcResponse> SendAsync(string op, string? baseName = null, JsonObject? args = null,
                                             int? deadlineMs = null, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var req = new IpcRequest { Id = id, Op = op, Base = baseName, Args = args, DeadlineMs = deadlineMs };
        if (_pipe is not { IsConnected: true } || State != HostState.Ready)
            return IpcResponse.Failure(id, IpcError.Of(Layers.Transport, $"host {Key} is {State}", retryable: true), 0);

        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var sw = Stopwatch.StartNew();
        try
        {
            await Frames.WriteAsync(_pipe, IpcJson.Serialize(req), _writeLock, ct);
            var grace = TimeSpan.FromMilliseconds(req.EffectiveDeadlineMs + 5000);
            using (ct.Register(() => _ = CancelAsync(id)))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(grace, CancellationToken.None));
                if (done == tcs.Task) return await tcs.Task;
            }
            _ = CancelAsync(id);
            return IpcResponse.Failure(id, IpcError.Of(Layers.Transport,
                $"host {Key} did not answer '{op}' within {grace.TotalSeconds:F0}s", retryable: true), sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return IpcResponse.Failure(id, IpcError.Of(Layers.Transport, $"pipe to {Key}: {ex.Message}", retryable: true),
                                       sw.ElapsedMilliseconds);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public IpcResponse Send(string op, string? baseName = null, JsonObject? args = null, int? deadlineMs = null)
        => SendAsync(op, baseName, args, deadlineMs).GetAwaiter().GetResult();

    private async Task CancelAsync(long targetId)
    {
        try
        {
            if (_pipe is not { IsConnected: true }) return;
            var req = new IpcRequest
            {
                Id = Interlocked.Increment(ref _nextId), Op = Ops.Cancel,
                Args = new JsonObject { ["targetId"] = targetId }
            };
            await Frames.WriteAsync(_pipe, IpcJson.Serialize(req), _writeLock);
        }
        catch { /* best effort */ }
    }

    /// <summary>Process-level numbers the supervisor reads without asking the host.</summary>
    public (long WorkingSetMb, long PrivateMb) Memory()
    {
        if (_proc is null || _proc.HasExited) return (0, 0);
        _proc.Refresh();
        return (_proc.WorkingSet64 / 1024 / 1024, _proc.PrivateMemorySize64 / 1024 / 1024);
    }

    /// <summary>
    /// The host has an exit code but has not finished exiting: a thread is stuck in exit code,
    /// such as 1C's ImageMagick crash filter looping on itself (NativeProcess in OneC.Interop).
    /// HasExited already says true for such a process, yet it keeps its memory, a core and its
    /// infobase files open until killed — and Kill still works on it. So "gone" means the
    /// process handle is signalled, never HasExited.
    /// </summary>
    public bool StuckInExit => _proc is not null && IsStuckInExit(_proc);

    public static bool IsStuckInExit(Process p)
    {
        try { return p.HasExited && !p.WaitForExit(0); }
        catch (InvalidOperationException) { return false; }
    }

    public void Kill()
    {
        try { if (_proc is not null && !_proc.WaitForExit(0)) _proc.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Closes stdin (the host's exit signal) and kills it if it lingers.</summary>
    public void Stop(TimeSpan grace)
    {
        if (_proc is null || _proc.WaitForExit(0)) return;
        if (!StuckInExit)
        {
            try { _proc.StandardInput.Close(); } catch { }
            if (_proc.WaitForExit(grace)) return;
        }
        Kill();
        _proc.WaitForExit(5000);
    }

    private void StdoutLoop()
    {
        string? line;
        while ((line = _proc!.StandardOutput.ReadLine()) is not null)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o && o["event"]?.GetValue<string>() == "fatal")
                {
                    LastError = o["error"]?.GetValue<string>();
                    State = HostState.Failed;
                    _ready.TrySetResult(false);
                }
            }
            catch { /* not JSON — diagnostic noise */ }
        }
    }

    private async void PipeLoop()
    {
        try
        {
            while (true)
            {
                var frame = await Frames.ReadAsync(_pipe!);
                if (frame is null) break;
                var node = JsonNode.Parse(frame) as JsonObject;
                if (node is null) continue;

                if (node["event"]?.GetValue<string>() == "ready")
                {
                    ReadyInfo = node;
                    State = HostState.Ready;
                    _ready.TrySetResult(true);
                    continue;
                }
                var resp = node.Deserialize<IpcResponse>(IpcJson.Options);
                if (resp is not null && _pending.TryRemove(resp.Id, out var tcs)) tcs.TrySetResult(resp);
            }
        }
        catch { /* broken pipe — OnExited or the monitor deals with it */ }
        FailPending("pipe closed");
    }

    private void OnExited()
    {
        try { ExitCode = _proc?.ExitCode; } catch { }
        if (State != HostState.Failed) State = HostState.Exited;
        _ready.TrySetResult(false);
        FailPending($"host exited (code {ExitCode})");
    }

    private void FailPending(string why)
    {
        foreach (var (id, tcs) in _pending)
            tcs.TrySetResult(IpcResponse.Failure(id, IpcError.Of(Layers.Transport, $"{Key}: {why}", retryable: true), 0));
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(10));
        try { _pipe?.Dispose(); } catch { }
        _proc?.Dispose();
    }
}
