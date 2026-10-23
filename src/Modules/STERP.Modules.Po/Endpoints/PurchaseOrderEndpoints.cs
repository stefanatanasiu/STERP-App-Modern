using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Endpoints;

/// <summary>
/// PurchaseOrderService (dotnet/STERP.Business/Purchasing/PurchaseOrderService.vb) order operations: open-order enquiry
/// (po.usp_OpenOrders), create / add line / amend / issue (POORDER) and the approval check / request / decision (POAPPRV).
/// FR-STERP-PO-021..023, 027..029.
/// </summary>
internal static class PurchaseOrderEndpoints
{
    /// <summary>PoOpenOrderRow property names (the procedure columns in the contract's camelCase).</summary>
    private static readonly string[] OpenOrderNames =
    [
        "orderNumber", "orderType", "siteCode", "supplierCode", "buyerUser", "orderDate", "currencyCode", "status",
        "deliveryStatus", "matchStatus", "termsCode", "termsSnapshot", "incotermsRule", "netAmount", "vatAmount", "grossAmount",
        "commitmentGbp", "approvedBy", "approvedAt", "issuedAt", "acknowledgedAt", "blanketOrderNumber", "budgetCode",
        "lineCount", "outstandingQty", "outstandingGbp", "receivedNotInvoicedGbp", "earliestDueDate", "latestDueDate",
        "overdueLines", "isCommitment",
    ];

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("po_listOpenOrders", OpenOrdersAsync);
        ops.Handle("po_createPurchaseOrder", CreateAsync);
        ops.Handle("po_addPurchaseOrderLine", AddLineAsync);
        ops.Handle("po_amendPurchaseOrder", AmendAsync);
        ops.Handle("po_issuePurchaseOrder", IssueAsync);
        ops.Handle("po_checkPurchaseOrderApproval", ctx => ApprovalAsync(ctx, "CHECK"));
        ops.Handle("po_requestPurchaseOrderApproval", ctx => ApprovalAsync(ctx, "REQUEST"));
        ops.Handle("po_decidePurchaseOrderApproval", ctx => ApprovalAsync(ctx, "DECIDE"));
    }

    private static string OrderNumber(OperationContext ctx) => ctx.Route("orderNumber").Trim().ToUpperInvariant();

    /// <summary>po_listOpenOrders: PurchaseOrderService.OpenOrders (PurchaseOrderService.vb:45-50) -> po.usp_OpenOrders (SqlHelper).</summary>
    private static async Task<IResult> OpenOrdersAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "PurchaseOrderService.OpenOrders";
        static string? Box(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("po.usp_OpenOrders", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SiteCode = ctx.Session.SiteCode,
            SupplierCode = Box(ctx.Query("supplierCode")),
            OrderNumber = Box(ctx.Query("orderNumber")),
            IncludeClosed = ctx.QueryBool("includeClosed") == true ? 1 : 0,
        }, ctx.Aborted);
        var named = new JsonArray();
        foreach (var row in rows)
        {
            var o = new JsonObject();
            foreach (var (k, v) in row!.AsObject())
            {
                var name = OpenOrderNames.FirstOrDefault(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase)) ?? k;
                o[name] = v?.DeepClone();
            }
            named.Add(o);
        }
        return ctx.List(named);
    }

    /// <summary>po_createPurchaseOrder: PurchaseOrderService.Create (PurchaseOrderService.vb:65-84) -> POORDER CREATE.</summary>
    private static async Task<IResult> CreateAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POORDER";
        var v = await ctx.BodyObjectAsync();
        var type = PoMap.S(v, "orderType");
        var ccy = PoMap.S(v, "currency");
        var or = new PoorderArea
        {
            OrderType = type.Length == 0 ? "STANDARD" : type,
            Supplier = PoMap.S(v, "supplier"),
            Buyer = ctx.Session.UserCode,
            OrderDate = PoMap.Business(ctx),
            Currency = ccy.Length == 0 ? ctx.Session.CompanyAccess?.BaseCurrency ?? "" : ccy,
            TermsCode = PoMap.S(v, "termsCode"),
            Incoterms = PoMap.S(v, "incoterms"),
            NamedPlace = PoMap.T(v, "namedPlace"),
            DeliveryAddr = PoMap.T(v, "deliveryAddr"),
            SupplierRef = PoMap.T(v, "supplierRef"),
            BudgetCode = PoMap.S(v, "budgetCode"),
            BlanketNumber = PoMap.S(v, "blanketNumber"),
            LinkedSo = PoMap.S(v, "linkedSo"),
        };
        return await RunAsync(ctx, "CREATE", or);
    }

    /// <summary>po_addPurchaseOrderLine: PurchaseOrderService.AddLine (PurchaseOrderService.vb:87-118) -> POORDER ADDLINE.</summary>
    private static async Task<IResult> AddLineAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POORDER";
        var v = await ctx.BodyObjectAsync();
        var lineType = PoMap.S(v, "lineType");
        var destSite = PoMap.S(v, "destSite");
        var price = PoMap.D(v, "unitPrice");
        var or = new PoorderArea
        {
            OrderNumber = OrderNumber(ctx),
            LineType = lineType.Length == 0 ? "STOCK" : lineType,
            ItemCode = PoMap.S(v, "itemCode"),
            Revision = PoMap.S(v, "revision"),
            Description = PoMap.T(v, "description"),
            OrderedQty = PoMap.D(v, "orderedQty"),
            UomCode = PoMap.S(v, "uomCode"),
            UnitPrice = price,
            PricePer = PoMap.D(v, "pricePer"),
            DiscountPct = PoMap.D(v, "discountPct"),
            ChargeAmt = PoMap.D(v, "chargeAmt"),
            VatCode = PoMap.S(v, "vatCode"),
            AccountCode = PoMap.S(v, "accountCode"),
            CostCentre = PoMap.S(v, "costCentre"),
            Project = PoMap.S(v, "project"),
            DestSite = destSite.Length == 0 ? ctx.Session.SiteCode : destSite,
            DestLocation = PoMap.S(v, "destLocation"),
            ReqNumber = PoMap.S(v, "reqNumber"),
            AutoPrice = price == 0m ? "Y" : "N",
            SchReqDate = PoMap.Dt(v, "schReqDate", PoMap.Business(ctx)),
        };
        return await RunAsync(ctx, "ADDLINE", or);
    }

    /// <summary>po_amendPurchaseOrder: PurchaseOrderService.Amend (PurchaseOrderService.vb:166-177) -> POORDER AMEND -> POAMEND.</summary>
    private static async Task<IResult> AmendAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POAMEND";
        var v = await ctx.BodyObjectAsync();
        var or = new PoorderArea
        {
            OrderNumber = OrderNumber(ctx),
            LineNum = PoMap.I(v, "lineNum"),
            AmendField = CaJson.Find(v, "amendField") is null ? "" : CaJson.Text(v, "amendField"),
            AmendNew = PoMap.T(v, "amendNew"),
            AmendReason = PoMap.T(v, "amendReason"),
        };
        return await RunAsync(ctx, "AMEND", or);
    }

    /// <summary>po_issuePurchaseOrder: PurchaseOrderService.Act("ISSUE") (PurchaseOrderService.vb:135-148) -> POORDER ISSUE.</summary>
    private static async Task<IResult> IssueAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POORDER";
        var v = await ctx.BodyObjectAsync();
        var or = new PoorderArea
        {
            OrderNumber = OrderNumber(ctx),
            CopyFrom = PoMap.S(v, "copyFrom"),
            BlanketNumber = PoMap.S(v, "blanketNumber"),
            Supplier = PoMap.S(v, "supplier"),
            SchQty = PoMap.D(v, "schQty"),
            ItemCode = PoMap.S(v, "itemCode"),
        };
        if (PoMap.T(v, "schReqDate").Length > 0) or.SchReqDate = PoMap.Dt(v, "schReqDate", PoMap.Business(ctx));
        return await RunAsync(ctx, "ISSUE", or);
    }

    private static async Task<IResult> RunAsync(OperationContext ctx, string function, PoorderArea or)
    {
        or.Normalize();
        var ca = CaHeader.From(ctx.Session, function);
        await ctx.Get<PoorderProgram>().RunAsync(ca, or, ctx.Aborted);
        return PoMap.Answer(ctx, ca, or.ToJson());
    }

    /// <summary>The approval operations: PurchaseOrderService.Approval (PurchaseOrderService.vb:251-266) -> POAPPRV.</summary>
    private static async Task<IResult> ApprovalAsync(OperationContext ctx, string function)
    {
        ctx.Session.LegacyProgram = "POAPPRV";
        var v = await ctx.BodyObjectAsync();
        var av = new PoapprvArea
        {
            OrderNumber = OrderNumber(ctx),
            ApprovalType = "PURCHASEORDER",
            Approver = ctx.Session.UserCode,
            Decision = function == "DECIDE" && CaJson.Find(v, "decision") is not null ? CaJson.Text(v, "decision") : "",
            Comment = function == "CHECK" ? "" : PoMap.T(v, "comment"),
        };
        av.Normalize();
        var ca = CaHeader.From(ctx.Session, function);
        await ctx.Get<PoapprvProgram>().RunAsync(ca, av, ctx.Aborted);
        return PoMap.Answer(ctx, ca, av.ToJson());
    }
}
