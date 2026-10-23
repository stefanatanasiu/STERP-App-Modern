using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Itm.Online;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Itm;

/// <summary>STERP-ITM (W1): Stock Control: item master, stock movements, availability, reservations, counts, lots/serials, units, substitutes, stock reports. Specification: application-specs/STERP-ITM.md.</summary>
public sealed class ItmModule : ISterpModule
{
    public string Slice => "STERP-ITM";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddItmPrograms();
    }

    public void MapOperations(OperationRegistry operations)
    {
        ItemEndpoints.Map(operations);
        StockQueryEndpoints.Map(operations);
        StockMovementEndpoints.Map(operations);
        CobolQueryEndpoints.Map(operations);
        CountEndpoints.Map(operations);
        Jobs.ItmJobSubmission.Map(operations, Jobs.ItmJobCatalog.All);
    }

    public void MapJobs(JobCatalog jobs)
    {
        foreach (var d in Jobs.ItmJobCatalog.All) jobs.Add(d);
    }
}
