using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Po.Endpoints;

/// <summary>
/// RequisitionService (dotnet/STERP.Business/Purchasing/RequisitionService.vb) operations: create, add line, enquire,
/// submit (POREQ) and the approved-source check (SOURCSEL CHECK). FR-STERP-PO-011..015.
/// </summary>
internal static class RequisitionEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("po_createRequisition", CreateAsync);
        ops.Handle("po_addRequisitionLine", AddLineAsync);
        ops.Handle("po_getRequisition", ctx => ActAsync(ctx, "ENQUIRE"));
        ops.Handle("po_submitRequisition", ctx => ActAsync(ctx, "SUBMIT"));
        ops.Handle("po_checkSupplierSource", SourceAsync);
    }

    /// <summary>RequisitionService.Area: the coding fields every header/line call carries (RequisitionService.vb:119-128).</summary>
    private static PoreqArea Area(JsonObject? v)
    {
        var rq = new PoreqArea();
        if (v is not null)
        {
            rq.CostCentre = PoMap.S(v, "costCentre");
            rq.Project = PoMap.S(v, "project");
            rq.JobRef = PoMap.S(v, "jobRef");
            rq.BudgetCode = PoMap.S(v, "budgetCode");
        }
        return rq;
    }

    /// <summary>po_createRequisition: RequisitionService.Create (RequisitionService.vb:34-46) -> POREQ CREATE.</summary>
    private static async Task<IResult> CreateAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POREQ";
        var v = await ctx.BodyObjectAsync();
        var business = PoMap.Business(ctx);
        var rq = Area(v);
        var requester = PoMap.S(v, "requester");
        rq.Requester = requester.Length == 0 ? ctx.Session.UserCode : requester;
        rq.Purpose = PoMap.T(v, "purpose");
        rq.ReqDate = business;
        rq.NeededBy = PoMap.Dt(v, "neededBy", business);
        var demand = PoMap.S(v, "demandType");
        rq.DemandType = demand.Length == 0 ? "MANUAL" : demand;
        rq.DemandRef = PoMap.S(v, "demandRef");
        return await RunAsync(ctx, "CREATE", rq);
    }

    /// <summary>po_addRequisitionLine: RequisitionService.AddLine (RequisitionService.vb:49-68) -> POREQ ADDLINE.</summary>
    private static async Task<IResult> AddLineAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "POREQ";
        var v = await ctx.BodyObjectAsync();
        var rq = Area(v);
        rq.ReqNumber = ReqNumber(ctx);
        rq.ItemCode = PoMap.S(v, "itemCode");
        rq.Description = PoMap.T(v, "description");
        rq.Quantity = PoMap.D(v, "quantity");
        rq.UomCode = PoMap.S(v, "uomCode");
        rq.EstUnitPrice = PoMap.D(v, "estUnitPrice");
        rq.SuggestSupp = PoMap.S(v, "suggestSupp");
        rq.AccountCode = PoMap.S(v, "accountCode");
        rq.LineNeededBy = PoMap.Dt(v, "lineNeededBy", PoMap.Business(ctx));
        return await RunAsync(ctx, "ADDLINE", rq);
    }

    /// <summary>po_getRequisition / po_submitRequisition: RequisitionService.Act (RequisitionService.vb:71-77).</summary>
    private static async Task<IResult> ActAsync(OperationContext ctx, string function)
    {
        ctx.Session.LegacyProgram = "POREQ";
        var rq = Area(null);
        rq.ReqNumber = ReqNumber(ctx);
        return await RunAsync(ctx, function, rq);
    }

    private static string ReqNumber(OperationContext ctx) => ctx.Route("reqNumber").Trim().ToUpperInvariant();

    private static async Task<IResult> RunAsync(OperationContext ctx, string function, PoreqArea rq)
    {
        rq.Normalize();
        var ca = CaHeader.From(ctx.Session, function);
        await ctx.Get<PoreqProgram>().RunAsync(ca, rq, ctx.Aborted);
        return PoMap.Answer(ctx, ca, rq.ToJson());
    }

    /// <summary>po_checkSupplierSource: RequisitionService.Source("CHECK") (RequisitionService.vb:105-117) -> SOURCSEL CHECK.</summary>
    private static async Task<IResult> SourceAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "SOURCSEL";
        var v = await ctx.BodyObjectAsync();
        var business = PoMap.Business(ctx);
        var ss = new SourcselArea
        {
            ReqNumber = PoMap.S(v, "reqNumber"),
            ItemCode = PoMap.S(v, "itemCode"),
            Supplier = PoMap.S(v, "supplier"),
            Quantity = PoMap.D(v, "quantity"),
            UomCode = PoMap.S(v, "uomCode"),
            Buyer = ctx.Session.UserCode,
            NeededBy = PoMap.Dt(v, "neededBy", business),
            EventDate = business,
        };
        ss.Normalize();
        var ca = CaHeader.From(ctx.Session, "CHECK");
        await ctx.Get<SourcselProgram>().RunAsync(ca, ss, ctx.Aborted);
        return PoMap.Answer(ctx, ca, ss.ToJson());
    }
}
