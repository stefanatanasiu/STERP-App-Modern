using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.ApiPortal.Portal;

// STERP-PORTAL (FR-STERP-PORTAL-001..014): the API Console - the thirteen binding OpenAPI documents with their legacy
// traceability, Try it out under the user's own Windows identity, Negotiate command lines, a contract-drift check against
// the running hosts and the 'How to call the STERP APIs' guide. Anonymous pages; the console reads documents only and never
// connects to the database.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff ";
});

var catalog = new DocumentCatalog(
    builder.Configuration["Sterp:Portal:ApiBaseUrl"] ?? DocumentCatalog.BindingApiUrl,
    builder.Configuration["Sterp:Portal:JobsBaseUrl"] ?? DocumentCatalog.BindingJobsUrl);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<HostStatusService>();
builder.Services.AddSingleton<ContractDriftService>();
// only the anonymous health and document endpoints of the configured hosts are read
builder.Services.AddHttpClient(HostStatusService.ClientName, c => c.Timeout = TimeSpan.FromSeconds(3))
    .ConfigurePrimaryHttpMessageHandler(LoopbackTolerantHandler);
builder.Services.AddHttpClient(ContractDriftService.ClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(LoopbackTolerantHandler);

var app = builder.Build();

app.UseHttpsRedirection();
app.Use(async (http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
// the console's script and styles are revalidated on every load, so a new version is never mixed with a cached one
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/", async (HostStatusService hosts, HttpContext http) =>
    Html(http, ConsolePages.Home(catalog, await hosts.GetAsync(false, http.RequestAborted))));
app.MapGet("/guide", async (HostStatusService hosts, HttpContext http) =>
    Html(http, ConsolePages.Guide(catalog, await hosts.GetAsync(false, http.RequestAborted))));

// binding documents: the exact bytes (.yaml) or their JSON form (.json)
var indented = new JsonSerializerOptions { WriteIndented = true };
app.MapGet("/docs/{name}.{format}", (string name, string format) =>
{
    var d = catalog.Find(name);
    if (d is null) return Results.NotFound();
    return format.ToLowerInvariant() switch
    {
        "yaml" or "yml" => Results.Bytes(d.YamlBytes, "application/yaml; charset=utf-8"),
        "json" => Results.Text(d.Json.ToJsonString(indented), "application/json; charset=utf-8"),
        _ => Results.NotFound(),
    };
});

// the copy the console renders: servers point at the configured STERP.Api / STERP.Jobs base URLs
app.MapGet("/console/openapi/{name}.json", (string name, HttpContext http) =>
{
    var d = catalog.Find(name);
    if (d is null) return Results.NotFound();
    http.Response.Headers.CacheControl = "no-cache";
    return Results.Bytes(catalog.ConsoleJson(d.Name), "application/json; charset=utf-8");
});

// status bar and delivery states; refresh=true probes the hosts again instead of answering from the short cache
app.MapGet("/console/status.json", async (HostStatusService hosts, HttpContext http, bool? refresh) =>
{
    http.Response.Headers.CacheControl = "no-store";
    var status = await hosts.GetAsync(refresh == true, http.RequestAborted);
    return Results.Text(status.ToJson(catalog).ToJsonString(), "application/json; charset=utf-8");
});

// contract drift: the binding copies against the documents the hosts publish
app.MapGet("/console/drift.json", async (ContractDriftService drift, HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Json(await drift.CheckAsync(http.RequestAborted));
});

// busy signal of the pages (wwwroot/console/activity.js)
app.MapGet("/_sterp/settle", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.NoContent();
});
app.MapGet("/_sterp/hold", async (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(60), http.RequestAborted);
    }
    catch (OperationCanceledException)
    {
    }
    return Results.NoContent();
});

app.MapGet("/health/live", () => Results.Json(new JsonObject { ["status"] = "Healthy", ["checks"] = new JsonArray() }));
app.MapGet("/health/ready", async (HostStatusService hosts, HttpContext http) =>
{
    var info = await hosts.GetAsync(false, http.RequestAborted);
    var documentsOk = catalog.Documents.Count == 13;
    var status = !documentsOk ? "Unhealthy" : info.Problems.Count == 0 ? "Healthy" : "Degraded";
    var checks = new JsonArray
    {
        new JsonObject
        {
            ["name"] = "documents",
            ["status"] = documentsOk ? "Healthy" : "Unhealthy",
            ["description"] = $"{catalog.Documents.Count} binding OpenAPI documents embedded",
        },
        new JsonObject
        {
            ["name"] = "document-sources",
            ["status"] = info.Problems.Count == 0 ? "Healthy" : "Degraded",
            ["description"] = info.Problems.Count == 0
                ? $"STERP.Api and STERP.Jobs serve {info.Delivered.Count} document(s)"
                : string.Join(" ", info.Problems),
        },
    };
    return Results.Json(new JsonObject { ["status"] = status, ["checks"] = checks }, statusCode: documentsOk ? 200 : 503);
});

app.Run();

static IResult Html(HttpContext http, string page)
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Content(page, "text/html; charset=utf-8");
}

// a local development certificate is accepted for loopback hosts only
static SocketsHttpHandler LoopbackTolerantHandler() => new()
{
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (sender, _, _, errors) =>
            errors == SslPolicyErrors.None || (sender is SslStream ssl && IsLoopbackName(ssl.TargetHostName)),
    },
};

static bool IsLoopbackName(string host) =>
    host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1" || host == "[::1]";
