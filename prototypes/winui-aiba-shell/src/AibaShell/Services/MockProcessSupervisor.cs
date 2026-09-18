using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AibaShell.Services;

/// <summary>
/// Flattened process tree. Depth drives the indent so the list stays a plain
/// virtualised ListView instead of a TreeView with per-node templates.
/// </summary>
public sealed class MockProcessSupervisor : IProcessSupervisor, ITickable
{
    private readonly Random _rng = new(5150);
    private readonly ILogService _log;

    public ObservableCollection<ProcessNode> Processes { get; } = new();

    public int RunningCount => Processes.Count(p => p.State == "running");

    public MockProcessSupervisor(ILogService log)
    {
        _log = log;

        Add("AIBA Connector", "shell", 0, 14002, 0, 322, 1.8, TimeSpan.FromHours(3.4), 0);
        Add("1C Adapter Router", "router", 1, 18100, 55890, 96, 0.6, TimeSpan.FromHours(3.4), 0);
        Add("oscript Worker 1", "oscript", 2, 18244, 55899, 412, 14.2, TimeSpan.FromMinutes(94), 1);
        Add("oscript Worker 2", "oscript", 2, 18310, 55901, 268, 9.4, TimeSpan.FromHours(7), 0);
        Add("oscript Worker 3", "oscript", 2, 18422, 55902, 96, 22.7, TimeSpan.FromSeconds(6), 3);
        Add("oscript Worker 4", "oscript", 2, 18510, 55903, 74, 0.2, TimeSpan.FromMinutes(3), 2);
        Add("Reports Worker", "reports", 1, 18740, 8013, 188, 3.1, TimeSpan.FromHours(3.4), 0);
        Add("1UZ Adapter", "1uz", 1, 19004, 55900, 540, 31.5, TimeSpan.FromMinutes(22), 0);
        Add("Bank Connector", "bank", 1, 19210, 8181, 214, 2.2, TimeSpan.FromHours(1.2), 1);
        Add("SQB Automation", "browser", 1, 19488, 0, 386, 6.8, TimeSpan.FromMinutes(41), 0);

        // Worker 4 is the one wedged behind the COM version mismatch.
        var wedged = Processes.First(p => p.Pid == 18510);
        wedged.State = "degraded";
        wedged.Status = StatusKind.Error;

        var sqb = Processes.First(p => p.Pid == 19488);
        sqb.State = "needs login";
        sqb.Status = StatusKind.Warning;
    }

    private void Add(string name, string kind, int depth, int pid, int port, double mem, double cpu, TimeSpan up, int restarts)
    {
        Processes.Add(new ProcessNode
        {
            Name = name,
            Kind = kind,
            Depth = depth,
            Pid = pid,
            Port = port,
            MemoryMb = mem,
            Cpu = cpu,
            Uptime = up,
            Restarts = restarts,
            State = "running",
            Status = StatusKind.Healthy
        });
    }

    public void Restart(ProcessNode node)
    {
        node.Pid = _rng.Next(14000, 19999);
        node.Uptime = TimeSpan.Zero;
        node.MemoryMb = 48;
        node.Cpu = 0.4;
        node.Restarts++;
        node.State = "running";
        node.Status = StatusKind.Healthy;
        _log.Write(LogLevel.Warning, "Router", node.Name + " restarted (pid " + node.Pid + ", restart #" + node.Restarts + ")");
    }

    public void Stop(ProcessNode node)
    {
        node.Pid = 0;
        node.MemoryMb = 0;
        node.Cpu = 0;
        node.Uptime = TimeSpan.Zero;
        node.State = "stopped";
        node.Status = StatusKind.Idle;
        _log.Write(LogLevel.Warning, "Router", node.Name + " stopped by operator");
    }

    public void Tick(int tick)
    {
        foreach (var p in Processes)
        {
            if (p.State == "stopped") continue;
            p.Uptime += TimeSpan.FromSeconds(1);
            p.MemoryMb = Math.Max(32, p.MemoryMb + (_rng.NextDouble() - 0.47) * 7);
            p.Cpu = Math.Clamp(p.Cpu + (_rng.NextDouble() - 0.5) * 4, 0.1, 96);
        }
    }
}
