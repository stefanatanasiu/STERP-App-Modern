using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Ap.Batch;
using Sterp.Modules.Ap.Enquiry;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Ap;

/// <summary>STERP-AP (W7): Creditors: suppliers, verification, invoice register, matching, payment runs, Bacs. Specification: application-specs/STERP-AP.md.</summary>
public sealed class ApModule : ISterpModule
{
    private readonly JobDefinition[] _jobs =
    [
        ApAgedJob.Definition,
        ApBacsJob.Definition,
        ApRemitJob.Definition,
        ApPprJob.Definition,
        ApVat6mJob.Definition,
        ApPayrunStream.Definition,
    ];

    public string Slice => "STERP-AP";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapOperations(OperationRegistry operations)
    {
        operations.Handle("ap_listSuppliers", ApEnquiryEndpoints.ListSuppliersAsync);
        operations.Handle("ap_listSupplierBanks", ApEnquiryEndpoints.ListSupplierBanksAsync);
        operations.Handle("ap_listPurchaseInvoices", ApEnquiryEndpoints.ListPurchaseInvoicesAsync);
        operations.Handle("ap_listOpenItems", ApEnquiryEndpoints.ListOpenItemsAsync);
        operations.Handle("ap_listPaymentProposals", ApEnquiryEndpoints.ListPaymentProposalsAsync);
        operations.Handle("ap_listPaymentProposalLines", ApEnquiryEndpoints.ListPaymentProposalLinesAsync);
        operations.Handle("ap_validateSupplier", Suppliers.SupplierEndpoints.ValidateAsync);
        operations.Handle("ap_checkSupplierCanPay", Suppliers.SupplierEndpoints.CanPayAsync);
        operations.Handle("ap_prepareSupplierBank", Suppliers.SupplierEndpoints.PrepareBankAsync);
        operations.Handle("ap_recordBankConfirmationOfPayee", Suppliers.SupplierEndpoints.ConfirmationOfPayeeAsync);
        operations.Handle("ap_recordBankCallback", Suppliers.SupplierEndpoints.RecordCallbackAsync);
        operations.Handle("ap_approveSupplierBank", Suppliers.SupplierEndpoints.ApproveBankAsync);
        operations.Handle("ap_checkDuplicateInvoice", Invoices.InvoiceEndpoints.DuplicateCheckAsync);
        operations.Handle("ap_matchPurchaseInvoice", Invoices.InvoiceEndpoints.MatchAllAsync);
        operations.Handle("ap_selectPaymentProposal", Payments.PaymentEndpoints.SelectAsync);
        operations.Handle("ap_getPaymentProposalTotals", Payments.PaymentEndpoints.TotalsAsync);
        operations.Handle("ap_reservePaymentProposal", Payments.PaymentEndpoints.ReserveAsync);
        operations.Handle("ap_releasePaymentProposal", Payments.PaymentEndpoints.ReleaseAsync);
        operations.Handle("ap_requestPaymentApproval", Payments.PaymentEndpoints.RequestApprovalAsync);
        operations.Handle("ap_decidePaymentApproval", Payments.PaymentEndpoints.DecideAsync);
        operations.Handle("ap_createPaymentInstructions", Payments.PaymentEndpoints.CreateInstructionsAsync);
        foreach (var d in _jobs)
        {
            var definition = d;
            if (definition.SubmitOperationId is { } op) operations.Handle(op, ctx => ApJobSubmission.SubmitAsync(ctx, definition));
        }
    }

    public void MapJobs(JobCatalog jobs)
    {
        foreach (var d in _jobs) jobs.Add(d);
    }
}
