using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>The response objects of the KITCREAT-backed operations (schemas KitExecutionKit, KitLifecycleResult, KitKindCheckResult).</summary>
internal static class KitCreatJson
{
    public static JsonObject Kit(KitCreatArea ca, IEnumerable<KitCreatLine> lines) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["execKitId"] = ca.ExecKitId,
        ["site"] = Pic.X(ca.KcSite, 10),
        ["parentItem"] = Pic.X(ca.ParentItem, 40),
        ["parentRev"] = Pic.X(ca.ParentRev, 4),
        ["bomId"] = ca.BomId,
        ["bomRev"] = Pic.X(ca.BomRev, 4),
        ["sourceType"] = Pic.X(ca.SourceType, 12),
        ["sourceRef"] = Pic.X(ca.SourceRef, 40),
        ["jobRef"] = Pic.X(ca.JobRef, 20),
        ["parentKitRef"] = Pic.X(ca.ParentKitRef, 30),
        ["plannedQty"] = Pic.S(ca.PlannedQty, 6),
        ["completedQty"] = Pic.S(ca.CompletedQty, 6),
        ["rejectedQty"] = Pic.S(ca.RejectedQty, 6),
        ["grossStarts"] = Pic.S(ca.GrossStarts, 6),
        ["yield"] = Pic.S(ca.Yield, 6),
        ["quantum"] = Pic.S(ca.Quantum, 6),
        ["batchSize"] = Pic.S(ca.BatchSize, 6),
        ["runCount"] = ca.RunCount,
        ["batchCount"] = ca.BatchCount,
        ["needDate"] = Pic.Json(ca.NeedDate),
        ["startDate"] = Pic.Json(ca.StartDate),
        ["completeDate"] = Pic.Json(ca.CompleteDate),
        ["selectionDate"] = Pic.Json(ca.SelectionDate),
        ["ownerUser"] = Pic.X(ca.OwnerUser, 20),
        ["status"] = Pic.X(ca.Status, 14),
        ["backflush"] = Pic.X(ca.Backflush, 1),
        ["wipValue"] = Pic.S(ca.WipValue, 2),
        ["approvalId"] = ca.ApprovalId,
        ["completeness"] = Pic.X(ca.Completeness, 12),
        ["shortageCnt"] = ca.ShortageCnt,
        ["warningText"] = Pic.X(ca.WarningText, 200),
        ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)Line(l)).ToArray()),
    };

    private static JsonObject Line(KitCreatLine l) => new()
    {
        ["lineNum"] = l.LineNum,
        ["path"] = Pic.X(l.Path, 20),
        ["item"] = Pic.X(l.Item, 40),
        ["revision"] = Pic.X(l.Rev, 4),
        ["description"] = Pic.X(l.Desc, 40),
        ["basis"] = Pic.X(l.Basis, 12),
        ["uom"] = Pic.X(l.Uom, 10),
        ["factor"] = Pic.S(l.Factor, 9),
        ["originalRequirement"] = Pic.S(l.OrigReq, 6),
        ["currentRequirement"] = Pic.S(l.CurrReq, 6),
        ["scrapPct"] = Pic.S(l.ScrapPct, 6),
        ["reserved"] = Pic.S(l.Reserved, 6),
        ["picked"] = Pic.S(l.Picked, 6),
        ["grossIssued"] = Pic.S(l.GrossIss, 6),
        ["returned"] = Pic.S(l.Returned, 6),
        ["scrapped"] = Pic.S(l.Scrapped, 6),
        ["netIssued"] = Pic.S(l.NetIss, 6),
        ["remaining"] = Pic.S(l.Remaining, 6),
        ["excess"] = Pic.S(l.Excess, 6),
        ["substitute"] = Pic.X(l.Subst, 40),
        ["needDate"] = Pic.Json(l.NeedDate),
        ["location"] = Pic.X(l.Location, 20),
        ["unitCost"] = Pic.S(l.UnitCost, 6),
        ["status"] = Pic.X(l.Status, 12),
    };

    public static JsonObject Lifecycle(KitCreatArea ca) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["status"] = Pic.X(ca.Status, 14),
        ["prevStatus"] = Pic.X(ca.PrevStatus, 14),
        ["completeness"] = Pic.X(ca.Completeness, 12),
        ["shortageCnt"] = ca.ShortageCnt,
        ["lineTotal"] = ca.LineTotal,
        ["needsAuth"] = Pic.X(ca.NeedsAuth, 1),
        ["reasonText"] = Pic.X(ca.ReasonText, 300),
        ["warningText"] = Pic.X(ca.WarningText, 200),
        ["wipValue"] = Pic.S(ca.WipValue, 2),
    };

    public static JsonObject KindCheck(KitCreatArea ca) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["kitKind"] = Pic.X(ca.KitKind, 12),
        ["parentItem"] = Pic.X(ca.ParentItem, 40),
        ["parentDesc"] = Pic.X(ca.ParentDesc, 40),
        ["mayReceive"] = Pic.X(ca.MayReceive, 1),
        ["reasonText"] = Pic.X(ca.ReasonText, 300),
    };
}
