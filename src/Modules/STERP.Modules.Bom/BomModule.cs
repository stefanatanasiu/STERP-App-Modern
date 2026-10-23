using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Bom.Application;
using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Data;
using Sterp.Modules.Bom.Domain;
using Sterp.Modules.Bom.Endpoints;
using Sterp.Modules.Bom.Jobs;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Bom;

/// <summary>STERP-BOM (W8): Bills of Material: BOM editor, explosion, where-used, costing, bulk change. Specification: application-specs/STERP-BOM.md.</summary>
public sealed class BomModule : ISterpModule
{
    public string Slice => "STERP-BOM";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // data access on the request's (or job step's) unit of work
        services.AddScoped<BillReader>();
        services.AddScoped<BillSelector>();
        // engines: no state outlives a call; results belong to the area that computed them
        services.AddScoped<ExplosionEngine>();
        services.AddScoped<CostEngine>();
        services.AddScoped<NetPlanner>();
        services.AddScoped<LeafViews>();
        services.AddScoped<BomFacade>();
        // structure checks and the bulk change preview: one instance per request or job step (the run unit), so the
        // working storage that legacy GETLINE calls page lives exactly as long as the calls that filled it
        services.AddScoped<CycleChecker>();
        services.AddScoped<WhereUsedWalker>();
        services.AddScoped<BillValidator>();
        services.AddScoped<ProposalBuilder>();
        services.AddScoped<BulkChangeEngine>();
        services.AddScoped<BomChangeFacade>();
        services.AddScoped<RevisionComparer>();
        // public in-process contracts (contracts section 3): IC-BOM-BOMEXPL for KIT, IC-BOM-BOMCOST for CST
        services.AddScoped<IBomExplosionEngine>(sp => sp.GetRequiredService<ExplosionEngine>());
        services.AddScoped<IBomCostEngine>(sp => sp.GetRequiredService<CostEngine>());
    }

    public void MapOperations(OperationRegistry operations)
    {
        operations.Handle("bom_explodeBill", BomEndpoints.ExplodeAsync);
        operations.Handle("bom_costBill", BomEndpoints.CostAsync);
        operations.Handle("bom_netBillRequirement", BomEndpoints.NetAsync);
        operations.Handle("bom_validateBill", BomEndpoints.ValidateAsync);
        operations.Handle("bom_previewBulkChange", BomEndpoints.PreviewBulkChangeAsync);
        operations.Handle("bom_getWhereUsed", BomEndpoints.WhereUsedAsync);
        operations.Handle("bom_compareBillRevisions", BomEndpoints.CompareAsync);
        operations.Handle("bom_listBillRevisions", BomEndpoints.RevisionsAsync);
        operations.Handle("bom_getBillHeader", BomEndpoints.HeaderAsync);
        operations.Handle("bom_listBillComponents", BomEndpoints.ComponentsAsync);
        operations.Handle("bom_listBillByProducts", BomEndpoints.ByProductsAsync);
        operations.Handle("bom_releaseBillRevision", BomEndpoints.ReleaseAsync);
        // the listings are launched as the report launcher did (catalogue row permission, launcher row + JOBLOG row)
        BomJobSubmission.Map(operations, Bomrp010.Value, "BOMRP010", ["BOMRP010", "BOMRP020", "RPT-012"]);
        BomJobSubmission.Map(operations, Bomrp030.Value, "BOMRP030", ["BOMRP030", "BOMRP040"]);
    }

    private static readonly Lazy<JobDefinition> Bomrp010 = new(Bomrp010Program.Definition);
    private static readonly Lazy<JobDefinition> Bomrp030 = new(Bomrp030Program.Definition);

    public void MapJobs(JobCatalog jobs)
    {
        jobs.Add(Bomrp010.Value);
        jobs.Add(Bomrp030.Value);
    }
}
