using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Po.Endpoints;

/// <summary>
/// PurchaseOrderService (dotnet/STERP.Business/Purchasing/PurchaseOrderService.vb) price, budget and supplier checks:
/// po_explainPurchasePrice, po_listPriceBreaks (POPRICE), po_checkBudget (BUDGCHK CHECK), po_checkSupplierDiligence
/// (POSUPCHK CHECK), po_checkPaymentTerms (PAYTERMC CHECK). FR-STERP-PO-024..026.
/// </summary>
internal static class PurchaseCheckEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("po_explainPurchasePrice", ExplainAsync);
        ops.Handle("po_listPriceBreaks", BreaksAsync);
        ops.Handle("po_checkBudget", CheckBudgetAsync);
        ops.Handle("po_checkSupplierDiligence", DiligenceAsync);
        ops.Handle("po_checkPaymentTerms", TermsAsync);
    }

    /// <summary>frmPurchaseOrder.V: the box text trimmed and upper-cased.</summary>
    private static string V(OperationContext ctx, string name) => (ctx.Query(name) ?? "").Trim().ToUpperInvariant();

    private static string BaseCurrency(OperationContext ctx) => ctx.Session.CompanyAccess?.BaseCurrency ?? "";

    /// <summary>PurchaseOrderService.Explain (PurchaseOrderService.vb:182-195) -> POPRICE PRICE at the business date.</summary>
    private static async Task<IResult> ExplainAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POPRICE";
        var qtyText = (ctx.Query("quantity") ?? "").Trim();
        var qty = PoMap.D(new JsonObject { ["q"] = qtyText }, "q");
        var currency = V(ctx, "currency");
        var pr = new PopriceArea
        {
            Supplier = V(ctx, "supplier"),
            ItemCode = V(ctx, "itemCode"),
            UomCode = V(ctx, "uomCode"),
            Currency = currency.Length == 0 ? BaseCurrency(ctx) : currency,
            Quantity = qty,
            PricingDate = PoMap.Business(ctx),
        };
        return await PriceAsync(ctx, "PRICE", pr);
    }

    /// <summary>PurchaseOrderService.Breaks (PurchaseOrderService.vb:218-227) -> POPRICE BREAKS without a quantity.</summary>
    private static async Task<IResult> BreaksAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POPRICE";
        var pr = new PopriceArea
        {
            Supplier = V(ctx, "supplier"),
            ItemCode = V(ctx, "itemCode"),
            PricingDate = PoMap.Business(ctx),
        };
        return await PriceAsync(ctx, "BREAKS", pr);
    }

    private static async Task<IResult> PriceAsync(OperationContext ctx, string function, PopriceArea pr)
    {
        pr.Normalize();
        var ca = CaHeader.From(ctx.Session, function);
        await ctx.Get<PopriceProgram>().RunAsync(ca, pr, ctx.Aborted);
        return PoMap.Answer(ctx, ca, pr.ToJson());
    }

    /// <summary>PurchaseOrderService.CheckBudget (PurchaseOrderService.vb:230-248) -> BUDGCHK CHECK; rule R4.</summary>
    private static async Task<IResult> CheckBudgetAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BUDGCHK";
        var v = await ctx.BodyObjectAsync();
        var docType = PoMap.S(v, "docType");
        var bc = new BudgchkArea
        {
            BudgetCode = PoMap.S(v, "budgetCode"),
            AccountCode = PoMap.S(v, "accountCode"),
            CostCentre = PoMap.S(v, "costCentre"),
            Project = PoMap.S(v, "project"),
            DocType = docType.Length == 0 ? "ORDER" : docType,
            DocRef = PoMap.S(v, "docRef"),
            Requester = ctx.Session.UserCode,
            MovementDate = PoMap.Business(ctx),
            NetGbp = PoMap.D(v, "netGbp"),
            NonrecVatGbp = PoMap.D(v, "nonrecVatGbp"),
            ChargeGbp = PoMap.D(v, "chargeGbp"),
            OverrideBy = PoMap.S(v, "overrideBy"),
            OverrideReason = PoMap.T(v, "overrideReason"),
        };
        bc.Normalize();
        var ca = CaHeader.From(ctx.Session, "CHECK");
        await ctx.Get<BudgchkProgram>().RunAsync(ca, bc, ctx.Aborted);
        return PoMap.Answer(ctx, ca, bc.ToJson(), dataOnProblem: true);
    }

    /// <summary>PurchaseOrderService.Diligence (PurchaseOrderService.vb:273-281) -> POSUPCHK CHECK; rule R4.</summary>
    private static async Task<IResult> DiligenceAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POSUPCHK";
        var v = await ctx.BodyObjectAsync();
        var dd = new PosupchkArea
        {
            Supplier = ctx.Route("supplierCode").Trim().ToUpperInvariant(),
            CheckDate = PoMap.Business(ctx),
            OrderValueGbp = PoMap.D(v, "orderValueGbp"),
            CheckedBy = ctx.Session.UserCode,
        };
        dd.Normalize();
        var ca = CaHeader.From(ctx.Session, "CHECK");
        await ctx.Get<PosupchkProgram>().RunAsync(ca, dd, ctx.Aborted);
        return PoMap.Answer(ctx, ca, dd.ToJson(), dataOnProblem: true);
    }

    /// <summary>PurchaseOrderService.Terms (PurchaseOrderService.vb:284-293) -> PAYTERMC CHECK; rule R4.</summary>
    private static async Task<IResult> TermsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "PAYTERMC";
        var v = await ctx.BodyObjectAsync();
        var pt = new PaytermcArea
        {
            Supplier = ctx.Route("supplierCode").Trim().ToUpperInvariant(),
            OrderNumber = PoMap.S(v, "orderNumber"),
            TermsCode = PoMap.S(v, "termsCode"),
            OrderDate = PoMap.Business(ctx),
            ValueGbp = PoMap.D(v, "valueGbp"),
        };
        pt.Normalize();
        var ca = CaHeader.From(ctx.Session, "CHECK");
        await ctx.Get<PaytermcProgram>().RunAsync(ca, pt, ctx.Aborted);
        return PoMap.Answer(ctx, ca, pt.ToJson(), dataOnProblem: true);
    }
}
