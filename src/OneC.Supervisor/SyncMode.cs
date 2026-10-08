using System.Globalization;
using System.Text.Json.Nodes;
using OneC.EventLog;
using OneC.Sessions;
using OneC.Sync.Abstractions;
using OneC.Sync.Stub;
using OneC.SyncState;
using OneC.Sync.Engine;
using OneC.Sync.Source;

namespace OneC.Supervisor;

/// <summary>
/// <c>run --sync-config f.json</c>: the sync engine inside the Supervisor, controlled through
/// <c>/v1/sync</c>. Targets allowed here (D-2): the in-memory stub, or a backend on a loopback
/// address (the isolated local instance of S12). A shared dev backend needs the developer's go and
/// the signed-in user's token, and is not wired here.
/// <code>
/// { "db": null | "path\\sync.db", "target": "stub" | "http://127.0.0.1:18041", "secrets": "…json (local target)",
///   "bases": [ { "name": "kansler", "connectionId": "…",
///                "tables": [ { "table": "Catalog_Банки", "name": "Банки", "family": "catalog", "isMovement": false,
///                              "from": "2025-01-01", "refreshEveryMinutes": 60 } ] } ] }
/// </code>
/// </summary>
internal static class SyncMode
{
    public static async Task<(SyncEngineHost Host, IDisposable Db)> StartAsync(Supervisor sup, IReadOnlyList<OneCBase> bases, string configPath)
    {
        var cfg = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        string dbPath = (string?)cfg["db"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                           "AIBA", "Connector", "sync", "sync.db");
        string targetSpec = (string?)cfg["target"] ?? "stub";
        // R10: one backend per base, a change only on an explicit switch, each backend with its own state.
        if (targetSpec != "stub")
            foreach (var b in cfg["bases"]!.AsArray().OfType<JsonObject>())
            {
                string name = (string)b["name"]!;
                string outcome = TargetBinding.Apply(TargetBinding.FileFor(dbPath), name,
                    TargetBinding.Identity(targetSpec, (string?)b["connectionId"] ?? name), dbPath, (bool?)cfg["switch"] == true, DateTimeOffset.UtcNow);
                Console.Error.WriteLine($"sync: {name} → {targetSpec} ({outcome})");
            }
        var db = SyncDb.Open(dbPath);
        IBackendSyncTarget target;
        Http = new HttpFaults { Armable = (bool?)cfg["faults"] == true && targetSpec != "stub" };
        if (targetSpec == "stub") target = new StubSyncTarget(StubMode.V2) { KeepJson = false };
        else if (targetSpec == "dev")
        {
            // S12, approved for DEVELOPMENT only: every base must name its own test 1C record.
            if (cfg["bases"]!.AsArray().Count != 1) throw new ArgumentException("sync dev target:exactly one base (one test 1C record) per run");
            var first = cfg["bases"]!.AsArray().OfType<JsonObject>().Select(b => (string?)b["connectionId"]).FirstOrDefault();
            if (first is not { Length: 24 }) throw new ArgumentException("sync dev target:each base needs \"connectionId\" = its test 1C record id on dev");
            // Developer switches that must never reach the shared server.
            if ((bool?)cfg["allowWithOldConnector"] == true) throw new ArgumentException("sync dev target:allowWithOldConnector is refused on the shared dev backend (D-1)");
            var dev = DevBackend.Connect((string?)cfg["session"] ?? throw new ArgumentException("sync dev target:\"session\" (the app's data folder) missing"),
                                         first, Http);
            await DevBackend.GuardAsync(dev.Http, dev.Cloud, first);        // test-owned record, no live connector — or no start
            target = dev.Target;
            RebuildRefusal = (bool?)cfg["allowRebuild"] == true ? null
                : "copying a table again deletes its rows on the shared dev backend: disabled for S12 unless the developer sets allowRebuild";
        }
        else if (targetSpec.StartsWith(RustBackend.Scheme, StringComparison.Ordinal))
        {
            // R3: the Rust onec module, local instance only; one Rust connection per base (its connectionId).
            if (cfg["bases"]!.AsArray().Count != 1) throw new ArgumentException("sync rust target: exactly one base per run");
            string conn = (string?)cfg["bases"]![0]!["connectionId"] ?? throw new ArgumentException("sync rust target: the base needs \"connectionId\" = its Rust connection id");
            var rust = RustBackend.Connect(targetSpec, (string?)cfg["secrets"] ?? throw new ArgumentException("sync rust target: \"secrets\" missing"), conn).Target;
            target = rust;
            // R9: normal writes from the cloud — off unless this run says so (writes are never implied by sync).
            if ((bool?)cfg["commands"] == true)
            {
                Commands = new CommandRunner(sup, rust, (string)cfg["bases"]![0]!["name"]!);
                Commands.Start();
            }
        }
        else
        {
            if (!new Uri(targetSpec).IsLoopback) throw new ArgumentException("sync target must be \"stub\", \"dev\", \"rust:<loopback URL>\" or a loopback URL (D-2: never production)");
            target = (await LocalBackend.ConnectAsync(targetSpec, (string)cfg["secrets"]!, "sync",
                                                      (string?)cfg["bases"]?[0]?["connectionId"])).Target;
        }

        var locator = new LogLocator();
        var plans = new List<BasePlan>();
        foreach (var b in cfg["bases"]!.AsArray().OfType<JsonObject>())
        {
            string name = (string)b["name"]!;
            var ob = bases.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException($"sync: base {name} is not in the base list");
            var (dir, _) = locator.Resolve(ob.ConnectionString);
            plans.Add(new BasePlan(name, ob.IsFile, dir, (string?)b["connectionId"] ?? name, Tables(b)));
        }
        if (Http.Armable)
            db.FaultHook = point =>
            {
                // S12 failure test "restart after upload, before local completion": the target has the
                // rows, the work item is still pending — the process dies right here.
                if (point == "complete" && Interlocked.Exchange(ref _crashOnComplete, 0) == 1)
                {
                    Console.Error.WriteLine("sync: injected crash after upload, before local completion (S12 failure test)");
                    Environment.Exit(97);
                }
            };
        var host = new SyncEngineHost(db, target, new SupervisorReader(sup), plans,
                                      options: new EngineOptions { AllowWithOldConnector = (bool?)cfg["allowWithOldConnector"] ?? false })
        { RebuildRefusal = RebuildRefusal };
        host.Start();
        return (host, db);
    }

