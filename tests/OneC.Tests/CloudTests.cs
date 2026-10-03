using System.Text.Json;
using OneC.Cloud;
using Xunit;
using Stub = OneC.CloudStub.CloudStub;

namespace OneC.Tests;

/// <summary>
/// Login, refresh, companies, linking and the status heartbeat against the local CloudStub,
/// which carries the prod rules and error codes (specs/2026-09-30-auth-and-cloud-link.md).
/// Nothing here talks to a real AIBA server.
/// </summary>
public class CloudTests : IAsyncLifetime
{
    private const string Phone = "+998900000001";
    private readonly string _pwd = "T3st!" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiba-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly Stub _stub = new();
    private CloudEnvironment _env = null!;

    public async Task InitializeAsync()
    {
        _stub.AddUser(new Stub.User("101", Phone, _pwd, "Test", "User"));
        _stub.AddUser(new Stub.User("102", "+998900000002", _pwd, "Other", "User"));
        for (int i = 0; i < 53; i++)                              // more than one page of 50
            _stub.AddCompany(new Stub.Company($"{500 + i}", $"Company {i}", $"3000{i:D5}", i == 7, "101"));
        _stub.AddCompany(new Stub.Company("900", "Not mine", "399999999", true, "102"));
        await _stub.StartAsync();
        _env = new CloudEnvironment("custom", "Stub", _stub.ApiBase, _stub.OneCBase);
    }

