using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sterp.Web.Components;

// STERP-WEB host (FR-STERP-WEB-001..020): serves the WebAssembly shell for every route behind Windows authentication. It has
// no database access: the browser calls STERP.Api and STERP.Jobs directly with the user's own credentials.
var builder = WebApplication.CreateBuilder(args);

// Negotiate (Windows) authentication is connection based: HTTP/1.1 only (contracts 5.1, 5.8)
builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1));
// build-output runs (run-host.ps1, environment Parity) serve the client's _framework and _content assets from their
// build locations; a published app has no runtime manifest and this is a no-op
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff ";
});

var app = builder.Build();
var config = app.Configuration;

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// anonymous-safe client configuration: product name and the API base URLs (no data, no secrets)
app.MapGet("/_sterp/config", () => Results.Json(new JsonObject
{
    ["productName"] = config["Sterp:Web:ProductName"] ?? "STERP - Supply Chain Management",
    ["apiBaseUrl"] = config["Sterp:Web:ApiBaseUrl"] ?? "https://localhost:7101",
    ["jobsBaseUrl"] = config["Sterp:Web:JobsBaseUrl"] ?? "https://localhost:7102",
    ["version"] = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
})).AllowAnonymous().DisableAntiforgery();

// end of a busy period of the shell (sterp.js): an empty response that marks the network as settled
app.MapGet("/_sterp/settle", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.NoContent();
}).AllowAnonymous();

// busy period of the shell (sterp.js keeper frame): answered when the browser abandons it, at the latest after 60 s
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
}).AllowAnonymous();

// minimal client-error log (STERP-WEB section 9): message and page only, never business data
app.MapPost("/_sterp/client-errors", async (HttpContext http, ILoggerFactory loggers) =>
{
    var logger = loggers.CreateLogger("STERP.Web.Client");
    JsonNode? body = null;
    try
    {
        body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest();
    }
    var message = Cut(body?["message"]?.ToString(), 2000);
    var detail = Cut(body?["detail"]?.ToString(), 8000);
    var page = Cut(body?["page"]?.ToString(), 200);
    logger.LogError("Client error on {Page} for {User}: {Message} {Detail}", page, http.User.Identity?.Name ?? "(anonymous)", message, detail);
    return Results.NoContent();
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/health/live", () => Results.Json(new JsonObject { ["status"] = "Healthy", ["checks"] = new JsonArray() })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Json(new JsonObject
{
    ["status"] = "Healthy",
    ["checks"] = new JsonArray(new JsonObject
    {
        ["name"] = "configuration",
        ["status"] = "Healthy",
        ["description"] = $"STERP.Api {config["Sterp:Web:ApiBaseUrl"]}, STERP.Jobs {config["Sterp:Web:JobsBaseUrl"]}",
    }),
})).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Sterp.Web.Client.PageModules).Assembly)
    .RequireAuthorization();

app.Run();

static string Cut(string? text, int length) => text is null ? "" : text.Length <= length ? text : text[..length];
