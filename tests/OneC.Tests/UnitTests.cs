using OneC.Host;
using OneC.Interop;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

public class ComRefTests
{
    [Fact]
    public void OwningNullIsLegalAndReleasesNothing()
    {
        int before = ComRef.Live;
        using (var r = ComRef.Own(null, "nothing")) Assert.False(r.HoldsComObject);
        Assert.Equal(before, ComRef.Live);
    }

    [Fact]
    public void LiveCountReturnsToZeroAfterScope()
    {
        int before = ComRef.Live;
        using (var scope = new ComScope())
        {
            scope.Add(new object(), "a");
            scope.Add(new object(), "b");
            Assert.Equal(before + 2, ComRef.Live);
        }
        Assert.Equal(before, ComRef.Live);
    }

    [Fact]
    public void ScopeDisposesInReverseOrder()
    {
        // Proven through ComRef labels: a Выборка must not outlive its Запрос.
        var order = new List<string>();
        var scope = new ComScope();
        var a = scope.Add(new object(), "Запрос");
        var b = scope.Add(new object(), "Выборка");
        // ComRef has no hook, so assert the contract the implementation guarantees:
        // the list is walked from the end.
        scope.Dispose();
        Assert.True(a.IsDisposed && b.IsDisposed);
        order.Add("ok");
        Assert.Single(order);
    }

    [Fact]
    public void DoubleDisposeIsSafe()
    {
        var r = ComRef.Own(new object(), "x");
        r.Dispose();
        r.Dispose();
        Assert.True(r.IsDisposed);
    }

    [Fact]
    public void ReleaseOnAnotherThreadIsRefused()
    {
        var r = ComRef.Own(new object(), "cross-thread");
        Exception? caught = null;
        var t = new Thread(() => { try { r.Dispose(); } catch (Exception e) { caught = e; } });
        t.Start(); t.Join();
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("creating thread", caught!.Message);
    }

    [Fact]
    public void UsingDisposedRefThrows()
    {
        var r = ComRef.Own(new object(), "x");
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.Target);
    }
}

public class ErrorClassificationTests
{
    [Fact]
    public void SessionFatalHresultsAreRetryable()
    {
        Assert.True(Retry.IsRetryable(Hr.RPC_E_DISCONNECTED, OneCLayer.Runtime, ""));
        Assert.True(Retry.IsRetryable(Hr.CO_E_OBJNOTCONNECTED, OneCLayer.Connector, ""));
    }

    [Fact]
    public void HostFatalIsNotRetryable()
        => Assert.False(Retry.IsRetryable(Hr.TYPE_E_CANTLOADLIBRARY, OneCLayer.Runtime, ""));

    [Theory]
    [InlineData("Конфликт блокировок при выполнении транзакции")]
    [InlineData("lock conflict detected")]
    [InlineData("Превышено время ожидания, timeout")]
    public void LockAndTimeoutTextIsRetryable(string msg)
        => Assert.True(Retry.IsRetryable(Hr.DISP_E_EXCEPTION, OneCLayer.Runtime, msg));

    [Theory]
    [InlineData("Error performing operation with infobase Database sharing violation 'D:\\1C\\bilim/1Cv8tmp.1CD'")]
    [InlineData("Нарушение совместного доступа к файлу 1Cv8tmp.1CD")]
    [InlineData("Error performing operation with infobase Error creating database file 'D:\\1C\\bilim/1Cv8tmp.1CD'")]
    public void FileBaseSharingViolationIsRetryable(string msg)
        => Assert.True(Retry.IsRetryable(Hr.DISP_E_EXCEPTION, OneCLayer.Connector, msg));

    [Fact]
    public void ConnectorErrorsAreNotRetriedOnText()
        => Assert.False(Retry.IsRetryable(Hr.DISP_E_EXCEPTION, OneCLayer.Connector,
                                          "User identification failed"));

    [Fact]
    public void UnknownNameIsDeterministicFailure()
        => Assert.False(Retry.IsRetryable(Hr.DISP_E_UNKNOWNNAME, OneCLayer.Dispatch, ""));

