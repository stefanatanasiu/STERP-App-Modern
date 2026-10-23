using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Ar.Data;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ar.Endpoints;

/// <summary>
/// The DebtorsService enquiry members (dotnet/STERP.Business/Debtors/DebtorsService.vb:17-58,90-96): stored procedure and
/// view reads through the SqlHelper family (one short connection per call, autocommit, "" -> NULL), returned direct.
/// </summary>
public static class FacadeQueries
{
    /// <summary>DebtorsService.CustomerPickSql (DebtorsService.vb:17-23).</summary>
    public const string CustomerPickSql =
        "SELECT CustomerCode, LegalName AS CustomerName, CurrencyCode, CreditState, BalanceGBP " +
        "FROM ar.vw_CustomerBalance WHERE CompanyCode = @CompanyCode " +
        "AND (@Search IS NULL OR CustomerCode LIKE @Search + '%' OR LegalName LIKE '%' + @Search + '%') " +
        "ORDER BY CustomerCode";

    /// <summary>DebtorsService.CreditOverrides (DebtorsService.vb:90-96).</summary>
    public const string CreditOverridesSql =
        "SELECT OverrideId, CustomerCode, Scope, DocumentRef, OverrideAmountGBP, Reason, " +
        "RequestedBy, ApprovedBy, ValidFrom, ExpiresOn, Status FROM ar.CreditOverride " +
        "WHERE CompanyCode = @CompanyCode AND (@RequestsOnly = 0 OR RequestedBy = @UserCode) " +
        "ORDER BY OverrideId DESC";

    /// <summary>ar_listCustomers: LookupService.Find over CustomerPickSql.</summary>
    public static async Task<IResult> ListCustomersAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(CustomerPickSql, ArParams.Helper(new { CompanyCode = ctx.Session.CompanyCode, Search = ctx.Query("search") }), ctx.Aborted);
        return ctx.List(ArSchemas.ConformRows(rows, "ArCustomerPick"));
    }

    /// <summary>ar_getCustomerEnquiry: DebtorsService.Enquiry -> ar.usp_CustomerEnquiry (five result sets).</summary>
    public static async Task<IResult> GetCustomerEnquiryAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var sets = await sql.QueryJsonSetsAsync("ar.usp_CustomerEnquiry", ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            CustomerCode = ctx.Route("customerCode"),
            UserCode = ctx.Session.UserCode,
        }), ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["balance"] = ArSchemas.FirstOrNull(sets, 0, "ArCustomerBalance"),
            ["agedProfile"] = ArSchemas.Rows(sets, 1, "ArAgedBucket"),
            ["turnover"] = ArSchemas.FirstOrNull(sets, 2, "ArTurnover"),
            ["openItems"] = ArSchemas.Rows(sets, 3, "ArEnquiryOpenItem"),
            ["collectionHistory"] = ArSchemas.Rows(sets, 4, "ArCollectionHistoryRow"),
        });
    }

    /// <summary>ar_getCustomerStatementLines: DebtorsService.Statement -> ar.usp_CustomerStatementLines (no @UserCode).</summary>
    public static async Task<IResult> GetCustomerStatementLinesAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var sets = await sql.QueryJsonSetsAsync("ar.usp_CustomerStatementLines", ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            CustomerCode = ctx.Route("customerCode"),
            AsOf = ctx.QueryDate("asAt"),
            Style = ctx.Query("style") ?? "OPENITEM",
        }), ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["header"] = ArSchemas.FirstOrNull(sets, 0, "ArStatementHeader"),
            ["lines"] = ArSchemas.Rows(sets, 1, "ArStatementLine"),
        });
    }

    /// <summary>ar_getAgedDebtSummary: DebtorsService.AgedDebt -> ar.usp_AgedDebtSummary @CompanyCode, @AsOf.</summary>
    public static async Task<IResult> GetAgedDebtSummaryAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var sets = await sql.QueryJsonSetsAsync("ar.usp_AgedDebtSummary", ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            AsOf = ctx.QueryDate("asOf"),
        }), ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["buckets"] = ArSchemas.Rows(sets, 0, "ArAgedDebtBucket"),
            ["accounts"] = ArSchemas.Rows(sets, 1, "ArAgedDebtAccount"),
        });
    }

    /// <summary>ar_listCreditOverrides: DebtorsService.CreditOverrides (ad-hoc query).</summary>
    public static async Task<IResult> ListCreditOverridesAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(CreditOverridesSql, ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            RequestsOnly = ctx.QueryBool("requestsOnly") ?? false,
            UserCode = ctx.Session.UserCode,
        }), ctx.Aborted);
        return ctx.List(ArSchemas.ConformRows(rows, "ArCreditOverrideRow"));
    }

    /// <summary>ar_getAllocatableItems: DebtorsService.Allocatable -> ar.usp_OpenItemsForAllocation (no @UserCode, no @AsOf).</summary>
    public static async Task<IResult> GetAllocatableItemsAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var sets = await sql.QueryJsonSetsAsync("ar.usp_OpenItemsForAllocation", ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            CustomerCode = ctx.Route("customerCode"),
            CurrencyCode = ctx.Query("currency"),
        }), ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["debitItems"] = ArSchemas.Rows(sets, 0, "ArAllocatableDebitItem"),
            ["credits"] = ArSchemas.Rows(sets, 1, "ArAllocatableCredit"),
            ["totals"] = ArSchemas.FirstOrNull(sets, 2, "ArAllocationTotals"),
        });
    }

    /// <summary>ar_getCreditControlWorklist: DebtorsService.Worklist -> ar.usp_CreditControlWorklist.</summary>
    public static async Task<IResult> GetCreditControlWorklistAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var sets = await sql.QueryJsonSetsAsync("ar.usp_CreditControlWorklist", ArParams.Helper(new
        {
            CompanyCode = ctx.Session.CompanyCode,
            UserCode = ctx.Session.UserCode,
            OwnerUser = ctx.Query("ownerUser"),
        }), ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["items"] = ArSchemas.Rows(sets, 0, "ArWorklistItem"),
            ["accounts"] = ArSchemas.Rows(sets, 1, "ArWorklistAccount"),
        });
    }
}
