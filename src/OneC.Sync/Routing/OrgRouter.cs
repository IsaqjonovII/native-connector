using OneC.Sync.Abstractions;
using OneC.Sync.Incremental;
using OneC.Sync.Mapping;
using OneC.Sync.Source;

namespace OneC.Sync.Routing;

/// <summary>The sync config and the local table plan disagree: sync must not start (§21).</summary>
public sealed class RoutingConfigException(string message) : Exception(message);

/// <summary>
/// Organisation routing (§21, D-4), a stage between the mapper and the batcher: reference rows
/// (catalogs, charts, independent registers) go to the shared partition; movement rows (documents,
/// recorded registers) go to the partition bound to their <c>orgRef</c>. A base without bindings is
/// single-org: everything is shared, as today. A movement row with no <c>orgRef</c> on a multi-org
/// base is a configuration error (never guessed into the shared partition); an organisation with no
/// binding yet returns null — the caller counts the row in <c>unmapped_orgs</c> and sends nothing,
/// and 1C still has it for when the binding appears.
/// </summary>
public sealed class OrgRouter : IPartitioner
{
    private readonly Dictionary<string, string> _byOrg = new(StringComparer.Ordinal);

    public OrgRouter(SyncConfig config)
    {
        Shared = config.SharedPartitionId;
        foreach (var p in config.Partitions)
            if (p.OrgRef is { Length: > 0 } org && Guid.TryParse(org, out var g)) _byOrg[g.ToString("D")] = p.PartitionId;
    }

    public string Shared { get; }
    public IReadOnlyList<string> All => _byOrg.Values.Prepend(Shared).Distinct(StringComparer.Ordinal).ToList();
    public bool MultiOrg => _byOrg.Count > 0;
    public IReadOnlyCollection<string> BoundOrgs => _byOrg.Keys;

    /// <summary>Independent-register rows with no organisation sent to the shared partition (a metric: no company reads them there).</summary>
    public long OrglessToShared => Interlocked.Read(ref _orgless);
    private long _orgless;

    public string? PartitionOf(TablePlan t, MappedRow row)
    {
        if (!t.IsMovement || !MultiOrg) return Shared;
        if (row.OrgRef is not { Length: > 0 } org || !Guid.TryParse(org, out var g))
        {
            // backend/1c files every InformationRegister_* as a movement table (onec_scope.py
            // _MOVEMENT_PREFIXES), independent ones without an organisation too (КурсыВалют):
            // those go where the old Connector put them, the shared partition, counted.
            if (t.Family == Families.IndependentInfoRegister) { Interlocked.Increment(ref _orgless); return Shared; }
            throw new RowMappingException(t.Table, $"movement row {row.Key} has no orgRef on a multi-organisation base");
        }
        return _byOrg.GetValueOrDefault(g.ToString("D"));
    }

    /// <summary>
    /// The engine's movement classification must match the backend's (v2 routes by its own
    /// <c>is_movement_table</c>); a table the backend does not list is refused too.
    /// </summary>
    public static void Validate(SyncConfig config, IEnumerable<TablePlan> tables)
    {
        var backend = config.Tables.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var t in tables)
        {
            if (!backend.TryGetValue(t.Table, out var b))
            {
                // No list stored for this connection: nothing to refuse against (the family rule below still holds).
                if (config.TableListStored) problems.Add($"{t.Table}: not in the backend's table list");
            }
            else if (b.IsMovement != t.IsMovement)
                problems.Add($"{t.Table}: backend says {(b.IsMovement ? "movement" : "reference")}, engine says {(t.IsMovement ? "movement" : "reference")}");
            // Documents and recorded registers are always movements, catalogs and charts never;
            // an independent information register is whatever the backend files it as.
            bool? movementFamily = t.Family switch
            {
                Families.IndependentInfoRegister => null,
                Families.Document => true,
                _ => Families.IsRecorded(t.Family)
            };
            if (movementFamily is { } m && t.IsMovement != m) problems.Add($"{t.Table}: family {t.Family} cannot be {(t.IsMovement ? "a movement" : "a reference")} table");
        }
        if (problems.Count > 0) throw new RoutingConfigException(string.Join("; ", problems));
    }

    /// <summary>Organisations that were counted as unmapped and now have a binding: their rows must be sent (verify, S11).</summary>
    public static IReadOnlyList<string> NewlyBound(IEnumerable<string> unmappedOrgs, OrgRouter now) =>
        unmappedOrgs.Where(o => Guid.TryParse(o, out var g) && now.BoundOrgs.Contains(g.ToString("D"))).Distinct().ToList();
}
