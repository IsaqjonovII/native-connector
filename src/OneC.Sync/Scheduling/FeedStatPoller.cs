namespace OneC.Sync.Scheduling;

/// <summary>The size and write time of a base's newest log file: the only thing an idle base costs.</summary>
public readonly record struct LogStat(string File, long Length, DateTime WriteTimeUtc);

public interface IFeedStat
{
    /// <summary>The newest <c>.lgp</c> of the log directory, or null when there is none / it cannot be read.</summary>
    LogStat? Stat(string logDir);
}

/// <summary>Real files: one directory listing and one <c>FileInfo</c> per call. No read, no lock.</summary>
public sealed class FileFeedStat : IFeedStat
{
    public LogStat? Stat(string logDir)
    {
        try
        {
            string? newest = null;
            foreach (var f in Directory.EnumerateFiles(logDir, "*.lgp"))
                if (newest is null || string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(newest)) > 0) newest = f;
            if (newest is null) return null;
            var fi = new FileInfo(newest);
            return new LogStat(fi.Name, fi.Length, fi.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// The shared gate in front of every base's feed (§5 rule 1, §14 idle cost): one timer, at most
/// <see cref="SyncBudgets.MaxStatsPerTick"/> stats per tick, round-robin over the bases whose next
/// poll is due. A base whose log grew recently is polled every <see cref="SyncBudgets.FeedPollActive"/>,
/// a quiet one every <see cref="SyncBudgets.FeedPollIdle"/>. Nothing else runs for an idle base.
/// </summary>
public sealed class FeedStatPoller
{
    private sealed class Entry
    {
        public required string BaseId;
        public required string LogDir;
        public LogStat? Last;
        public DateTimeOffset NextPollAt;
        public DateTimeOffset LastGrowthAt;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IFeedStat _stat;
    private readonly SyncBudgets _budgets;
    private int _rr;

    public FeedStatPoller(IFeedStat stat, SyncBudgets budgets)
    {
        _stat = stat;
        _budgets = budgets;
    }

    public long StatCalls { get; private set; }

    /// <summary>Starts watching a base's log. The first stat only records the size (no growth yet).</summary>
    public void Watch(string baseId, string logDir, DateTimeOffset now) =>
        _entries[baseId] = new Entry { BaseId = baseId, LogDir = logDir, NextPollAt = now, LastGrowthAt = DateTimeOffset.MinValue };

    public void Unwatch(string baseId) => _entries.Remove(baseId);

    /// <summary>Polls the due bases; returns those whose newest log file changed since the last stat.</summary>
    public List<string> Tick(DateTimeOffset now)
    {
        var grown = new List<string>();
        if (_entries.Count == 0) return grown;
        var list = _entries.Values.ToList();
        int budget = _budgets.MaxStatsPerTick;
        for (int i = 0; i < list.Count && budget > 0; i++)
        {
            var e = list[(_rr + i) % list.Count];
            if (e.NextPollAt > now) continue;
            budget--;
            StatCalls++;
            var s = _stat.Stat(e.LogDir);
            bool changed = e.Last is { } prev && s is { } cur && (cur.File != prev.File || cur.Length != prev.Length || cur.WriteTimeUtc != prev.WriteTimeUtc);
            if (changed) { e.LastGrowthAt = now; grown.Add(e.BaseId); }
            e.Last = s ?? e.Last;
            var interval = now - e.LastGrowthAt < _budgets.FeedQuietAfter ? _budgets.FeedPollActive : _budgets.FeedPollIdle;
            e.NextPollAt = now + interval;
        }
        _rr = (_rr + 1) % list.Count;
        return grown;
    }
}
