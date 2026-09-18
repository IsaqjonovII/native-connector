using System;

namespace AibaShell.Services;

/// <summary>
/// Coarse health buckets. Every list/table cell in the prototype colours itself
/// from this, so a new state only needs one entry here plus one brush.
/// </summary>
public enum StatusKind
{
    Idle,
    Busy,
    Healthy,
    Warning,
    Error
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>A 1C information base ("infobaza") as the shell sees it.</summary>
public sealed class InfoBase : ObservableObjectBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Name { get; set; } = "";

    /// <summary>File / Server / 1uz(SQL) — mirrors adapterDbType in the current app.</summary>
    public string Kind { get; set; } = "Server";

    public string OneCVersion { get; set; } = "8.3.24.1548";
    public string ComVersion { get; set; } = "8.3.24";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Organization { get; set; } = "";

    private string _state = "Idle";
    public string State { get => _state; set => Set(ref _state, value); }

    private StatusKind _status = StatusKind.Idle;
    public StatusKind Status { get => _status; set => Set(ref _status, value); }

    private string _comState = "Ready";
    public string ComState { get => _comState; set => Set(ref _comState, value); }

    private StatusKind _comStatus = StatusKind.Healthy;
    public StatusKind ComStatus { get => _comStatus; set => Set(ref _comStatus, value); }

    private string _worker = "none";
    public string Worker { get => _worker; set => Set(ref _worker, value); }

    private int _workerPid;
    public int WorkerPid { get => _workerPid; set { Set(ref _workerPid, value); Raise(nameof(WorkerPidText)); } }
    public string WorkerPidText => WorkerPid == 0 ? "none" : WorkerPid.ToString();

    private int _workerPort = 55899;
    public int WorkerPort { get => _workerPort; set => Set(ref _workerPort, value); }

    private double _workerMemoryMb = 180;
    public double WorkerMemoryMb { get => _workerMemoryMb; set { Set(ref _workerMemoryMb, value); Raise(nameof(WorkerMemoryText)); } }
    public string WorkerMemoryText => WorkerMemoryMb.ToString("N0") + " MB";

    private TimeSpan _workerUptime = TimeSpan.FromMinutes(12);
    public TimeSpan WorkerUptime { get => _workerUptime; set { Set(ref _workerUptime, value); Raise(nameof(WorkerUptimeText)); } }
    public string WorkerUptimeText => Format.Duration(WorkerUptime);

    private int _requestsServed;
    public int RequestsServed { get => _requestsServed; set => Set(ref _requestsServed, value); }

    private int _queueDepth;
    public int QueueDepth { get => _queueDepth; set => Set(ref _queueDepth, value); }

    private double _rowsPerSecond;
    public double RowsPerSecond { get => _rowsPerSecond; set { Set(ref _rowsPerSecond, value); Raise(nameof(RowsPerSecondText)); } }
    public string RowsPerSecondText => RowsPerSecond <= 0 ? "-" : RowsPerSecond.ToString("N0") + "/s";

    private long _rowsRead;
    public long RowsRead { get => _rowsRead; set { Set(ref _rowsRead, value); Raise(nameof(RowsReadText)); } }
    public string RowsReadText => RowsRead.ToString("N0");

    private double _progress;
    public double Progress { get => _progress; set { Set(ref _progress, value); Raise(nameof(ProgressText)); } }
    public string ProgressText => Progress.ToString("N0") + "%";

    private string _currentObject = "idle";
    public string CurrentObject { get => _currentObject; set => Set(ref _currentObject, value); }

    private DateTimeOffset? _lastSync = DateTimeOffset.Now.AddMinutes(-4);
    public DateTimeOffset? LastSync { get => _lastSync; set { Set(ref _lastSync, value); Raise(nameof(LastSyncText)); } }
    public string LastSyncText => LastSync is null ? "never" : Format.Ago(LastSync.Value);

    private string _problem = "";
    /// <summary>Terminal, operator-actionable error (COM version mismatch and friends).</summary>
    public string Problem { get => _problem; set { Set(ref _problem, value); Raise(nameof(HasProblem)); } }
    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public string Location => Kind == "File" ? Database : Server + "\\" + Database;
}

