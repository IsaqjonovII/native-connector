using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OneC.Ipc;

namespace OneC.Host;

/// <summary>
/// The host end of the supervisor pipe (IPC_CONTRACT.md §1). One connection, many concurrent
/// requests multiplexed by id. Per-user ACL, first-instance only, so nothing else on the
/// machine can read the pipe or squat its name.
///
/// Backpressure (§6): at most <see cref="MaxInFlight"/> requests at once; the next one is
/// answered immediately with a Busy error instead of being queued.
/// </summary>
public sealed class PipeServer
{
    private readonly string _name;
    private readonly Operations _ops;
    private readonly JsonObject _readyInfo;
    private readonly SemaphoreSlim _inFlight;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public int MaxInFlight { get; }

    public PipeServer(string name, Operations ops, JsonObject readyInfo, int maxInFlight)
    {
        _name = name;
        _ops = ops;
        _readyInfo = readyInfo;
        MaxInFlight = maxInFlight;
        _inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
    }

    private NamedPipeServerStream Create()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                                             PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1 << 16, 1 << 16, sec);
    }

    /// <summary>Serves until <paramref name="stop"/> fires. Accepts a new connection after each disconnect.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        await using var pipe = Create();
        while (!stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { break; }

            var writeLock = new SemaphoreSlim(1, 1);
            var ready = new JsonObject { ["v"] = 1, ["event"] = "ready" };
            foreach (var (k, v) in _readyInfo) ready[k] = v?.DeepClone();
            await Frames.WriteAsync(pipe, System.Text.Encoding.UTF8.GetBytes(ready.ToJsonString()), writeLock, stop);

            var tasks = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await Frames.ReadAsync(pipe, stop);
                    if (frame is null) break;                       // supervisor disconnected
                    try
                    {
                        await HandleFrame(pipe, writeLock, frame, tasks, stop);
                    }
                    catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
                    {
                        // One bad frame must never end the connection — that would take down
                        // every in-flight request and look like a host crash to the supervisor.
                        Console.Error.WriteLine($"frame handling failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (IOException) { /* broken pipe: fall through to reconnect */ }
            catch (OperationCanceledException) { }

            foreach (var c in _running.Values) c.Cancel();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(_ => { });
            if (pipe.IsConnected) pipe.Disconnect();
        }
    }

    private async Task HandleFrame(Stream pipe, SemaphoreSlim writeLock, byte[] frame, List<Task> tasks,
                                   CancellationToken stop)
    {
        IpcRequest req;
        try { req = IpcJson.Deserialize<IpcRequest>(frame); }
        catch (Exception ex)
        {
            await Send(pipe, writeLock, IpcResponse.Failure(0, IpcError.Of(Layers.Validation, "bad request: " + ex.Message), 0), stop);
            return;
        }

        if (req.Op == Ops.Cancel)
        {
            long target = req.Args?["targetId"]?.GetValue<long>() ?? -1;
            bool hit = _running.TryGetValue(target, out var cts);
            // The request may finish between the lookup and the Cancel; its source is never
            // disposed (no timer, nothing to free), so a late Cancel is a harmless no-op.
            if (hit) cts!.Cancel();
            await Send(pipe, writeLock, IpcResponse.Success(req.Id, IpcJson.ToNode(new { cancelled = hit }), 0), stop);
            return;
        }

        if (!_inFlight.Wait(0))
        {
            await Send(pipe, writeLock, IpcResponse.Failure(req.Id, IpcError.Of(Layers.Busy,
                $"host has {MaxInFlight} requests in flight", retryable: true), 0), stop);
            return;
        }

        // Registered before the work starts, so a cancel that arrives immediately still hits.
        var source = new CancellationTokenSource();
        _running[req.Id] = source;
        tasks.RemoveAll(t => t.IsCompleted);
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                var resp = _ops.Execute(req, source.Token);
                await Send(pipe, writeLock, resp, CancellationToken.None);
            }
            catch (IOException) { /* pipe went away; nothing to answer */ }
            catch (ObjectDisposedException) { /* connection torn down while answering */ }
            finally
            {
                _running.TryRemove(req.Id, out _);
                _inFlight.Release();
            }
        }, CancellationToken.None));
    }

    private static Task Send(Stream s, SemaphoreSlim writeLock, IpcResponse r, CancellationToken ct) =>
        Frames.WriteAsync(s, IpcJson.Serialize(r), writeLock, ct);
}
