using System.Diagnostics;
using OneC.Desktop.Services;
using Xunit;

namespace OneC.Tests;

public class DesktopCoreUnitTests
{
    [Fact]
    public void ConnectionStringRoundTripsQuotesAndCyrillic()
    {
        var b = new StoredBase
        {
            Kind = BaseKind.Server, Server = "WIN-11-2070", Ref = "KAN",
            User = "Эркин Ибрагимов"
        };
        string cs = b.ConnectionString("pa\"ss;word");
        var p = BaseStore.ParseConnectionString(cs);
        Assert.Equal("WIN-11-2070", p["srvr"]);
        Assert.Equal("KAN", p["ref"]);
        Assert.Equal("Эркин Ибрагимов", p["usr"]);
        Assert.Equal("pa\"ss;word", p["pwd"]);
    }

    [Fact]
    public void FileBaseConnectionString()
    {
        var b = new StoredBase { Kind = BaseKind.File, FilePath = @"D:\1C\bilim", User = "u" };
        var p = BaseStore.ParseConnectionString(b.ConnectionString(""));
        Assert.Equal(@"D:\1C\bilim", p["file"]);
        Assert.False(p.ContainsKey("srvr"));
    }

    [Fact]
    public void PasswordsAreProtectedNotStoredInClear()
    {
        string enc = BaseStore.Protect("hunter2");
        Assert.DoesNotContain("hunter2", enc);
        Assert.Equal("hunter2", BaseStore.Unprotect(enc));
    }

    [Fact]
    public void StoreSavesSortedAndReloadsWithoutClearPasswords()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aiba-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new BaseStore(Path.Combine(dir, "bases.json"));
            s.Upsert(new StoredBase { Name = "zeta", Kind = BaseKind.File, FilePath = "D:\\z", PasswordProtected = BaseStore.Protect("secret-z") });
            s.Upsert(new StoredBase { Name = "alpha", Kind = BaseKind.File, FilePath = "D:\\a", PasswordProtected = BaseStore.Protect("secret-a") });

            string onDisk = File.ReadAllText(s.FilePath);
            Assert.DoesNotContain("secret-", onDisk);

            var again = new BaseStore(s.FilePath);
            Assert.Equal(new[] { "alpha", "zeta" }, again.Bases.Select(b => b.Name));
            Assert.Contains("secret-a", again.SupervisorPayload());     // decrypted only in memory

            again.Remove("alpha");
            Assert.Equal(new[] { "zeta" }, new BaseStore(s.FilePath).Bases.Select(b => b.Name));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UndecryptablePasswordDoesNotBreakThePayload()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aiba-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new BaseStore(Path.Combine(dir, "bases.json"));
            s.Upsert(new StoredBase { Name = "copied", Kind = BaseKind.File, FilePath = "D:\\c",
                                      PasswordProtected = Convert.ToBase64String(new byte[64]) });
            Assert.Contains("\"copied\"", s.SupervisorPayload());        // no exception, empty password
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("{(1, 165)}: Field not found \"ПолноеНаименование\" ВЫБРАТЬ ПЕРВЫЕ 100 Наименование КАК v0",
                "Field not found \"ПолноеНаименование\"")]
    [InlineData("Failed to post: \"Реализация товаров и услуг\"!", "Failed to post: \"Реализация товаров и услуг\"!")]
    [InlineData("{(1, 1)}: Expression expected \"SELECT\" <<?>>x SELECT y", "Expression expected \"SELECT\" <<?>>x")]
    public void QueryEchoIsTrimmedFromOneCErrors(string raw, string expected)
        => Assert.Equal(expected, EdgeException.WithoutQueryEcho(raw));
}

/// <summary>
/// The desktop app's engine path without the UI: launch OneC.Supervisor exactly as the app
/// does (bases over stdin, port 0), talk to the edge, stop, and prove nothing is left running.
/// </summary>
[Collection("onec-live")]
public class DesktopEngineLiveTests
{
    private readonly LiveFixture _f;
    public DesktopEngineLiveTests(LiveFixture f) => _f = f;

    [Fact]
    public async Task AppLaunchPathStartsTheEngineServesReadsAndCleansUp()
    {
        if (!_f.Available) return;
        string path = Environment.GetEnvironmentVariable("ONEC_TEST_BASES")!;
        string dir = Path.Combine(Path.GetTempPath(), "aiba-store-" + Guid.NewGuid().ToString("N"));
        using var sup = new SupervisorProcess();
        try
        {
            var store = new BaseStore(Path.Combine(dir, "bases.json"));
            Assert.Equal(_f.Bases.Count, store.ImportConnectionStrings(path));

            var sw = Stopwatch.StartNew();
            await sup.StartAsync(store.SupervisorPayload());
            Assert.True(sup.State == SupervisorState.Running, $"state {sup.State}: {sup.LastError}");
            Assert.True(sup.Port > 0);
            int supervisorPid = sup.Pid!.Value;

            var client = sup.Client!;
            var hosts = await client.Hosts();
            Assert.NotEmpty(hosts);

            string baseName = (_f.Server ?? _f.File!).Name;
            var r = await client.Read(baseName, "Справочник.Номенклатура", new[] { "Наименование" }, 5, "text");
            Assert.NotEmpty(r["rows"]!.AsArray());

            // Connection test = connect + metadata probe (old adapter parity, main.os:2048).
            var t = await client.Test(baseName);
            Assert.False(string.IsNullOrWhiteSpace(t["configuration"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(t["platformVersion"]?.GetValue<string>()));

            var err = await Assert.ThrowsAsync<EdgeException>(() =>
                client.Read(baseName, "Документ.НетТакогоДокумента", new[] { "Ссылка" }, 1, "text"));
            Assert.Equal("Runtime", err.Layer);
            Assert.StartsWith("1C reported an error: Table not found", err.Friendly);

            var activity = await client.Activity(10);
            Assert.True(activity.Count >= 2);

            var hostPids = hosts.Select(h => h!["pid"]!.GetValue<int>()).ToList();
            sup.Stop();
            Assert.Equal(SupervisorState.Stopped, sup.State);
            Thread.Sleep(1000);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(supervisorPid));
            foreach (var pid in hostPids) Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
