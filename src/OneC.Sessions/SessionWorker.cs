using System.Collections.Concurrent;
using System.Diagnostics;
using OneC.Interop;

namespace OneC.Sessions;

/// <summary>
/// What a unit of work sees: the live 1C connection plus the error context to stamp on
/// anything that goes wrong. Every COM object reached from <see cref="Connection"/> is
/// created on this worker's thread and must be released there — put it in a ComScope.
/// </summary>
public sealed class SessionContext
{
    public required object Connection { get; init; }
    public required ErrorContext Error { get; init; }
    public required int SessionId { get; init; }
    public required OneCBase Base { get; init; }
}

/// <summary>
/// One 1C session pinned to one dedicated thread. Every call into this session — including
/// the Connect that opens it and the release that closes it — runs on that thread, so a
/// wrong-thread COM release is structurally impossible rather than merely discouraged.
/// </summary>
public sealed class SessionWorker : IDisposable
{
    // Bounded on principle. Only the one caller holding this session from the pool can
    // enqueue, and Execute blocks until its item finishes, so the real depth is 1.
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>(), 8);
    private readonly Thread _thread;
    private readonly object _connector;
    private readonly ManualResetEventSlim _ready = new(false);

    private object? _connection;
    private Exception? _startupError;
    private volatile bool _broken;
    private volatile bool _disposed;

    public int Id { get; }
    public OneCBase Base { get; }
    public long ConnectMs { get; private set; }
    /// <summary>Time spent queued on the machine-wide file-connect lock (file bases only).</summary>
    public long LockWaitMs { get; private set; }
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
    public long OperationCount { get; private set; }
    public string? PlatformVersion { get; private set; }
    public bool IsBroken => _broken;

    private SessionWorker(object connector, OneCBase b, int id)
    {
        _connector = connector;
        Base = b;
        Id = id;
        _thread = new Thread(Pump, 1024 * 1024)
        {
            IsBackground = true,
            Name = $"onec-session-{b.Name}-{id}"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Opens the session. Throws <see cref="OneCException"/> if 1C refuses.</summary>
    public static SessionWorker Open(object connector, OneCBase b, int id)
    {
        var w = new SessionWorker(connector, b, id);
        w._thread.Start();
        w._ready.Wait();
        if (w._startupError is not null)
        {
            w.Dispose();
            throw w._startupError;
        }
        return w;
    }

    private void Pump()
    {
        var ctx = new ErrorContext(Base.Name, null);
        try
        {
            var sw = Stopwatch.StartNew();
            if (Base.IsFile)
            {
                // Old adapter parity (main.os:2177 ФайловаяБазаДоступна): a missing file base is
                // answered from the file system, never by a failing COM Connect — which is also
                // the call path that once crashed the process (DECISIONS D27).
                string? dir = Base.FilePath;
                if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "1Cv8.1CD")))
                    throw OneCException.Host($"1Cv8.1CD not found at: {dir}", ctx, "Connect",
                                             category: ErrorCategory.Path);
            }

            // File-base Connects queue machine-wide (FileConnectLock); server Connects never wait.
            using (Base.IsFile ? FileConnectLock.Acquire() : null)
            {
                LockWaitMs = sw.ElapsedMilliseconds;
                // Vtable, not IDispatch: the connector's IDispatch needs the registered type
                // library (ConnectorApi), and the host must not depend on registry state.
                _connection = ConnectorApi.Connect(_connector, Base.ConnectionString, ctx);
            }
            ConnectMs = sw.ElapsedMilliseconds;
            PlatformVersion = ReadPlatformVersion(_connection, ctx);
        }
        catch (Exception ex)
        {
            _startupError = ex as OneCException
                            ?? OneCException.Host(ex.Message, ctx, "Connect", inner: ex);
        }
        finally { _ready.Set(); }

        if (_startupError is not null) return;

        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* each item captures its own outcome */ }
        }

        // Shutdown runs here, on the creating thread, which is the whole point.
        if (_connection is not null)
        {
            using var r = ComRef.Own(_connection, $"connection[{Base.Name}#{Id}]");
            _connection = null;
        }
    }

    private static string? ReadPlatformVersion(object connection, ErrorContext ctx)
    {
        try
        {
            using var scope = new ComScope();
            var si = scope.Track(Dispatch.Call(connection, "NewObject", ctx, "СистемнаяИнформация"),
                                 "СистемнаяИнформация");
            return Dispatch.GetString(si, "ВерсияПриложения", ctx);
        }
        catch { return null; }
    }

    /// <summary>Runs <paramref name="work"/> on this session's thread and returns its result.</summary>
    public T Execute<T>(Func<SessionContext, T> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is null)
            throw OneCException.Host("session is not connected", new ErrorContext(Base.Name, PlatformVersion), "Execute");

        var ctx = new SessionContext
        {
            Connection = _connection,
            Error = new ErrorContext(Base.Name, PlatformVersion),
            SessionId = Id,
            Base = Base
        };

        using var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? error = null;

        _queue.Add(() =>
        {
            try { result = work(ctx); }
            catch (Exception ex)
            {
                error = ex;
                if (ex is OneCException oe && oe.IsSessionFatal) _broken = true;
                if (ex is System.Runtime.InteropServices.InvalidComObjectException) _broken = true;
            }
            finally { done.Set(); }
        }, ct);

        try { done.Wait(ct); }
        catch (OperationCanceledException)
        {
            // The work item is still queued or running on the session thread. Handing this
            // session back to the pool would let that item fire under the next renter, so
            // the session is retired instead.
            _broken = true;
            throw;
        }
        LastUsedUtc = DateTime.UtcNow;
        OperationCount++;
        // Keeps the session thread's stack; a bare `throw error` would restart it here.
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    public void Execute(Action<SessionContext> work, CancellationToken ct = default)
        => Execute<object?>(c => { work(c); return null; }, ct);

    public void MarkBroken() => _broken = true;

    /// <summary>
    /// Is the 1C session still alive? A metadata read on the session thread — the old adapter's
    /// СоединениеЖиво check (main.os:882). False, and the session is marked broken, if it fails.
    /// </summary>
    public bool Probe()
    {
        if (_broken || _disposed || _connection is null) return false;
        try
        {
            Execute(ctx =>
            {
                using var scope = new ComScope();
                scope.Track(Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
            });
            return true;
        }
        catch (Exception)
        {
            _broken = true;
            return false;
        }
    }

    /// <summary>Test hook: release the connection behind the pool's back, as a dead server would.</summary>
    internal void KillConnectionForTest() =>
        Execute(_ =>
        {
            var c = _connection;
            if (c is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(c);
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(30));
        _queue.Dispose();
        _ready.Dispose();
    }
}
