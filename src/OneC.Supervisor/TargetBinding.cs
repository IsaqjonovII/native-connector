using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneC.Supervisor;

/// <summary>
/// R10 (PYTHON_TO_RUST_MIGRATION_PLAN): which backend a base syncs into. One backend per base — never
/// Python and Rust for the same base — and a change of backend only on an explicit switch.
/// <list type="bullet">
/// <item>The binding lives in <c>sync-targets.json</c> next to the sync state: per base, the ACTIVE backend
/// (target + connection + the local sync-state file it uses) and the PREVIOUS one.</item>
/// <item>A run whose target differs from the active binding is refused unless it says <c>"switch": true</c>.</item>
/// <item>Each backend keeps its own local sync state (its own <c>db</c> file): a switch to a backend never
/// fed makes a full first copy there (with coverage "loading" first); a switch BACK (rollback) resumes that
/// backend's own cursor and catches up everything that changed in 1C meanwhile.</item>
/// </list>
/// </summary>
public static class TargetBinding
{
    public sealed record Binding(string Identity, string Db, DateTimeOffset Since);

    public sealed class RefusedException(string message) : Exception(message);

    public static string FileFor(string dbPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "sync-targets.json");

    public static string Identity(string targetSpec, string connectionId) => $"{targetSpec}|{connectionId}";

    /// <summary>
    /// Checks and records the binding of <paramref name="baseName"/> for this run. Returns what happened
    /// ("bound", "same", "switched from …", "rolled back to …"); throws <see cref="RefusedException"/> when the
    /// run would feed a second backend without an explicit switch, or would reuse another backend's state.
    /// </summary>
    public static string Apply(string file, string baseName, string identity, string dbPath, bool explicitSwitch, DateTimeOffset now)
    {
        var all = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject();
        var entry = all[baseName] as JsonObject;
        string db = Path.GetFullPath(dbPath);
        static Binding? Read(JsonNode? n) => n is JsonObject o
            ? new Binding((string)o["identity"]!, (string)o["db"]!, DateTimeOffset.Parse((string)o["since"]!, System.Globalization.CultureInfo.InvariantCulture))
            : null;
        static JsonObject Write(Binding b) => new() { ["identity"] = b.Identity, ["db"] = b.Db, ["since"] = b.Since.ToString("o") };

        var active = Read(entry?["active"]);
        var previous = Read(entry?["previous"]);
        string outcome;
        if (active is null) outcome = "bound";
        else if (active.Identity == identity)
        {
            if (!string.Equals(active.Db, db, StringComparison.OrdinalIgnoreCase))
                throw new RefusedException($"{baseName}: bound to {identity} with its sync state in {active.Db}; this run names {db} — one backend, one state");
            return "same";
        }
        else
        {
            if (!explicitSwitch)
                throw new RefusedException($"{baseName} syncs into {active.Identity} (since {active.Since:yyyy-MM-dd HH:mm}); " +
                                           $"this run names {identity}. One backend per base: moving it needs \"switch\": true (R10).");
            if (string.Equals(active.Db, db, StringComparison.OrdinalIgnoreCase))
                throw new RefusedException($"{baseName}: {db} holds the sync state of {active.Identity}; the new backend needs its own \"db\"");
            outcome = previous?.Identity == identity ? $"rolled back from {active.Identity}" : $"switched from {active.Identity}";
            if (previous?.Identity == identity && !string.Equals(previous.Db, db, StringComparison.OrdinalIgnoreCase))
                throw new RefusedException($"{baseName}: rolling back to {identity} must reuse its own state {previous.Db}");
        }
        var next = new JsonObject { ["active"] = Write(new Binding(identity, db, now)) };
        if (active is not null && active.Identity != identity) next["previous"] = Write(active);
        else if (previous is not null) next["previous"] = Write(previous);
        all[baseName] = next;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
        return outcome;
    }
}
