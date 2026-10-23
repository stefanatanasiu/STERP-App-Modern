using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Enquiry;

/// <summary>
/// The read-only facade enquiries of the slice (SqlHelper family: one short autocommit connection per call, "" -> NULL):
/// SupplierService.Suppliers/Banks, InvoiceService.Register/OpenItems, PaymentService.Proposals/Lines over the procedures of
/// db/procs/ap/usp_ApEnquiry.sql.
/// </summary>
public static class ApEnquiryEndpoints
{
    /// <summary>ap_listSuppliers - SupplierService.Suppliers (SupplierService.vb:41-43) -> ap.usp_SupplierEnquiry (FR-STERP-AP-010).</summary>
    public static async Task<IResult> ListSuppliersAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_SupplierEnquiry", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SupplierCode = ctx.Query("filter"),
            Mode = ctx.Query("mode") ?? "ALL",
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ap_listSupplierBanks - SupplierService.Banks (SupplierService.vb:46-48) -> ap.usp_ListSupplierBanks (FR-STERP-AP-011).</summary>
    public static async Task<IResult> ListSupplierBanksAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_ListSupplierBanks", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SupplierCode = ctx.Route("supplierCode"),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ap_listPurchaseInvoices - InvoiceService.Register (InvoiceService.vb:22-24) -> ap.usp_ListInvoiceRegister (FR-STERP-AP-020).</summary>
    public static async Task<IResult> ListPurchaseInvoicesAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_ListInvoiceRegister", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SupplierCode = ctx.Query("filter"),
            Mode = ctx.Query("mode") ?? "ALL",
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ap_listOpenItems - InvoiceService.OpenItems (InvoiceService.vb:27-29) -> ap.usp_ListApOpenItems (FR-STERP-AP-021).</summary>
    public static async Task<IResult> ListOpenItemsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_ListApOpenItems", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SupplierCode = ctx.Query("supplierCode"),
            Mode = ctx.Query("mode") ?? "OPEN",
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ap_listPaymentProposals - PaymentService.Proposals (PaymentService.vb:24-26) -> ap.usp_ListProposals @ProposalRef "" (FR-STERP-AP-030).</summary>
    public static async Task<IResult> ListPaymentProposalsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_ListProposals", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            ProposalRef = "",
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ap_listPaymentProposalLines - PaymentService.Lines (PaymentService.vb:29-31) -> ap.usp_ListProposals @ProposalRef (FR-STERP-AP-031).</summary>
    public static async Task<IResult> ListPaymentProposalLinesAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("ap.usp_ListProposals", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            ProposalRef = ctx.Route("proposalRef"),
        }, ctx.Aborted);
        return ctx.List(rows);
    }
}