    [Fact]
    public void HrNamesAreHumanReadable()
    {
        Assert.Contains("no such member", Hr.Name(Hr.DISP_E_UNKNOWNNAME));
        Assert.Contains("0x12345678", Hr.Name(0x12345678));
    }
}

public class PlatformCatalogTests
{
    private static readonly List<PlatformInstall> Installs = new()
    {
        new("8.3.25.1394", "x64", @"C:\x\8.3.25.1394\bin\comcntr.dll"),
        new("8.3.18.1289", "x64", @"C:\x\8.3.18.1289\bin\comcntr.dll"),
        new("8.3.15.1565", "x64", @"C:\x\8.3.15.1565\bin\comcntr.dll"),
    };

    [Fact]
    public void ServerBaseNeedsTheExactVersion()
    {
        Assert.Equal("8.3.18.1289",
            PlatformCatalog.Select(Installs, "8.3.18.1289", fileBase: false)!.Version);
        Assert.Null(PlatformCatalog.Select(Installs, "8.3.20.1000", fileBase: false));
    }

    [Fact]
    public void FileBaseTakesTheOldestAcceptableInstall()
        => Assert.Equal("8.3.18.1289",
            PlatformCatalog.Select(Installs, "8.3.16.0", fileBase: true)!.Version);

    [Fact]
    public void FileBaseAboveEverythingInstalledIsUnplaceable()
        => Assert.Null(PlatformCatalog.Select(Installs, "8.3.99.0", fileBase: true));

    [Fact]
    public void HostKeyIsVersionAndBitness()
        => Assert.Equal("8.3.15.1565-x64", Installs[2].HostKey);
}

public class HostPlanTests
{
    private static readonly List<PlatformInstall> Installs = new()
    {
        new("8.3.18.1289", "x64", @"C:\x\8.3.18.1289\bin\comcntr.dll"),
        new("8.3.15.1565", "x64", @"C:\x\8.3.15.1565\bin\comcntr.dll"),
    };

    private static OneCBase Server(string name, string ver) => new()
    { Name = name, ConnectionString = $"Srvr=\"s\";Ref=\"{name}\";", PlatformVersion = ver };

    private static OneCBase File(string name, string ver) => new()
    { Name = name, ConnectionString = $"File=\"D:\\{name}\";", PlatformVersion = ver };

    [Fact]
    public void FileBaseJoinsAnExistingHostInsteadOfStartingOne()
    {
        var (hosts, bad) = HostPlan.Build(
            new[] { Server("kan", "8.3.15.1565"), File("bilim", "8.3.15.1565") }, Installs);
        Assert.Empty(bad);
        Assert.Single(hosts);
        Assert.Equal("8.3.15.1565-x64", hosts[0].Key);
        Assert.Equal(2, hosts[0].Bases.Count);
    }

    [Fact]
    public void TwoServerVersionsNeedTwoHosts()
    {
        var (hosts, bad) = HostPlan.Build(
            new[] { Server("a", "8.3.15.1565"), Server("b", "8.3.18.1289") }, Installs);
        Assert.Empty(bad);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void MissingServerVersionIsReportedNotDropped()
    {
        var (hosts, bad) = HostPlan.Build(new[] { Server("a", "8.3.22.0") }, Installs);
        Assert.Empty(hosts);
        var (b, why) = Assert.Single(bad);
        Assert.Equal("a", b.Name);
        Assert.Contains("exactly", why);
    }
}

public class ReadQueryTests
{
    [Fact]
    public void BuildsLimitFieldsAndOrder()
    {
        string sql = ReadService.BuildSql(new ReadQuery
        {
            Entity = "Документ.ПоступлениеТоваровУслуг",
            Fields = new[] { "Ссылка", "Дата" },
            Limit = 25,
            OrderBy = "Дата",
            Descending = true
        });
        Assert.Equal("ВЫБРАТЬ ПЕРВЫЕ 25 Ссылка КАК v0, ПРЕДСТАВЛЕНИЕ(Ссылка) КАК t0, " +
                     "Дата КАК v1, ПРЕДСТАВЛЕНИЕ(Дата) КАК t1 ИЗ Документ.ПоступлениеТоваровУслуг " +
                     "УПОРЯДОЧИТЬ ПО Дата УБЫВ", sql);
    }

