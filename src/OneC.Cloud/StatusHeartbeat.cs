namespace OneC.Cloud;

/// <summary>
/// Keeps backend/1c's <c>status</c> field "active" for linked bases that are healthy here:
/// <c>PATCH /onec/connection/{id}</c> every 60 s (the backend demotes after 3 min without one),
/// "inactive" on a clean shutdown — the old Connector's connection-status.tsx rules.
///
/// Only records this app created. This is the legacy "active" signal; it does NOT make the base
/// look connected in the new cloud UIs or route writes here — that needs the connector WebSocket,
/// which is deliberately not built yet (spec §5.4, §9 q2–q3).
/// </summary>
public static class StatusHeartbeat
{
    public sealed record Beat(string BaseName, string OneCId, string Status, string? Error);

    public static async Task<IReadOnlyList<Beat>> BeatAsync(CloudClient cloud, IEnumerable<CloudLink> links,
                                                           Func<string, bool> healthy, CancellationToken ct = default)
    {
        var results = new List<Beat>();
        foreach (var l in links.Where(l => l.CreatedHere))
        {
            string status = healthy(l.BaseName) ? "active" : "inactive";
            try
            {
                await cloud.PatchStatusAsync(l.OneCId, status, ct);
                results.Add(new Beat(l.BaseName, l.OneCId, status, null));
            }
            catch (CloudException e) { results.Add(new Beat(l.BaseName, l.OneCId, status, e.Message)); }
            catch (HttpRequestException e) { results.Add(new Beat(l.BaseName, l.OneCId, status, e.Message)); }
        }
        return results;
    }
}
