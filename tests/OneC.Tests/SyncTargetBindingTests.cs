using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>R10: one backend per base, switched only explicitly, each backend with its own sync state; rollback reuses the old state.</summary>
public sealed class SyncTargetBindingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aiba-binding-").FullName;
    private string File => Path.Combine(_dir, "sync-targets.json");
    private string Db(string n) => Path.Combine(_dir, n + ".db");
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private const string Rust = "rust:http://127.0.0.1:18112|13", Python = "http://127.0.0.1:18041|0123456789abcdef01234567";

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ABaseStaysOnItsBackendUntilAnExplicitSwitchAndRollsBackToItsOwnState()
    {
        Assert.Equal("bound", TargetBinding.Apply(File, "bilim", Rust, Db("rust"), false, T0));
        Assert.Equal("same", TargetBinding.Apply(File, "bilim", Rust, Db("rust"), false, T0.AddHours(1)));

        // Another backend without "switch": refused — never two backends for one base.
        var e = Assert.Throws<TargetBinding.RefusedException>(() => TargetBinding.Apply(File, "bilim", Python, Db("python"), false, T0));
        Assert.Contains("switch", e.Message);
        // A switch must not reuse the other backend's state.
        Assert.Throws<TargetBinding.RefusedException>(() => TargetBinding.Apply(File, "bilim", Python, Db("rust"), true, T0));

        Assert.StartsWith("switched from " + Rust, TargetBinding.Apply(File, "bilim", Python, Db("python"), true, T0.AddHours(2)));
        // Now Rust is the one refused without a switch.
        Assert.Throws<TargetBinding.RefusedException>(() => TargetBinding.Apply(File, "bilim", Rust, Db("rust"), false, T0));
        // Rollback must reuse Rust's own state (its cursor catches up what changed meanwhile).
        Assert.Throws<TargetBinding.RefusedException>(() => TargetBinding.Apply(File, "bilim", Rust, Db("rust-fresh"), true, T0));
        Assert.StartsWith("rolled back from " + Python, TargetBinding.Apply(File, "bilim", Rust, Db("rust"), true, T0.AddHours(3)));
        // Bases are independent.
        Assert.Equal("bound", TargetBinding.Apply(File, "kansler", Python, Db("kan-python"), false, T0));
    }
}
