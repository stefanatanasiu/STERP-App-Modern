using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Sterp.GuiV2.Bff;

/// <summary>
/// Forwards one GUIv2 call, unchanged, to an existing STERP.Api or STERP.Jobs operation under the signed-in user's own Windows
/// identity (Negotiate on GUIv2, impersonation for the outbound call, Negotiate again on the API host). The APIs therefore see the
/// same user, permissions, context headers and audit identity as when the classic GUI calls them; this class adds no business
/// behavior. Each Windows user gets a separate connection pool because NTLM and Kerberos authenticate connections, not requests.
/// </summary>
public sealed class ApiForwarder(GuiV2Options options, ILogger<ApiForwarder> logger) : IDisposable
{
    /// <summary>Sent by the GUIv2 client on every call; a page of another site cannot add it without a CORS preflight, which this host never grants.</summary>
    public const string GuardHeader = "X-STERP-GUIv2";

    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    private const long MaxBodyBytes = 1024 * 1024;
    private const int MaxPools = 256;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(130);

    // the request context of the STERP contracts (section 4.5) - nothing else of the browser request reaches the API
    private static readonly string[] RequestHeaders =
        ["Accept", "Accept-Language", "X-STERP-Company", "X-STERP-Site", "X-STERP-Business-Date", "X-STERP-Run-As", "Idempotency-Key"];

    // WWW-Authenticate is never passed back: a refused forwarded identity must not make the browser prompt for credentials
    private static readonly string[] ResponseHeaders = ["Location", "Retry-After", "traceparent"];

    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _pools = new(StringComparer.Ordinal);

    public async Task ForwardAsync(HttpContext http, ForwardTarget target)
    {
        if (!IsOwnCall(http.Request))
        {
            await Problem(http, 403, "Cross-site call refused", "GUIv2 forwards only calls made by its own pages.", "GUIV2-403");
            return;
        }
        if (!TryTargetUri(http, target, out var uri))
        {
            await Problem(http, 404, "Not forwarded", "GUIv2 does not forward this path to the STERP services.", "GUIV2-404");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            await Problem(http, 501, "Windows required", "GUIv2 calls the STERP APIs with the signed-in Windows identity, which needs Windows.", "GUIV2-501");
            return;
        }
        if (http.User.Identity is not WindowsIdentity { IsAuthenticated: true } user)
        {
            await Problem(http, 401, "Not authenticated", "Windows authentication (Negotiate) is required.", "GUIV2-401");
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), uri);
        foreach (var name in RequestHeaders)
        {
            if (http.Request.Headers.TryGetValue(name, out var values)) request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            if (http.Request.ContentLength > MaxBodyBytes)
            {
                await Problem(http, 413, "Request too large", "GUIv2 forwards request bodies of up to 1 MB.", "GUIV2-413");
                return;
            }
            // buffered: Negotiate answers the first attempt with 401, and the body must be sent again with the credentials
            var body = await ReadBodyAsync(http.Request, http.RequestAborted);
            if (body is null)
            {
                await Problem(http, 413, "Request too large", "GUIv2 forwards request bodies of up to 1 MB.", "GUIV2-413");
                return;
            }
            request.Content = new ByteArrayContent(body);
            if (MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)) request.Content.Headers.ContentType = contentType;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        timeout.CancelAfter(CallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await SendAsUserAsync(user, request, timeout.Token);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("{Target} did not answer {Method} {Path} within {Seconds} s", target, request.Method, uri.AbsolutePath, CallTimeout.TotalSeconds);
            await Problem(http, 504, "No answer", $"{HostName(target)} did not answer within {CallTimeout.TotalSeconds:0} seconds.", "GUIV2-504");
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Target} is not reachable for {Method} {Path}: {Message}", target, request.Method, uri.AbsolutePath, ex.Message);
            await Problem(http, 502, $"{HostName(target)} is not reachable",
                $"{HostName(target)} ({options.BaseUrl(target).GetLeftPart(UriPartial.Authority)}) did not accept the connection: is it running?", "GUIV2-502");
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ResponseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values)) http.Response.Headers[name] = values.ToArray();
            }
            if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
            if (response.Content.Headers.ContentDisposition is { } disposition) http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.CacheControl = "no-store";
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }

    public void Dispose()
    {
        foreach (var pool in _pools.Values) pool.Dispose();
        _pools.Clear();
    }

    /// <summary>Same-origin fetch of a GUIv2 page: the guard header, and no foreign Origin or Sec-Fetch-Site.</summary>
    internal static bool IsOwnCall(HttpRequest request)
    {
        if (!request.Headers.ContainsKey(GuardHeader)) return false;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin") return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// /bff/api/{path} -> {ApiBaseUrl}/api/{path}, /bff/jobs/{path} -> {JobsBaseUrl}/api/{path}, from the raw request target so the
    /// encoding of item codes reaches the API exactly as the browser sent it. Only the configured prefixes are forwarded.
    /// </summary>
    internal bool TryTargetUri(HttpContext http, ForwardTarget target, out Uri uri)
    {
        uri = null!;
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/') raw = (http.Request.PathBase + http.Request.Path).ToUriComponent() + http.Request.QueryString;
        var prefix = target == ForwardTarget.Jobs ? "/bff/jobs/" : "/bff/api/";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = raw[prefix.Length..];
        var queryAt = rest.IndexOf('?');
        var path = queryAt < 0 ? rest : rest[..queryAt];
        var query = queryAt < 0 ? "" : rest[queryAt..];
        if (path.Length == 0 || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal)) return false;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return false;
        }
        if (decoded.Split('/').Any(segment => segment is "." or "..")) return false;
        var apiPath = "/api/" + decoded;
        if (!options.Prefixes(target).Any(p => apiPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
        if (!Uri.TryCreate(options.BaseUrl(target), "api/" + path + query, out var built)) return false;
        if (!built.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)) return false;
        uri = built;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private Task<HttpResponseMessage> SendAsUserAsync(WindowsIdentity user, HttpRequestMessage request, CancellationToken ct)
    {
        var key = user.User?.Value ?? user.Name;
        if (!_pools.ContainsKey(key) && _pools.Count >= MaxPools)
        {
            foreach (var stale in _pools.Keys.ToArray())
            {
                if (_pools.TryRemove(stale, out var pool)) pool.Dispose();
            }
        }
        var invoker = _pools.GetOrAdd(key, _ => new HttpMessageInvoker(CreateHandler(), disposeHandler: true));
        // the outbound Negotiate handshake runs as the user, so STERP.Api resolves the same STERP user as for the classic GUI
        return WindowsIdentity.RunImpersonatedAsync(user.AccessToken, () => invoker.SendAsync(request, ct));
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        Credentials = CredentialCache.DefaultCredentials,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        SslOptions = new SslClientAuthenticationOptions
        {
            // the ASP.NET Core development certificate is accepted for loopback hosts only (as the API Console does)
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None || (sender is SslStream ssl && IsLoopbackName(ssl.TargetHostName)),
        },
    };

    private static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]";

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string HostName(ForwardTarget target) => target == ForwardTarget.Jobs ? "STERP.Jobs" : "STERP.Api";

    /// <summary>An RFC 9457 problem in the shape the STERP APIs use, so the client handles both alike.</summary>
    internal static async Task Problem(HttpContext http, int status, string title, string detail, string reasonCode)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsync(new JsonObject
        {
            ["type"] = "https://sterp.local/problems/guiv2",
            ["title"] = title,
            ["status"] = status,
            ["detail"] = detail,
            ["reasonCode"] = reasonCode,
        }.ToJsonString(), http.RequestAborted);
    }
}
