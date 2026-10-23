using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.So.Application;
using Sterp.Modules.So.Domain;
using Sterp.Modules.So.Endpoints;
using Sterp.Modules.So.Jobs;
using Sterp.Modules.So.Lookups;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;

namespace Sterp.Modules.So;

/// <summary>STERP-SO (W3): Sales: sales order entry, pricing, totals, VAT, margin, availability, reservation, acceptance, despatch, invoicing run. Specification: application-specs/STERP-SO.md.</summary>
public sealed class SoModule : ISterpModule
{
    public string Slice => "STERP-SO";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // the re-implemented COBOL programs (scoped: they run on the request's engine unit of work)
        services.AddScoped<PricingProgram>();
        services.AddScoped<SodiscProgram>();
        services.AddScoped<SovatProgram>();
        services.AddScoped<SomargnProgram>();
        services.AddScoped<SoavailProgram>();
        services.AddScoped<SoresvProgram>();
        services.AddScoped<SoacceptProgram>();
        services.AddScoped<DespatchProgram>();
        // named F2 lookups (STERP-PLATFORM 4.4)
        services.AddScoped<ILookupSource, OrderLookup>();
        services.AddScoped<ILookupSource, PickListLookup>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        SalesOrderQueries.Map(operations);
        ProgramEndpoints.Map(operations);
        // batch program submissions (STERP.Jobs)
        SoJobs.MapSubmissions(operations);
    }

    public void MapJobs(JobCatalog jobs)
    {
        SoJobs.Map(jobs);
    }
}
