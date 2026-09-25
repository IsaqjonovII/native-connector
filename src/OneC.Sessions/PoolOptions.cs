namespace OneC.Sessions;

/// <summary>
/// Pool shape. Defaults are deliberately lazy: K=4 is the measured ceiling of *useful*
/// concurrency for a base, not a number of sessions to create for every configured base.
/// A client with 30 configured bases and one active must cost one session, not 120.
/// </summary>
public sealed record PoolOptions
{
    /// <summary>Hard ceiling on live sessions across every base in this host process.</summary>
    public int GlobalMaxSessions { get; init; } = 8;

    /// <summary>Upper bound per base; the base's own MaxConcurrency still applies.</summary>
    public int PerBaseMaxSessions { get; init; } = 4;

    /// <summary>Sessions kept warm for a base with no traffic. Zero = fully lazy.</summary>
    public int PerBaseMinWarm { get; init; } = 0;

    /// <summary>An idle session past this age is retired and its memory returned.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often idle sessions are swept.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long Rent waits for a free slot before giving up.</summary>
    public TimeSpan RentTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Recycle a session after this many operations. Zero disables it.</summary>
    public long RecycleAfterOperations { get; init; } = 0;

    /// <summary>
    /// Working-set ceiling for this host process, in MB. A count-only budget is not enough:
    /// a file-base session costs ~210 MB and a server session ~55 MB, so eight sessions is
    /// anywhere between 0.4 GB and 1.7 GB. When a new session would cross this line the
    /// manager retires an idle session first, and refuses if it still cannot fit.
    /// Default suits the supported 16 GB minimum with room for 1C, the UI and Windows.
    /// </summary>
    public int MaxWorkingSetMb { get; init; } = 1200;

    /// <summary>
    /// The connector's own PoolCapacity. Measured and left off: with PoolCapacity=4 a
    /// released session frees nothing (284 MB stayed resident on a server base against
    /// 73 MB with it off), so 1C's pool makes reconnect free but makes idle retirement
    /// impossible. Our warm pool already gives the reuse, and it can also give the memory
    /// back. Set this only for a workload that is provably connect-bound.
    /// </summary>
    public int ConnectorPoolCapacity { get; init; } = 0;

    /// <summary>
    /// An idle session older than this is probed (a metadata read, ~1 ms) before it is handed
    /// out; a dead one is replaced instead of failing the request. The old adapter probed on
    /// every request (main.os:2782); sessions used moments ago are trusted.
    /// </summary>
    public TimeSpan ValidateIdleAfter { get; init; } = TimeSpan.FromSeconds(10);
}
