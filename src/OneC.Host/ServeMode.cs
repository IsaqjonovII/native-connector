using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>Startup config, sent by the supervisor as the first stdin line — never a file.</summary>
public sealed record ServeConfig
{
    public required string Comcntr { get; init; }
    public required List<OneCBase> Bases { get; init; }
    public int GlobalMaxSessions { get; init; } = 8;
    public int MaxWorkingSetMb { get; init; } = 1200;
    public int IdleTimeoutSeconds { get; init; } = 300;
    public string WritePrefix { get; init; } = "AIBA_";
}

/// <summary>
/// Long-running host (IPC_CONTRACT.md §1): <c>OneC.Host serve --pipe &lt;name&gt;</c>.
///   stdin  line 1   config JSON with credentials — never on disk, never on the pipe
///   stdin  EOF      the supervisor is gone → the host exits (no orphans)
///   pipe            every request and response
///   stdout          one diagnostic line: ready or fatal (for humans and startup logs)
/// </summary>
public static class ServeMode
{
    private static readonly JsonSerializerOptions Out = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Run(string[] argv)
    {
        string? pipe = null;
        for (int i = 0; i < argv.Length - 1; i++) if (argv[i] == "--pipe") pipe = argv[i + 1];
        if (pipe is null) return Fatal("serve needs --pipe <name>", 4);

        // The config carries Cyrillic user names; the console default is the OEM code page.
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        string? first = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(first)) return Fatal("no config on stdin", 4);

        ServeConfig cfg;
        try { cfg = JsonSerializer.Deserialize<ServeConfig>(first) ?? throw new JsonException("null config"); }
        catch (Exception ex) { return Fatal("bad config: " + ex.Message, 4); }

        SessionManager m;
        try
        {
            m = new SessionManager(cfg.Comcntr, new PoolOptions
            {
                GlobalMaxSessions = cfg.GlobalMaxSessions,
                MaxWorkingSetMb = cfg.MaxWorkingSetMb,
                IdleTimeout = TimeSpan.FromSeconds(cfg.IdleTimeoutSeconds)
            });
            foreach (var b in cfg.Bases) m.Register(b);
        }
        catch (Exception ex) { return Fatal(ex.Message, 5, hostFatal: true); }

        var ready = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["pipe"] = pipe,
            ["comcntr"] = m.ComcntrPath,
            ["comcntrVersion"] = PlatformCatalog.FileVersionOf(m.ComcntrPath),
            ["comcntrModulesMapped"] = ComActivator.LoadedComcntrModules().Count,
            ["bases"] = new JsonArray(cfg.Bases.Select(b => (JsonNode?)b.Name).ToArray())
        };

        using var stop = new CancellationTokenSource();
        // Lifetime: stdin EOF means the supervisor let go or died.
        new Thread(() =>
        {
            try { while (Console.In.ReadLine() is not null) { } } catch { }
            stop.Cancel();
        }) { IsBackground = true, Name = "stdin-lifetime" }.Start();

        var server = new PipeServer(pipe, new Operations(m, cfg.WritePrefix), ready,
                                    maxInFlight: Math.Max(4, cfg.GlobalMaxSessions * 4));
        try
        {
            var run = server.RunAsync(stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", pid = Environment.ProcessId, pipe }, Out));
            Console.Out.Flush();
            run.GetAwaiter().GetResult();
        }
        catch (Exception ex) { m.Dispose(); return Fatal("pipe server: " + ex.Message, 6); }

        m.Dispose();
        return 0;
    }

    private static int Fatal(string error, int code, bool hostFatal = false)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "fatal", error, hostFatal }, Out));
        Console.Out.Flush();
        return code;
    }
}
