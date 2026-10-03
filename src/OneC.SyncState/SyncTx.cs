using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OneC.SyncState;

/// <summary>
/// One transaction on the store. Obtained only from <see cref="SyncDb.Write{T}"/> /
/// <see cref="SyncDb.Read{T}"/>; the operations are split by table across partial files.
/// </summary>
public sealed partial class SyncTx
{
    private readonly SqliteConnection _conn;
    private readonly SqliteTransaction _tx;
    private readonly SyncDb _db;

    internal SyncTx(SqliteConnection conn, SqliteTransaction tx, SyncDb db)
    {
        _conn = conn;
        _tx = tx;
        _db = db;
    }

    public DateTimeOffset Now => _db.Now;
    private string NowIso => SyncDb.Iso(_db.Now);

    /// <summary>Unprepared, possibly multi-statement (migrations).</summary>
    internal void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Prepared once per transaction and reused: a batch of 5 000 version writes is one statement
    // compiled once, not 5 000 times (measured: StoreBench).
    private readonly Dictionary<string, SqliteCommand> _prepared = new(StringComparer.Ordinal);

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] ps)
    {
        if (!_prepared.TryGetValue(sql, out var cmd))
        {
            cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            foreach (var (n, _) in ps) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
            cmd.Prepare();
            _prepared[sql] = cmd;
        }
        foreach (var (n, v) in ps) cmd.Parameters[n].Value = v ?? DBNull.Value;
        return cmd;
    }

    /// <summary>Called by <see cref="SyncDb"/> when the transaction ends.</summary>
    internal void DisposeCommands()
    {
        foreach (var c in _prepared.Values) c.Dispose();
        _prepared.Clear();
    }

    private int Run(string sql, params (string, object?)[] ps) => Command(sql, ps).ExecuteNonQuery();

    private object? Scalar(string sql, params (string, object?)[] ps)
    {
        var v = Command(sql, ps).ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        var cmd = Command(sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

    // ---------------- meta ----------------

    public string? GetMeta(string key) => Scalar("SELECT value FROM meta WHERE key=$k", ("$k", key)) as string;

    public void SetMeta(string key, string value) =>
        Run("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    public void DeleteMeta(string key) => Run("DELETE FROM meta WHERE key=$k", ("$k", key));
}
