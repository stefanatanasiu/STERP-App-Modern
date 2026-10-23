using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Cst.Endpoints;
using Sterp.Modules.Cst.Engines;
using Sterp.Modules.Cst.Jobs;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Cst;

/// <summary>STERP-CST (W10): Costing: cost sets, cost rollup, cost enquiry, job costing, inventory valuation. Specification: application-specs/STERP-CST.md.</summary>
public sealed class CstModule : ISterpModule
{
    public string Slice => "STERP-CST";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // engines are request (or job step) scoped: no WORKING-STORAGE survives the request (spec 7.4, CS-01..CS-04)
        services.AddScoped<StdCostEngine>();
        services.AddScoped<JobCostEngine>();
        services.AddScoped<WipCalcEngine>();
        services.AddScoped<LabOvhEngine>();
        services.AddScoped<CostRollEngine>();
        services.AddScoped<CostNormEngine>();
        services.AddScoped<MarginEngine>();
        services.AddScoped<WhatIfEngine>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        CstEndpoints.Map(operations);
        // the listings are launched as the report launcher did (launcher row + the program's own JOBLOG row, SME-010)
        CstJobs.MapSubmissions(operations);
    }

    public void MapJobs(JobCatalog jobs)
    {
        // CSTINVAL is also STEP030 of the FIN-owned MONTHEND stream (found by program name)
        CstJobs.MapJobs(jobs);
    }
}
