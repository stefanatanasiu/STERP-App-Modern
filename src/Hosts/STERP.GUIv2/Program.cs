using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Sterp.GuiV2.Bff;

// STERP-GUIv2 host: serves the GUIv2 single-page application (built from client\ into wwwroot\) and forwards its calls to the
// existing STERP.Api and STERP.Jobs operations under the signed-in user's own Windows identity. It owns no business logic and
// never connects to the database: every figure GUIv2 shows is an answer of the existing APIs.
var builder = WebApplication.CreateBuilder(args);

// Negotiate (Windows) authentication is connection based: HTTP/1.1 only, as STERP.Web
builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1));
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization();

var options = GuiV2Options.From(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ApiForwarder>();
builder.Services.AddHttpClient(SpaPages.ProbeClient, c => c.Timeout = TimeSpan.FromSeconds(3))
    .ConfigurePrimaryHttpMessageHandler(SpaPages.LoopbackTolerantHandler);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff ";
});

var app = builder.Build();

// the project build writes the bundle (npm run build); the folder may only appear after the host started
var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(webRoot);
var bundle = new PhysicalFileProvider(webRoot);

app.UseHttpsRedirection();
app.Use(SpaPages.SecurityHeaders);

// hashed bundle files never change; everything else is revalidated so a rebuilt GUIv2 is picked up on the next load. Static files
// come before routing: the client-route fallback below matches every path, and a matched endpoint would bypass them.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = bundle,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// anonymous-safe client configuration (no data, no secrets)
app.MapGet("/bff/config", () => Results.Json(new JsonObject
{
    ["productName"] = options.ProductName,
    ["version"] = typeof(ApiForwarder).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    ["classicGuiUrl"] = options.ClassicGuiUrl,
    ["apiConsoleUrl"] = options.ApiConsoleUrl,
})).AllowAnonymous();

// the existing operations, forwarded unchanged under the caller's identity
app.MapMethods("/bff/api/{**path}", ApiForwarder.Methods, (HttpContext http, ApiForwarder forwarder) => forwarder.ForwardAsync(http, ForwardTarget.Api))
    .RequireAuthorization();
app.MapMethods("/bff/jobs/{**path}", ApiForwarder.Methods, (HttpContext http, ApiForwarder forwarder) => forwarder.ForwardAsync(http, ForwardTarget.Jobs))
    .RequireAuthorization();

app.MapPost("/bff/client-errors", SpaPages.ClientErrorAsync).AllowAnonymous();

app.MapGet("/health/live", () => Results.Json(new JsonObject { ["status"] = "Healthy", ["checks"] = new JsonArray() })).AllowAnonymous();
app.MapGet("/health/ready", (IHttpClientFactory clients, HttpContext http) => SpaPages.ReadyAsync(bundle, options, clients, http.RequestAborted))
    .AllowAnonymous();

// client routes: item codes such as SEATPOST-27.2X350 contain dots, so the fallback must not use the default 'nonfile' constraint
app.MapFallback("{**path}", (HttpContext http) => SpaPages.Index(http, bundle, options)).AllowAnonymous();

app.Run();
