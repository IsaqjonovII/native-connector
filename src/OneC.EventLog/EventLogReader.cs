using System.Collections.Concurrent;
using System.Text;
using System.Text.Unicode;

namespace OneC.EventLog;

/// <summary>A position in one base's event log. <c>LgfGuid</c> pins it to one log instance.</summary>
public sealed record LogCursor(string LgfGuid, string File, long Offset)
{
    /// <summary>
    /// The file name of <see cref="StartOf"/>: "from the first log file, whenever it appears". Sync
    /// S0 Q6: a base with no .lgp yet has no tail to take, and a first sync must not lose the
    /// events written after its handshake.
    /// </summary>
    public const string FirstFile = "*";

    public static LogCursor StartOf(string lgfGuid) => new(lgfGuid, FirstFile, 0);

    public bool IsStart => File == FirstFile;

    public override string ToString() => $"{LgfGuid}|{File}|{Offset}";

    public static LogCursor Parse(string s)
    {
        var p = s.Split('|');
        if (p.Length != 3 || !Guid.TryParse(p[0], out _) || !long.TryParse(p[2], out long off) || off < 0)
            throw new ArgumentException($"'{s}' is not an event-log cursor");
        if (p[1] == FirstFile && off == 0) return StartOf(p[0]);
        if (!p[1].EndsWith(".lgp", StringComparison.OrdinalIgnoreCase) || p[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{s}' is not an event-log cursor");
        return new LogCursor(p[0], p[1], off);
    }
}

/// <summary>A data change: <c>Kind</c> New/Update/Post/Unpost/Delete, <c>Ref</c> the object's GUID when it has one.</summary>
public sealed record ChangeEvent(string Ts, string Kind, string Metadata, string? Ref);

/// <param name="Reset">The log was recreated, truncated or rotated past the cursor: events may
/// be missing, the caller must resync by other means. <paramref name="Cursor"/> is valid again.</param>
/// <param name="More">Bytes remain after <paramref name="Cursor"/> — call again.</param>
public sealed record ChangeBatch(List<ChangeEvent> Events, LogCursor? Cursor, bool Reset, string? ResetReason, bool More, int Records)
{
    /// <summary>
    /// The infobase was restored from a backup inside this range (<c>_$InfoBase$_.RestoreFinish</c>):
    /// the log goes on, but the data jumped back — events before it no longer describe 1C (sync §11).
    /// </summary>
    public bool Restored { get; init; }
}

/// <summary>
/// Pull reader over one base's <c>1Cv8Log</c>: give it the last cursor, get the data changes
/// after it and the next cursor. Stateless apart from a dictionary cache; the caller owns the
/// cursor (the connector's push watcher lost events whenever its emit had no listener).
///
/// Rules carried over from the connector's reader: a first read starts at the tail (history
/// belongs to the scheduled sync); rolled-back records (<c>R</c>) are skipped; a changed
/// instance GUID, a vanished cursor file or a file shorter than the cursor is a reset; reads
/// are capped per call (a week offline must not pull hundreds of MB in one go); a record
/// larger than the cap is skipped with a reset rather than stalling forever.
/// </summary>
public sealed class EventLogReader
{
    public const long DefaultMaxBytes = 8 * 1024 * 1024;

    /// <summary>Records dropped because an id stayed unknown after the dictionary re-read (a metric; should stay 0).</summary>
    public long UnknownIds => Interlocked.Read(ref _unknownIds);
    private long _unknownIds;

    private sealed record DictEntry(long Length, long ConsumedBytes, LogDictionary Dict);
    private readonly ConcurrentDictionary<string, DictEntry> _dicts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="includeRolledBack">Diagnostics only: keep data records of rolled-back transactions.</param>
    public ChangeBatch Read(string logDir, LogCursor? cursor, long maxBytes = DefaultMaxBytes, bool includeRolledBack = false)
    {
        // Transactions seen rolled back in this read. Their Begin marker (letter R) is written
        // before their data records, so a rollback read in one piece is filtered; one split
        // across two reads shows up as a change that did not persist — a harmless extra sync,
        // never a missed one.
        var rolledBack = new HashSet<string>(StringComparer.Ordinal);
        var dict = Dictionary(logDir);
        var files = Directory.EnumerateFiles(logDir, "*.lgp").Select(Path.GetFileName).OfType<string>()
                             .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return new ChangeBatch(new(), cursor, false, null, false, 0);

        string newest = files[^1];
        LogCursor Tail() => new(dict.InstanceGuid, newest, new FileInfo(Path.Combine(logDir, newest)).Length);

        if (cursor is null) return new ChangeBatch(new(), Tail(), false, null, false, 0);
        if (!string.Equals(cursor.LgfGuid, dict.InstanceGuid, StringComparison.OrdinalIgnoreCase))
            return new ChangeBatch(new(), Tail(), true, "log_recreated", false, 0);
        if (cursor.IsStart) cursor = new LogCursor(cursor.LgfGuid, files[0], 0);
        int start = files.FindIndex(f => string.Equals(f, cursor.File, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return new ChangeBatch(new(), Tail(), true, "cursor_file_gone", false, 0);

        var events = new List<ChangeEvent>();
        var pos = cursor;
        long budget = maxBytes;
        int records = 0;
        bool restored = false, refreshed = false;
        for (int idx = start; idx < files.Count; idx++)
        {
            string file = files[idx];
            long offset = idx == start ? cursor.Offset : 0;
            string path = Path.Combine(logDir, file);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (offset > length)
                return new ChangeBatch(events, new LogCursor(dict.InstanceGuid, file, length), true, "log_truncated", false, records) { Restored = restored };
            if (offset == length) { pos = new LogCursor(dict.InstanceGuid, file, offset); continue; }
            if (budget <= 0) return new ChangeBatch(events, pos, false, null, true, records) { Restored = restored };

            int toRead = (int)Math.Min(budget, length - offset);
            var buf = new byte[toRead];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = fs.ReadAtLeast(buf, toRead, throwOnEndOfStream: false);
            budget -= read;
            string text = ValidPrefix(buf.AsSpan(0, read));
            var (recs, consumed) = LogFormat.ParseRecords(text);
            records += recs.Count;
            foreach (var r in recs)
            {
                if (r.TxStatus == "R" && r.TxId is not null) rolledBack.Add(r.TxId);
                // 1C writes a new id to 1Cv8.lgf around the record that first uses it: an id this
                // dictionary does not know yet gets one re-read before the record is judged (§11).
                if ((!dict.Events.ContainsKey(r.EventId) || (r.MetadataId != 0 && !dict.Metadata.ContainsKey(r.MetadataId))) && !refreshed)
                {
                    dict = Dictionary(logDir);
                    refreshed = true;
                }
                if (!dict.Events.TryGetValue(r.EventId, out var name)) { Interlocked.Increment(ref _unknownIds); continue; }
                if (name == "_$InfoBase$_.RestoreFinish") restored = true;
                if (LogFormat.DataEventKind(name) is not { } kind) continue;
                if (!includeRolledBack && r.TxId is not null && rolledBack.Contains(r.TxId)) continue;
                if (!dict.Metadata.TryGetValue(r.MetadataId, out var meta)) { Interlocked.Increment(ref _unknownIds); continue; }
                if (meta.Length == 0) continue;
                events.Add(new ChangeEvent(r.Ts, kind, meta, LogFormat.HexToUuid(r.RefHex)));
            }
            long consumedBytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed));
            bool hitCap = offset + read < length;
            if (consumedBytes == 0 && hitCap)
                // One record bigger than the whole read: skip it rather than stall forever.
                return new ChangeBatch(events, new LogCursor(dict.InstanceGuid, file, offset + read), true, "record_over_cap", true, records) { Restored = restored };
            pos = new LogCursor(dict.InstanceGuid, file, offset + consumedBytes);
            if (hitCap) return new ChangeBatch(events, pos, false, null, true, records) { Restored = restored };
        }
        return new ChangeBatch(events, pos, false, null, false, records) { Restored = restored };
    }

    /// <summary>
    /// The first-sync handshake position (§11): the tail, or — when the log has no .lgp file yet —
    /// the start of whichever file comes first, so nothing written after the handshake is lost.
    /// </summary>
    public LogCursor Handshake(string logDir)
    {
        var dict = Dictionary(logDir);
        return Read(logDir, null).Cursor ?? LogCursor.StartOf(dict.InstanceGuid);
    }

    /// <summary>The longest prefix that is complete, valid UTF-8 (a record cut mid-character is re-read next time).</summary>
    private static string ValidPrefix(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        Utf8.ToUtf16(bytes, chars, out _, out int written, replaceInvalidSequences: false, isFinalBlock: false);
        return new string(chars, 0, written);
    }

    /// <summary>
    /// 1Cv8.lgf, parsed incrementally: it only ever grows, until the log is recreated — which
    /// the header's instance GUID shows even when the new file has already outgrown the old.
    /// A cached dictionary is never mutated (readers may hold it): growth parses into a copy.
    /// </summary>
    private LogDictionary Dictionary(string logDir)
    {
        string path = Path.Combine(logDir, "1Cv8.lgf");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        var entry = _dicts.GetValueOrDefault(path);
        if (entry is not null && HeaderGuid(fs) != entry.Dict.InstanceGuid) entry = null;
        if (entry is not null && entry.Length == length) return entry.Dict;

        LogDictionary dict;
        long from;
        if (entry is null || length < entry.Length) { dict = new LogDictionary(); from = 0; }
        else { dict = entry.Dict.Copy(); from = entry.ConsumedBytes; }

        var buf = new byte[length - from];
        fs.Seek(from, SeekOrigin.Begin);
        int read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        string text = ValidPrefix(buf.AsSpan(0, read));
        int consumed = LogFormat.ParseDictionary(text, dict);
        _dicts[path] = new DictEntry(length, from + Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed)), dict);
        return dict;
    }

    private static string HeaderGuid(FileStream fs)
    {
        var head = new byte[Math.Min(256, fs.Length)];
        fs.Seek(0, SeekOrigin.Begin);
        int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var probe = new LogDictionary();
        LogFormat.ParseDictionary(ValidPrefix(head.AsSpan(0, n)), probe);
        return probe.InstanceGuid;
    }
}
