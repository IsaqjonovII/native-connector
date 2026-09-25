using System.Text;
using OneC.EventLog;
using OneC.Host;
using OneC.Sessions;
using Xunit;

namespace OneC.Tests;

/// <summary>
/// Milestone 5.6 — the event-log reader, without 1C. Fixtures are the connector Rust reader's
/// verbatim samples (eventlog.rs tests), so both readers are held to the same files.
/// </summary>
public class ChangeFeedUnitTests
{
    // Verbatim from D:\1C\TEST 1.3\1Cv8Log\1Cv8.lgf (2026-08-07), via eventlog.rs.
    private const string Lgf = "\uFEFF1CV8LOG(ver 2.0)\r\n0c868b83-4c4a-467d-ba71-1a35befabccf\r\n\r\n{1,071523a4-516f-4fce-ba4b-0d11ab7a1893,\"\",1},\r\n{2,\"win-11-2070\",1},\r\n{3,\"Designer\",1},\r\n{4,\"_$Session$_.Authentication\",1},\r\n{13,1,1},\r\n{4,\"_$Data$_.Update\",8},\r\n{5,bac41d45-249f-40b4-a013-008e261369c8,\"РегистрСведений.РезультатыОбменаДанными\",1},\r\n{5,c22c0ede-e73b-46c4-861b-f16528ce8bba,\"Справочник.uzbled_ОператорыЭДО\",2},\r\n{4,\"_$Data$_.New\",12},\r\n{4,\"_$Data$_.Post\",13},\r\n";

    // Verbatim record shapes from D:\1C\express\1Cv8Log\20260708000000.lgp, via eventlog.rs.
    private const string Lgp = "\uFEFF1CV8LOG(ver 2.0)\r\n6bab1462-2836-412d-9181-66f39fcd3cda\r\n\r\n{20260708102053,U,\r\n{245574232fb50,2c9},2,1,4,1,24,I,\"\",67,\r\n{\"R\",749:b4bab42e998ffa1711f17a8cbcb6677f},\"Поступление (акт, накладная) 0000-000019 dated 2026/07/08 12:00:00\",0,0,0,2,0,\r\n{2,1,1,2,1}\r\n},\r\n{20260708105211,R,\r\n{2455743513c90,170b},2,1,4,1,24,I,\"\",67,\r\n{\"R\",749:b4bab42e998ffa1711f17a9128679773},\"rolled back\",0,0,0,2,0,\r\n{2,1,1,2,1}\r\n},\r\n{20260807020232,N,\r\n{0,0},0,1,3,1,6,I,\"\",0,\r\n{\"P\",\r\n{6,\r\n{\"S\",\"Главный бухгалтер\"},\r\n{\"S\",\"WIN-11-2070\\\\ilxom\"}\r\n}\r\n},\"\",0,0,0,2,0,\r\n{0}\r\n},\r\n";

    [Fact]
    public void RefHexBecomesTheUuidProvenLive()
    {
        Assert.Equal("e69b7d56-4261-11f1-b46d-00117fbb4ed6", LogFormat.HexToUuid("b46d00117fbb4ed611f14261e69b7d56"));
        Assert.Null(LogFormat.HexToUuid("zz"));
        Assert.Null(LogFormat.HexToUuid(""));
    }

    [Fact]
    public void DictionaryHasTheInstanceGuidEventsAndMetadata()
    {
        var d = new LogDictionary();
        LogFormat.ParseDictionary(Lgf, d);
        Assert.Equal("0c868b83-4c4a-467d-ba71-1a35befabccf", d.InstanceGuid);
        Assert.Equal("_$Data$_.Update", d.Events[8]);
        Assert.Equal("_$Data$_.New", d.Events[12]);
        Assert.Equal("_$Data$_.Post", d.Events[13]);
        Assert.Equal("Справочник.uzbled_ОператорыЭДО", d.Metadata[2]);
    }

