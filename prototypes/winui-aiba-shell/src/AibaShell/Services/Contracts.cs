using System;
using System.Collections.ObjectModel;

namespace AibaShell.Services;

/// <summary>
/// Everything below is a seam, not an implementation. The real system would swap
/// the Mock* classes for adapters over the oscript router, the COM host, the bank
/// connector and the OS process table — the pages would not change.
/// </summary>
public interface ITickable
{
    /// <summary>Called once per simulation tick on the UI thread.</summary>
    void Tick(int tickIndex);
}

public interface IOneCService
{
    ObservableCollection<InfoBase> InfoBases { get; }
    InfoBase? Find(string id);
    void Connect(InfoBase b);
    void Disconnect(InfoBase b);
    void RestartWorker(InfoBase b);
    ObservableCollection<DocumentRow> Documents { get; }
    ObservableCollection<ReportRow> Reports { get; }
}

public interface ISyncService
{
    ObservableCollection<SyncLane> Lanes { get; }
    InfoBase? CurrentBase { get; }
    string CurrentObject { get; }
    double Progress { get; }
    long RowsProcessed { get; }
    double RowsPerSecond { get; }
    TimeSpan Elapsed { get; }
    TimeSpan Eta { get; }
    bool IsRunning { get; }

    event EventHandler? Changed;

    void Start(InfoBase? b = null);
    void Stop();
}

public interface IProcessSupervisor
{
    ObservableCollection<ProcessNode> Processes { get; }
    void Restart(ProcessNode node);
    void Stop(ProcessNode node);
    int RunningCount { get; }
}

public interface ILogService
{
    ObservableCollection<LogEntry> Entries { get; }
    bool IsPaused { get; set; }
    void Clear();
    void Write(LogLevel level, string component, string message);
}

public interface IBankService
{
    ObservableCollection<BankConnection> Banks { get; }
    int WarningCount { get; }
    void Reconnect(BankConnection bank);
}

public interface ISystemMetricsService
{
    double RamUsedMb { get; }
    double RamTotalMb { get; }
    double CpuPercent { get; }
    int ProcessCount { get; }
    TimeSpan Uptime { get; }
    event EventHandler? Changed;
}

public interface ISettingsService
{
    bool StartWithWindows { get; set; }
    bool MinimizeToTray { get; set; }
    string Theme { get; set; }

    int MaxWorkers { get; set; }
    int ColdReadWorkers { get; set; }
    int WorkerMemoryBudgetMb { get; set; }
    string ComVersion { get; set; }
    bool AutoRestartWorkers { get; set; }

    int PollIntervalSeconds { get; set; }
    bool BulkMode { get; set; }
    bool ParallelReads { get; set; }

    string LogLevel { get; set; }
    int LogRetentionDays { get; set; }

    event EventHandler? Changed;
}
