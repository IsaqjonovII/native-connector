using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OneC.Sync.Targets.Python;

namespace OneC.Supervisor;

/// <summary>
/// S12 measurement helper: a connection on the ISOLATED LOCAL backend/1c started for the sync
/// tests (loopback only, its own database), with a test token minted from that instance's own test
/// secret. Not a production path: the desktop signs in and passes the user's token.
/// </summary>
internal static class LocalBackend
{
    private const string UserId = "5f0c2a8e-1c1e-4d7b-9a55-5ab1c0de0001";

    private sealed class StaticToken(string t) : ITokenSource
    {
        public Task<string> GetAsync(CancellationToken ct) => Task.FromResult(t);
        public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
    }

    /// <param name="connectionId">An existing record on that instance to sync into (R10 keeps one per base); null = create a new one.</param>
    public static async Task<(PythonMongoSyncTarget Target, string ConnectionId)> ConnectAsync(string url, string secretsFile, string baseName,
                                                                                               string? connectionId = null)
    {
        var s = JsonNode.Parse(await File.ReadAllTextAsync(secretsFile))!;
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        if (connectionId is { Length: 24 })
            return (new PythonMongoSyncTarget(http, new StaticToken(Mint((string)s["jwt"]!)), connectionId), connectionId);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/onec")
        {
            Content = JsonContent.Create(new
            {
                userId = UserId, provider = "unisoft", odataName = $"{baseName}-{Guid.NewGuid():N}"[..20],
                companyId = Guid.NewGuid().ToString(), name = "Sync bench " + baseName
            })
        };
        req.Headers.Add("X-Service-Secret", (string)s["service"]!);
        var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"create connection: {(int)resp.StatusCode} {text}");
        string id = (string)JsonNode.Parse(text)!["id"]!;
        return (new PythonMongoSyncTarget(http, new StaticToken(Mint((string)s["jwt"]!)), id), id);
    }

    /// <summary>A client and a test token for reading the isolated instance back (sync-verify, R10).</summary>
    public static (HttpClient Http, string Token) Reader(string url, string secretsFile)
    {
        if (!new Uri(url).IsLoopback) throw new ArgumentException("local backend/1c reads are loopback only");
        var s = JsonNode.Parse(File.ReadAllText(secretsFile))!;
        return (new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) }, Mint((string)s["jwt"]!));
    }

    private static string Mint(string secret)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string head = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
        string body = B64(Encoding.UTF8.GetBytes($"{{\"sub\":\"{UserId}\",\"exp\":{DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()}}}"));
        return $"{head}.{body}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)))}";
    }
}
