using System.Diagnostics;
using System.Text.Json;
using OneC.Desktop.Services;
using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>Milestone 5.1 — connection lifecycle, ported from the old adapter.</summary>
public class ConnectionLifecycleUnitTests
{
    /// <summary>Parity with main.os:1951 КлассифицироватьОшибку1С, using real 1C texts where we have them.</summary>
    [Theory]
    [InlineData("User identification failed Incorrect user name or password", "auth")]
    [InlineData("Идентификация пользователя не выполнена", "auth")]
    [InlineData("Infobase not found Database file is missing 'D:\\1C\\no-such-base-here/1Cv8.1CD'", "path")]
    [InlineData("Не обнаружен файл базы данных", "path")]
    [InlineData("Не обнаружена лицензия. Не найден ключ защиты программы", "license")]
    [InlineData("Несовместимая версия", "version")]
    [InlineData("Недостаточно прав для входа", "permission")]
    [InlineData("Error performing operation with infobase server_addr=no-such-host-xyz descr=11001: No such host is known.", "network")]
    [InlineData("Кластер серверов недоступен", "network")]
    [InlineData("something else entirely", "unknown")]
    [InlineData("", "unknown")]
    public void ErrorCategoriesMatchTheOldAdapter(string message, string code)
        => Assert.Equal(code, ErrorCategory.Classify(message));

    [Theory]
    [InlineData("File=\"D:\\1C\\bilim\";Usr=\"u\";Pwd=\"p\";", "D:\\1C\\bilim")]
    [InlineData("File=\"D:\\A \"\"quoted\"\" dir\";Usr=\"u\";", "D:\\A \"quoted\" dir")]
    [InlineData("file=D:\\bare;Usr=u;", "D:\\bare")]
    [InlineData("Srvr=\"s\";Ref=\"r\";", null)]
    public void FilePathIsParsedFromTheConnectionString(string cs, string? expected)
        => Assert.Equal(expected, new OneCBase { Name = "b", ConnectionString = cs }.FilePath);

