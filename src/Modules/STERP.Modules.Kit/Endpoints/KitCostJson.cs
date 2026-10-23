using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>Schema KitCostPosition (KITCOSTCA after COST/VARIANCE and GETELEM).</summary>
internal static class KitCostJson
{
    public static JsonObject Position(KitCostArea ca, IEnumerable<KitCostElement> elements) => new()
    {
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["costSet"] = Pic.X(ca.CostSet, 20),
        ["asAt"] = Pic.Json(ca.AsAt),
        ["currency"] = Pic.X(ca.Currency, 3),
        ["plannedGbp"] = Pic.S(ca.PlannedGbp, 2),
        ["issuedGbp"] = Pic.S(ca.IssuedGbp, 2),
        ["returnedGbp"] = Pic.S(ca.ReturnedGbp, 2),
        ["scrappedGbp"] = Pic.S(ca.ScrappedGbp, 2),
        ["absorbedGbp"] = Pic.S(ca.AbsorbedGbp, 2),
        ["completedGbp"] = Pic.S(ca.CompletedGbp, 2),
        ["residualGbp"] = Pic.S(ca.ResidualGbp, 2),
        ["varianceGbp"] = Pic.S(ca.VarianceGbp, 2),
        ["plannedQty"] = Pic.S(ca.PlannedQty, 6),
        ["completedQty"] = Pic.S(ca.CompletedQty, 6),
        ["unitCost"] = Pic.S(ca.UnitCost, 6),
        ["planUnitCost"] = Pic.S(ca.PlanUnitCost, 6),
        ["subassyCnt"] = ca.SubassyCnt,
        ["subassyGbp"] = Pic.S(ca.SubassyGbp, 2),
        ["doubleCheck"] = Pic.X(ca.DoubleCheck, 1),
        ["reconciled"] = Pic.X(ca.Reconciled, 1),
        ["kitStatus"] = Pic.X(ca.KitStatus, 14),
        ["noteText"] = Pic.X(ca.NoteText, 200),
        ["elements"] = new JsonArray(elements.Select(e => (JsonNode?)new JsonObject
        {
            ["element"] = Pic.X(e.Element, 10),
            ["description"] = Pic.X(e.Desc, 30),
            ["planned"] = Pic.S(e.Planned, 2),
            ["actual"] = Pic.S(e.Actual, 2),
            ["variance"] = Pic.S(e.Variance, 2),
            ["quantity"] = Pic.S(e.Qty, 6),
            ["uom"] = Pic.X(e.Uom, 10),
        }).ToArray()),
    };
}
