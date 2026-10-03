using System.Text.Json.Nodes;

namespace OneC.Cloud;

/// <summary>A company the user owns or was invited to (main API <c>GET /company/</c>).</summary>
public sealed record CloudCompany(string Id, string Name, string Inn, string Form, bool IsDefault);

/// <summary>
/// A 1C connection record on backend/1c (<c>GET /onec</c>). Its <see cref="Id"/> is the oneCId
/// every later call (upload, status) uses. <see cref="ConnectionId"/> / <see cref="IsShared"/>
/// mark multi-organisation bindings, which this app does not handle yet.
/// </summary>
public sealed record OneCRecord(string Id, string Name, string OdataName, string Provider, string CompanyId,
                                string Status, string Version, string? ConnectionId, bool IsShared, string? ConnectionState)
{
    public bool IsMultiOrg => ConnectionId is { Length: > 0 } || IsShared;

    /// <summary>
    /// The cloud's view of the sync, whoever does it (today the old Connector): rows it was told
    /// to expect, the share already stored (backend/1c OneCResponse.from_document), last error.
    /// </summary>
    public long TotalCount { get; init; }
    public double Percentage { get; init; }
    public string? LastError { get; init; }

    internal static OneCRecord From(JsonNode n) => new(
        Str(n["id"]) ?? Str(n["_id"]) ?? "",
        Str(n["name"]) ?? "", Str(n["odataName"]) ?? "", Str(n["provider"]) ?? "", Str(n["companyId"]) ?? "",
        Str(n["status"]) ?? "", Str(n["version"]) ?? "", Str(n["connectionId"]),
        n["isShared"]?.GetValueKind() == System.Text.Json.JsonValueKind.True, Str(n["connection_state"]))
    {
        TotalCount = Num(n["totalCount"]) is { } t ? (long)t : 0,
        Percentage = Num(n["percentage"]) ?? 0,
        LastError = Str(n["lastError"]) is { Length: > 0 } e ? e : null
    };

    private static double? Num(JsonNode? n) =>
        n?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<double>() : null;

    /// <summary>Ids come back as strings or numbers depending on the service.</summary>
    internal static string? Str(JsonNode? n) => n?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => n.GetValue<string>(),
        System.Text.Json.JsonValueKind.Number => n.ToJsonString(),
        _ => null
    };
}

/// <summary>
/// A failed cloud call, with the backend's own code (e.g. <c>auth.password_incorrect</c>) and a
/// message a person can act on. The old app showed the raw i18n key (spec §1); this one doesn't.
/// </summary>
public sealed class CloudException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public CloudException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public static string Friendly(int status, string code) => code switch
    {
        "auth.user_not_found" => "There is no AIBA account with this phone number.",
        "auth.password_incorrect" => "Wrong password.",
        "auth.invalid_phone_number" => "This phone number is not valid. Write it with the country code, e.g. +998 90 123 45 67.",
        "auth.invalid_refresh_token" or "auth.session_logged_out" => "Your session has ended. Sign in again.",
        _ when status == 429 => "Too many attempts. Wait a minute and try again.",
        _ when status == 401 => "Your session has ended. Sign in again.",
        _ when status == 403 => "Your account has no access to this.",
        _ when status == 409 => "This already exists in the cloud, or is being deleted.",
        _ when status >= 500 => "The AIBA cloud is not answering properly right now. Try again later.",
        _ => code.Length > 0 ? code : $"The AIBA cloud refused the request (HTTP {status})."
    };
}
