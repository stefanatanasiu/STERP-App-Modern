using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Online;

/// <summary>
/// itm_postStockMovement (FR-STERP-ITM-016): StockService.Post (StockService.vb:51-81) mapping the page values into STKMOVECA
/// and LINKing STKMOVE with the request's Idempotency-Key as CA-CORRELATION-ID.
/// </summary>
public static class StockMovementEndpoints
{
    public static void Map(OperationRegistry ops) => ops.Handle("itm_postStockMovement", PostStockMovementAsync);

    public static async Task<IResult> PostStockMovementAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "STKMOVE";
        var v = await ctx.BodyObjectAsync();
        var site = Json.S(v, "site");
        var condition = Json.S(v, "condition");
        // contracts 4.4: COMMAREA input normalization (CobolCodec): text fitted to the PIC X widths, numbers rounded to scale
        var sm = new StkmoveCommarea
        {
            ItemCode = Json.S(v, "itemCode"),
            Revision = Json.S(v, "revision"),
            Site = site.Length == 0 ? ctx.Session.SiteCode : site,
            Warehouse = Json.S(v, "warehouse"),
            Location = Json.S(v, "location"),
            ToSite = Json.S(v, "toSite"),
            ToWarehouse = Json.S(v, "toWarehouse"),
            ToLocation = Json.S(v, "toLocation"),
            LotCode = Json.S(v, "lotCode"),
            SerialNo = Json.S(v, "serialNo"),
            Condition = condition.Length == 0 ? "ACCEPTED" : condition,
            ToCondition = Json.S(v, "toCondition"),
            UomCode = Json.S(v, "uomCode"),
            ReasonCode = Json.S(v, "reasonCode"),
            Reason = Json.S(v, "reason"),
            SourceModule = "ITM",
            SourceDocType = Json.S(v, "sourceDocType"),
            SourceDocRef = Json.S(v, "sourceDocRef"),
            ViaTransit = Json.S(v, "viaTransit") == "Y" ? "Y" : "N",
            HoldType = Json.S(v, "holdType"),
            HoldOutcome = Json.S(v, "holdOutcome"),
            Quantity = CobolNumeric.Normalize(Json.D(v, "quantity"), 19, 6, signed: true),
            UnitCost = CobolNumeric.Normalize(Json.D(v, "unitCost"), 17, 6, signed: true),
            MovementDate = Json.Date(v, "movementDate") ?? ctx.Session.BusinessDate,
        };
        var function = CobolText.Trimmed(Json.S(v, "function"), 8);
        var service = ctx.Get<IStockMovementService>();
        var result = await service.ExecuteAsync(function, sm, ItmCall.From(ctx.Session), ctx.Aborted);
        return ctx.Envelope(result, result.ReturnCode is 0 or 4 or 6 ? Result(sm) : null);
    }

    /// <summary>ItmStockMovementResult: the STKMOVECA body after the call.</summary>
    public static JsonObject Result(StkmoveCommarea sm) => new()
    {
        ["itemCode"] = sm.ItemCode,
        ["revision"] = sm.Revision,
        ["site"] = sm.Site,
        ["warehouse"] = sm.Warehouse,
        ["location"] = sm.Location,
        ["toSite"] = sm.ToSite,
        ["toWarehouse"] = sm.ToWarehouse,
        ["toLocation"] = sm.ToLocation,
        ["lotCode"] = sm.LotCode,
        ["serialNo"] = sm.SerialNo,
        ["ownerType"] = sm.OwnerType,
        ["ownerCode"] = sm.OwnerCode,
        ["condition"] = sm.Condition,
        ["toCondition"] = sm.ToCondition,
        ["quantity"] = CobolNumeric.WithScale(sm.Quantity, 6),
        ["uomCode"] = sm.UomCode,
        ["stockQty"] = CobolNumeric.WithScale(sm.StockQty, 6),
        ["factor"] = CobolNumeric.WithScale(sm.Factor, 9),
        ["unitCost"] = CobolNumeric.WithScale(sm.UnitCost, 6),
        ["valueGbp"] = CobolNumeric.WithScale(sm.ValueGbp, 2),
        ["movementDate"] = Iso(sm.MovementDate),
        ["postingDate"] = Iso(sm.PostingDate),
        ["expiryDate"] = Iso(sm.ExpiryDate),
        ["reasonCode"] = sm.ReasonCode,
        ["reason"] = sm.Reason,
        ["approvalRef"] = sm.ApprovalRef,
        ["sourceModule"] = sm.SourceModule,
        ["sourceDocType"] = sm.SourceDocType,
        ["sourceDocRef"] = sm.SourceDocRef,
        ["sourceDocLine"] = sm.SourceDocLine,
        ["viaTransit"] = sm.ViaTransit,
        ["holdType"] = sm.HoldType,
        ["holdId"] = sm.HoldId,
        ["holdOutcome"] = sm.HoldOutcome,
        ["movementId"] = sm.MovementId,
        ["movementId2"] = sm.MovementId2,
        ["qtyBefore"] = CobolNumeric.WithScale(sm.QtyBefore, 6),
        ["qtyAfter"] = CobolNumeric.WithScale(sm.QtyAfter, 6),
        ["movementType"] = sm.MovementType,
        ["duplicate"] = sm.Duplicate,
        ["transitLoc"] = sm.TransitLoc,
        ["narrative"] = sm.Narrative,
    };

    public static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
