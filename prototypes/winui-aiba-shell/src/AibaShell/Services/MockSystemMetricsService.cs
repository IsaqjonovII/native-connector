using System;

namespace AibaShell.Services;

public sealed class MockSystemMetricsService : ISystemMetricsService, ITickable
{
    private readonly Random _rng = new(2026);
    private readonly IProcessSupervisor _supervisor;

    public double RamUsedMb { get; private set; } = 2_640;
    public double RamTotalMb { get; } = 16_384;
    public double CpuPercent { get; private set; } = 18.4;
    public int ProcessCount => _supervisor.Processes.Count;
    public TimeSpan Uptime { get; private set; } = TimeSpan.FromHours(3.4);

    public event EventHandler? Changed;

    public MockSystemMetricsService(IProcessSupervisor supervisor) => _supervisor = supervisor;

    public void Tick(int tick)
    {
        Uptime += TimeSpan.FromSeconds(1);
        RamUsedMb = Math.Clamp(RamUsedMb + (_rng.NextDouble() - 0.48) * 90, 1_200, 12_000);
        CpuPercent = Math.Clamp(CpuPercent + (_rng.NextDouble() - 0.5) * 9, 2, 92);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
