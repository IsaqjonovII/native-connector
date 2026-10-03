using OneC.EventLog;
using OneC.SyncState;
using OneC.Sync.Scheduling;

namespace OneC.Sync.Incremental;

/// <param name="Backpressured">Stopped because the base has too much pending work; the log keeps the rest.</param>
/// <param name="Reset">The feed cannot be trusted past this point: the base went to Recovery.</param>
public sealed record FeedDrain(int Reads, int Events, int Items, bool Backpressured, bool Reset, string? ResetReason);

/// <summary>
/// The feed side of the transactional outbox (§5, §11): read the log after the stored cursor,
/// coalesce, and commit the work items <em>and</em> the new cursor in one transaction — a crash
/// leaves both or neither, so the cursor never runs ahead of the work it produced. A base with
/// more than the high-water mark of pending items stops reading until it drains below the low
/// mark; nothing is held in memory meanwhile. A reset or a restore never jumps to the tail: the
/// base goes to Recovery with the tail as the cursor to adopt once the verify pass is done.
/// </summary>
public sealed class FeedService(SyncDb db, EventLogReader reader, SyncBudgets budgets)
{
    private readonly HashSet<string> _holding = new(StringComparer.Ordinal);

    public long MaxBytesPerRead { get; init; } = EventLogReader.DefaultMaxBytes;

    /// <summary>Recovery adopts this cursor when its verify pass completes (§11).</summary>
    public static string RecoveryCursorKey(string baseId) => "recovery_cursor:" + baseId;

    /// <summary>
    /// First sync (§11 handshake): the log position is stored <em>before</em> the snapshot starts,
    /// so every change made during the snapshot is in the log after it. Idempotent: an existing
    /// cursor is kept.
    /// </summary>
    public string Handshake(string baseId, string logDir)
    {
        var existing = db.Read(tx => tx.GetCursor(baseId))?.Cursor;
        if (existing is not null) return existing;
        string c = reader.Handshake(logDir).ToString();
        db.Write(tx => tx.SetCursor(baseId, c, logDir));
        return c;
    }

    public FeedDrain Drain(string baseId, string logDir, TableMap map, CancellationToken ct = default)
    {
        int reads = 0, events = 0, items = 0;
        while (!ct.IsCancellationRequested)
        {
            long pending = db.Read(tx => tx.CountWork(baseId));
            lock (_holding)
            {
                if (pending >= budgets.PendingWorkHighWater) _holding.Add(baseId);
                else if (pending <= budgets.PendingWorkLowWater) _holding.Remove(baseId);
                if (_holding.Contains(baseId)) return new FeedDrain(reads, events, items, true, false, null);
            }

            string stored = db.Read(tx => tx.GetCursor(baseId))?.Cursor
                ?? throw new InvalidOperationException($"{baseId}: no feed cursor (handshake first)");
            var batch = reader.Read(logDir, LogCursor.Parse(stored), MaxBytesPerRead);
            reads++;

            if (batch.Reset || batch.Restored)
            {
                string reason = batch.Restored ? "infobase_restored" : batch.ResetReason ?? "reset";
                db.Write(tx =>
                {
                    if (tx.GetBase(baseId) is not null) tx.SetMode(baseId, SyncModes.Recovery, "feed: " + reason);
                    tx.SetMeta(RecoveryCursorKey(baseId), batch.Cursor?.ToString() ?? stored);
                    tx.SetCursor(baseId, stored, logDir, resetReason: reason);          // the old cursor stays until Recovery commits
                });
                return new FeedDrain(reads, events + batch.Events.Count, items, false, true, reason);
            }

            var work = EventCoalescer.Coalesce(baseId, batch.Events, map);
            events += batch.Events.Count;
            items += work.Count;
            string next = batch.Cursor?.ToString() ?? stored;
            DateTimeOffset? lastEvent = batch.Events.Count > 0
                // Log timestamps are the 1C server's local time.
                ? DateTimeOffset.Parse(batch.Events[^1].Ts, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AssumeLocal) : null;
            db.Write(tx =>
            {
                foreach (var w in work) tx.UpsertWork(w);
                tx.SetCursor(baseId, next, logDir, lastEvent);
            });
            if (!batch.More) break;
        }
        return new FeedDrain(reads, events, items, false, false, null);
    }
}
