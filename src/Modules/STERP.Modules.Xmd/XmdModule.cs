using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sterp.Modules.Xmd.Jobs;
using Sterp.Modules.Xmd.Online;
using Sterp.Modules.Xmd.Services;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Xmd;

/// <summary>STERP-XMD (W4): Controls and reconciliation: reconciliation control, chain trace, legacy conversion, statutory obligations. Specification: application-specs/STERP-XMD.md.</summary>
public sealed class XmdModule : ISterpModule
{
    public string Slice => "STERP-XMD";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IChainTraceService, ChainTraceProgram>();
        services.Configure<XmdOptions>(configuration.GetSection(XmdOptions.Section));
        services.AddOptions<XmdOptions>().PostConfigure<IHostEnvironment>((o, env) => o.LegacyRoot = SterpPaths.Resolve(o.LegacyRoot, env.ContentRootPath));
    }

    public void MapOperations(OperationRegistry operations)
    {
        ChainTraceEndpoints.Map(operations);
        XmdJobSubmission.Map(operations, XmdJobs.All);
    }

    public void MapJobs(JobCatalog jobs)
    {
        foreach (var d in XmdJobs.All) jobs.Add(d);
    }
}

/// <summary>Configuration of the slice (section "Xmd").</summary>
public sealed class XmdOptions
{
    public const string Section = "Xmd";

    /// <summary>
    /// The legacy application root a relative CONVLOAD FILE resolves against (D-XMD-08: the legacy working directory). A relative
    /// value is resolved against the host's content root; the default is the legacy STERP-App clone next to this repository.
    /// </summary>
    public string LegacyRoot { get; set; } = "../../../../STERP-App";
}
