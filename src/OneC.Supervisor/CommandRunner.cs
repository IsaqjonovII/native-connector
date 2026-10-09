using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Ipc;
using OneC.Sync.Targets.Rust;

namespace OneC.Supervisor;

/// <summary>
/// R9 (PYTHON_TO_RUST_MIGRATION_PLAN): normal writes the cloud stored as commands on the Rust module
/// (RUST_SYNC_CONTRACT §9), pulled — the Supervisor opens nothing inbound (D41) — and run through this
/// Supervisor's own OneC.Host, with exactly the host's write operations: create, update, post, unpost,
/// mark for deletion. The host keeps its own guards (AIBA ownership marker, no exchange mode, D18/D38).
/// One command at a time, oldest first, so a create and the post that follows it run in order.
/// <para>Outcome: a host answer, or an error the caller can act on (validation, refusal, a 1C error that
/// is not retryable), is reported as succeeded / failed with 1C's own message. A transport failure, a
/// timeout or a retryable 1C error is NOT reported: the lease runs out and the command is handed out
/// again (every operation here is safe to repeat: create finds its own marker, the rest are states).
/// The change itself reaches the cloud through Sync, never from this result.</para>
/// </summary>
public sealed class CommandRunner(Supervisor sup, RustSyncTarget target, string baseName) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;

    /// <summary>
    /// D53: polling is this phase's transport only. The command model (id, payload, state, result,
    /// idempotency, lease, retry) lives on the backend and does not depend on it: a future push channel
    /// calls <see cref="Wake"/> (lease now instead of after the interval) — or hands a leased command to
    /// <see cref="ExecuteAsync"/> — and nothing else changes.
    /// </summary>
    public void Wake() => _wake.Release();

    public TimeSpan Every { get; init; } = TimeSpan.FromSeconds(2);
    public int LeaseSeconds { get; init; } = 300;
    public int DeadlineMs { get; init; } = 240_000;
    public long Ran => Interlocked.Read(ref _ran);
    private long _ran;

    public void Start() => _loop = Task.Run(() => LoopAsync(_stop.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var leased = await target.LeaseCommandsAsync(1, LeaseSeconds, ct);
                foreach (var c in leased) await ExecuteAsync(c, ct);
                if (leased.Count > 0) continue;                                  // more may be waiting
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { Console.Error.WriteLine($"commands: {e.Message}"); }
            try { await _wake.WaitAsync(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The host operation and its arguments for a command; null = not a command this Connector runs.</summary>
    public static (string Op, JsonObject Args)? Map(string kind, JsonObject p)
    {
        JsonObject Args(params string[] keys)
        {
            var a = new JsonObject();
            foreach (var k in keys)
                if (p[k] is { } v) a[k] = v.DeepClone();
            return a;
        }
        return kind switch
        {
            "document.create" => (Ops.Create, Args("docType", "body", "post")),
            "document.update" => (Ops.Update, Args("docType", "ref", "fields", "post", "autoUnpost", "expected", "expectedVersion")),
            "document.post" => (Ops.Post, Args("docType", "ref")),
            "document.unpost" => (Ops.Unpost, Args("docType", "ref")),
            "document.markDeleted" => (Ops.MarkDeleted, Args("docType", "ref")),
            "catalog.create" => (Ops.CatalogCreate, Args("catalog", "body")),
            "catalog.update" => (Ops.CatalogUpdate, Args("catalog", "ref", "expected", "set")),
            _ => null
        };
    }

    /// <summary>Repeating later can succeed (the lease will hand it out again) — not an answer to report.</summary>
    public static bool Retryable(IpcError e) =>
        e.Retryable || e.HostFatal || e.Layer is Layers.Transport or Layers.Busy or Layers.Timeout or Layers.Cancelled;

    /// <summary>Runs one leased command through the host and reports its outcome — whatever delivered it.</summary>
    public async Task ExecuteAsync(RustSyncTarget.LeasedCommand c, CancellationToken ct)
    {
        Interlocked.Increment(ref _ran);
        if (Map(c.Kind, c.Payload) is not { } call)
        {
            await target.ReportCommandAsync(c, false, null,
                new JsonObject { ["layer"] = Layers.Validation, ["message"] = $"this Connector does not run {c.Kind}" }, ct);
            return;
        }
        var resp = await sup.SendAsync(baseName, call.Op, call.Args, DeadlineMs, ct);
        if (!resp.Ok && Retryable(resp.Error!))
        {
            Console.Error.WriteLine($"command {c.CommandId} {c.Kind} attempt {c.Attempt}: {resp.Error!.Layer} {resp.Error.Message} — left for a retry");
            return;
        }
        var error = resp.Ok ? null : JsonSerializer.SerializeToNode(resp.Error, IpcJson.Options);
        var r = await target.ReportCommandAsync(c, resp.Ok, resp.Result, error, ct);
        if (!r.Ok) Console.Error.WriteLine($"command {c.CommandId}: report {r.Outcome} {r.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
        _wake.Dispose();
    }
}
