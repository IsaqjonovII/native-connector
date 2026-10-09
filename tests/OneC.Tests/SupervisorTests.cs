using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

public class IpcUnitTests
{
    [Fact]
    public async Task FramesRoundTrip()
    {
        var ms = new MemoryStream();
        var lk = new SemaphoreSlim(1, 1);
        await Frames.WriteAsync(ms, "{\"a\":1}"u8.ToArray(), lk);
        await Frames.WriteAsync(ms, "Номенклатура"u8.ToArray(), lk);
        ms.Position = 0;
        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString((await Frames.ReadAsync(ms))!));
        Assert.Equal("Номенклатура", System.Text.Encoding.UTF8.GetString((await Frames.ReadAsync(ms))!));
        Assert.Null(await Frames.ReadAsync(ms));                     // clean EOF
    }

    [Fact]
    public async Task TruncatedFrameIsAnError()
    {
        var ms = new MemoryStream(new byte[] { 10, 0, 0, 0, 1, 2 });  // says 10 bytes, has 2
        await Assert.ThrowsAsync<EndOfStreamException>(() => Frames.ReadAsync(ms));
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAllocating()
    {
        var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        await Assert.ThrowsAsync<InvalidDataException>(() => Frames.ReadAsync(ms));
    }

    [Fact]
    public void EnvelopeIsCamelCaseAndDeadlineIsClamped()
    {
        var r = new IpcRequest { Id = 7, Op = Ops.Read, Base = "b", DeadlineMs = 10_000_000 };
        string json = System.Text.Encoding.UTF8.GetString(IpcJson.Serialize(r));
        Assert.Contains("\"op\":\"read\"", json);
        Assert.Contains("\"deadlineMs\"", json);
        Assert.Equal(IpcRequest.MaxDeadlineMs, r.EffectiveDeadlineMs);
        Assert.Equal(IpcRequest.DefaultDeadlineMs, new IpcRequest { Op = "x" }.EffectiveDeadlineMs);
        Assert.Equal(7, IpcJson.Deserialize<IpcRequest>(IpcJson.Serialize(r)).Id);
    }

    [Theory]
    [InlineData("8.3.15.1565-x64", true)]
    [InlineData("8.3.15 x64", false)]
    [InlineData("..\\evil", false)]
    public void PipeNamesAcceptOnlySafeHostKeys(string key, bool ok)
    {
        if (ok)
        {
            Assert.Equal($"aiba-onec-{key}-42-n1", PipeNames.For(key, 42, "n1"));
            Assert.NotEqual(PipeNames.For(key, 42), PipeNames.For(key, 42));   // unique per instance
        }
        else Assert.Throws<ArgumentException>(() => PipeNames.For(key, 42));
    }

    [Theory]
    [InlineData(Layers.Validation, null, false, false, 400)]
    [InlineData(Layers.Host, ErrorKinds.NotFound, false, false, 404)]
    [InlineData(Layers.Host, ErrorKinds.Forbidden, false, false, 403)]
    [InlineData(Layers.Runtime, null, false, false, 422)]
    [InlineData(Layers.Runtime, null, true, false, 503)]        // lock conflict → retry
    [InlineData(Layers.Connector, null, false, false, 502)]
    [InlineData(Layers.Connector, null, false, true, 503)]      // host-fatal
    [InlineData(Layers.Timeout, null, false, false, 504)]
    [InlineData(Layers.Cancelled, null, false, false, 499)]
    [InlineData(Layers.Busy, null, true, false, 503)]
    [InlineData(Layers.Transport, null, true, false, 503)]
    [InlineData(Layers.Host, null, false, false, 500)]
    public void EdgeStatusMapping(string layer, string? kind, bool retry, bool hostFatal, int status)
        => Assert.Equal(status, EdgeServer.StatusFor(new IpcError
        { Layer = layer, Message = "m", Kind = kind, Retryable = retry, HostFatal = hostFatal }));

    [Fact]
    public void MissingHostExeIsRejected()
        => Assert.Throws<FileNotFoundException>(() =>
            new OneC.Supervisor.Supervisor(new SupervisorOptions { HostExe = @"C:\nope\OneC.Host.exe" }));
}