    [Fact]
    public void FileConnectLockIsExclusiveAndTimesOut()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aiba-lock-test-{Guid.NewGuid():N}.lock");
        try
        {
            using var held = FileConnectLock.Acquire(path, TimeSpan.FromSeconds(1));
            Assert.NotNull(held);

            var sw = Stopwatch.StartNew();
            var second = FileConnectLock.Acquire(path, TimeSpan.FromMilliseconds(700));
            Assert.Null(second);                                        // gave up, did not deadlock
            Assert.InRange(sw.ElapsedMilliseconds, 650, 3000);

            held!.Dispose();
            using var third = FileConnectLock.Acquire(path, TimeSpan.FromSeconds(1));
            Assert.NotNull(third);                                      // released → available
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void OldConnectorConfigImportsComBasesAndSkipsHttp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aiba-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string old = Path.Combine(dir, "config.json");
            File.WriteAllText(old, JsonSerializer.Serialize(new
            {
                server = new { host = "127.0.0.1", port = 55899 },
                databases = new Dictionary<string, object>
                {
                    // Distinctive password: a short one can appear in random DPAPI base64 by chance.
                    ["KAN"] = new { type = "server", server = "WIN-11-2070", database = "KAN", user = "Эркин", password = "PLAINTEXT-SECRET-KAN", comVersion = "8.3.15.1565" },
                    ["bilim"] = new { type = "file", path = "D:\\1C\\bilim", user = "u", password = "p2" },
                    ["legacy"] = new { type = "file", server = "srv", database = "LEG", user = "u", password = "p3" },
                    ["cloud1"] = new { type = "http", url = "https://x" }
                }
            }));
            string before = File.ReadAllText(old);

            var store = new BaseStore(Path.Combine(dir, "bases.json"));
            var report = store.ImportOldConnectorConfig(old);

            Assert.Equal(3, report.Imported);
            Assert.Single(report.Skipped);
            Assert.Contains("cloud1", report.Skipped[0]);
            Assert.Equal(before, File.ReadAllText(old));                  // source untouched

            var kan = store.Bases.Single(b => b.Name == "KAN");
            Assert.Equal((BaseKind.Server, "WIN-11-2070", "KAN", "8.3.15.1565"), (kan.Kind, kan.Server, kan.Ref, kan.PlatformVersion));
            Assert.Equal("PLAINTEXT-SECRET-KAN", BaseStore.Unprotect(kan.PasswordProtected));
            Assert.Equal(BaseKind.File, store.Bases.Single(b => b.Name == "bilim").Kind);
            Assert.Equal(BaseKind.Server, store.Bases.Single(b => b.Name == "legacy").Kind);   // old rule
            Assert.DoesNotContain("PLAINTEXT-SECRET", File.ReadAllText(store.FilePath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

[Collection("onec-live")]
public class ConnectionLifecycleLiveTests
{
    private static readonly ReadQuery Q = new()
    { Entity = "Справочник.Номенклатура", Fields = new[] { "Наименование" }, Limit = 1 };

    private readonly LiveFixture _f;
    public ConnectionLifecycleLiveTests(LiveFixture f) => _f = f;

    [Fact]
    public void MissingFileBaseFailsFromTheFileSystemWithoutTouchingCom()
    {
        if (!_f.Available) return;
        string name = "missing-" + Guid.NewGuid().ToString("N")[..6];
        _f.Manager!.Register(new OneCBase { Name = name, ConnectionString = "File=\"D:\\1C\\definitely-not-here\";" });

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<OneCException>(() => new ReadService(_f.Manager).Read(name, Q));
        Assert.Equal(ErrorCategory.Path, ex.Category);
        Assert.Equal(OneCLayer.Host, ex.Layer);
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms — did it call COM?");
    }

    [Fact]
    public void ThreeFailedConnectsOpenTheBreakerAndTheFourthFailsFast()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        string name = "breaker-" + Guid.NewGuid().ToString("N")[..6];
        _f.Manager!.Register(b with
        {
            Name = name,
            ConnectionString = System.Text.RegularExpressions.Regex.Replace(b.ConnectionString, "Pwd=[^;]*", "Pwd=definitely-wrong")
        });
        var svc = new ReadService(_f.Manager);

        for (int i = 0; i < SessionManagerBreaker.Threshold; i++)
        {
            var e = Assert.Throws<OneCException>(() => svc.Read(name, Q));
            Assert.Equal(ErrorCategory.Auth, e.Category);
        }

        var sw = Stopwatch.StartNew();
        var fast = Assert.Throws<OneCException>(() => svc.Read(name, Q));
        Assert.True(sw.ElapsedMilliseconds < 200, $"breaker did not short-circuit: {sw.ElapsedMilliseconds} ms");
        Assert.Contains("times in a row", fast.Message);
        Assert.Equal(ErrorCategory.Auth, fast.Category);                  // keeps the real reason
        Assert.True(fast.IsRetryable);

        // Other bases on the same connector are unaffected.
        Assert.NotEmpty(svc.Read(b.Name, Q).Rows);
    }

    /// <summary>
    /// A session that died while idle (server restart, network drop) is detected on borrow and
    /// replaced — the request succeeds instead of failing (old adapter: СоединениеЖиво, main.os:2782).
    /// </summary>
    [Fact]
    public void DeadIdleSessionIsReplacedOnBorrow()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions { ValidateIdleAfter = TimeSpan.Zero });
        m.Register(b);
        var svc = new ReadService(m);

        Assert.NotEmpty(svc.Read(b.Name, Q).Rows);              // one warm, idle session
        Assert.Equal(1, m.KillIdleConnectionsForTest(b.Name));  // …whose connection now dies

        Assert.NotEmpty(svc.Read(b.Name, Q).Rows);              // served anyway
        Assert.Equal(1, m.ProbeFailures(b.Name));
        var s = m.Stats().Single();
        Assert.Equal(2, s.Created);
        Assert.Equal(1, s.Live);
    }

    /// <summary>
    /// The machine-wide queue is real: while another holder (here: this test, standing in for
    /// the old adapter) has the lock, a file-base Connect waits, then proceeds.
    /// </summary>
    [Fact]
    public void FileBaseConnectWaitsForTheMachineWideLock()
    {
        if (!_f.Available || _f.File is null) return;
        using var m = new SessionManager(_f.Manager!.ComcntrPath, new PoolOptions());
        m.Register(_f.File);

        long waited;
        var holder = FileConnectLock.Acquire(FileConnectLock.LockPath, TimeSpan.FromSeconds(30));
        Assert.NotNull(holder);
        var release = Task.Run(async () => { await Task.Delay(2000); holder!.Dispose(); });

        var sw = Stopwatch.StartNew();
        var rows = new ReadService(m).Read(_f.File.Name, Q).Rows;
        waited = sw.ElapsedMilliseconds;
        release.Wait();

        Assert.NotEmpty(rows);
        Assert.True(waited >= 1500, $"file connect did not wait for the lock ({waited} ms)");
    }
}

/// <summary>Named constants the tests read, so a tuning change does not silently break them.</summary>
internal static class SessionManagerBreaker
{
    public const int Threshold = BasePool.BreakerThreshold;
}
