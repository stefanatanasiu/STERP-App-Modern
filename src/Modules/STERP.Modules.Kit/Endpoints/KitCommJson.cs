using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>Schema KitCommercialSets (KITCOMMCA after SETS).</summary>
internal static class KitCommJson
{
    public static JsonObject Sets(KitCommArea ca, IEnumerable<KitCommLine> lines) => new()
    {
        ["kitItem"] = Pic.X(ca.KitItem, 40),
        ["kitVersion"] = Pic.X(ca.KitVersion, 4),
        ["kitDefId"] = ca.KitDefId,
        ["site"] = Pic.X(ca.KvSite, 10),
        ["orderRef"] = Pic.X(ca.OrderRef, 40),
        ["orderLine"] = ca.OrderLine,
        ["parentQty"] = Pic.S(ca.ParentQty, 6),
        ["supplyKind"] = Pic.X(ca.SupplyKind, 10),
        ["asAt"] = Pic.Json(ca.AsAt),
        ["currency"] = Pic.X(ca.Currency, 3),
        ["completeSets"] = Pic.S(ca.CompleteSets, 6),
        ["limitItem"] = Pic.X(ca.LimitItem, 40),
        ["limitShort"] = Pic.S(ca.LimitShort, 6),
        ["mayDespatch"] = Pic.X(ca.MayDespatch, 1),
        ["parentStock"] = Pic.X(ca.ParentStock, 1),
        ["lineCnt"] = ca.LineCnt,
        ["noteText"] = Pic.X(ca.NoteText, 200),
        ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)new JsonObject
        {
            ["lineNum"] = l.LineNum,
            ["item"] = Pic.X(l.Item, 40),
            ["description"] = Pic.X(l.Desc, 40),
            ["qtyPer"] = Pic.S(l.QtyPer, 6),
            ["uom"] = Pic.X(l.Uom, 10),
            ["required"] = Pic.S(l.Required, 6),
            ["free"] = Pic.S(l.Free, 6),
            ["delivered"] = Pic.S(l.Delivered, 6),
            ["sets"] = Pic.S(l.Sets, 6),
            ["standalonePrice"] = Pic.S(l.Ssp, 6),
            ["allocationPct"] = Pic.S(l.AllocPct, 6),
            ["allocatedNet"] = Pic.S(l.AllocNet, 2),
            ["unitCost"] = Pic.S(l.UnitCost, 6),
            ["costGbp"] = Pic.S(l.Cost, 2),
            ["vatCode"] = Pic.X(l.Vat, 4),
            ["customerSpecified"] = Pic.X(l.CustSpec, 1),
            ["substituteOk"] = Pic.X(l.SubOk, 1),
            ["limiting"] = Pic.X(l.Limiting, 1),
        }).ToArray()),
    };
}
