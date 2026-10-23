using System.Text.Json.Nodes;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>Schema KitGenealogyTrace (GENEALOGCA summary of the last call and every drained window).</summary>
internal static class GenealogJson
{
    public static JsonObject Trace(GenealogArea ca, IEnumerable<GenealogLine> lines) => new()
    {
        ["direction"] = Pic.X(ca.Function, 10),
        ["kitRef"] = Pic.X(ca.KitRef, 30),
        ["itemCode"] = Pic.X(ca.ItemCode, 40),
        ["lotCode"] = Pic.X(ca.LotCode, 30),
        ["serialNo"] = Pic.X(ca.SerialNo, 40),
        ["maxDepth"] = ca.MaxDepth,
        ["lineTotal"] = ca.LineTotal,
        ["depthReached"] = ca.DepthReached,
        ["substituteCnt"] = ca.SubstituteCnt,
        ["returnCnt"] = ca.ReturnCnt,
        ["scrapCnt"] = ca.ScrapCnt,
        ["kitCnt"] = ca.KitCnt,
        ["truncated"] = Pic.X(ca.Truncated, 1),
        ["summaryText"] = Pic.X(ca.SummaryText, 200),
        ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)new JsonObject
        {
            ["seq"] = l.Seq,
            ["depth"] = l.Depth,
            ["kitRef"] = Pic.X(l.KitRef, 30),
            ["parent"] = Pic.X(l.Parent, 40),
            ["parentLot"] = Pic.X(l.ParLot, 30),
            ["parentSerial"] = Pic.X(l.ParSerial, 40),
            ["item"] = Pic.X(l.Item, 40),
            ["lot"] = Pic.X(l.Lot, 30),
            ["serial"] = Pic.X(l.Serial, 40),
            ["quantity"] = Pic.S(l.Qty, 6),
            ["uom"] = Pic.X(l.Uom, 10),
            ["plannedItem"] = Pic.X(l.Planned, 40),
            ["substitute"] = Pic.X(l.Substitute, 1),
            ["event"] = Pic.X(l.Event, 12),
            ["happened"] = Pic.Json(l.When),
            ["operator"] = Pic.X(l.Operator, 20),
            ["note"] = Pic.X(l.Note, 60),
        }).ToArray()),
    };
}
