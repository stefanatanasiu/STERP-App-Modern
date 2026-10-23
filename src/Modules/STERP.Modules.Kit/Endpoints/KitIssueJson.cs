using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>Schemas KitIssueResult and KitBackflushResult (KITISSUECA after the call).</summary>
internal static class KitIssueJson
{
    public static JsonObject Issue(KitIssueArea ca) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["lineNum"] = ca.LineNum,
        ["itemCode"] = Pic.X(ca.ItemCode, 40),
        ["plannedItem"] = Pic.X(ca.PlannedItem, 40),
        ["revision"] = Pic.X(ca.Revision, 4),
        ["site"] = Pic.X(ca.KsSite, 10),
        ["warehouse"] = Pic.X(ca.Warehouse, 20),
        ["location"] = Pic.X(ca.Location, 20),
        ["lotCode"] = Pic.X(ca.LotCode, 30),
        ["serialNo"] = Pic.X(ca.SerialNo, 40),
        ["quantity"] = Pic.S(ca.Quantity, 6),
        ["uomCode"] = Pic.X(ca.UomCode, 10),
        ["stockQty"] = Pic.S(ca.StockQty, 6),
        ["factor"] = Pic.S(ca.Factor, 9),
        ["unitCost"] = Pic.S(ca.UnitCost, 6),
        ["valueGbp"] = Pic.S(ca.ValueGbp, 2),
        ["kitIssueId"] = ca.KitIssueId,
        ["movementId"] = ca.MovementId,
        ["lineGross"] = Pic.S(ca.LineGross, 6),
        ["lineNet"] = Pic.S(ca.LineNet, 6),
        ["lineRemaining"] = Pic.S(ca.LineRemaining, 6),
        ["lineExcess"] = Pic.S(ca.LineExcess, 6),
        ["lineStatus"] = Pic.X(ca.LineStatus, 12),
        ["kitStatus"] = Pic.X(ca.KitStatus, 14),
        ["wipValue"] = Pic.S(ca.WipValue, 2),
        ["shortageQty"] = Pic.S(ca.ShortageQty, 6),
        ["freeQty"] = Pic.S(ca.FreeQty, 6),
        ["traced"] = Pic.X(ca.Traced, 1),
        ["duplicate"] = Pic.X(ca.Duplicate, 1),
        ["impactText"] = Pic.X(ca.ImpactText, 200),
        ["actualDate"] = Pic.Json(ca.ActualDate),
        ["postingDate"] = Pic.Json(ca.PostingDate),
    };

    public static JsonObject Backflush(KitIssueArea ca) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["goodQty"] = Pic.S(ca.GoodQty, 6),
        ["linesDone"] = ca.LinesDone,
        ["linesFailed"] = ca.LinesFailed,
        ["failedItem"] = Pic.X(ca.FailedItem, 40),
        ["impactText"] = Pic.X(ca.ImpactText, 200),
        ["kitStatus"] = Pic.X(ca.KitStatus, 14),
        ["wipValue"] = Pic.S(ca.WipValue, 2),
        ["lineNum"] = ca.LineNum,
        ["lineStatus"] = Pic.X(ca.LineStatus, 12),
        ["shortageQty"] = Pic.S(ca.ShortageQty, 6),
    };
}