/// <summary>
/// Live supervisor + pipe + edge tests: real OneC.Host child processes (OneC.Host.exe is copied
/// next to the test assembly by the project reference).
/// </summary>
[Collection("onec-live")]
public class SupervisorLiveTests
{
    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe");
    private static readonly JsonObject ReadArgs = new()
    { ["entity"] = "Справочник.Номенклатура", ["fields"] = new JsonArray("Наименование"), ["limit"] = 3 };

    private readonly LiveFixture _f;
    public SupervisorLiveTests(LiveFixture f) => _f = f;

    private OneC.Supervisor.Supervisor New(int recycleMb = 0, int idleSeconds = 300) => new(new SupervisorOptions
    {
        HostExe = HostExe, RecycleIdleAboveMb = recycleMb, HostIdleTimeoutSeconds = idleSeconds,
        MonitorInterval = TimeSpan.FromHours(1)          // tests drive Tick() by hand
    });

    private static void ReadOk(OneC.Supervisor.Supervisor s, string baseName)
    {
        var r = s.Send(baseName, Ops.Read, (JsonObject)ReadArgs.DeepClone());
        var h = s.HostFor(baseName);
        Assert.True(r.Ok, $"{r.Error?.Message} | host state={h.State} exit={h.ExitCode} last output: {h.LastError}");
        Assert.True(r.Result!["rows"]!.AsArray().Count > 0);
    }

    [Fact]
    public void SpawnsOneHostPerPlatformAndEachReportsItsOwnVersion()
    {
        if (!_f.Available || _f.Server is null || _f.File is null) return;
        if (Gate.Skip(!PlatformCatalog.Discover().Any(i => i.Version == "8.3.18.1289"), "1C 8.3.18.1289 is not installed")) return;
        var bases = new List<OneCBase> { _f.Server, _f.File with { PlatformVersion = "8.3.18.1289" } };

        using var s = New();
        s.Start(bases);
        Assert.Equal(2, s.Hosts.Count);
        Assert.All(s.Hosts, h => Assert.Equal(HostState.Ready, h.State));
        foreach (var b in bases)
        {
            var v = s.Send(b.Name, Ops.Version);
            Assert.True(v.Ok, v.Error?.Message);
            Assert.Equal(s.HostFor(b.Name).Assignment.Install.Version, v.Result!["platformVersion"]!.GetValue<string>());
            ReadOk(s, b.Name);
        }
        Assert.NotEqual(s.HostFor(bases[0].Name).Pid, s.HostFor(bases[1].Name).Pid);
    }

    [Fact]
    public void AKilledHostIsRestartedAndServesAgain()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = New();
        s.Start(new[] { b });
        ReadOk(s, b.Name);

        var h = s.HostFor(b.Name);
        int oldPid = h.Pid!.Value;
        h.Kill();
        var sw = Stopwatch.StartNew();
        while (h.State == HostState.Ready && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);

        var during = s.Send(b.Name, Ops.Read, (JsonObject)ReadArgs.DeepClone());
        Assert.False(during.Ok);
        Assert.Equal(Layers.Transport, during.Error!.Layer);      // dead host: Transport, not a hang
        Assert.True(during.Error.Retryable);