    [Fact]
    public void RecordsParseAndATruncatedTailWaits()
    {
        var (recs, consumed) = LogFormat.ParseRecords(Lgp);
        Assert.Equal(3, recs.Count);
        Assert.Equal(new LogRecord("2026-07-08T10:20:53", "U", 24, 67, "b4bab42e998ffa1711f17a8cbcb6677f", "245574232fb50,2c9"), recs[0]);
        Assert.Null(recs[2].TxId);                                              // {0,0}: outside a transaction
        Assert.Equal("R", recs[1].TxStatus);
        Assert.Null(recs[2].RefHex);
        Assert.Equal(Lgp.Length, consumed);

        var (part, used) = LogFormat.ParseRecords(Lgp[..(Lgp.Length - 40)]);
        Assert.Equal(2, part.Count);
        Assert.True(used <= Lgp.IndexOf("{20260807020232", StringComparison.Ordinal));
        Assert.Single(LogFormat.ParseRecords(Lgp[used..]).Records);

        var quoted = "{20260708102053,N,\r\n{0,0},1,1,1,1,5,E,\"he said \"\"hi{}\"\", ok\",0,\r\n{\"U\"},\"\",0,0,0,2,0,\r\n{0}\r\n},";
        Assert.Equal(5, LogFormat.ParseRecords(quoted).Records.Single().EventId);
    }