    public async Task DisposeAsync()
    {
        await _stub.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private CloudClient Client() => new(_env, new SessionStore(_dir), "test-device");

    [Theory]
    [InlineData("+998 90 000 00 01", "+998900000001")]
    [InlineData("998900000001", "+998900000001")]
    [InlineData("90 000-00-01", "+998900000001")]
    [InlineData("(90) 0000001", "+998900000001")]
    [InlineData("", "")]
    public void PhonesAreSentWithTheCountryCode(string input, string expected) =>
        Assert.Equal(expected, CloudClient.NormalizePhone(input));

    [Fact]
    public async Task LoginKeepsTheSessionEncryptedAndNeverStoresThirdPartyTokens()
    {
        using var c = Client();
        var s = await c.LoginAsync("90 000 00 01", _pwd);
        Assert.Equal("101", s.UserId);                                   // from the JWT sub, what backend/1c compares
        Assert.Equal("Test User", s.DisplayName);

        string file = File.ReadAllText(Path.Combine(_dir, "session.json"));
        Assert.DoesNotContain(s.AccessToken, file);
        Assert.DoesNotContain(s.RefreshToken, file);
        Assert.DoesNotContain(_pwd, file);
        Assert.DoesNotContain("SOCIAL-SECRET", file);

        using var again = Client();                                      // restart: session restored
        Assert.True(again.SignedIn);
        Assert.Equal(s.AccessToken, again.Session!.AccessToken);
    }

    [Theory]
    [InlineData("+998900000009", "right", "There is no AIBA account")]
    [InlineData(Phone, "wrong", "Wrong password.")]
    [InlineData("12", "right", "not valid")]
    public async Task LoginErrorsAreReadable(string phone, string which, string expected)
    {
        using var c = Client();
        var e = await Assert.ThrowsAsync<CloudException>(() => c.LoginAsync(phone, which == "right" ? _pwd : "nope"));
        Assert.Contains(expected, e.Message);
        Assert.False(c.SignedIn);
        Assert.False(File.Exists(Path.Combine(_dir, "session.json")));
    }

    [Fact]
    public async Task TooManyLoginsAreReportedNotRetried()
    {
        _stub.LoginRateLimitPerMinute = 2;
        using var c = Client();
        for (int i = 0; i < 2; i++) await Assert.ThrowsAsync<CloudException>(() => c.LoginAsync(Phone, "nope"));
        var e = await Assert.ThrowsAsync<CloudException>(() => c.LoginAsync(Phone, _pwd));
        Assert.Equal(429, e.Status);
        Assert.Contains("Wait a minute", e.Message);
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshedOnceAndTheCallSucceeds()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        string before = c.Session!.AccessToken;
        _stub.ExpireAccessTokens();

        var companies = await c.CompaniesAsync();
        Assert.Equal(53, companies.Count);
        Assert.Equal(1, _stub.RefreshCalls);
        Assert.NotEqual(before, c.Session!.AccessToken);
        Assert.DoesNotContain(c.Session.AccessToken, File.ReadAllText(Path.Combine(_dir, "session.json")));
    }

    [Fact]
    public async Task ParallelCallsShareOneRefresh()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        _stub.ExpireAccessTokens();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.CompaniesAsync()));
        Assert.Equal(1, _stub.RefreshCalls);
    }

    [Fact]
    public async Task RefusedRefreshSignsOut()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        bool signedOut = false;
        c.SignedOut += (_, _) => signedOut = true;
        _stub.ExpireAccessTokens();
        _stub.RevokeRefreshTokens();

        var e = await Assert.ThrowsAsync<CloudException>(() => c.CompaniesAsync());
        Assert.Equal(401, e.Status);
        Assert.True(signedOut);
        Assert.False(c.SignedIn);
        Assert.False(File.Exists(Path.Combine(_dir, "session.json")));
    }

    [Fact]
    public async Task ASessionBelongsToOneEnvironment()
    {
        using (var c = Client()) await c.LoginAsync(Phone, _pwd);
        using var dev = new CloudClient(_env with { Key = "dev" }, new SessionStore(_dir), "test-device");
        Assert.False(dev.SignedIn);
    }

    [Fact]
    public async Task CompaniesComeFromEveryPageAndOnlyTheUsersOwn()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        var list = await c.CompaniesAsync();
        Assert.Equal(53, list.Count);
        Assert.DoesNotContain(list, x => x.Id == "900");
        Assert.Single(list, x => x.IsDefault);
    }

    [Fact]
    public async Task LinkingCreatesARecordAndADuplicateIsRefused()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        var rec = await c.OneCCreateAsync("501", "kansler", "kansler", "unisoft", "3.0");
        Assert.Equal(24, rec.Id.Length);

        var list = await c.OneCListAsync("501");
        Assert.Single(list, r => r.Id == rec.Id && r.OdataName == "kansler");

        var dup = await Assert.ThrowsAsync<CloudException>(() => c.OneCCreateAsync("501", "kansler", "kansler", "unisoft", "3.0"));
        Assert.Equal(409, dup.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => c.OneCCreateAsync("501", "x", "x", "1c", "3.0"));
    }

    [Fact]
    public async Task RecordsBeingDeletedAreNotListed()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        var rec = await c.OneCCreateAsync("502", "bilim", "bilim", "unisoft", "3.0");
        _stub.MarkDeleting(rec.Id);
        Assert.DoesNotContain(await c.OneCListAsync("502"), r => r.Id == rec.Id);
    }

    [Fact]
    public async Task DeletedRecordLeavesTheListAndDeletingAGoneOneIsNotAnError()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        var rec = await c.OneCCreateAsync("503", "KAN", "KAN", "venkon", "3.0");
        await c.OneCDeleteAsync(rec.Id);
        Assert.Equal("deleting", _stub.Records.Single(r => r.Id == rec.Id).Status);
        Assert.DoesNotContain(await c.OneCListAsync("503"), r => r.Id == rec.Id);
        await c.OneCDeleteAsync("000000000000000000000000");             // 404: already gone, no error
    }

    [Fact]
    public async Task SyncProgressIsReadFromTheRecord()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        _stub.AddRecord("101", "504", "Done", "done", totalCount: 48210, percentage: 100);
        _stub.AddRecord("101", "504", "Half", "half", totalCount: 9000, percentage: 37.5);
        _stub.AddRecord("101", "504", "Broken", "broken", lastError: "1C ulanish xatosi");
        var list = (await c.OneCListAsync("504")).ToDictionary(r => r.OdataName);
        Assert.Equal((48210L, 100.0, (string?)null), (list["done"].TotalCount, list["done"].Percentage, list["done"].LastError));
        Assert.Equal(37.5, list["half"].Percentage);
        Assert.Equal((0L, "1C ulanish xatosi"), (list["broken"].TotalCount, list["broken"].LastError));
    }

    [Fact]
    public async Task HeartbeatMarksOnlyRecordsThisAppCreated()
    {
        using var c = Client();
        await c.LoginAsync(Phone, _pwd);
        var mine = await c.OneCCreateAsync("501", "kansler", "kansler", "unisoft", "3.0");
        var other = await c.OneCCreateAsync("501", "legacy", "legacy", "unisoft", "3.0");
        var links = new[]
        {
            new CloudLink("custom", "101", "kansler", mine.Id, "501", "Company 1", "kansler", "kansler", "unisoft", true, DateTime.UtcNow),
            new CloudLink("custom", "101", "old", other.Id, "501", "Company 1", "legacy", "legacy", "unisoft", false, DateTime.UtcNow)
        };

        var beats = await StatusHeartbeat.BeatAsync(c, links, _ => true);
        Assert.Single(beats);
        Assert.Equal("active", _stub.Records.Single(r => r.Id == mine.Id).Status);
        Assert.Equal(0, _stub.Records.Single(r => r.Id == other.Id).Patches);

        await StatusHeartbeat.BeatAsync(c, links, _ => false);
        Assert.Equal("inactive", _stub.Records.Single(r => r.Id == mine.Id).Status);

        _stub.MarkDeleting(mine.Id);
        var gone = await StatusHeartbeat.BeatAsync(c, links, _ => true);
        Assert.Contains("deleted", gone.Single().Error);
    }

    [Fact]
    public void LinksAreKeptPerEnvironmentAndUserWithoutSecrets()
    {
        var store = new LinkStore(_dir);
        store.Set(new CloudLink("prod", "101", "KAN", "abc", "501", "A", "KAN", "KAN", "unisoft", true, DateTime.UtcNow));
        store.Set(new CloudLink("dev", "101", "KAN", "def", "501", "A", "KAN", "KAN", "unisoft", true, DateTime.UtcNow));
        var reread = new LinkStore(_dir);
        Assert.Equal("abc", reread.Get("prod", "101", "kan")!.OneCId);
        Assert.Equal("def", reread.Get("dev", "101", "KAN")!.OneCId);
        Assert.Null(reread.Get("prod", "102", "KAN"));
        reread.Remove("prod", "101", "KAN");
        Assert.Null(new LinkStore(_dir).Get("prod", "101", "KAN"));
    }

    [Fact]
    public void CustomEnvironmentRefusesPlainHttpToAnotherMachine()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cloud.json"),
            JsonSerializer.Serialize(new { apiBase = "http://example.com/api/v1", oneCBase = "http://example.com/api/v2" }));
        Assert.Null(CloudEnvironment.Custom(_dir));
        File.WriteAllText(Path.Combine(_dir, "cloud.json"),
            JsonSerializer.Serialize(new { label = "Stub", apiBase = "http://127.0.0.1:5/api/v1", oneCBase = "http://127.0.0.1:5/api/v2" }));
        Assert.Equal("Stub", CloudEnvironment.Custom(_dir)!.Label);
        Assert.Equal(CloudEnvironment.Prod, CloudEnvironment.Find(_dir, null));
    }
}
