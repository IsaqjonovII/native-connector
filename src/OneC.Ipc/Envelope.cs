using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OneC.Ipc;

/// <summary>Operation names, IPC_CONTRACT.md §3.</summary>
public static class Ops
{
    public const string Health = "health";
    public const string Stats = "stats";
    public const string Version = "version";
    public const string Test = "test";
    public const string Read = "read";
    public const string Catalog = "catalog";
    public const string Document = "document";
    public const string Register = "register";
    public const string Slices = "slices";
    /// <summary>Sync:(id, ВерсияДанных) of a catalog/document table — a keyset page or a list of ids.</summary>
    public const string Versions = "versions";
    /// <summary>Sync:chart of accounts rows, the old <c>/api/charts/{name}</c> shape.</summary>
    public const string Chart = "chart";
    /// <summary>Sync:document types that write movements to a register.</summary>
    public const string Recorders = "recorders";
    /// <summary>Sync:which document type a recorder GUID belongs to, among registers' recorder types.</summary>
    public const string RecorderOf = "recorderOf";
    /// <summary>Sync:every catalog, document, chart of accounts and register of the configuration.</summary>
    public const string Tables = "tables";
    public const string Create = "create";
    public const string Update = "update";
    public const string Post = "post";
    public const string Unpost = "unpost";
    public const string MarkDeleted = "markDeleted";
    public const string Delete = "delete";
    /// <summary>R9: a catalog item AIBA owns (Комментарий marker), idempotent by marker.</summary>
    public const string CatalogCreate = "catalogCreate";
    /// <summary>R9: compare-and-set update of an item AIBA owns: <c>catalog, ref, expected{}, set{}</c>.</summary>
    public const string CatalogUpdate = "catalogUpdate";
    /// <summary>Owned item delete — local edge only (test cleanup), never a cloud command.</summary>
    public const string CatalogDelete = "catalogDelete";
    public const string FindOwned = "findOwned";
    public const string Sweep = "sweep";
    public const string Cancel = "cancel";
}

/// <summary>Error layers, IPC_CONTRACT.md §4. The first five mirror OneCLayer.</summary>
public static class Layers
{
    public const string Dispatch = "Dispatch";
    public const string Connector = "Connector";
    public const string Runtime = "Runtime";
    public const string Host = "Host";
    public const string Unknown = "Unknown";
    public const string Validation = "Validation";
    public const string Timeout = "Timeout";
    public const string Cancelled = "Cancelled";
    public const string Busy = "Busy";
    public const string Transport = "Transport";
}

public sealed record IpcRequest
{
    public int V { get; init; } = 1;
    public long Id { get; init; }
    public required string Op { get; init; }
    public string? Base { get; init; }
    public int? DeadlineMs { get; init; }
    public JsonObject? Args { get; init; }

    public const int DefaultDeadlineMs = 60_000;
    public const int MaxDeadlineMs = 600_000;

    public int EffectiveDeadlineMs =>
        Math.Clamp(DeadlineMs ?? DefaultDeadlineMs, 1, MaxDeadlineMs);
}

public sealed record IpcError
{
    public required string Layer { get; init; }
    public required string Message { get; init; }
    public string? Hr { get; init; }
    public int OneCCode { get; init; }
    public string? Scode { get; init; }
    public string? Source { get; init; }
    public string? Op { get; init; }
    public string? Member { get; init; }
    public string? Base { get; init; }
    public string? PlatformVersion { get; init; }
    public bool Retryable { get; init; }
    public bool SessionFatal { get; init; }
    public bool HostFatal { get; init; }

    /// <summary>auth / license / path / version / permission / network / unknown — the old adapter's codes.</summary>
    public string? Category { get; init; }

    /// <summary>Set for refusals the HTTP edge maps to 403 / 404 / 422 (IPC_CONTRACT.md §7).</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// With <see cref="ErrorKinds.NotFound"/>: <c>object</c> (the table exists, this GUID does not —
    /// the only not-found a sync may turn into a delete) or <c>metadata</c> (no such table / base).
    /// Sync §10: "definitely not found" vs "unknown" (fixes P6).
    /// </summary>
    public string? NotFoundScope { get; init; }

    /// <summary>Structured details of a refused write: <c>fillDiagnostics</c>, the document name, … (D38).</summary>
    public System.Text.Json.Nodes.JsonNode? Data { get; init; }

    public static IpcError Of(string layer, string message, bool retryable = false, string? kind = null) =>
        new() { Layer = layer, Message = message, Retryable = retryable, Kind = kind };
}

public static class ErrorKinds
{
    public const string NotFound = "notFound";
    public const string Forbidden = "forbidden";

    /// <summary>The request was understood but cannot be carried out as given (an unresolved reference): 422.</summary>
    public const string Unprocessable = "unprocessable";
}

public sealed record IpcResponse
{
    public int V { get; init; } = 1;
    public long Id { get; init; }
    public bool Ok { get; init; }
    public long ElapsedMs { get; init; }
    public JsonNode? Result { get; init; }
    public IpcError? Error { get; init; }

    public static IpcResponse Success(long id, JsonNode? result, long ms) =>
        new() { Id = id, Ok = true, Result = result, ElapsedMs = ms };

    public static IpcResponse Failure(long id, IpcError error, long ms) =>
        new() { Id = id, Ok = false, Error = error, ElapsedMs = ms };
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new JsonException($"null {typeof(T).Name}");

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
}

/// <summary>Pipe naming, IPC_CONTRACT.md §1.</summary>
public static class PipeNames
{
    /// <summary>
    /// <c>aiba-onec-&lt;hostKey&gt;-&lt;supervisorPid&gt;-&lt;nonce&gt;</c>. The nonce makes every
    /// host instance's pipe unique: a restarted or recycled host, or a second supervisor in the
    /// same process (the test suite), can never connect to a predecessor that is still exiting.
    /// </summary>
    public static string For(string hostKey, int supervisorPid, string? nonce = null)
    {
        foreach (char c in hostKey)
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_'))
                throw new ArgumentException($"host key '{hostKey}' is not pipe-safe", nameof(hostKey));
        nonce ??= Guid.NewGuid().ToString("N")[..8];
        return $"aiba-onec-{hostKey}-{supervisorPid}-{nonce}";
    }
}