    [Fact]
    public void ReaderStartsAtTheTailFollowsAppendsAndSkipsRollbacks()
    {
        string dir = Directory.CreateTempSubdirectory("aiba-log-").FullName;
        try
        {
            string lgf = "\uFEFF1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n{4,\"_$Data$_.Update\",24},\r\n{5,1b2c,\"Документ.ПоступлениеТоваровУслуг\",67},\r\n";
            File.WriteAllText(Path.Combine(dir, "1Cv8.lgf"), lgf, new UTF8Encoding(false));
            string file = Path.Combine(dir, "20260708000000.lgp");
            File.WriteAllText(file, Lgp[..Lgp.IndexOf("{20260708102053", StringComparison.Ordinal)], new UTF8Encoding(false));

            var reader = new EventLogReader();
            var first = reader.Read(dir, null);
            Assert.Empty(first.Events);                                         // tail start: no history replay
            Assert.Equal("20260708000000.lgp", first.Cursor!.File);

            File.AppendAllText(file, Lgp[Lgp.IndexOf("{20260708102053", StringComparison.Ordinal)..], new UTF8Encoding(false));
            var next = reader.Read(dir, first.Cursor);
            var e = Assert.Single(next.Events);                                // the "R" record is skipped
            Assert.Equal(new ChangeEvent("2026-07-08T10:20:53", "Update", "Документ.ПоступлениеТоваровУслуг", "bcb6677f-7a8c-11f1-b4ba-b42e998ffa17"), e);
            Assert.False(next.Reset);
            Assert.Equal(new FileInfo(file).Length, next.Cursor!.Offset);

            // A second file: the reader moves on to it.
            File.WriteAllText(Path.Combine(dir, "20260709000000.lgp"), Lgp, new UTF8Encoding(false));
            var third = reader.Read(dir, next.Cursor);
            Assert.Single(third.Events);
            Assert.Equal("20260709000000.lgp", third.Cursor!.File);

            // The log is recreated: a new instance GUID means a reset, never a silent resume.
            File.WriteAllText(Path.Combine(dir, "1Cv8.lgf"), lgf.Replace("11111111", "99999999") + "{4,\"x\",1},{4,\"y\",2},{4,\"z\",3},", new UTF8Encoding(false));
            var reset = reader.Read(dir, third.Cursor);
            Assert.True(reset.Reset);
            Assert.Equal("log_recreated", reset.ResetReason);

            // A file shorter than the cursor (cleared the same day, same name) is a reset too.
            var cur = reader.Read(dir, reset.Cursor).Cursor!;
            File.WriteAllText(Path.Combine(dir, cur.File), "\uFEFF1CV8LOG(ver 2.0)\r\n", new UTF8Encoding(false));
            Assert.Equal("log_truncated", reader.Read(dir, cur).ResetReason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AReadIsCappedAndResumesExactlyWhereItStopped()
    {
        string dir = Directory.CreateTempSubdirectory("aiba-log-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "1Cv8.lgf"), "\uFEFF1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n{4,\"_$Data$_.Update\",24},\r\n{5,1b2c,\"Документ.X\",67},\r\n", new UTF8Encoding(false));
            string one = Lgp[Lgp.IndexOf("{20260708102053", StringComparison.Ordinal)..Lgp.IndexOf("{20260708105211", StringComparison.Ordinal)];
            string file = Path.Combine(dir, "20260708000000.lgp");
            File.WriteAllText(file, "\uFEFF1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n", new UTF8Encoding(false));
            var reader = new EventLogReader();
            var cursor = reader.Read(dir, null).Cursor;
            File.AppendAllText(file, string.Concat(Enumerable.Repeat(one, 50)), new UTF8Encoding(false));

            int total = 0, calls = 0;
            ChangeBatch b;
            do { b = reader.Read(dir, cursor, maxBytes: 1000); cursor = b.Cursor; total += b.Events.Count; calls++; } while (b.More);
            Assert.Equal(50, total);                                            // Cyrillic cut mid-character never loses a record
            Assert.True(calls > 5);
            Assert.Equal(new FileInfo(file).Length, cursor!.Offset);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ServerBaseDiscoveryPartsMatchTheConnector()
    {
        var r = LogLocator.ParseRagentCommandLine("\"C:\\Program Files\\1cv8\\8.3.15.1565\\bin\\ragent.exe\" -debug -srvc -agent -regport 1541 -port 1540 -range 1560:1591 -d \"C:\\Program Files\\1cv8\\srvinfo\"");
        Assert.Equal(new RagentInfo("C:\\Program Files\\1cv8\\srvinfo", 1541, "C:\\Program Files\\1cv8\\8.3.15.1565\\bin\\ragent.exe"), r);
        Assert.Null(LogLocator.ParseRagentCommandLine("ragent.exe -d x"));             // no -regport: never guess 1541

        var list = LogLocator.ParseClusterList("\uFEFF{2,\r\n{71990e64-b651-4a71-9b88-04242b40e214,\"KAN\",\"\",\"PostgreSQL\",\"localhost\",\"kan\",\"postgres\"},\r\n{c1a2cd0b-c87e-439a-ab92-1e3e43f33503,\"Kan\"\"stik\",\"d\",\"MSSQLServer\"\r\n");
        Assert.Equal(new[] { ("71990e64-b651-4a71-9b88-04242b40e214", "KAN"), ("c1a2cd0b-c87e-439a-ab92-1e3e43f33503", "Kan\"stik") },
                     list.Select(e => (e.Guid, e.Name)));

        Assert.True(LogLocator.IsLocalHost("WIN-11-2070:1541", "win-11-2070", null));
        Assert.True(LogLocator.IsLocalHost("[::1]:1541", "x", null));
        Assert.True(LogLocator.IsLocalHost("127.0.0.5", "x", null));
        Assert.False(LogLocator.IsLocalHost("win-11-2070.corp.local", "win-11-2070", null));    // no domain to verify: fail closed
        Assert.True(LogLocator.IsLocalHost("win-11-2070.corp.local", "win-11-2070", "corp.local"));
        Assert.False(LogLocator.IsLocalHost("other-host", "win-11-2070", null));

        var cs = LogLocator.ConnectionString("Srvr=\"WIN-11-2070\";Ref=\"KAN\";Usr=\"a\"\"b\";");
        Assert.Equal(("WIN-11-2070", "KAN", "a\"b"), (cs["srvr"], cs["ref"], cs["usr"]));
        Assert.Equal("D:\\1C\\bilim", LogLocator.ConnectionString("File=\"D:\\1C\\bilim\";Usr=x")["file"]);
    }
}

[Collection("onec-live")]
public class ChangeFeedLiveTests
{
    private readonly LiveFixture _f;
    public ChangeFeedLiveTests(LiveFixture f) => _f = f;

    /// <summary>
    /// The feed against 1C's own export of the same window, with a test-owned document written,
    /// posted (file base) and deleted inside it: the multisets must be equal, and the document's
    /// GUID must come out as New … Delete. Server base resolved through ragent + 1CV8Clst.lst.
    /// </summary>
    [Fact]
    public void FeedMatchesOnesOwnExportAndCarriesOurWrites()
    {
        if (!_f.Available) return;
        foreach (var b in new[] { _f.File, _f.Server }.OfType<OneCBase>())
            Assert.Equal(0, EventLogExport.Parity(_f.Manager!, b, waitSeconds: 3, write: true));
    }

    [Fact]
    public void ARolledBackWriteIsNotAChange()
    {
        if (!_f.Available || _f.File is null) return;
        Assert.Equal(0, EventLogExport.Parity(_f.Manager!, _f.File, waitSeconds: 3, rollback: true));
    }

    /// <summary>The feed through the supervisor's edge: tail cursor, a write, the write comes back.</summary>
    [Fact]
    public async Task ChangesRouteServesTheFeed()
    {
        if (!_f.Available || _f.File is null) return;
        using var s = new OneC.Supervisor.Supervisor(new OneC.Supervisor.SupervisorOptions
        {
            HostExe = Path.Combine(AppContext.BaseDirectory, "OneC.Host.exe"), MonitorInterval = TimeSpan.FromHours(1)
        });
        s.Start(new[] { _f.File });
        // Port 0: the old adapter's workers listen on 55901+ when it runs on this machine.
        await using var edge = new OneC.Supervisor.EdgeServer(s, 0);
        await edge.StartAsync();
        int port = edge.Port;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-AIBA-Token", edge.Token);
        string url = $"/v1/bases/{Uri.EscapeDataString(_f.File.Name)}/changes";

        var tail = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(url))!;
        Assert.Empty(tail["events"]!.AsArray());
        string cursor = tail["cursor"]!.GetValue<string>();

        var w = new WriteService(_f.Manager!, "AIBA_REWRITE_");
        var doc = w.CreateByClone(_f.File.Name, "ПоступлениеТоваровУслуг", $"AIBA_REWRITE_FEED_{DateTime.Now:MMddHHmmss}");
        w.Delete(_f.File.Name, "ПоступлениеТоваровУслуг", doc.Ref);

        var kinds = new List<string>();
        for (int i = 0; i < 20 && !kinds.Contains("Delete"); i++)
        {
            var page = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync($"{url}?cursor={Uri.EscapeDataString(cursor)}"))!;
            Assert.False(page["reset"]!.GetValue<bool>());
            kinds.AddRange(page["events"]!.AsArray().Where(e => e!["ref"]?.GetValue<string>() == doc.Ref).Select(e => e!["kind"]!.GetValue<string>()));
            cursor = page["cursor"]!.GetValue<string>();
            if (!kinds.Contains("Delete")) await Task.Delay(500);
        }
        Assert.Contains("New", kinds);
        Assert.Contains("Delete", kinds);

        Assert.Equal(400, (int)(await http.GetAsync($"{url}?cursor=garbage")).StatusCode);
        Assert.Equal(404, (int)(await http.GetAsync("/v1/bases/NoSuchBase/changes")).StatusCode);
    }

    [Fact]
    public void BothBasesLogDirectoriesResolve()
    {
        if (!_f.Available) return;
        var locator = new LogLocator();
        foreach (var b in new[] { _f.File, _f.Server }.OfType<OneCBase>())
        {
            var (dir, err) = locator.Resolve(b.ConnectionString);
            Assert.True(dir is not null, $"{b.Name}: {err}");
            Assert.True(File.Exists(Path.Combine(dir!, "1Cv8.lgf")));
        }
        Assert.Equal("not_local", locator.Resolve("Srvr=\"some-other-host\";Ref=\"KAN\"").Error);
        Assert.Equal("infobase_not_in_cluster", locator.Resolve($"Srvr=\"{Environment.MachineName}\";Ref=\"NoSuchBase{Guid.NewGuid():N}\"").Error);
    }
}
