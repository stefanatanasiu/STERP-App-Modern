using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Data;
using Sterp.Modules.Kit.Endpoints;
using Sterp.Modules.Kit.Jobs;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Kit;

/// <summary>STERP-KIT (W9): Kits: kit maintenance, trial, execution, genealogy, readiness. Specification: application-specs/STERP-KIT.md.</summary>
public sealed class KitModule : ISterpModule
{
    public string Slice => "STERP-KIT";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // KITDB on the request's (or job step's) SQLIO unit of work; its line cursor lives for the request only
        services.AddScoped<KitDb>();
        // engines: one instance per request - their WORKING-STORAGE never outlives the calls of one request (HD-16)
        services.AddScoped<KitCreat>();
        services.AddScoped<KitService>();
        services.AddScoped<KitCost>();
        services.AddScoped<KitCostService>();
        services.AddScoped<KitIssue>();
        services.AddScoped<KitIssueService>();
        services.AddScoped<KitTrial>();
        services.AddScoped<KitTrialService>();
        services.AddScoped<KitComm>();
        services.AddScoped<KitCommService>();
        services.AddScoped<Genealog>();
        services.AddScoped<GenealogService>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        operations.Handle("kit_listKitWorklist", KitEndpoints.WorklistAsync);
        operations.Handle("kit_getKit", KitEndpoints.GetKitAsync);
        operations.Handle("kit_reviewKit", KitEndpoints.ReviewAsync);
        operations.Handle("kit_releaseKit", KitEndpoints.ReleaseAsync);
        operations.Handle("kit_checkKitKind", KitEndpoints.KindCheckAsync);
        operations.Handle("kit_getKitCost", KitEndpoints.CostAsync);
        operations.Handle("kit_checkComponentIssue", KitEndpoints.CheckIssueAsync);
        operations.Handle("kit_issueComponent", KitEndpoints.IssueAsync);
        operations.Handle("kit_backflushKit", KitEndpoints.BackflushAsync);
        operations.Handle("kit_runTrialKitting", KitEndpoints.TrialAsync);
        operations.Handle("kit_runMaxBuildTrial", KitEndpoints.MaxBuildAsync);
        operations.Handle("kit_checkCommercialKitSets", KitEndpoints.CommercialSetsAsync);
        operations.Handle("kit_traceGenealogy", KitEndpoints.GenealogyAsync);
        operations.Handle("kit_listRecallTrace", KitEndpoints.RecallListAsync);
    }

    public void MapJobs(JobCatalog jobs)
    {
        foreach (var d in KitJobs.All) jobs.Add(d);
    }
}
