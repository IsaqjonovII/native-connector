using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AibaShell.Services;

public sealed class MockSyncService : ISyncService, ITickable
{
    private readonly Random _rng = new(99);
    private readonly IOneCService _onec;
    private readonly ILogService _log;

    private static readonly string[] Objects =
    {
        "AccountingRegister.Hozraschetnyy",
        "AccumulationRegister.RoznichnayaVyruchka",
        "Catalog.Nomenklatura",
        "Catalog.Kontragenty",
        "Document.RealizatsiyaTovarovUslug",
        "Document.PostuplenieNaRaschetnyyMinusSchet",
        "InformationRegister.KursyValyut"
    };

    private int _objectIndex;

    public ObservableCollection<SyncLane> Lanes { get; } = new();
    public InfoBase? CurrentBase { get; private set; }
    public string CurrentObject { get; private set; } = Objects[0];
    public double Progress { get; private set; } = 62;
    public long RowsProcessed { get; private set; } = 2_481_390;
    public double RowsPerSecond { get; private set; } = 367;
    public TimeSpan Elapsed { get; private set; } = TimeSpan.FromMinutes(9);
    public TimeSpan Eta { get; private set; } = TimeSpan.FromMinutes(6);
    public bool IsRunning { get; private set; } = true;

    public event EventHandler? Changed;

    public MockSyncService(IOneCService onec, ILogService log)
    {
        _onec = onec;
        _log = log;
        CurrentBase = onec.InfoBases.FirstOrDefault(b => b.State == "Syncing");

        Lanes.Add(new SyncLane { Name = "Worker 1", Slice = "2007 - 2012", State = "reading", Status = StatusKind.Busy, Rows = 612_004, RowsPerSecond = 82, Progress = 71 });
        Lanes.Add(new SyncLane { Name = "Worker 2", Slice = "2012 - 2017", State = "reading", Status = StatusKind.Busy, Rows = 548_220, RowsPerSecond = 74, Progress = 58 });
        Lanes.Add(new SyncLane { Name = "Worker 3", Slice = "2017 - 2022", State = "reading", Status = StatusKind.Busy, Rows = 704_918, RowsPerSecond = 93, Progress = 66 });
        Lanes.Add(new SyncLane { Name = "Worker 4", Slice = "2022 - 2026", State = "reading", Status = StatusKind.Busy, Rows = 616_248, RowsPerSecond = 118, Progress = 54 });
    }

    public void Start(InfoBase? b = null)
    {
        CurrentBase = b ?? CurrentBase ?? _onec.InfoBases.FirstOrDefault();
        IsRunning = true;
        if (CurrentBase is not null)
        {
            CurrentBase.State = "Syncing";
            CurrentBase.Status = StatusKind.Busy;
        }
        foreach (var lane in Lanes)
        {
            lane.State = "reading";
            lane.Status = StatusKind.Busy;
        }
        _log.Write(LogLevel.Info, "Sync", "Sync started on " + (CurrentBase?.Name ?? "unknown") + " (" + Lanes.Count + " lanes)");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        IsRunning = false;
        foreach (var lane in Lanes)
        {
            lane.State = "stopped";
            lane.Status = StatusKind.Idle;
            lane.RowsPerSecond = 0;
        }
        if (CurrentBase is not null)
        {
            CurrentBase.State = "Connected";
            CurrentBase.Status = StatusKind.Healthy;
            CurrentBase.RowsPerSecond = 0;
        }
        _log.Write(LogLevel.Warning, "Sync", "Sync stopped by operator");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Tick(int tick)
    {
        if (!IsRunning) return;

        Elapsed += TimeSpan.FromSeconds(1);
        double total = 0;

        foreach (var lane in Lanes)
        {
            lane.RowsPerSecond = Math.Max(20, lane.RowsPerSecond + (_rng.NextDouble() - 0.5) * 14);
            lane.Rows += (long)lane.RowsPerSecond;
            lane.Progress = Math.Min(100, lane.Progress + lane.RowsPerSecond / 900.0);
            if (lane.Progress >= 100)
            {
                lane.State = "done";
                lane.Status = StatusKind.Healthy;
                lane.RowsPerSecond = 0;
            }
            total += lane.RowsPerSecond;
        }

        RowsPerSecond = total;
        RowsProcessed += (long)total;
        Progress = Math.Min(99.6, Lanes.Average(l => l.Progress));

        var remaining = Math.Max(0, 100 - Progress);
        Eta = TimeSpan.FromSeconds(total <= 1 ? 0 : remaining * 9);

        // Rotate the object under read every ~20 s so the console looks alive.
        if (tick % 20 == 0)
        {
            _objectIndex = (_objectIndex + 1) % Objects.Length;
            CurrentObject = Objects[_objectIndex];
            if (CurrentBase is not null) CurrentBase.CurrentObject = CurrentObject;
            _log.Write(LogLevel.Info, "Sync", CurrentObject + " started");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