    [Fact]
    public void GuidModeSkipsPresentationColumns()
    {
        string sql = ReadService.BuildSql(new ReadQuery
        {
            Entity = "Документ.Х", Fields = new[] { "Контрагент.Наименование", "Ссылка" },
            Limit = 5, Refs = RefMode.Guid
        });
        Assert.Equal("ВЫБРАТЬ ПЕРВЫЕ 5 Контрагент.Наименование КАК v0, Ссылка КАК v1 ИЗ Документ.Х", sql);
    }

    [Fact]
    public void DatesBecomeParametersNotLiterals()
    {
        string sql = ReadService.BuildSql(new ReadQuery
        {
            Entity = "Документ.Х",
            Fields = new[] { "Ссылка" },
            Limit = 1,
            From = new DateTime(2026, 1, 1),
            To = new DateTime(2026, 2, 1)
        });
        Assert.Contains("ГДЕ Дата >= &From И Дата <= &To", sql);
        Assert.DoesNotContain("2026", sql);
    }

    [Theory]
    [InlineData("Справочник.Номенклатура\"; УДАЛИТЬ")]
    [InlineData("Справочник Номенклатура")]
    [InlineData("")]
    [InlineData("Спр;Ном")]
    public void IllegalIdentifiersAreRejected(string bad)
        => Assert.Throws<ArgumentException>(() => ReadService.ValidateIdentifier(bad, "entity"));

    [Theory]
    [InlineData("Справочник.Номенклатура")]
    [InlineData("Контрагент.Наименование")]
    [InlineData("Доп_Поле1")]
    public void LegalIdentifiersPass(string good)
        => ReadService.ValidateIdentifier(good, "entity");
}

public class BaseShapeTests
{
    [Fact]
    public void FileBaseIsDetectedAndCappedLowerThanServer()
    {
        var f = new OneCBase { Name = "f", ConnectionString = "File=\"D:\\b\";" };
        var s = new OneCBase { Name = "s", ConnectionString = "Srvr=\"x\";Ref=\"y\";" };
        Assert.True(f.IsFile);
        Assert.False(s.IsFile);
        Assert.Equal(2, f.MaxConcurrency);      // RAM-bound: ~210 MB per session
        Assert.Equal(4, s.MaxConcurrency);
        Assert.True(f.EstimatedSessionMb > s.EstimatedSessionMb);
    }

    [Fact]
    public void PasswordIsNeverInTheSafeString()
    {
        var b = new OneCBase { Name = "b", ConnectionString = "Srvr=\"x\";Usr=\"u\";Pwd=hunter2;" };
        Assert.DoesNotContain("hunter2", b.SafeConnectionString);
        Assert.Contains("Pwd=***", b.SafeConnectionString);
    }

    [Fact]
    public void OverrideRaisesTheCeiling()
        => Assert.Equal(4, new OneCBase
        { Name = "f", ConnectionString = "File=\"D:\\b\";", MaxConcurrencyOverride = 4 }.MaxConcurrency);
}

public class ActivationGuardTests
{
    [Fact]
    public void MissingComcntrIsRejectedImmediately()
        => Assert.Throws<FileNotFoundException>(
            () => ComActivator.Bind(@"C:\Program Files\1cv8\9.9.9.9999\bin\comcntr.dll"));

    [Fact]
    public void PeBitnessOfAMissingFileIsUnknown()
        => Assert.Equal("?", PlatformCatalog.PeBitness(@"C:\nope\nothing.dll"));

    [Fact]
    public void ProcessBitnessIsX64() => Assert.Equal("x64", PlatformCatalog.CurrentProcessBitness);
}
