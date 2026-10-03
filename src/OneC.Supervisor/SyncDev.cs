using System.Text.Json.Nodes;
using OneC.Cloud;

namespace OneC.Supervisor;

/// <summary>
/// <c>OneC.Supervisor sync-dev …</c> — the S12 shared-dev helpers (DEV_SYNC_TEST_RUNBOOK.md). All use
/// the app's Development session in <c>--session</c>; none takes a URL.
///   companies     --session D                                   read-only: the user's dev companies
///   check         --session D --record ID                       read-only: the record is test-owned, offline, its bindings and tables
///   create-record --session D --company ID --base NAME [--provider unisoft|venkon]
///                                                               the ONE write: a test-owned record, named SYNC-TEST …
/// </summary>
internal static class SyncDev
{
    public static async Task<int> Run(string[] argv, Func<string, string?> arg)
    {
        string sub = argv.Length > 1 ? argv[1] : "";
        string session = arg("session") ?? throw new ArgumentException("--session (the app's data folder with the Development sign-in) is required");
        var cloud = new CloudClient(CloudEnvironment.Dev, new SessionStore(session), DeviceId.Get());
        if (!cloud.SignedIn) { Console.Error.WriteLine($"no Development session in {session}: sign in to AIBA (Development) in the app first"); return 1; }
        switch (sub)
        {
            case "companies":
                foreach (var c in await cloud.CompaniesAsync())
                    Console.WriteLine($"{c.Id}  {c.Name}  INN {c.Inn}{(c.IsDefault ? "  (default)" : "")}");
                return 0;

            case "check":
            {
                string id = arg("record") ?? throw new ArgumentException("--record ID");
                using var http = new HttpClient { BaseAddress = DevBackend.Root, Timeout = TimeSpan.FromMinutes(1) };
                var rec = await DevBackend.GuardAsync(http, cloud, id);
                var bindings = await DevBackend.GetJsonAsync(http, cloud, $"api/v2/onec/{Uri.EscapeDataString(id)}/org-bindings");
                var tables = await DevBackend.GetJsonAsync(http, cloud, $"api/v1/onec/{Uri.EscapeDataString(id)}/sync-tables");
                Console.WriteLine($"OK: {id} \"{rec["name"]}\" odataName \"{rec["odataName"]}\" company {rec["companyId"]} connection_state {rec["connection_state"]}");
                Console.WriteLine($"org bindings: {(bindings["bindings"] as JsonArray)?.Count ?? 0}; sync-tables: {(tables["tables"] as JsonArray)?.Count ?? 0}");
                foreach (var b in (bindings["bindings"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    Console.WriteLine($"  binding {b["id"]} orgRef {b["orgRef"]} company {b["companyId"]} status {b["status"]}");
                return 0;
            }

            case "create-record":
            {
                string company = arg("company") ?? throw new ArgumentException("--company ID (the dedicated dev test company, from `companies`)");
                string baseName = arg("base") ?? throw new ArgumentException("--base NAME (the local test base, e.g. bilim)");
                if (!(await cloud.CompaniesAsync()).Any(c => c.Id == company)) { Console.Error.WriteLine($"company {company} is not one of this user's dev companies"); return 1; }
                // name and odataName both test-marked: the old Connector matches records to its local bases by
                // odataName, so no old Connector ever picks this one up (D-1).
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
                var rec = await cloud.OneCCreateAsync(company, $"{DevBackend.TestPrefix} {baseName} {stamp}", $"{DevBackend.TestPrefix}-{baseName}-{stamp}",
                                                      arg("provider") ?? "unisoft", null);
                Console.WriteLine($"created test record {rec.Id}  \"{rec.Name}\"  odataName \"{rec.OdataName}\"  company {company}");
                Console.WriteLine("put this id into sync.json as the base's connectionId");
                return 0;
            }

            default:
                Console.Error.WriteLine("sync-dev companies|check|create-record --session D …");
                return 1;
        }
    }
}
