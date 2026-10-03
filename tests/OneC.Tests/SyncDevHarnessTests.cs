using System.Net;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>S12: the HTTP layer the dev run measures and injects faults through — nothing injected unless armed.</summary>
public class SyncDevHarnessTests
{
    private sealed class Backend : HttpMessageHandler
    {
        public int Calls;
        public string? LastAuth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastAuth = r.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(LastAuth == "good" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }
    }

    private static HttpRequestMessage Upload() => new(HttpMethod.Post, "http://x/api/v2/entity/upload")
    {
        Content = new StringContent("payload"),
        Headers = { Authorization = new("Bearer", "good") }
    };

    [Fact]
    public async Task FaultsFireOnlyWhenArmedAndEachOnlyOnce()
    {
        var backend = new Backend();
        var faults = new HttpFaults { Armable = true, InnerHandler = backend };
        using var http = new HttpClient(faults);

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload())).StatusCode);       // nothing armed: straight through

        faults.Arm(fail503: 1, failNetwork: 1, badToken: 1, reject400: 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(Upload()));     // network first
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.SendAsync(Upload())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Upload())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Upload())).StatusCode);   // the backend's own 401 for the bad token
        Assert.Equal("invalid-token-s12-failure-test", backend.LastAuth);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Upload())).StatusCode);       // all used up
        Assert.Equal(3, backend.Calls);                                                     // the injected ones never reached it

        var calls = faults.Calls;
        Assert.Equal(6, calls.Count);
        Assert.All(calls, c => Assert.Equal("/api/v2/entity/upload", c.Path));
        Assert.Equal(new[] { 200, 0, 503, 400, 401, 200 }, calls.Select(c => c.Status));
        Assert.All(calls.Where(c => c.Status != 0), c => Assert.Equal(7, c.RequestBytes));
    }

    [Fact]
    public void ANonArmableTargetRefusesFaults()
    {
        var faults = new HttpFaults();
        Assert.Throws<InvalidOperationException>(() => faults.Arm(1, 0, 0, 0));
    }

    [Fact]
    public void TheDevTargetIsTheDevelopmentHostOnly()
    {
        Assert.Equal("aiba-1c-dev.aiba.uz", DevBackendRoot().Host);
        Assert.DoesNotContain(".aiba.group", DevBackendRoot().Host);
    }

    private static Uri DevBackendRoot() =>
        (Uri)typeof(HttpFaults).Assembly.GetType("OneC.Supervisor.DevBackend")!.GetField("Root")!.GetValue(null)!;

    // ---------------- the start guard: test-owned record, nobody else serving it ----------------

    private sealed class Records(string json) : HttpMessageHandler
    {
        public string? Auth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Auth = r.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static async Task Guard(string recordJson)
    {
        string dir = Directory.CreateTempSubdirectory("aiba-s12guard-").FullName;
        try
        {
            var store = new OneC.Cloud.SessionStore(dir);
            store.Save(new OneC.Cloud.CloudSession("dev", "u1", "+998000000000", "T", "T", "access-1", "refresh-1"));
            var cloud = new OneC.Cloud.CloudClient(OneC.Cloud.CloudEnvironment.Dev, store, "test-device");
            var handler = new Records(recordJson);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://aiba-1c-dev.aiba.uz/") };
            await (Task)typeof(HttpFaults).Assembly.GetType("OneC.Supervisor.DevBackend")!.GetMethod("GuardAsync")!
                .Invoke(null, new object?[] { http, cloud, "0123456789abcdef01234567", CancellationToken.None })!;
            Assert.Equal("access-1", handler.Auth);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TheStartGuardAcceptsOnlyAnOfflineTestOwnedRecord()
    {
        await Guard("""{"name":"SYNC-TEST bilim 20261002-0900","odataName":"SYNC-TEST-bilim-20261002-0900","connection_state":"offline"}""");

        var real = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Guard("""{"name":"Kansler OOO","odataName":"KAN","connection_state":"offline"}"""));
        Assert.Contains("not test-owned", real.Message);

        // A test name but a real base's odataName: an old Connector with a base of that name would sync it too.
        var odata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Guard("""{"name":"SYNC-TEST bilim","odataName":"bilim","connection_state":"offline"}"""));
        Assert.Contains("not test-owned", odata.Message);

        var live = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Guard("""{"name":"SYNC-TEST bilim","odataName":"SYNC-TEST-bilim","connection_state":"writable"}"""));
        Assert.Contains("connection_state=writable", live.Message);
    }

    [Fact]
    public async Task ARebuildIsRefusedWhereTheTargetSaysSo()
    {
        string dir = Directory.CreateTempSubdirectory("aiba-s12rebuild-").FullName;
        try
        {
            using var db = OneC.SyncState.SyncDb.Open(Path.Combine(dir, "sync.db"));
            using var host = new OneC.Sync.Engine.SyncEngineHost(db, new OneC.Sync.Stub.StubSyncTarget(), new FakeOneC(),
                new[] { new OneC.Sync.Engine.BasePlan("b", false, null, "conn", new[] { new OneC.Sync.Source.TablePlan("Catalog_X", "X", "catalog", false) }) })
            { RebuildRefusal = "disabled on the shared dev backend" };
            var (done, message) = await host.RebuildAsync("b", "Catalog_X", confirmed: true, CancellationToken.None);
            Assert.False(done);
            Assert.Equal("disabled on the shared dev backend", message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