        s.Tick();
        var nh = s.HostFor(b.Name);
        Assert.Equal(HostState.Ready, nh.State);
        Assert.NotEqual(oldPid, nh.Pid);
        Assert.Equal(1, s.Restarts(nh.Key));
        ReadOk(s, b.Name);
    }

    [Fact]
    public void HostWithWarmSessionsIsNeverRecycled()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = New(recycleMb: 1);                          // every host is "above"
        s.Start(new[] { b });
        ReadOk(s, b.Name);                                         // leaves one warm session
        int pid = s.HostFor(b.Name).Pid!.Value;
        s.Tick();
        Assert.Equal(pid, s.HostFor(b.Name).Pid);
    }

    [Fact]
    public void HostWithNoSessionsAboveThresholdIsRecycled()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = New(recycleMb: 1);
        s.Start(new[] { b });                                      // no request: zero sessions
        int pid = s.HostFor(b.Name).Pid!.Value;
        s.Tick();
        Assert.NotEqual(pid, s.HostFor(b.Name).Pid);
        Assert.Contains(s.Events, e => e.What.StartsWith("recycle: no sessions"));
        ReadOk(s, b.Name);
    }

    [Fact]
    public void DeadlineBecomesATimeoutError()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = New();
        s.Start(new[] { b });
        // 1 ms cannot fit a Connect; the host must answer Timeout rather than hang or crash.
        var r = s.Send(b.Name, Ops.Read, (JsonObject)ReadArgs.DeepClone(), deadlineMs: 1);
        Assert.False(r.Ok);
        Assert.Equal(Layers.Timeout, r.Error!.Layer);
        ReadOk(s, b.Name);                                          // and the host still serves
    }

    [Fact]
    public void CancelOfAnUnknownRequestIsANoOp()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        using var s = New();
        s.Start(new[] { b });
        var r = s.HostFor(b.Name).Send(Ops.Cancel, args: new JsonObject { ["targetId"] = 999_999 });
        Assert.True(r.Ok);
        Assert.False(r.Result!["cancelled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EdgeRefusesAForeignDocumentWith403AndNeedsTheToken()
    {
        if (!_f.Available || _f.Server is null) return;
        const string Doc = "ПоступлениеТоваровУслуг";
        string b = _f.Server.Name;
        string foreign = _f.Manager!.Use(b, ctx =>
        {
            using var scope = new ComScope();
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Dispatch.Set(q, "Текст",
                $"ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка ИЗ Документ.{Doc} ГДЕ НЕ Комментарий ПОДОБНО \"AIBA%\" УПОРЯДОЧИТЬ ПО Дата УБЫВ", ctx.Error);
            var res = scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res");
            var cur = scope.Track(Dispatch.Call(res, "Выбрать", ctx.Error), "cur");
            Assert.True(Dispatch.CallBool(cur, "Следующий", ctx.Error));
            return OneC.Host.OneCValue.RefGuid(scope.Track(Dispatch.Get(cur, "Ссылка", ctx.Error), "ref"), ctx)!;
        });

        using var s = New();
        s.Start(new[] { _f.Server });
        await using var edge = new EdgeServer(s, 0);                // OS-picked free port
        await edge.StartAsync();
        int port = edge.Port;

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var noToken = await http.PatchAsJsonAsync($"/v1/bases/{b}/documents/{Doc}/{foreign}",
            new JsonObject { ["fields"] = new JsonObject { ["Комментарий"] = "AIBA_X" } });
        Assert.Equal(401, (int)noToken.StatusCode);

        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        // D53 (2026-10-08) replaced D18's blanket refusal: a customer document changes only by
        // compare-and-set, never takes the AIBA marker, and is never deleted. Every refusal is
        // decided before anything is written.
        string Version() => _f.Manager!.Use(b, ctx =>
        {
            using var scope = new ComScope();
            var q = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "Запрос"), "Запрос");
            Dispatch.Set(q, "Текст", $"ВЫБРАТЬ ВерсияДанных ИЗ Документ.{Doc} ГДЕ Ссылка = &r", ctx.Error);
            var manager = scope.Track(Dispatch.Get(scope.Track(Dispatch.Get(ctx.Connection, "Документы", ctx.Error), "Документы"), Doc, ctx.Error), "mgr");
            var uuid = scope.Track(Dispatch.Call(ctx.Connection, "NewObject", ctx.Error, "УникальныйИдентификатор", foreign), "uuid");
            var r = scope.Track(Dispatch.Call(manager, "ПолучитьСсылку", ctx.Error, uuid), "ref");
            Dispatch.Call(q, "УстановитьПараметр", ctx.Error, "r", r);                // a procedure: returns nothing
            var cur = scope.Track(Dispatch.Call(scope.Track(Dispatch.Call(q, "Выполнить", ctx.Error), "res"), "Выбрать", ctx.Error), "cur");
            Assert.True(Dispatch.CallBool(cur, "Следующий", ctx.Error));
            return (string)Dispatch.Get(cur, "ВерсияДанных", ctx.Error)!;
        });
        string before = Version();
        var claim = await http.PatchAsJsonAsync($"/v1/bases/{b}/documents/{Doc}/{foreign}",
            new JsonObject { ["fields"] = new JsonObject { ["Комментарий"] = "AIBA_X" } });
        Assert.Equal(400, (int)claim.StatusCode);                                     // the AIBA marker on a customer document
        Assert.Contains("must not take an AIBA marker", await claim.Content.ReadAsStringAsync());
        var blind = await http.PatchAsJsonAsync($"/v1/bases/{b}/documents/{Doc}/{foreign}",
            new JsonObject { ["fields"] = new JsonObject { ["Комментарий"] = "blind update" } });
        Assert.Equal(422, (int)blind.StatusCode);                                     // no compare-and-set
        Assert.Contains("expected_state_required", await blind.Content.ReadAsStringAsync());
        var del = await http.DeleteAsync($"/v1/bases/{b}/documents/{Doc}/{foreign}");
        Assert.Equal(403, (int)del.StatusCode);
        Assert.Equal(before, Version());                                              // nothing was written
    }

    /// <summary>
    /// Backpressure (IPC_CONTRACT.md §6): a host with a 1-session budget accepts 4 requests in
    /// flight; the rest must be refused at once with a retryable Busy error, not queued.
    /// </summary>
    [Fact]
    public async Task OverloadedHostAnswersBusyInsteadOfQueueing()
    {
        if (!_f.Available || _f.Server is null) return;
        using var s = new OneC.Supervisor.Supervisor(new SupervisorOptions
        {
            HostExe = HostExe, RecycleIdleAboveMb = 0, MonitorInterval = TimeSpan.FromHours(1),
            HostGlobalMaxSessions = 1
        });
        s.Start(new[] { _f.Server });
        var big = new JsonObject
        { ["entity"] = "Справочник.Контрагенты", ["fields"] = new JsonArray("Наименование"), ["limit"] = 100000 };

        var sw = Stopwatch.StartNew();
        var all = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => s.SendAsync(_f.Server.Name, Ops.Read, (JsonObject)big.DeepClone())));
        var busy = all.Where(r => !r.Ok && r.Error!.Layer == Layers.Busy).ToList();

        Assert.True(busy.Count >= 1, "no request was refused as Busy");
        Assert.All(busy, r => { Assert.True(r.Error!.Retryable); Assert.True(r.ElapsedMs < 100, $"Busy took {r.ElapsedMs} ms"); });
        Assert.True(all.Count(r => r.Ok) <= 4 && all.Count(r => r.Ok) >= 1,
                    $"{all.Count(r => r.Ok)} succeeded; the host limit is 4 in flight");
        Assert.DoesNotContain(all, r => !r.Ok && r.Error!.Layer != Layers.Busy);
        ReadOk(s, _f.Server.Name);
    }

    /// <summary>
    /// Cancelling a request whose 1C read is running: the host stops between rows, answers
    /// Cancelled, and keeps serving (IPC_CONTRACT.md §5).
    ///
    /// The read must spend its time in the ROW LOOP, where cancellation acts — not inside one
    /// 1C call, which nothing can interrupt. GUID mode on 100 000 register rows does that
    /// (no ПРЕДСТАВЛЕНИЕ, so Выполнить is quick; each row costs several COM calls). With
    /// presentations the server does the heavy work inside Выполнить and a cancel can only
    /// land afterwards — that is the contract, and it made the previous version of this test
    /// time out (2026-09-24). When DispatchMemo made row reads ~2× cheaper, three columns no
    /// longer kept the row loop dominant; eight (five references, one XMLСтрока each) do.
    /// </summary>
    [Fact]
    public async Task CancellingARunningReadStopsItAndTheHostKeepsServing()
    {
        if (!_f.Available || _f.Server is null) return;
        using var s = New();
        s.Start(new[] { _f.Server });
        JsonObject Slow() => new()
        {
            ["entity"] = "РегистрБухгалтерии.Хозрасчетный",
            ["fields"] = new JsonArray("Период", "Сумма", "Регистратор", "СчетДт", "СчетКт", "Организация", "Содержание", "НомерСтроки"),
            ["limit"] = 100000,
            ["refs"] = "guid"
        };

        // Warm first: a cold first read measured 15.7 s and the next one 3.2 s (1C caches),
        // so timing the cold one put the cancel after the warm read had already finished.
        Assert.True((await s.SendAsync(_f.Server.Name, Ops.Read, Slow())).Ok);
        var full = Stopwatch.StartNew();
        var whole = await s.SendAsync(_f.Server.Name, Ops.Read, Slow());
        long fullMs = full.ElapsedMilliseconds;
        Assert.True(whole.Ok, whole.Error?.Message);
        Assert.True(fullMs >= 1000, $"read too quick ({fullMs} ms) to cancel mid-way");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(fullMs / 4));
        var sw = Stopwatch.StartNew();
        var r = await s.SendAsync(_f.Server.Name, Ops.Read, Slow(), ct: cts.Token);

        Assert.False(r.Ok, $"not cancelled; finished after {sw.ElapsedMilliseconds} ms (full read {fullMs} ms)");
        Assert.Equal(Layers.Cancelled, r.Error!.Layer);
        // A Cancelled answer (not OK) already proves the cancel landed before the read ended.
        // How early depends on how long the uninterruptible Выполнить took this time (varies
        // 1–2 s run to run), so only require that it beat the full read.
        Assert.True(sw.ElapsedMilliseconds < fullMs,
                    $"cancel at {fullMs / 4} ms landed at {sw.ElapsedMilliseconds} ms; full read {fullMs} ms");
        ReadOk(s, _f.Server.Name);
    }

    /// <summary>
    /// Deterministic cancellation: a request waiting for the only session is cancelled while it
    /// waits, answers Cancelled promptly, and the request holding the session is unaffected.
    /// </summary>
    [Fact]
    public async Task CancellingAWaitingRequestIsPromptAndLeavesOthersAlone()
    {
        if (!_f.Available || _f.Server is null) return;
        using var s = new OneC.Supervisor.Supervisor(new SupervisorOptions
        {
            HostExe = HostExe, RecycleIdleAboveMb = 0, MonitorInterval = TimeSpan.FromHours(1),
            HostGlobalMaxSessions = 1
        });
        s.Start(new[] { _f.Server });

        var holder = s.SendAsync(_f.Server.Name, Ops.Read, new JsonObject
        {
            ["entity"] = "РегистрБухгалтерии.Хозрасчетный",
            ["fields"] = new JsonArray("Период", "Сумма", "Регистратор"),
            ["limit"] = 20000
        });
        await Task.Delay(1000);                                        // holder owns the session

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        var waiting = await s.SendAsync(_f.Server.Name, Ops.Read, (JsonObject)ReadArgs.DeepClone(), ct: cts.Token);

        Assert.False(waiting.Ok);
        Assert.Equal(Layers.Cancelled, waiting.Error!.Layer);
        Assert.True(sw.ElapsedMilliseconds < 2500, $"waiting request took {sw.ElapsedMilliseconds} ms to cancel");

        var held = await holder;
        Assert.True(held.Ok, held.Error?.Message);                    // untouched by the other cancel
        ReadOk(s, _f.Server.Name);
    }

    [Fact]
    public void ShutdownLeavesNoOrphanHosts()
    {
        if (!_f.Available) return;
        var b = _f.Server ?? _f.File!;
        var s = New();
        s.Start(new[] { b });
        int pid = s.HostFor(b.Name).Pid!.Value;
        s.Dispose();
        Assert.True(Gone(pid, TimeSpan.FromSeconds(5)), $"host {pid} still in the process list");
    }

    /// <summary>
    /// Stopping a host while a 1C call runs must end the process for real. A normal exit runs
    /// every DLL's detach code; 1C's bundled ImageMagick (CORE_RL_magick_.dll) then spins one
    /// core forever when the thread killed by ExitProcess held its lock (2026-09-24: ten such
    /// test hosts, 83 % CPU). Such a process already has an exit code, so GetProcessById and
    /// TerminateProcess both treat it as gone — only the process list shows it.
    /// </summary>
    [Fact]
    public async Task StoppingABusyHostLeavesNoProcessBehind()
    {
        if (!_f.Available || _f.Server is null) return;
        var s = New();
        s.Start(new[] { _f.Server });
        int pid = s.HostFor(_f.Server.Name).Pid!.Value;
        var busy = s.SendAsync(_f.Server.Name, Ops.Read, new JsonObject
        {
            ["entity"] = "РегистрБухгалтерии.Хозрасчетный",
            ["fields"] = new JsonArray("Период", "Сумма", "Регистратор", "СчетДт", "СчетКт", "Организация", "Содержание", "НомерСтроки"),
            ["limit"] = 100000,
            ["refs"] = "guid"
        });
        await Task.Delay(2000);                                        // inside the read
        Assert.False(busy.IsCompleted, "read finished before the stop; nothing was busy");

        s.Dispose();
        Assert.True(Gone(pid, TimeSpan.FromSeconds(20)), $"host {pid} still in the process list after stop");
        Assert.False((await busy).Ok);
    }

    /// <summary>Absent from the process list — not just "has an exit code".</summary>
    private static bool Gone(int pid, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within)
        {
            if (!Process.GetProcesses().Any(p => p.Id == pid)) return true;
            Thread.Sleep(200);
        }
        return false;
    }
}
