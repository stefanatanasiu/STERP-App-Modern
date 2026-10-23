using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Sterp.Web.Client;
using Sterp.Web.Client.Shell;
using Sterp.Web.Framework;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Registry;
using Sterp.Web.Framework.Shell;

// STERP-WEB shell (Interactive WebAssembly, FR-STERP-WEB-001..020). The browser calls STERP.Api and STERP.Jobs directly
// with the user's Windows credentials; the base URLs come from the anonymous /_sterp/config endpoint of STERP.Web.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
// the browser console carries warnings and errors only (no per-request HttpClient traces)
builder.Logging.SetMinimumLevel(LogLevel.Warning);

SterpClientOptions options;
using (var boot = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) })
{
    try
    {
        options = await boot.GetFromJsonAsync<SterpClientOptions>("_sterp/config") ?? new SterpClientOptions();
    }
    catch (Exception)
    {
        options = new SterpClientOptions();
    }
}

builder.Services.AddSterpWebFramework(options);
builder.Services
    .AddHttpClient<ApiClient>(client => client.Timeout = TimeSpan.FromSeconds(130))
    .AddResilienceHandler("sterp-idempotent-get", pipeline =>
    {
        // idempotent GETs only (STERP-WEB section 5): a dropped connection is retried, a legacy outcome never is
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(300),
            BackoffType = DelayBackoffType.Constant,
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is HttpRequestException &&
                args.Context.GetRequestMessage()?.Method == HttpMethod.Get),
        });
    });
builder.Services.AddSingleton(new PageRegistry(PageModules.All()));
builder.Services.AddSingleton<ShellService>();
builder.Services.AddSingleton<IShell>(sp => sp.GetRequiredService<ShellService>());
builder.Services.AddSingleton<KeyboardRouter>();

await builder.Build().RunAsync();
