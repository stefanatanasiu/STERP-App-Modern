using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;
using Microsoft.Extensions.DependencyInjection;

namespace Sterp.Modules.Itm.Online;

/// <summary>
/// COBOL-backed online reads: availability (FR-STERP-ITM-013, AVAILQ AVAIL), lot/serial trace (FR-STERP-ITM-015, LOTSER
/// TRACE) and the activation review (FR-STERP-ITM-011, ITEMVAL VALIDATE), each LINKed as the facade did.
/// </summary>
public static class CobolQueryEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("itm_getItemAvailability", GetItemAvailabilityAsync);
        ops.Handle("itm_traceItemLotSerial", TraceItemLotSerialAsync);
        ops.Handle("itm_reviewItemActivation", ReviewItemActivationAsync);
    }

    /// <summary>StockService.Availability (StockService.vb:34-48): AVAILQ AVAIL with AV-AS-AT = business date.</summary>
    public static async Task<IResult> GetItemAvailabilityAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "AVAILQ";
        var av = new AvailqCommarea
        {
            ItemCode = ctx.Route("itemCode"),
            Site = ctx.Query("siteCode") ?? "",
            AsAt = ctx.Session.BusinessDate,
        };
        var result = await ctx.Get<IAvailabilityService>().ExecuteAsync(AvailqFunctions.Avail, av, ItmCall.From(ctx.Session), ctx.Aborted);
        return ctx.Envelope(result, result.ReturnCode is 0 or 4 or 6 ? Availability(av) : null);
    }

    /// <summary>StockService.Trace (StockService.vb:102-111): LOTSER TRACE with LS-AS-AT = business date.</summary>
    public static async Task<IResult> TraceItemLotSerialAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "LOTSER";
        var ls = new LotserCommarea
        {
            ItemCode = ctx.Route("itemCode"),
            LotCode = ctx.Query("lotCode") ?? "",
            SerialNo = ctx.Query("serialNo") ?? "",
            AsAt = ctx.Session.BusinessDate,
        };
        var result = await ctx.Get<ILotSerialService>().ExecuteAsync(LotserFunctions.Trace, ls, ItmCall.From(ctx.Session), ctx.Aborted);
        return ctx.Envelope(result, result.ReturnCode is 0 or 4 or 6 ? Trace(ls) : null);
    }

    /// <summary>
    /// ItemService.Review(item, "VALIDATE") (ItemService.vb:76-83): ITEMVAL VALIDATE; the faults are returned on every outcome
    /// (the form bound them before showing the result), in the problem data for refusals (contracts 4.7).
    /// </summary>
    public static async Task<IResult> ReviewItemActivationAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "ITEMVAL";
        var iv = new ItemvalArea
        {
            Ca = CaHeader.For("VALIDATE", ItmCall.From(ctx.Session)),
            ItemCode = Cob.X(ctx.Route("itemCode"), 40),
            Action = "VALIDATE",
        };
        await ctx.Services.GetRequiredService<ItemValProgram>().RunAsync(iv, ctx.Aborted);
        return ctx.Envelope(iv.Ca.Result, Review(iv));
    }

    private static decimal Q6(decimal v) => CobolNumeric.WithScale(v, 6);

    private static string? Iso(DateOnly? d) => StockMovementEndpoints.Iso(d);

    /// <summary>ItmAvailability: the AVAILQCA body after the call.</summary>
    public static JsonObject Availability(AvailqCommarea av) => new()
    {
        ["itemCode"] = av.ItemCode,
        ["revision"] = av.Revision,
        ["site"] = av.Site,
        ["warehouse"] = av.Warehouse,
        ["location"] = av.Location,
        ["lotCode"] = av.LotCode,
        ["serialNo"] = av.SerialNo,
        ["ownerType"] = av.OwnerType,
        ["ownerCode"] = av.OwnerCode,
        ["condition"] = av.Condition,
        ["asAt"] = Iso(av.AsAt),
        ["demandType"] = av.DemandType,
        ["demandRef"] = av.DemandRef,
        ["demandLine"] = av.DemandLine,
        ["stockUom"] = av.StockUom,
        ["qtyPhysical"] = Q6(av.QtyPhysical),
        ["qtyEligible"] = Q6(av.QtyEligible),
        ["qtyReserved"] = Q6(av.QtyReserved),
        ["qtyFree"] = Q6(av.QtyFree),
        ["qtyExpected"] = Q6(av.QtyExpected),
        ["qtyPlanned"] = Q6(av.QtyPlanned),
        ["qtyQuarantine"] = Q6(av.QtyQuarantine),
        ["qtyInspection"] = Q6(av.QtyInspection),
        ["qtyRejected"] = Q6(av.QtyRejected),
        ["qtyExpired"] = Q6(av.QtyExpired),
        ["qtyIntransit"] = Q6(av.QtyIntransit),
        ["qtyThirdparty"] = Q6(av.QtyThirdparty),
        ["qtyUnavailLoc"] = Q6(av.QtyUnavailLoc),
        ["qtyOwnDemand"] = Q6(av.QtyOwnDemand),
        ["qtyForDemand"] = Q6(av.QtyForDemand),
        ["qtySafety"] = Q6(av.QtySafety),
        ["qtyPolicy"] = Q6(av.QtyPolicy),
        ["expectedDate"] = Iso(av.ExpectedDate),
        ["dataComplete"] = av.DataComplete,
        ["restricted"] = av.Restricted,
        ["lines"] = new JsonArray(av.Lines.Select(l => (JsonNode?)new JsonObject
        {
            ["lSite"] = l.Site,
            ["lWarehouse"] = l.Warehouse,
            ["lLocation"] = l.Location,
            ["lLocType"] = l.LocType,
            ["lRevision"] = l.Revision,
            ["lLot"] = l.Lot,
            ["lSerial"] = l.Serial,
            ["lOwnerType"] = l.OwnerType,
            ["lOwner"] = l.Owner,
            ["lCondition"] = l.Condition,
            ["lQty"] = Q6(l.Qty),
            ["lReserved"] = Q6(l.Reserved),
            ["lFree"] = Q6(l.Free),
            ["lExpiry"] = Iso(l.Expiry),
            ["lEligible"] = l.Eligible,
            ["lWhy"] = l.Why,
        }).ToArray()),
    };

    /// <summary>ItmLotSerialTrace: the LOTSERCA body after TRACE.</summary>
    public static JsonObject Trace(LotserCommarea ls) => new()
    {
        ["itemCode"] = ls.ItemCode,
        ["revision"] = ls.Revision,
        ["lotCode"] = ls.LotCode,
        ["serialNo"] = ls.SerialNo,
        ["site"] = ls.Site,
        ["warehouse"] = ls.Warehouse,
        ["location"] = ls.Location,
        ["direction"] = ls.Direction,
        ["quantity"] = Q6(ls.Quantity),
        ["asAt"] = Iso(ls.AsAt),
        ["traceability"] = ls.Traceability,
        ["shelfLifeDays"] = ls.ShelfLifeDays,
        ["minRemainDays"] = ls.MinRemainDays,
        ["expiryDate"] = Iso(ls.ExpiryDate),
        ["manufactured"] = Iso(ls.Manufactured),
        ["receivedDate"] = Iso(ls.ReceivedDate),
        ["daysRemaining"] = ls.DaysRemaining,
        ["supplierCode"] = ls.SupplierCode,
        ["supplierLot"] = ls.SupplierLot,
        ["certificateRef"] = ls.CertificateRef,
        ["originCountry"] = ls.OriginCountry,
        ["condition"] = ls.Condition,
        ["serialStatus"] = ls.SerialStatus,
        ["usable"] = ls.Usable,
        ["created"] = ls.Created,
        ["trace"] = new JsonArray(ls.Trace.Select(t => (JsonNode?)new JsonObject
        {
            ["tMoveId"] = t.MoveId,
            ["tType"] = t.Type,
            ["tDate"] = Iso(t.Date),
            ["tQty"] = Q6(t.Qty),
            ["tSite"] = t.Site,
            ["tLocation"] = t.Location,
            ["tDocType"] = t.DocType,
            ["tDocRef"] = t.DocRef,
            ["tUser"] = t.User,
        }).ToArray()),
    };

    /// <summary>ItmItemActivationReview: the ITEMVALCA body after VALIDATE.</summary>
    internal static JsonObject Review(ItemvalArea iv) => new()
    {
        ["itemCode"] = iv.ItemCode,
        ["partnerType"] = iv.PartnerType,
        ["partnerCode"] = iv.PartnerCode,
        ["action"] = iv.Action,
        ["revision"] = iv.Revision,
        ["description"] = iv.Description,
        ["itemClass"] = iv.ItemClass,
        ["category"] = iv.Category,
        ["steward"] = iv.Steward,
        ["lifecycle"] = iv.Lifecycle,
        ["stockUom"] = iv.StockUom,
        ["traceability"] = iv.Traceability,
        ["shelfLifeDays"] = iv.ShelfLifeDays,
        ["quantum"] = Q6(iv.Quantum),
        ["indivisible"] = iv.Indivisible,
        ["makeBuy"] = iv.MakeBuy,
        ["valuationGrp"] = iv.ValuationGrp,
        ["costMethod"] = iv.CostMethod,
        ["currentRev"] = iv.CurrentRev,
        ["isStocked"] = iv.IsStocked,
        ["isPurchasable"] = iv.IsPurchasable,
        ["isSaleable"] = iv.IsSaleable,
        ["isManufactured"] = iv.IsManufactured,
        ["isConsumable"] = iv.IsConsumable,
        ["reqInspection"] = iv.ReqInspection,
        ["drcGoods"] = iv.DrcGoods,
        ["successor"] = iv.Successor,
        ["predecessor"] = iv.Predecessor,
        ["revStatus"] = iv.RevStatus,
        ["revFrom"] = Iso(Cob.Date(iv.RevFrom)),
        ["revTo"] = Iso(Cob.Date(iv.RevTo)),
        ["revInterchange"] = iv.RevInterchange,
        ["allowed"] = iv.Allowed,
        ["ready"] = iv.Ready,
        ["faults"] = new JsonArray(iv.Faults.Select(f => (JsonNode?)new JsonObject
        {
            ["fReq"] = f.Req,
            ["fSeverity"] = f.Severity,
            ["fField"] = f.Field,
            ["fText"] = f.Text,
        }).ToArray()),
    };
}
