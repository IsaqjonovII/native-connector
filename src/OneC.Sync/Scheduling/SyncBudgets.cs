namespace OneC.Sync.Scheduling;

/// <summary>
/// The sync subsystem's resource budgets (SYNC_ENGINE_ARCHITECTURE §14–15). Defaults are sized for a
/// 16 GB client from the measured numbers (server session ~50 MB, file session ~206 MB). Every
/// figure is a cap from configuration, never from data size.
/// </summary>
public sealed record SyncBudgets
{
    /// <summary>Bases doing sync work at the same time.</summary>
    public int MaxActiveBases { get; init; } = 3;
    /// <summary>A file base's second session costs ~206 MB and competes with the user: sync gets one.</summary>
    public int SyncSessionsPerFileBase { get; init; } = 1;
    /// <summary><c>MaxConcurrency − 1</c> of a server base (4): the user always keeps one.</summary>
    public int SyncSessionsPerServerBase { get; init; } = 3;
    /// <summary><c>GlobalMaxSessions − 2</c> (8): 3 bases × 3 slices ask for 9, this admits 6.</summary>
    public int SyncSessionsGlobal { get; init; } = 6;
    public int UploadConcurrency { get; init; } = 4;
    public int UploadConcurrencyPerBase { get; init; } = 2;
    public long BufferedBytesMax { get; init; } = 64L * 1024 * 1024;
    public int PendingWorkHighWater { get; init; } = 10_000;
    public int PendingWorkLowWater { get; init; } = 8_000;

    /// <summary>A base yields after this long of continuous work while others wait (fairness).</summary>
    public TimeSpan MaxSlice { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>An active base with nothing to do is deactivated after this.</summary>
    public TimeSpan IdleDeactivate { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Feed stat interval for a base with recent log growth…</summary>
    public TimeSpan FeedPollActive { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>…and after <see cref="FeedQuietAfter"/> without growth.</summary>
    public TimeSpan FeedPollIdle { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan FeedQuietAfter { get; init; } = TimeSpan.FromHours(1);
    /// <summary>At most this many log files are stat-ed per scheduler tick.</summary>
    public int MaxStatsPerTick { get; init; } = 16;
    /// <summary>After a user write on a base, no new sync items start for this long.</summary>
    public TimeSpan WriteQuiet { get; init; } = TimeSpan.FromSeconds(5);

    public int SessionsFor(bool isFile) => isFile ? SyncSessionsPerFileBase : SyncSessionsPerServerBase;
}
