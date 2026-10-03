using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Incremental;

/// <summary>What a handler did: state to commit together with the item's completion (§13).</summary>
public sealed record HandlerOutcome(Action<SyncTx>? Commit = null, bool Skipped = false)
{
    public static readonly HandlerOutcome Done = new();
}

/// <summary>Executes one kind of work item. Throws to fail it; the executor classifies (§22).</summary>
public interface IWorkHandler
{
    Task<HandlerOutcome> HandleAsync(string baseId, WorkItem item, CancellationToken ct);
}

public sealed record ExecutorStats(int Done, int Skipped, int Failed, int DeadLettered, int Released);

/// <summary>
/// Runs a base's pending work items (§5): claims up to the base's parallelism (file 1, server 2),
/// one item per object at a time (the row lease is the lock), re-reads 1C inside the handler, and
/// completes the item together with its state in one transaction. Failures follow §22: transient
/// ones back off in the queue (never in memory), a validation or policy refusal becomes a dead
/// letter so the base keeps going, lost auth or a deleted connection stops the run.
/// </summary>
public sealed class WorkExecutor(SyncDb db, IReadOnlyDictionary<string, IWorkHandler> handlers)
{
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxTransientAttempts { get; init; } = 20;
    public Func<int, TimeSpan> Backoff { get; init; } = attempt =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempt - 1, 6))) * (0.8 + Random.Shared.NextDouble() * 0.4));

    public async Task<ExecutorStats> RunAsync(BaseActivation a, int? maxItems = null)
    {
        int parallel = a.IsFile ? 1 : 2, done = 0, skipped = 0, failed = 0, dead = 0, released = 0, total = 0, failedInARow = 0;
        while (!a.Stop.IsCancellationRequested && !a.Leases.ShouldYield(a.BaseId) && (maxItems is null || total < maxItems))
        {
            // Two rounds that only failed: the backend (or 1C) is down. Stop here — every item waits
            // in its queue backoff — instead of holding the base's slot while other bases wait (S15).
            if (failedInARow >= 2) break;
            var claimed = db.Write(tx => tx.ClaimWork(a.BaseId, parallel, Lease));
            if (claimed.Count == 0) break;
            total += claimed.Count;
            var results = await Task.WhenAll(claimed.Select(i => RunOne(a.BaseId, i, a.Stop)));
            foreach (var r in results)
                switch (r)
                {
                    case 'd': done++; break;
                    case 's': skipped++; break;
                    case 'f': failed++; break;
                    case 'x': dead++; break;
                    default: released++; break;
                }
            if (results.Contains('!')) break;                 // auth lost / connection gone: the base pauses
            failedInARow = results.All(r => r == 'f') ? failedInARow + 1 : 0;
        }
        return new ExecutorStats(done, skipped, failed, dead, released);
    }

    /// <summary>d done · s skipped (unchanged) · f failed, retry later · x dead letter · r released · ! stop the base.</summary>
    private async Task<char> RunOne(string baseId, WorkItem item, CancellationToken stop)
    {
        try
        {
            if (!handlers.TryGetValue(item.Kind, out var h))
                return DeadLetter(item, DeadLetterCategories.MissingConfig, $"no handler for {item.Kind}");
            var outcome = await h.HandleAsync(baseId, item, stop);
            db.Write(tx =>
            {
                // A merge while it ran keeps the item for another run; its state still commits —
                // it is true for the version that was read.
                outcome.Commit?.Invoke(tx);
                tx.CompleteWork(item);
            });
            return outcome.Skipped ? 's' : 'd';
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            db.Write(tx => tx.FailWork(item, "stopped", tx.Now));
            return 'r';
        }
        catch (SyncPausedException p) when (p.Outcome is Outcome.Auth or Outcome.Gone)
        {
            db.Write(tx => tx.FailWork(item, p.Message, tx.Now + TimeSpan.FromMinutes(1)));
            return '!';
        }
        catch (SyncPausedException p) when (p.Outcome == Outcome.Policy)
        {
            return DeadLetter(item, DeadLetterCategories.NeedsApproval, p.Message, p.HttpStatus, manualOnly: true);
        }
        catch (SyncPausedException p) when (p.Outcome == Outcome.Validation)
        {
            return DeadLetter(item, DeadLetterCategories.Validation, p.Message, p.HttpStatus, manualOnly: true);
        }
        catch (RefreshRegisterHandler.NotDueException nd)
        {
            db.Write(tx => tx.FailWork(item, "waiting for the table's refresh interval", nd.Due));
            return 'r';
        }
        catch (OneCMetadataMissingException m)
        {
            return DeadLetter(item, DeadLetterCategories.MissingConfig, m.Message, manualOnly: true);
        }
        catch (RowMappingException m)
        {
            return DeadLetter(item, DeadLetterCategories.Validation, m.Message, manualOnly: true);
        }
        catch (Exception e)
        {
            // Transient: network, 5xx after the uploader's own retries, 1C busy, host restart.
            if (item.Attempts >= MaxTransientAttempts)
                return DeadLetter(item, DeadLetterCategories.TransientExhausted, e.Message, retryIn: TimeSpan.FromMinutes(30));
            db.Write(tx => tx.FailWork(item, $"{e.GetType().Name}: {e.Message}", tx.Now + Backoff(item.Attempts)));
            return 'f';
        }
    }

    private char DeadLetter(WorkItem item, string category, string error, int? http = null, bool manualOnly = false, TimeSpan? retryIn = null)
    {
        bool moved = db.Write(tx => tx.DeadLetterWork(item, category, error, http,
                                                      manualOnly ? null : tx.Now + (retryIn ?? TimeSpan.FromMinutes(30))));
        return moved ? 'x' : 'r';                              // refused: a newer intent was merged in, it runs again
    }
}
