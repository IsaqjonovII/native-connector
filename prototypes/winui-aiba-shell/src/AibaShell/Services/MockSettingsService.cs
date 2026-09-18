using System;

namespace AibaShell.Services;

/// <summary>
/// In-memory only. The real implementation would sit on top of a settings store
/// (registry / %LOCALAPPDATA% JSON) and raise the same Changed event.
/// </summary>
public sealed class MockSettingsService : ISettingsService
{
    public event EventHandler? Changed;

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private bool _startWithWindows = true;
    public bool StartWithWindows { get => _startWithWindows; set { _startWithWindows = value; Notify(); } }

    private bool _minimizeToTray = true;
    public bool MinimizeToTray { get => _minimizeToTray; set { _minimizeToTray = value; Notify(); } }

    private string _theme = "System";
    public string Theme { get => _theme; set { _theme = value; Notify(); } }

    private int _maxWorkers = 4;
    public int MaxWorkers { get => _maxWorkers; set { _maxWorkers = value; Notify(); } }

    private int _coldReadWorkers = 4;
    public int ColdReadWorkers { get => _coldReadWorkers; set { _coldReadWorkers = value; Notify(); } }

    private int _workerMemoryBudgetMb = 768;
    public int WorkerMemoryBudgetMb { get => _workerMemoryBudgetMb; set { _workerMemoryBudgetMb = value; Notify(); } }

    private string _comVersion = "8.3.24";
    public string ComVersion { get => _comVersion; set { _comVersion = value; Notify(); } }

    private bool _autoRestartWorkers = true;
    public bool AutoRestartWorkers { get => _autoRestartWorkers; set { _autoRestartWorkers = value; Notify(); } }

    private int _pollIntervalSeconds = 60;
    public int PollIntervalSeconds { get => _pollIntervalSeconds; set { _pollIntervalSeconds = value; Notify(); } }

    private bool _bulkMode = true;
    public bool BulkMode { get => _bulkMode; set { _bulkMode = value; Notify(); } }

    private bool _parallelReads = true;
    public bool ParallelReads { get => _parallelReads; set { _parallelReads = value; Notify(); } }

    private string _logLevel = "Info";
    public string LogLevel { get => _logLevel; set { _logLevel = value; Notify(); } }

    private int _logRetentionDays = 14;
    public int LogRetentionDays { get => _logRetentionDays; set { _logRetentionDays = value; Notify(); } }
}