    /// <summary>
    /// A base's tables from sync.json, as the engine must run them:
    ///  - the table name is the backend's (<see cref="BackendTableNames"/>: <c>…Хозрасчетный_RecordType</c>),
    ///    so rows join the old Connector's table instead of starting a second one;
    ///  - movement = the backend's own rule for the family (documents and every register; catalogs and
    ///    charts never): backend/1c files an independent information register as a movement table
    ///    too, and routing must match it (<c>OrgRouter.Validate</c>) — a config saying otherwise is overridden;
    ///  - one plan per table (a table listed twice under both spellings is one table).
    /// </summary>
    internal static List<TablePlan> Tables(JsonObject b) =>
        b["tables"]!.AsArray().OfType<JsonObject>().Select(t =>
        {
            string family = (string)t["family"]!;
            return new TablePlan(BackendTableNames.Of((string)t["table"]!), (string)t["name"]!, family,
                family is not (Families.Catalog or Families.Chart),
                t["from"] is JsonValue f ? DateTime.Parse(f.GetValue<string>(), CultureInfo.InvariantCulture) : null,
                t["refreshEveryMinutes"] is JsonValue m ? TimeSpan.FromMinutes(m.GetValue<double>()) : null);
        }).DistinctBy(t => t.Table, StringComparer.Ordinal).ToList();

    /// <summary>The command runner of a Rust target with <c>"commands": true</c>; null otherwise.</summary>
    public static CommandRunner? Commands { get; private set; }

    /// <summary>The sync target's HTTP calls (timings, sizes) and the S12 fault switches.</summary>
    public static HttpFaults Http { get; private set; } = new();

    private static int _crashOnComplete;

    /// <summary>Set on the shared dev target: why a rebuild (which deletes cloud rows) is refused there.</summary>
    private static string? RebuildRefusal { get; set; }

    /// <summary>Arms "exit at the next work-item completion" (S12 failure test; only with faults enabled).</summary>
    public static void ArmCrashOnComplete()
    {
        if (!Http.Armable) throw new InvalidOperationException("faults are not enabled for this target");
        Interlocked.Exchange(ref _crashOnComplete, 1);
    }
}
