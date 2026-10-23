using Microsoft.Extensions.DependencyInjection;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Dialogs;
using Sterp.Web.Framework.Jobs;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Framework;

public static class FrameworkServices
{
    /// <summary>Registers the page framework services (session, API client, dialogs, job runner, browser interop).</summary>
    public static IServiceCollection AddSterpWebFramework(this IServiceCollection services, SterpClientOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<SessionService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<BrowserInterop>();
        services.AddSingleton<JobRunner>();
        return services;
    }
}
