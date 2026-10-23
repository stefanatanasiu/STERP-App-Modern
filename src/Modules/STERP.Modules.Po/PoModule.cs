using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Po.Endpoints;
using Sterp.Modules.Po.Lookups;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;

namespace Sterp.Modules.Po;

/// <summary>STERP-PO (W6): Purchasing: requisitions, purchase orders, approvals, goods receipt, inspection. Specification: application-specs/STERP-PO.md.</summary>
public sealed class PoModule : ISterpModule
{
    public string Slice => "STERP-PO";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // one instance per legacy program, scoped: they run on the request's (or job run's) engine connection
        services.AddScoped<BudgchkProgram>();
        services.AddScoped<PoreqProgram>();
        services.AddScoped<PopriceProgram>();
        services.AddScoped<SourcselProgram>();
        services.AddScoped<PosupchkProgram>();
        services.AddScoped<PaytermcProgram>();
        services.AddScoped<PoapprvProgram>();
        services.AddScoped<PoamendProgram>();
        services.AddScoped<PoorderProgram>();
        services.AddScoped<TolchkProgram>();
        services.AddScoped<PopostProgram>();
        services.AddScoped<ReceiptProgram>();
        services.AddScoped<InspectProgram>();
        services.AddScoped<ILookupSource, OpenOrdersLookup>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        RequisitionEndpoints.Map(operations);
        PurchaseCheckEndpoints.Map(operations);
        PurchaseOrderEndpoints.Map(operations);
        GoodsReceiptEndpoints.Map(operations);
    }

    public void MapJobs(JobCatalog jobs)
    {
        jobs.Add(Jobs.ReqgenJob.Definition);
        jobs.Add(Jobs.PopipeJob.Definition);
        jobs.Add(Jobs.OverdueJob.Definition);
        jobs.Add(Jobs.SuppperfJob.Definition);
        jobs.Add(Jobs.PodocJob.Definition);
    }
}
