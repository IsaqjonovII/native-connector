using System.Text.Json.Nodes;
using OneC.EventLog;

namespace OneC.Sync;

/// <summary>
/// The reads the sync needs, in the host's row shapes (D33/D34) — implemented by the supervisor
/// over its pipe ops and change feed, and by a fake in tests.
/// </summary>
public interface IOneCSource
{
    /// <summary>Rows in 1C (limit 0 read). Catalogs and registers: all; documents: from <see cref="SyncTable.From"/>.</summary>
    Task<long> CountAsync(string baseName, SyncTable table, CancellationToken ct);

    /// <summary>Catalog keyset page after <paramref name="after"/> (zero GUID = first page).</summary>
    Task<List<JsonObject>> CatalogPageAsync(string baseName, string catalog, string after, int limit, CancellationToken ct);

    /// <summary>One catalog element with its tabular sections; null when it does not exist.</summary>
    Task<JsonObject?> CatalogByIdAsync(string baseName, string catalog, string id, CancellationToken ct);

    /// <summary>Documents in date order from the cursor: rows and the cursor for the next page.</summary>
    Task<(List<JsonObject> Rows, string? NextCursorDate, int NextSkip)> DocumentPageAsync(
        string baseName, string document, string? cursorDate, int skip, int limit, DateTime? from, CancellationToken ct);

    /// <summary>Documents by id, every tabular section; missing ids are absent.</summary>
    Task<List<JsonObject>> DocumentsByIdsAsync(string baseName, string document, IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>Register rows in period order from the cursor.</summary>
    Task<(List<JsonObject> Rows, string? NextCursorDate, int NextSkip, bool HasMore)> RegisterPageAsync(
        string baseName, SyncTable register, string? cursorDate, int skip, int limit, CancellationToken ct);

    /// <summary>One document's movements in a register.</summary>
    Task<List<JsonObject>> RegisterByRecorderAsync(string baseName, SyncTable register, string recorderDocument, string recorderId, CancellationToken ct);

    /// <summary>The change feed (D36): data events after the cursor; no cursor = start at the tail.</summary>
    Task<ChangeBatch> ChangesAsync(string baseName, LogCursor? cursor, CancellationToken ct);
}
