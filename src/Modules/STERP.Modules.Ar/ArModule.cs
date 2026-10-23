using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Ar.Application;
using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Endpoints;
using Sterp.Modules.Ar.Jobs;
using Sterp.Modules.Ar.Lookups;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;

namespace Sterp.Modules.Ar;

/// <summary>STERP-AR (W2): Debtors: customers, credit control, receipts, allocation, invoicing/posting, dunning, direct debits, e-invoices. Specification: application-specs/STERP-AR.md.</summary>
public sealed class ArModule : ISterpModule
{
    public string Slice => "STERP-AR";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
#if STERP_KERNEL
        // engines (scoped: they run on the request's or job step's engine unit of work)
        services.AddScoped<ArGlPostingEngine>();
        services.AddScoped<CustomerMetricsEngine>();
        services.AddScoped<ReceiptAllocationEngine>();
        services.AddScoped<DisputeEngine>();
        services.AddScoped<CreditCheckEngine>();
        services.AddScoped<VatWriterEngine>();
        services.AddScoped<DepositEngine>();
        services.AddScoped<InvoicePostingEngine>();
        services.AddScoped<CreditNoteEngine>();
        // public in-process contracts consumed by STERP-SO (contracts section 3; STERP-AR.md section 7.2)
        services.AddScoped<ICreditCheckService>(sp => sp.GetRequiredService<CreditCheckEngine>());
        services.AddScoped<IArDepositService>(sp => sp.GetRequiredService<DepositEngine>());
        services.AddScoped<IArInvoicePostingService>(sp => sp.GetRequiredService<InvoicePostingEngine>());
        services.AddScoped<IArCreditNoteService>(sp => sp.GetRequiredService<CreditNoteEngine>());
#else
        services.AddScoped<ICreditCheckService, PendingCreditCheckService>();
        services.AddScoped<IArDepositService, PendingArDepositService>();
        services.AddScoped<IArInvoicePostingService, PendingArInvoicePostingService>();
        services.AddScoped<IArCreditNoteService, PendingArCreditNoteService>();
#endif
        // named F2 lookups (STERP-PLATFORM 4.4)
        services.AddScoped<ILookupSource, CustomerLookup>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        // DebtorsService enquiry members (SqlHelper family, direct outcome)
        operations.Handle("ar_listCustomers", FacadeQueries.ListCustomersAsync);
        operations.Handle("ar_getCustomerEnquiry", FacadeQueries.GetCustomerEnquiryAsync);
        operations.Handle("ar_getCustomerStatementLines", FacadeQueries.GetCustomerStatementLinesAsync);
        operations.Handle("ar_getAgedDebtSummary", FacadeQueries.GetAgedDebtSummaryAsync);
        operations.Handle("ar_listCreditOverrides", FacadeQueries.ListCreditOverridesAsync);
        operations.Handle("ar_getAllocatableItems", FacadeQueries.GetAllocatableItemsAsync);
        operations.Handle("ar_getCreditControlWorklist", FacadeQueries.GetCreditControlWorklistAsync);
#if STERP_KERNEL
        // DebtorsService engine members (COBOL-backed, envelope outcome)
        operations.Handle("ar_getCustomerMetrics", EngineEndpoints.GetCustomerMetricsAsync);
        operations.Handle("ar_checkCustomerCredit", EngineEndpoints.CheckCustomerCreditAsync);
        operations.Handle("ar_requestCreditOverride", EngineEndpoints.RequestCreditOverrideAsync);
        operations.Handle("ar_approveCreditOverride", EngineEndpoints.ApproveCreditOverrideAsync);
        operations.Handle("ar_recordReceipt", EngineEndpoints.RecordReceiptAsync);
        operations.Handle("ar_previewAllocation", EngineEndpoints.PreviewAllocationAsync);
        operations.Handle("ar_allocateReceipt", EngineEndpoints.AllocateReceiptAsync);
        operations.Handle("ar_unallocateAllocation", EngineEndpoints.UnallocateAllocationAsync);
        operations.Handle("ar_raiseDispute", EngineEndpoints.RaiseDisputeAsync);
        operations.Handle("ar_recordPromiseToPay", EngineEndpoints.RecordPromiseToPayAsync);
        // batch programs and streams (STERP-JOBS submissions)
        ArJobs.MapSubmissions(operations);
#endif
    }

    public void MapJobs(JobCatalog jobs)
    {
#if STERP_KERNEL
        ArJobs.Map(jobs);
#endif
    }
}
