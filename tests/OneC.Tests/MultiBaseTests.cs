using Xunit;

namespace OneC.Tests;

/// <summary>
/// Milestone 2.2 regressions: several bases inside one host, the shared connector, and the
/// working-set budget under pressure. Each scenario opens its own SessionManager, so each runs in
/// a child process (<see cref="LiveChild"/>, scenarios in <c>tests/OneCLiveChild</c>): these are
/// where 1C corrupted the test process's heap (0xC0000374) and aborted whole runs (2026-10-01 gate).
/// </summary>
[Collection("onec-live")]
public class MultiBaseTests
{
    private readonly LiveFixture _f;
    public MultiBaseTests(LiveFixture f) => _f = f;

    private bool NoBoth => !_f.Available || _f.Server is null || _f.File is null;

    /// <summary>
    /// Releasing the last connector and creating another crashed comcntr with 0xC0000005.
    /// Managers must now share one process-lifetime connector and survive being recreated.
    /// </summary>
    [Fact]
    public void ManagersCanBeRecreatedBecauseTheConnectorIsShared()
    {
        if (!_f.Available) return;
        LiveChild.Run("managers-recreated");
    }

    [Fact]
    public void TwoBasesServeConcurrentlyWithinTheirOwnCaps()
    {
        if (NoBoth) return;
        LiveChild.Run("two-bases-concurrent");
    }

    /// <summary>
    /// Under a tight working-set ceiling a busy base must evict the idle sessions of a quiet
    /// base — and must never evict its own idle session to open another for itself.
    /// </summary>
    [Fact]
    public void PressureEvictsTheQuietBaseNeverTheRequester()
    {
        if (NoBoth) return;
        LiveChild.Run("pressure-evicts");
    }

    [Fact]
    public void AQuietBaseGivesItsSessionsBack()
    {
        if (NoBoth) return;
        LiveChild.Run("quiet-base");
    }
}