/// <summary>One parallel read lane of the current sync — a date slice handed to a worker.</summary>
public sealed class SyncLane : ObservableObjectBase
{
    public string Name { get; set; } = "";
    public string Slice { get; set; } = "";

    private string _state = "idle";
    public string State { get => _state; set => Set(ref _state, value); }

    private StatusKind _status = StatusKind.Idle;
    public StatusKind Status { get => _status; set => Set(ref _status, value); }

    private long _rows;
    public long Rows { get => _rows; set { Set(ref _rows, value); Raise(nameof(RowsText)); } }
    public string RowsText => Rows.ToString("N0") + " rows";

    private double _rowsPerSecond;
    public double RowsPerSecond { get => _rowsPerSecond; set { Set(ref _rowsPerSecond, value); Raise(nameof(RowsPerSecondText)); } }
    public string RowsPerSecondText => RowsPerSecond.ToString("N0") + " rows/s";

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
}

/// <summary>A supervised child process. Children are the oscript workers under the router.</summary>
public sealed class ProcessNode : ObservableObjectBase
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Depth { get; set; }
    public double IndentWidth => Depth * 22;
    public bool ShowBranch => Depth > 0;

    private int _pid;
    public int Pid { get => _pid; set { Set(ref _pid, value); Raise(nameof(PidText)); } }
    public string PidText => Pid == 0 ? "-" : Pid.ToString();

    private string _state = "running";
    public string State { get => _state; set => Set(ref _state, value); }

    private StatusKind _status = StatusKind.Healthy;
    public StatusKind Status { get => _status; set => Set(ref _status, value); }

    private TimeSpan _uptime;
    public TimeSpan Uptime { get => _uptime; set { Set(ref _uptime, value); Raise(nameof(UptimeText)); } }
    public string UptimeText => Format.Duration(Uptime);

    private double _memoryMb;
    public double MemoryMb { get => _memoryMb; set { Set(ref _memoryMb, value); Raise(nameof(MemoryText)); } }
    public string MemoryText => MemoryMb.ToString("N0") + " MB";

    private double _cpu;
    public double Cpu { get => _cpu; set { Set(ref _cpu, value); Raise(nameof(CpuText)); } }
    public string CpuText => Cpu.ToString("N1") + " %";

    public int Port { get; set; }
    public string PortText => Port == 0 ? "-" : Port.ToString();

    private int _restarts;
    public int Restarts { get => _restarts; set => Set(ref _restarts, value); }
}

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public LogLevel Level { get; set; }
    public string Component { get; set; } = "App";
    public string Message { get; set; } = "";

    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
    public string ComponentText => "[" + Component + "]";
}

public sealed class BankConnection : ObservableObjectBase
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int Accounts { get; set; }
    public string AccountsText => Accounts + " account(s)";

    private string _state = "Connected";
    public string State { get => _state; set => Set(ref _state, value); }

    private StatusKind _status = StatusKind.Healthy;
    public StatusKind Status { get => _status; set => Set(ref _status, value); }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private DateTimeOffset _lastSync = DateTimeOffset.Now.AddMinutes(-6);
    public DateTimeOffset LastSync { get => _lastSync; set { Set(ref _lastSync, value); Raise(nameof(LastSyncText)); } }
    public string LastSyncText => Format.Ago(LastSync);
}

public sealed class ActivityItem
{
    public string Glyph { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public StatusKind Status { get; set; } = StatusKind.Idle;
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string AtText => Format.Ago(At);
}

public sealed class DocumentRow
{
    public string Number { get; set; } = "";
    public string Type { get; set; } = "";
    public string Base { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string Amount { get; set; } = "";
    public string State { get; set; } = "";
    public StatusKind Status { get; set; }
    public string Marker { get; set; } = "";
}

public sealed class ReportRow
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Base { get; set; } = "";
    public string Period { get; set; } = "";
    public string Built { get; set; } = "";
    public string State { get; set; } = "";
    public StatusKind Status { get; set; }
}

public static class Format
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
        if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m " + t.Seconds + "s";
        return (int)t.TotalSeconds + "s";
    }

    public static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d.TotalSeconds < 60) return (int)Math.Max(0, d.TotalSeconds) + " sec ago";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
        if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
        return at.ToString("dd MMM HH:mm");
    }
}
