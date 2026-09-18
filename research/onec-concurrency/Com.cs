using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OneC.Research;

/// <summary>
/// Every 1C COM object is released on the thread that created it. Leaving them to the GC
/// finalizer kills the process with 0xC0000005 after a few dozen calls — proven A/B in
/// ../com-threading-probe. Nothing in this harness may rely on the finalizer.
/// </summary>
public static class Com
{
    public static void Rel(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        catch { }
    }

    public static object NewConnector()
    {
        var t = Type.GetTypeFromProgID("V83.COMConnector")
                ?? throw new InvalidOperationException("V83.COMConnector is not registered");
        return Activator.CreateInstance(t)!;
    }
}

public sealed class BaseCfg
{
    public string Name { get; init; } = "";
    public string ConnectionString { get; init; } = "";
    public bool IsFile => ConnectionString.StartsWith("File=", StringComparison.OrdinalIgnoreCase);
    public string Kind => IsFile ? "FILE" : "SERVER";

    public static BaseCfg Load(string path)
    {
        var r = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return new BaseCfg
        {
            Name = r.GetProperty("baseName").GetString()!,
            ConnectionString = r.GetProperty("connectionString").GetString()!
        };
    }
}

/// <summary>One live 1C COM session. Disposing releases it deterministically.</summary>
public sealed class Session : IDisposable
{
    public dynamic Handle { get; private set; }
    public int Id { get; }
    public long ConnectMs { get; }
    public bool Broken { get; private set; }

    private Session(dynamic handle, int id, long connectMs)
    {
        Handle = handle; Id = id; ConnectMs = connectMs;
    }

    public static Session Open(object connector, string connString, int id)
    {
        var sw = Stopwatch.StartNew();
        dynamic h = ((dynamic)connector).Connect(connString);
        return new Session(h, id, sw.ElapsedMilliseconds);
    }

    public void MarkBroken() => Broken = true;

    public void Dispose()
    {
        if (Handle != null) { Com.Rel(Handle); Handle = null; }
    }
}

/// <summary>
/// Fixed-size pool of warm sessions over ONE connector. Rent blocks until one is free.
/// A session reported broken is disposed and replaced on next rent, without touching
/// the connector or the other sessions — that independence is the thing being tested.
/// </summary>
public sealed class SessionPool : IDisposable
{
    private readonly object _connector;
    private readonly string _cs;
    private readonly SemaphoreSlim _slots;
    private readonly Stack<Session> _free = new();
    private readonly object _lock = new();
    private int _nextId;

    public int Size { get; }
    public int Recreated { get; private set; }

    public SessionPool(object connector, string cs, int size, bool prewarm = true)
    {
        _connector = connector; _cs = cs; Size = size;
        _slots = new SemaphoreSlim(size, size);
        if (prewarm)
            for (int i = 0; i < size; i++)
                _free.Push(Session.Open(connector, cs, _nextId++));
    }

    public Session Rent()
    {
        _slots.Wait();
        lock (_lock)
        {
            while (_free.Count > 0)
            {
                var s = _free.Pop();
                if (!s.Broken) return s;
                s.Dispose();
                Recreated++;
            }
        }
        return Session.Open(_connector, _cs, Interlocked.Increment(ref _nextId));
    }

    public void Return(Session s)
    {
        lock (_lock)
        {
            if (s.Broken) { s.Dispose(); Recreated++; }
            else _free.Push(s);
        }
        _slots.Release();
    }

    public void Dispose()
    {
        lock (_lock) { while (_free.Count > 0) _free.Pop().Dispose(); }
        _slots.Dispose();
    }
}
