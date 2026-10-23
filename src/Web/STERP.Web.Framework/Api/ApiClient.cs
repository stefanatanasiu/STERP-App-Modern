using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Framework.Api;

/// <summary>Which web service an operation belongs to (contracts 1: STERP.Api online, STERP.Jobs batch).</summary>
public enum ApiHost
{
    Api,
    Jobs,
}

/// <summary>Per-call options: idempotency key of the user instruction, context override and extra headers.</summary>
public sealed class ApiCallOptions
{
    /// <summary>Idempotency-Key header (contracts 4.5/4.8): one stable key per user instruction where the legacy form reused a correlation id.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Context to send instead of the shell session (the sign-on page works on a candidate context).</summary>
    public SessionContext? Session { get; init; }

    /// <summary>Sends no context headers at all (health probes, documents).</summary>
    public bool NoContext { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Outcome envelope of a COBOL-backed operation (contracts 4.6).</summary>
public sealed record ApiOutcome(int ReturnCode, string Severity, string ReasonCode, string Message, int MessageCount, string DocumentRef, string CorrelationId)
{
    public static ApiOutcome From(JsonNode o) => new(
        Json.Int(o["returnCode"]), Json.Str(o["severity"]), Json.Str(o["reasonCode"]).Trim(), Json.Str(o["message"]),
        Json.Int(o["messageCount"]), Json.Str(o["documentRef"]), Json.Str(o["correlationId"]));
}

/// <summary>RFC 9457 problem body with the STERP extensions (contracts 4.7).</summary>
public sealed record ApiProblem(string Type, string Title, int Status, string Detail, string ReasonCode, int? ReturnCode, string Severity, JsonNode? Data, JsonNode? Errors)
{
    /// <summary>Problem kind: the last segment of <c>type</c> (business-rule, severe, not-found, validation, forbidden, unauthenticated, not-implemented).</summary>
    public string Kind => Type.Contains('/') ? Type[(Type.LastIndexOf('/') + 1)..] : Type;

    public static ApiProblem From(JsonNode? j, int status, string fallbackText)
    {
        int? rc = j?["returnCode"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        return new ApiProblem(
            Json.Str(j?["type"]), Json.Str(j?["title"]), j?["status"] is null ? status : Json.Int(j["status"]),
            Json.Str(j?["detail"]) is { Length: > 0 } d ? d : (Json.Str(j?["title"]) is { Length: > 0 } t ? t : fallbackText),
            Json.Str(j?["reasonCode"]).Trim(), rc, Json.Str(j?["severity"]), j?["data"], j?["errors"]);
    }
}

/// <summary>A response of STERP.Api or STERP.Jobs as the pages see it.</summary>
public sealed class ApiResponse
{
    public int Status { get; init; }

    public JsonNode? Json { get; init; }

    public string Text { get; init; } = "";

    public string ContentType { get; init; } = "";

    public string? NetworkError { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public bool IsSuccess => NetworkError is null && Status is >= 200 and < 300;

    /// <summary>Envelope outcome (x-sterp-outcome envelope) of a success response, when present.</summary>
    public ApiOutcome? Outcome => Json?["outcome"] is JsonObject o ? ApiOutcome.From(o) : null;

    /// <summary>The payload: envelope data, or the direct body.</summary>
    public JsonNode? Data => Json is JsonObject obj && obj.ContainsKey("outcome") ? obj["data"] : Json;

    /// <summary>The problem of a failure response (network failures become SYS0001 problems).</summary>
    public ApiProblem? Problem => IsSuccess ? null
        : NetworkError is not null ? new ApiProblem("", "Network error", 0, NetworkError, "SYS0001", null, "ERROR", null, null)
        : ApiProblem.From(Json, Status, Text.Length > 0 && Text.Length < 400 ? Text : $"HTTP {Status}");

    public JsonArray Items => Json?["items"] as JsonArray ?? [];

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>
/// HTTPS access to STERP.Api and STERP.Jobs with the browser's Windows credentials (<c>credentials: 'include'</c>, contracts
/// 5.1) and the context headers of contracts 4.5 on every call (company, site, business date, run-as). The GUI never talks
/// to the database (FR-STERP-WEB-001).
/// </summary>
public sealed class ApiClient(HttpClient http, SterpClientOptions options, SessionService session, BrowserInterop browser)
{
    public SterpClientOptions Options => options;

    public string BaseUrl(ApiHost host) => (host == ApiHost.Jobs ? options.JobsBaseUrl : options.ApiBaseUrl).TrimEnd('/');

    public Task<ApiResponse> GetAsync(ApiHost host, string pathAndQuery, ApiCallOptions? call = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, host, pathAndQuery, null, call, ct);

    public Task<ApiResponse> GetAsync(string pathAndQuery, ApiCallOptions? call = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, ApiHost.Api, pathAndQuery, null, call, ct);

    public Task<ApiResponse> PostAsync(ApiHost host, string pathAndQuery, JsonNode? body, ApiCallOptions? call = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, host, pathAndQuery, body ?? new JsonObject(), call, ct);

    public Task<ApiResponse> PostAsync(string pathAndQuery, JsonNode? body, ApiCallOptions? call = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiHost.Api, pathAndQuery, body ?? new JsonObject(), call, ct);

    /// <summary>Absolute URL of a host-relative path (anonymous probes such as /health/ready).</summary>
    public Task<ApiResponse> GetAbsoluteAsync(string url, CancellationToken ct = default) =>
        SendCoreAsync(HttpMethod.Get, url, null, new ApiCallOptions { NoContext = true }, ct);

    public Task<ApiResponse> SendAsync(HttpMethod method, ApiHost host, string pathAndQuery, JsonNode? body, ApiCallOptions? call = null, CancellationToken ct = default) =>
        SendCoreAsync(method, BaseUrl(host) + pathAndQuery, body, call, ct);

    private async Task<ApiResponse> SendCoreAsync(HttpMethod method, string url, JsonNode? body, ApiCallOptions? call, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        var context = call?.Session ?? session.Current;
        if (call?.NoContext != true && context is not null)
        {
            if (context.CompanyCode.Length > 0) request.Headers.TryAddWithoutValidation("X-STERP-Company", context.CompanyCode);
            if (context.CompanyCode.Length > 0 && context.SiteCode.Length > 0) request.Headers.TryAddWithoutValidation("X-STERP-Site", context.SiteCode);
            request.Headers.TryAddWithoutValidation("X-STERP-Business-Date", context.BusinessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (context.RunAs is { } runAs) request.Headers.TryAddWithoutValidation("X-STERP-Run-As", runAs);
        }
        if (call?.IdempotencyKey is { Length: > 0 } key) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (call?.Headers is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (body is not null && method != HttpMethod.Get)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        browser.ActivityBegin();
        try
        {
            using var response = await http.SendAsync(request, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            var charset = response.Content.Headers.ContentType?.CharSet ?? "";
            var text = Decode(bytes, charset);
            JsonNode? json = null;
            if (media.Contains("json", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
            {
                try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { json = null; }
            }
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var h in response.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            foreach (var h in response.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            return new ApiResponse { Status = (int)response.StatusCode, Json = json, Text = text, ContentType = media, Headers = headers };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ApiResponse { Status = 0, NetworkError = ex.GetBaseException().Message };
        }
        finally
        {
            browser.ActivityEnd();
        }
    }

    /// <summary>Spool and SYSOUT are text/plain; charset=windows-1252 as stored (contracts 4.2); other text is UTF-8.</summary>
    public static string Decode(byte[] bytes, string charset)
    {
        if (charset.Contains("1252", StringComparison.Ordinal) || charset.Equals("windows-1252", StringComparison.OrdinalIgnoreCase))
        {
            return Cp1252.Decode(bytes);
        }
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}

/// <summary>Windows-1252 decoding without the code-pages provider (not available in the browser runtime).</summary>
public static class Cp1252
{
    private static readonly char[] High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];

    public static string Decode(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? High[b - 0x80] : (char)b;
        }
        return new string(chars);
    }
}

/// <summary>
/// A failed call raised inside <c>StPage.GuardAsync</c>: presented as the legacy Guard presented a service failure (the
/// legacy message with its reason code, SYS0001 when the problem carries none).
/// </summary>
public sealed class ApiFailure(ApiResponse response) : Exception(response.Problem?.Detail ?? "HTTP " + response.Status)
{
    public ApiResponse Response { get; } = response;
}

/// <summary>One user instruction: the Idempotency-Key the legacy form kept for it (SterpSession.NewCorrelationId, COM-015).</summary>
public sealed class Instruction
{
    public string Key { get; } = Guid.NewGuid().ToString();

    public static Instruction New() => new();

    public ApiCallOptions Options() => new() { IdempotencyKey = Key };
}

/// <summary>Query string builder that omits empty values.</summary>
public static class Query
{
    public static string Build(params (string Name, string? Value)[] values)
    {
        var parts = values.Where(v => !string.IsNullOrEmpty(v.Value))
            .Select(v => Uri.EscapeDataString(v.Name) + "=" + Uri.EscapeDataString(v.Value!)).ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    public static string Path(string value) => Uri.EscapeDataString(value);
}
