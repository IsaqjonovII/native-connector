using System.Globalization;
using System.Threading.Channels;
using OneC.Sync.Abstractions;
using OneC.SyncState;
using OneC.Sync.Mapping;
using OneC.Sync.Scheduling;
using OneC.Sync.Source;
using OneC.Sync.Upload;

namespace OneC.Sync.Snapshot;

public sealed record SnapshotOptions
{
    public int PageSize { get; init; } = 500;
    /// <summary>Documents and registers above this many rows are read in period slices, on server bases only (§4).</summary>
    public long SliceThresholdRows { get; init; } = 200_000;
    /// <summary>Upload batch caps (§15), lowered further by the target's own.</summary>
    public long MaxBatchBytes { get; init; } = 8L * 1024 * 1024;
    public int MaxBatchRows { get; init; } = 2_000;
}

public enum SnapshotOutcome { Complete, Stopped, NoSession }

public sealed record SnapshotResult(SnapshotOutcome Outcome, long RowsRead, long RowsAccepted, int Slices);

/// <summary>
/// The cold-read pipeline for one table (§4): planner → bounded readers (one sync session lease
/// each) → <c>Channel&lt;page&gt;</c> (2 per reader) → mapper → batches per partition → upload →
/// per-slice checkpoint after the target accepted a page. Memory is bounded by the channel and the
/// shared byte budget; a full channel blocks the readers (and their 1C sessions) — the intended
/// backpressure. Stop requests (fairness, foreground work) end reading at a page boundary; pages
/// already read are still uploaded and checkpointed, so a stop loses nothing.
/// </summary>
public sealed class SnapshotRunner(SyncDb db, IOneCReader reader, Uploader uploader, UploadGate gate,
                                   TargetCapabilities caps, Incremental.IPartitioner partitions, SnapshotOptions? options = null)
{
    private readonly SnapshotOptions _o = options ?? new SnapshotOptions();

    private sealed record RawPage(int SliceNo, int Seq, PagePosition After, bool Last, List<System.Text.Json.Nodes.JsonObject> Rows);
    /// <param name="NotStored">After upload: keys the target did not take (rejected, dead-lettered, or no
    /// binding for their organisation) — they keep no version or hash, so a later pass sends them.</param>
    private sealed record ReadPage(int SliceNo, int Seq, PagePosition After, bool Last, List<MappedRow> Rows, long Bytes,
                                   IReadOnlySet<string>? NotStored = null);

    private sealed class SliceProgress
    {
        public int Issued;
        public int NextToCommit;
        public readonly Dictionary<int, ReadPage> Done = new();
    }

    public async Task<SnapshotResult> RunTableAsync(BaseActivation a, TablePlan t, long runId)
    {
        var slices = await PlanAsync(a, t, runId);
        var pending = new Queue<SnapshotSlice>(slices.Where(s => !s.Done));
        if (pending.Count == 0) return new SnapshotResult(SnapshotOutcome.Complete, 0, 0, slices.Count);

        var leases = new List<SyncLeases.Lease>();
        int want = Math.Min(pending.Count, a.Budgets.SessionsFor(a.IsFile));
        for (int i = 0; i < want && a.Leases.TryAcquire(a.BaseId, a.IsFile) is { } l; i++) leases.Add(l);
        if (leases.Count == 0) return new SnapshotResult(SnapshotOutcome.NoSession, 0, 0, slices.Count);

        var progress = slices.ToDictionary(s => s.SliceNo, _ => new SliceProgress());
        // Readers only read (their 1C session never waits on JSON work); mappers turn pages into
        // canonical rows; uploaders send them. Both channels are bounded: 2 pages per reader each.
        var raw = Channel.CreateBounded<RawPage>(new BoundedChannelOptions(2 * leases.Count) { SingleWriter = false, SingleReader = false });
        var channel = Channel.CreateBounded<ReadPage>(new BoundedChannelOptions(2 * leases.Count) { SingleWriter = false, SingleReader = false });
        using var fail = new CancellationTokenSource();
        long rowsRead = 0, rowsAccepted = 0;

        // Any stage that fails stops every other stage at once: a reader blocked on a full channel, a
        // mapper or an uploader waiting on an empty one would otherwise wait forever (2026-10-01
        // review: one failed mapper left the readers, their leases and the base's slot stuck).
        async Task Stage(Func<Task> body)
        {
            try { await body(); }
            catch { fail.Cancel(); throw; }
        }

        async Task ReadLoop()
        {
            while (true)
            {
                SnapshotSlice? slice;
                lock (pending) slice = pending.Count > 0 ? pending.Dequeue() : null;
                if (slice is null) return;
                var pos = PagePosition.Decode(slice.Cursor);
                var sp = progress[slice.SliceNo];
                while (true)
                {
                    if (a.Stop.IsCancellationRequested || a.Leases.ShouldYield(a.BaseId) || fail.IsCancellationRequested) return;
                    var page = await ReadAsync(a.BaseId, t, slice, pos, fail.Token);
                    Interlocked.Add(ref rowsRead, page.Rows.Count);
                    pos = PagePosition.Next(t.Family, pos, page);
                    bool last = !page.HasMore;
                    int seq;
                    lock (sp) seq = sp.Issued++;
                    await raw.Writer.WriteAsync(new RawPage(slice.SliceNo, seq, pos, last, page.Rows), fail.Token);
                    if (last) break;
                }
            }
        }

        async Task MapLoop()
        {
            await foreach (var p in raw.Reader.ReadAllAsync(fail.Token))
            {
                var mapped = p.Rows.Select(r => CanonicalMapper.Map(t, r)).ToList();
                long bytes = mapped.Sum(m => (long)m.Bytes);
                await gate.Bytes.AcquireAsync(bytes, fail.Token);
                try { await channel.Writer.WriteAsync(new ReadPage(p.SliceNo, p.Seq, p.After, p.Last, mapped, bytes), fail.Token); }
                catch { gate.Bytes.Release(bytes); throw; }
            }
        }

        async Task UploadLoop()
        {
            await foreach (var p in channel.Reader.ReadAllAsync(fail.Token))
            {
                try
                {
                    var notStored = await UploadPageAsync(a.BaseId, t, p, fail.Token);
                    Interlocked.Add(ref rowsAccepted, p.Rows.Count - notStored.Count);
                    Commit(a.BaseId, t, runId, progress[p.SliceNo], p with { NotStored = notStored });
                }
                finally { gate.Bytes.Release(p.Bytes); }
            }
        }

        var readers = leases.Select(_ => Task.Run(() => Stage(ReadLoop))).ToList();
        var mappers = Enumerable.Range(0, Math.Max(1, leases.Count / 2)).Select(_ => Task.Run(() => Stage(MapLoop))).ToList();
        var uploaders = Enumerable.Range(0, a.Budgets.UploadConcurrencyPerBase).Select(_ => Task.Run(() => Stage(UploadLoop))).ToList();
        var all = readers.Concat(mappers).Concat(uploaders).ToList();
        try
        {
            var readersDone = Task.WhenAll(readers);
            var downstream = Task.WhenAll(mappers.Concat(uploaders));
            var first = await Task.WhenAny(readersDone, downstream);
            if (first != readersDone || readersDone.IsFaulted) fail.Cancel();       // mapping or upload failed: stop reading too
            try { await readersDone; } finally { raw.Writer.TryComplete(); }
            try { await Task.WhenAll(mappers); } finally { channel.Writer.TryComplete(); }
            await Task.WhenAll(uploaders);
        }
        catch
        {
            fail.Cancel();
            raw.Writer.TryComplete();
            channel.Writer.TryComplete();
            await Task.WhenAll(all.Select(x => x.ContinueWith(_ => { })));
            // Pages mapped but never uploaded give their bytes back.
            while (channel.Reader.TryRead(out var left)) gate.Bytes.Release(left.Bytes);
            var errors = all.Where(x => x.IsFaulted).SelectMany(x => x.Exception!.InnerExceptions).ToList();
            if (errors.FirstOrDefault(e => e is not OperationCanceledException) is { } real)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(real);
            throw;
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }

        bool complete = db.Read(tx => tx.GetSlices(a.BaseId, t.Table, runId)).All(s => s.Done);
        return new SnapshotResult(complete ? SnapshotOutcome.Complete : SnapshotOutcome.Stopped, rowsRead, rowsAccepted, slices.Count);
    }

    /// <summary>Existing slices of the run (resume), or a new plan persisted before reading starts.</summary>
    private async Task<List<SnapshotSlice>> PlanAsync(BaseActivation a, TablePlan t, long runId)
    {
        var existing = db.Read(tx => tx.GetSlices(a.BaseId, t.Table, runId));
        if (existing.Count > 0) return existing;

        var plan = new List<SnapshotSlice>();
        bool sliceable = !a.IsFile && (t.Family == Families.Document || Families.IsRecorded(t.Family));
        if (sliceable && await reader.CountAsync(a.BaseId, t, a.Stop) > _o.SliceThresholdRows)
        {
            var parts = await reader.SlicesAsync(a.BaseId, t, a.Budgets.SessionsFor(false), a.Stop);
            for (int i = 0; i < parts.Count; i++)
                plan.Add(new SnapshotSlice(a.BaseId, t.Table, runId, i, Iso(parts[i].From), Iso(parts[i].To), null, null, 0, false));
        }
        if (plan.Count == 0) plan.Add(new SnapshotSlice(a.BaseId, t.Table, runId, 0, Iso(t.From), null, null, null, 0, false));
        else if (t.From is not null && plan[0].From is null) plan[0] = plan[0] with { From = Iso(t.From) };
        db.Write(tx => { foreach (var s in plan) tx.UpsertSlice(s); });
        return plan;
    }

    private Task<SourcePage> ReadAsync(string baseId, TablePlan t, SnapshotSlice s, PagePosition pos, CancellationToken ct)
    {
        int n = _o.PageSize;
        DateTime? from = Date(s.From), to = Date(s.To);
        return t.Family switch
        {
            Families.Catalog => reader.CatalogPageAsync(baseId, t.Name, pos.After, n, ct),
            Families.Chart => reader.ChartPageAsync(baseId, t.Name, pos.Offset, n, ct),
            Families.Document => reader.DocumentPageAsync(baseId, t.Name, pos.Date ?? s.From, pos.Skip, n, from, to, ct),
            Families.IndependentInfoRegister => reader.RegisterPageAsync(baseId, t, null, 0, pos.Key, n, null, ct),
            _ => reader.RegisterPageAsync(baseId, t, pos.Date ?? s.From, pos.Skip, null, n, to, ct)
        };
    }

    /// <summary>
    /// One page's rows as batches per partition: keys unique within a batch (last wins), rows and bytes
    /// capped. Returns the keys the target did not store.
    /// </summary>
    private async Task<HashSet<string>> UploadPageAsync(string baseId, TablePlan t, ReadPage p, CancellationToken ct)
    {
        long maxBytes = Math.Min(_o.MaxBatchBytes, caps.MaxBatchBytes);
        int maxRows = Math.Min(_o.MaxBatchRows, caps.MaxBatchRows);
        var notStored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in p.Rows.GroupBy(r => partitions.PartitionOf(t, r) ?? ""))
        {
            if (group.Key.Length == 0)
            {
                // No binding for these rows' organisation yet (§21): counted, not sent; a verify
                // pass sends them once the binding appears.
                db.Write(tx => { foreach (var o in group.GroupBy(r => r.OrgRef ?? "")) tx.CountUnmappedOrg(baseId, o.Key, t.Table, o.Count()); });
                foreach (var r in group) notStored.Add(r.Key);
                continue;
            }
            var unique = new Dictionary<string, MappedRow>(StringComparer.Ordinal);
            foreach (var r in group) unique[r.Key] = r;
            var batch = new List<SyncRow>();
            long bytes = 0;
            foreach (var r in unique.Values)
            {
                if (batch.Count > 0 && (batch.Count >= maxRows || bytes + r.Bytes > maxBytes))
                {
                    await SendAsync(baseId, group.Key, t, batch, notStored, ct);
                    batch = new List<SyncRow>();
                    bytes = 0;
                }
                batch.Add(r.Row);
                bytes += r.Bytes;
            }
            if (batch.Count > 0) await SendAsync(baseId, group.Key, t, batch, notStored, ct);
        }
        return notStored;
    }

    /// <summary>
    /// A target with per-row results (v3) names the rows it refuses; one without (backend/1c v2) refuses
    /// the whole batch with a 400. Then the batch is halved until the refused row is alone: it becomes
    /// a dead letter and every other row is stored — one bad row never stalls a first copy
    /// (2026-10-01 review: the page was retried every activation, forever).
    /// </summary>
    private async Task SendAsync(string baseId, string partition, TablePlan t, List<SyncRow> rows, HashSet<string> notStored, CancellationToken ct)
    {
        UploadResult r;
        try { r = await uploader.UploadAsync(baseId, new UploadBatch(Guid.NewGuid().ToString("N"), partition, t.Table, rows), ct); }
        catch (SyncPausedException e) when (e.Outcome == Outcome.Validation)
        {
            if (rows.Count > 1)
            {
                int half = rows.Count / 2;
                await SendAsync(baseId, partition, t, rows.GetRange(0, half), notStored, ct);
                await SendAsync(baseId, partition, t, rows.GetRange(half, rows.Count - half), notStored, ct);
                return;
            }
            r = new UploadResult(Outcome.Ok, 0, Array.Empty<string>(), new[] { new RowRejection(rows[0].Key, e.Message, false) });
        }
        if (r.Rejected.Count > 0)
            db.Write(tx =>
            {
                foreach (var rej in r.Rejected)
                {
                    notStored.Add(rej.Key);
                    tx.AddDeadLetter(new DeadLetter(0, baseId, "snapshot", t.Table, rej.Key, DeadLetterCategories.Validation, rej.Reason,
                                                    400, 1, tx.Now, tx.Now, null, partition));
                }
            });
    }

    /// <summary>Advances the slice's checkpoint over every consecutive accepted page; versions are stored with it.</summary>
    private void Commit(string baseId, TablePlan t, long runId, SliceProgress sp, ReadPage p)
    {
        lock (sp)
        {
            sp.Done[p.Seq] = p;
            while (sp.Done.Remove(sp.NextToCommit, out var c))
            {
                db.Write(tx =>
                {
                    tx.CheckpointSlice(baseId, t.Table, runId, c.SliceNo, c.After.Encode(), null, c.Rows.Count, c.Last);
                    // Only rows that were sent: an unbound organisation's rows keep no version, so the
                    // verify pass after its binding sees them as new and sends them.
                    // Rows not stored (refused, or no binding yet) keep neither: the next verify / refresh sends them.
                    bool Stored(MappedRow r) => c.NotStored?.Contains(r.Key) != true;
                    if (t.Family is Families.Catalog or Families.Document)
                        foreach (var r in c.Rows.Where(Stored)) tx.SetVersion(baseId, t.Table, r.Key, r.DataVersion);
                    // What each item shows elsewhere, so a later rename is recognised (SyncObjectHandler).
                    if (t.Family == Families.Catalog)
                        foreach (var r in c.Rows.Where(r => Stored(r) && r.Shown is not null))
                            tx.SetMeta(CanonicalMapper.ShownKey(baseId, t.Table, r.Key), r.Shown!);
                    else if (t.Family == Families.IndependentInfoRegister)
                        foreach (var r in c.Rows.Where(Stored)) tx.SetRegisterRow(baseId, t.Table, r.Key, Incremental.RefreshRegisterHandler.Hash(r.Row), 0);
                    // Several partitions: where each object / recorder went, so a later delete or
                    // reconcile asks exactly those partitions (single-partition bases need no history).
                    if (partitions.All.Count > 1 && (t.Family == Families.Document || Families.IsRecorded(t.Family)))
                        // a recorded register's key is recorder#line; history is kept per recorder
                        foreach (var (key, part) in c.Rows.Select(r => (Key: Families.IsRecorded(t.Family) && r.Key.IndexOf('#') is var h and > 0 ? r.Key[..h]
                                                                              : Families.IsRecorded(t.Family) ? null : r.Key,
                                                                         Part: partitions.PartitionOf(t, r)))
                                                          .Where(x => x.Key is not null && x.Part is not null).Distinct())
                            tx.AddPartition(baseId, key!, part!);
                });
                sp.NextToCommit++;
            }
        }
    }

    private static string? Iso(DateTime? d) => d?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    private static DateTime? Date(string? s) => s is null ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
