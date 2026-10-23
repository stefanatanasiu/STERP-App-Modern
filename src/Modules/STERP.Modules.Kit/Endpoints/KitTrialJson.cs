using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>Schemas KitTrialSummary and KitTrialResult (KITTRIALCA after TRIAL/MAXBUILD and the GETLINE drain).</summary>
internal static class KitTrialJson
{
    public static JsonObject Summary(KitTrialArea ca) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["parentItem"] = Pic.X(ca.ParentItem, 40),
        ["site"] = Pic.X(ca.KtSite, 10),
        ["requestedQty"] = Pic.S(ca.RequestedQty, 6),
        ["reqDate"] = Pic.Json(ca.ReqDate),
        ["selectionDate"] = Pic.Json(ca.SelectionDate),
        ["feasible"] = Pic.X(ca.Feasible, 1),
        ["committed"] = Pic.X(ca.Committed, 1),
        ["completeness"] = Pic.X(ca.Completeness, 12),
        ["maxBuildable"] = Pic.S(ca.MaxBuildable, 6),
        ["limitingItem"] = Pic.X(ca.LimitingItem, 40),
        ["limitingShort"] = Pic.S(ca.LimitingShort, 6),
        ["shortageCnt"] = ca.ShortageCnt,
        ["lineTotal"] = ca.LineTotal,
        ["warnings"] = Pic.X(ca.Warnings, 300),
        ["actionHint"] = Pic.X(ca.ActionHint, 60),
    };

    public static JsonObject Result(KitTrialArea ca, IEnumerable<KitTrialLine> lines)
    {
        var o = Summary(ca);
        o["lines"] = new JsonArray(lines.Select(l => (JsonNode?)new JsonObject
        {
            ["lineNum"] = l.LineNum,
            ["path"] = Pic.X(l.Path, 20),
            ["depth"] = l.Depth,
            ["item"] = Pic.X(l.Item, 40),
            ["revision"] = Pic.X(l.Rev, 4),
            ["uom"] = Pic.X(l.Uom, 10),
            ["gross"] = Pic.S(l.Gross, 6),
            ["netRequired"] = Pic.S(l.NetReq, 6),
            ["free"] = Pic.S(l.Free, 6),
            ["ownReserved"] = Pic.S(l.OwnResv, 6),
            ["otherReserved"] = Pic.S(l.OtherResv, 6),
            ["inbound"] = Pic.S(l.Inbound, 6),
            ["inboundDate"] = Pic.Json(l.InbDate),
            ["allocated"] = Pic.S(l.Allocated, 6),
            ["shortage"] = Pic.S(l.Shortage, 6),
            ["shortDate"] = Pic.Json(l.ShortDate),
            ["limiting"] = Pic.X(l.Limiting, 1),
            ["action"] = Pic.X(l.Action, 40),
            ["warning"] = Pic.X(l.Warning, 60),
        }).ToArray());
        return o;
    }
}
