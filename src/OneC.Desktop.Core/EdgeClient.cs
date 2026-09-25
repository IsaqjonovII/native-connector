using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace OneC.Desktop.Services;

/// <summary>A failed edge call, carrying the contract's error object (IPC_CONTRACT.md §4).</summary>
public sealed class EdgeException : Exception
{
    public int Status { get; }
    public string Layer { get; }
    public JsonObject? Error { get; }

    public EdgeException(int status, string layer, string message, JsonObject? error) : base(message)
    {
        Status = status; Layer = layer; Error = error;
    }

    /// <summary>auth / license / path / version / permission / network / unknown.</summary>
    public string Category => Error?["category"]?.GetValue<string>() ?? "unknown";

    /// <summary>What an accountant should read — the 1C text when there is one.</summary>
    public string Friendly => (Layer, Category) switch
    {
        (_, "auth") => "Wrong 1C user name or password. " + Message,
        (_, "path") => "The 1C database was not found. " + Message,
        (_, "license") => "1C has no free license for this connection. " + Message,
        (_, "version") => "The 1C version does not match this database. " + Message,
        (_, "permission") => "This 1C user is not allowed to connect. " + Message,
        ("Connector", "network") => "The 1C server cannot be reached. " + Message,
        ("Connector", _) => "1C refused the connection: " + Message,
        ("Runtime", _) => "1C reported an error: " + WithoutQueryEcho(Message),
        ("Validation", _) => "The request is not valid: " + Message,
        ("Timeout", _) => "1C did not answer in time.",
        ("Busy", _) => "The connector is busy right now. Try again in a moment.",
        ("Transport", _) => "The 1C host is restarting. Try again in a moment.",
        _ => Message
    };

    /// <summary>
    /// 1C query errors read "{(1, 165)}: Field not found "X" ВЫБРАТЬ … &lt;&lt;?&gt;&gt; …" — the
    /// position prefix and the echo of our generated query mean nothing to a user. Keep the
    /// sentence in between. The full text stays in <see cref="Exception.Message"/>.
    /// </summary>
    public static string WithoutQueryEcho(string m)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(m, @"^\{\(\d+,\s*\d+\)\}:\s*", "");
        foreach (var marker in new[] { " ВЫБРАТЬ ", " SELECT " })
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i > 0) { s = s[..i]; break; }
        }
        return s.Trim();
    }
}

/// <summary>Typed calls to the supervisor's HTTP edge (IPC_CONTRACT.md §7).</summary>
public sealed class EdgeClient : IDisposable
{
    private readonly HttpClient _http;

    public EdgeClient(int port, string token)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(11) };
        _http.DefaultRequestHeaders.Add("X-AIBA-Token", token);
    }

    public Task<JsonArray> Hosts(CancellationToken ct = default) => GetArray("/v1/hosts", ct);
    public Task<JsonArray> Bases(CancellationToken ct = default) => GetArray("/v1/bases", ct);
    public Task<JsonArray> Events(int limit = 200, CancellationToken ct = default) => GetArray($"/v1/events?limit={limit}", ct);
    public Task<JsonArray> Activity(int limit = 200, CancellationToken ct = default) => GetArray($"/v1/activity?limit={limit}", ct);
    public Task<JsonObject> Supervisor(CancellationToken ct = default) => GetObject("/v1/supervisor", ct);

    public Task<JsonObject> Version(string baseName, CancellationToken ct = default) =>
        GetObject($"/v1/bases/{Uri.EscapeDataString(baseName)}/version", ct);

    /// <summary>Connects and probes metadata: configuration name, synonym, versions.</summary>
    public Task<JsonObject> Test(string baseName, CancellationToken ct = default) =>
        GetObject($"/v1/bases/{Uri.EscapeDataString(baseName)}/test", ct);

    public async Task<JsonObject> Read(string baseName, string entity, IEnumerable<string> fields, int limit,
                                       string refs, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["entity"] = entity,
            ["fields"] = new JsonArray(fields.Select(f => (JsonNode?)f).ToArray()),
            ["limit"] = limit,
            ["refs"] = refs
        };
        using var r = await _http.PostAsJsonAsync($"/v1/bases/{Uri.EscapeDataString(baseName)}/read", body, ct);
        return await Unwrap<JsonObject>(r, ct);
    }

    private async Task<JsonArray> GetArray(string path, CancellationToken ct)
    {
        using var r = await _http.GetAsync(path, ct);
        return await Unwrap<JsonArray>(r, ct);
    }

    private async Task<JsonObject> GetObject(string path, CancellationToken ct)
    {
        using var r = await _http.GetAsync(path, ct);
        return await Unwrap<JsonObject>(r, ct);
    }

    private static async Task<T> Unwrap<T>(HttpResponseMessage r, CancellationToken ct) where T : JsonNode
    {
        var node = await JsonNode.ParseAsync(await r.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (r.IsSuccessStatusCode && node is T ok) return ok;

        var err = node?["error"] as JsonObject;
        throw new EdgeException((int)r.StatusCode,
                                err?["layer"]?.GetValue<string>() ?? "Edge",
                                err?["message"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}",
                                err);
    }

    public void Dispose() => _http.Dispose();
}
