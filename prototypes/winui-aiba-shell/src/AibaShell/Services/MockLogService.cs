using System;
using System.Collections.ObjectModel;

namespace AibaShell.Services;

public sealed class MockLogService : ILogService, ITickable
{
    private const int MaxEntries = 600;
    private readonly Random _rng = new(7);
    private int _seq;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public bool IsPaused { get; set; }

    private static readonly (LogLevel Level, string Component, string Message)[] Chatter =
    {
        (LogLevel.Info,    "1C",      "Connected to signum"),
        (LogLevel.Info,    "Sync",    "AccountingRegister_Hozraschetnyy started"),
        (LogLevel.Debug,   "Worker-3","118 rows/sec"),
        (LogLevel.Info,    "COM",     "Worker connection healthy"),
        (LogLevel.Debug,   "Router",  "lane=read slot 2/4 acquired"),
        (LogLevel.Info,    "Sync",    "keyset page 812 -> 74 ms"),
        (LogLevel.Warning, "COM",     "V83.ComConnector finalizer delayed 1.4 s"),
        (LogLevel.Info,    "Bank",    "Kapitalbank statement pulled: 38 rows"),
        (LogLevel.Debug,   "App",     "heartbeat ok, ws latency 41 ms"),
        (LogLevel.Info,    "Reports", "sverka razvernutoe built in 4.1 s"),
        (LogLevel.Error,   "COM",     "Production-01: CANTLOADLIBRARY comcntr 8.3.25"),
        (LogLevel.Warning, "Bank",    "SQB session expired, re-login required"),
        (LogLevel.Info,    "Router",  "worker oscript-2 queue drained"),
        (LogLevel.Debug,   "Sync",    "hash diff: 1 204 changed rows of 88 210"),
        (LogLevel.Info,    "1C",      "schema_get Document.RealizatsiyaTovarovUslug -> 42 fields"),
        (LogLevel.Warning, "Sync",    "cursor livelock guard tripped, resetting window"),
        (LogLevel.Info,    "App",     "settings saved"),
        (LogLevel.Debug,   "COM",     "ComConnector reused (STA, 1 per process)")
    };

    public MockLogService()
    {
        // Backfill so the viewer is never empty on first paint.
        for (int i = 20; i > 0; i--)
        {
            var c = Chatter[_rng.Next(Chatter.Length)];
            Entries.Add(new LogEntry
            {
                Timestamp = DateTimeOffset.Now.AddSeconds(-i * 3),
                Level = c.Level,
                Component = c.Component,
                Message = c.Message
            });
        }
    }

    public void Clear() => Entries.Clear();

    public void Write(LogLevel level, string component, string message)
    {
        if (IsPaused) return;
        Entries.Add(new LogEntry { Level = level, Component = component, Message = message });
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    public void Tick(int tick)
    {
        if (IsPaused) return;
        // Roughly one line every 1.5 s so the stream reads as live but not noisy.
        if (tick % 3 == 0 || _rng.NextDouble() > 0.65)
        {
            var c = Chatter[_rng.Next(Chatter.Length)];
            _seq++;
            Write(c.Level, c.Component, c.Message);
        }
    }
}
