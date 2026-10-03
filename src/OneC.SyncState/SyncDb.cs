using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace OneC.SyncState;

/// <summary>The file holds a schema newer than this build knows: refuse rather than guess (§13).</summary>
public sealed class SyncDbTooNewException(int found, int known)
    : Exception($"sync.db schema {found} is newer than this build's {known}; refusing to run")
{
    public int Found { get; } = found;
    public int Known { get; } = known;
}

/// <summary>
/// The sync engine's durable state (SYNC_ENGINE_ARCHITECTURE §13): one SQLite file per Supervisor,
/// WAL, one writer. Every change goes through <see cref="Write{T}"/>, which runs one transaction
/// on one connection under a lock — the atomicity rules of §13 (cursor with its work items,
/// completion with its version, one checkpoint per page) are each one call.
///
/// Opening checks integrity first. A file that fails <c>quick_check</c> or is not a database is
/// moved aside and a fresh one is created with <see cref="RecoveryRequired"/> set: the engine then
/// puts every base in Recovery, never "start from the tail" — lost state must not look like a new
/// base (§13 Corruption).
/// </summary>
public sealed class SyncDb : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _conn;
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    public string Path { get; }
    /// <summary>The previous file was corrupt and has been moved to <see cref="QuarantinedTo"/>.</summary>
    public string? QuarantinedTo { get; }

    /// <summary>
    /// Test hook: called at named points inside transactions (e.g. <c>"cursor"</c> between the work
    /// items and the cursor). Throwing from it must leave nothing of that transaction visible.
    /// </summary>
    public Action<string>? FaultHook { get; set; }

    private SyncDb(SqliteConnection conn, string path, string? quarantinedTo, Func<DateTimeOffset> clock)
    {
        _conn = conn;
        Path = path;
        QuarantinedTo = quarantinedTo;
        _clock = clock;
    }

    public DateTimeOffset Now => _clock();

    /// <summary>Opens (creating or migrating) the store at <paramref name="path"/>.</summary>
    public static SyncDb Open(string path, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string? quarantined = null;
        SqliteConnection conn;
        try
        {
            conn = Connect(path);
            if (!QuickCheck(conn)) { conn.Dispose(); quarantined = Quarantine(path); conn = Connect(path); }
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 26 or 11)   // NOTADB, CORRUPT
        {
            SqliteConnection.ClearAllPools();
            quarantined = Quarantine(path);
            conn = Connect(path);
        }

        var db = new SyncDb(conn, path, quarantined, clock ?? (() => DateTimeOffset.UtcNow));
        try
        {
            db.Migrate();
            if (quarantined is not null) db.Write(tx => tx.SetMeta("recovery_required", quarantined));
        }
        catch { db.Dispose(); throw; }
        return db;
    }

    private static SqliteConnection Connect(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            conn.Open();
            // cache_size -32000 = 32 MB, the §15 SQLite budget: with the default 2 MB, inserting
            // 1 M random GUID keys ran at ~6 000 rows/s (every insert a page miss; StoreBench).
            Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=5000; PRAGMA cache_size=-32000;");
            return conn;
        }
        catch { conn.Dispose(); throw; }   // a garbage file fails here; the handle must go before it is moved aside
    }

    private static bool QuickCheck(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetString(0) == "ok" && !r.Read();
    }

    private static string Quarantine(string path)
    {
        string to = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, to);
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(path + ext)) File.Move(path + ext, to + ext);
        return to;
    }

    // ---------------- migrations ----------------

    private static IReadOnlyList<(int Version, string Sql)> Migrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
                  .Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql"))
                  .Select(n =>
                  {
                      string file = n[(n.IndexOf(".Migrations.", StringComparison.Ordinal) + 12)..];
                      int v = int.Parse(file[..file.IndexOf('_')], CultureInfo.InvariantCulture);
                      using var s = new StreamReader(asm.GetManifestResourceStream(n)!);
                      return (v, s.ReadToEnd());
                  })
                  .OrderBy(m => m.v).ToList();
    }

    private void Migrate()
    {
        bool hasMeta = Scalar<long?>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta'") > 0;
        int current = hasMeta ? CurrentSchemaVersion : 0;
        if (current > SchemaVersion) throw new SyncDbTooNewException(current, SchemaVersion);
        foreach (var (version, sql) in Migrations().Where(m => m.Version > current))
        {
            Write(tx =>
            {
                tx.Exec(sql);
                tx.SetMeta("schema_version", version.ToString(CultureInfo.InvariantCulture));
                if (version == 1) tx.SetMeta("created_at", Iso(Now));
            });
        }
    }

    public int CurrentSchemaVersion => (int)(Scalar<long?>("SELECT CAST(value AS INTEGER) FROM meta WHERE key='schema_version'") ?? 0);

    /// <summary>Set when the state was lost (corrupt file); the engine clears it once every base is in Recovery.</summary>
    public string? RecoveryRequired => Read(tx => tx.GetMeta("recovery_required"));

    // ---------------- transactions ----------------

    /// <summary>Runs <paramref name="work"/> in one IMMEDIATE transaction; commits only if it returns.</summary>
    public T Write<T>(Func<SyncTx, T> work)
    {
        lock (_gate)
        {
            using var t = _conn.BeginTransaction(deferred: false);
            var tx = new SyncTx(_conn, t, this);
            try
            {
                T result = work(tx);
                FaultHook?.Invoke("commit");
                t.Commit();
                return result;
            }
            finally { tx.DisposeCommands(); }
        }
    }

    public void Write(Action<SyncTx> work) => Write<object?>(tx => { work(tx); return null; });

    /// <summary>A read on the same connection (consistent with the last commit).</summary>
    public T Read<T>(Func<SyncTx, T> work)
    {
        lock (_gate)
        {
            using var t = _conn.BeginTransaction(deferred: true);
            var tx = new SyncTx(_conn, t, this);
            try { return work(tx); }
            finally { tx.DisposeCommands(); }
        }
    }

    /// <summary>Folds the WAL back into the main file (it otherwise grows until SQLite's auto-checkpoint).</summary>
    public void Checkpoint()
    {
        lock (_gate) Exec(_conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    internal void Fault(string point) => FaultHook?.Invoke(point);

    private T? Scalar<T>(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            object? v = cmd.ExecuteScalar();
            return v is null or DBNull ? default : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
        }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The one timestamp format in the file: UTC, fixed width, so text order is time order.</summary>
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
