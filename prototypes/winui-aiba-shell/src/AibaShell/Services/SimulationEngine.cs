using System.Collections.Generic;
using Microsoft.UI.Dispatching;

namespace AibaShell.Services;

/// <summary>
/// Single heartbeat for the whole prototype. Every mock service is ticked from the
/// UI thread, so nothing in the mock layer has to marshal or lock. In the real app
/// this would be replaced by push events arriving from the supervisor and the router.
/// </summary>
public sealed class SimulationEngine
{
    private readonly DispatcherQueueTimer _timer;
    private readonly List<ITickable> _targets = new();
    private int _tick;

    public SimulationEngine(DispatcherQueue queue, IEnumerable<ITickable> targets)
    {
        _targets.AddRange(targets);
        _timer = queue.CreateTimer();
        _timer.Interval = System.TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            _tick++;
            foreach (var t in _targets) t.Tick(_tick);
        };
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
