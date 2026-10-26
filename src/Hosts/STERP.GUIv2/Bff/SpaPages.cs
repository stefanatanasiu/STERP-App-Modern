using System.Net.Security;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

namespace Sterp.GuiV2.Bff;

/// <summary>The application shell around the forwarder: security headers, index.html for client routes, health and client errors.</summary>
internal static class SpaPages
{
    public const string ProbeClient = "guiv2-probe";

    // Vite emits module scripts only (no inline script); three.js text rendering starts workers from blob: URLs, and the worker
    // loads its own code with importScripts(blob:), which script-src governs
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
        "connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static async Task SecurityHeaders(HttpContext http, Func<Task> next)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h.ContentSecurityPolicy = ContentSecurityPolicy;
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        await next();
    }

    /// <summary>Every client route (deep links such as /explode/FG-CAKE-CELEB-8IN) answers index.html; the router takes it from there.</summary>
    public static IResult Index(HttpContext http, IFileProvider bundle, GuiV2Options options)
    {
        var path = http.Request.Path;
        if (path.StartsWithSegments("/bff") || path.StartsWithSegments("/health") || path.StartsWithSegments("/assets")) return Results.NotFound();
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)) return Results.StatusCode(405);
        http.Response.Headers.CacheControl = "no-cache";
        var index = bundle.GetFileInfo("index.html");
        if (index.Exists && index.PhysicalPath is { } file) return Results.File(file, "text/html; charset=utf-8");
        return Results.Content(NotBuiltPage(options), "text/html; charset=utf-8", statusCode: 503);
    }

    public static bool BundleExists(IFileProvider bundle) => bundle.GetFileInfo("index.html").Exists;

    /// <summary>/health/ready: the bundle and the two STERP services GUIv2 forwards to (their anonymous liveness).</summary>
    public static async Task<IResult> ReadyAsync(IFileProvider bundle, GuiV2Options options, IHttpClientFactory clients, CancellationToken ct)
    {
        var checks = new JsonArray();
        var bundled = BundleExists(bundle);
        checks.Add(Check("front-end", bundled, bundled ? "GUIv2 bundle present" : "GUIv2 front end not built: install Node.js LTS and build again"));
        var client = clients.CreateClient(ProbeClient);
        var api = await ProbeAsync(client, options.ApiBaseUrl, ct);
        var jobs = await ProbeAsync(client, options.JobsBaseUrl, ct);
        checks.Add(Check("api", api, api ? $"STERP.Api {options.ApiBaseUrl} answers" : $"STERP.Api {options.ApiBaseUrl} does not answer"));
        checks.Add(Check("jobs", jobs, jobs ? $"STERP.Jobs {options.JobsBaseUrl} answers" : $"STERP.Jobs {options.JobsBaseUrl} does not answer"));
        var status = bundled && api && jobs ? "Healthy" : "Degraded";
        return Results.Json(new JsonObject { ["status"] = status, ["checks"] = checks });
    }

    public static SocketsHttpHandler LoopbackTolerantHandler() => new()
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None || (sender is SslStream ssl &&
                    (ssl.TargetHostName.Equals("localhost", StringComparison.OrdinalIgnoreCase) || ssl.TargetHostName is "127.0.0.1" or "::1")),
        },
    };

    /// <summary>Minimal client-error log: message and page only, never business data (as STERP.Web).</summary>
    public static async Task<IResult> ClientErrorAsync(HttpContext http, ILoggerFactory loggers)
    {
        if (!ApiForwarder.IsOwnCall(http.Request)) return Results.StatusCode(403);
        if (http.Request.ContentLength > 32 * 1024) return Results.StatusCode(413);
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest();
        }
        loggers.CreateLogger("STERP.GUIv2.Client").LogError("Client error on {Page} for {User}: {Message} {Detail}",
            Cut(body?["page"]?.ToString(), 200), http.User.Identity?.Name ?? "(anonymous)", Cut(body?["message"]?.ToString(), 2000),
            Cut(body?["detail"]?.ToString(), 8000));
        return Results.NoContent();
    }

    private static JsonObject Check(string name, bool ok, string description) => new()
    {
        ["name"] = name,
        ["status"] = ok ? "Healthy" : "Degraded",
        ["description"] = description,
    };

    private static async Task<bool> ProbeAsync(HttpClient client, Uri baseUrl, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(baseUrl, "health/live"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string Cut(string? text, int length) => text is null ? "" : text.Length <= length ? text : text[..length];

    private static string NotBuiltPage(GuiV2Options options) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>STERP GUIv2 - front end not built</title>
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
                 background: radial-gradient(1200px 600px at 20% 10%, #2a1b5c 0%, transparent 60%), radial-gradient(900px 500px at 90% 90%, #0b3b4f 0%, transparent 60%), #070a12; color: #e6e9f2; }
          main { max-width: 640px; margin: 24px; padding: 40px 44px; border-radius: 24px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.12);
                 box-shadow: 0 30px 80px rgba(0,0,0,.45); backdrop-filter: blur(12px); }
          h1 { margin: 0 0 6px; font-size: 26px; letter-spacing: -.02em; }
          .tag { display: inline-block; margin-bottom: 18px; padding: 3px 10px; border-radius: 999px; font-size: 12px; letter-spacing: .08em; text-transform: uppercase;
                 background: linear-gradient(90deg, #8b5cf6, #22d3ee); color: #0b0e17; font-weight: 700; }
          code { padding: 2px 8px; border-radius: 8px; background: rgba(255,255,255,.08); font-family: "Cascadia Code", Consolas, monospace; font-size: 13px; }
          ol { padding-left: 20px; } li { margin: 8px 0; }
          a { color: #67e8f9; }
        </style>
        </head>
        <body>
        <main>
          <span class="tag">GUIv2</span>
          <h1>The GUIv2 front end has not been built yet</h1>
          <p>This host is running, but its pages are produced by the front-end build, which needs Node.js. The other STERP services are not affected.</p>
          <ol>
            <li>Install Node.js LTS: <code>winget install OpenJS.NodeJS.LTS</code></li>
            <li>Rebuild and restart: <code>run-sterp.bat /restart</code></li>
          </ol>
          <p>Meanwhile the classic GUI is at <a href="{{options.ClassicGuiUrl}}">{{options.ClassicGuiUrl}}</a>.</p>
        </main>
        </body>
        </html>
        """;
}
