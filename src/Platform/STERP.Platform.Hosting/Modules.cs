using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Platform.Hosting;

/// <summary>
/// One slice library (contracts section 3): registers its services, its online operation handlers (STERP.Api) and its job
/// definitions (STERP.Jobs). Cross-slice calls use the callee slice's public in-process contracts registered here.
/// </summary>
public interface ISterpModule
{
    /// <summary>Target application id of the slice, e.g. "STERP-ITM".</summary>
    string Slice { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    /// <summary>Online and job-run operation handlers of the slice's binding document.</summary>
    void MapOperations(OperationRegistry operations)
    {
    }

    /// <summary>Job definitions (programs, streams) owned by the slice.</summary>
    void MapJobs(JobCatalog jobs)
    {
    }
}

/// <summary>The modules composed into a host (FR-STERP-API-002, FR-STERP-JOBS-002).</summary>
public sealed class SterpModuleSet(IReadOnlyList<ISterpModule> modules)
{
    public IReadOnlyList<ISterpModule> Modules { get; } = modules;

    public IEnumerable<string> Slices => Modules.Select(m => m.Slice);
}
