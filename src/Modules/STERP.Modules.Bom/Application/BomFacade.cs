using System.Text.Json.Nodes;
using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Bom.Application;

/// <summary>The STCAHDR header the gateway fills on every LINK (CobolGateway.PrepareHeader, CobolGateway.cs:61-74).</summary>
internal sealed record EngineHeader(string Company, string Site, string User, int BusinessDate, string CorrelationId)
{
    public static EngineHeader From(SterpContext s) =>
        new(CobolText.Trimmed(s.CompanyCode, 10), CobolText.Trimmed(s.SiteCode, 10), CobolText.Trimmed(s.UserCode, 20),
            CobolDate.ToYyyymmdd(s.BusinessDate), CobolText.Trimmed(s.CorrelationId, 36));
}

/// <summary>
/// BomService (dotnet/STERP.Business/Bom/BomService.vb) explosion-family members: fill the COMMAREA exactly as the facade
/// did, LINK, keep the summary of the first call, read every window and return the outcome of the last engine call
/// (spec 4.2, D-BOM-03). Each LINK starts with the gateway's header reset (RC 0, blank reason and message).
/// </summary>
internal sealed class BomFacade(LeafViews leaf, CostEngine cost, NetPlanner net)
{
    /// <summary>BomService.ViewToFunction (BomService.vb:399-408).</summary>
    public static string ViewToFunction(string? view) => (view ?? "").Trim().ToUpperInvariant() switch
    {
        "SINGLE" => "SINGLE",
        "LEAF" => "FLATTEN",
        _ => "INDENT",
    };

    /// <summary>BomService.Explode (BomService.vb:138-195).</summary>
    public async Task<(LegacyResult Outcome, JsonObject? Data)> ExplodeAsync(EngineHeader h, string itemCode, string revision, decimal qty,
        int effectiveDate, string view, int maxDepth, CancellationToken ct)
    {
        var ca = new LeafArea
        {
            ParentItem = CobolText.Trimmed(itemCode, 40),
            ParentRev = CobolText.Trimmed(revision, 4),
            Purpose = "STANDARD",
            ReqQty = CobolNumeric.Normalize(qty, 21, 6, true),
            EffDate = effectiveDate,
            MaxDepth = (int)CobolNumeric.Normalize(maxDepth, 4, 0, true),
            Reexplode = "Y",
            FromLine = 0,
            WinMax = 25,
        };
        var fn = ViewToFunction(view);
        await LinkLeafAsync(h, ca, fn, ct);
        var r = ca.Outcome;
        if (r.ReturnCode > 4) return (r, null);
        var data = new JsonObject
        {
            ["completeness"] = ca.Completeness,
            ["stopSummary"] = ca.StopSummary,
            ["occurrences"] = ca.OccCount,
            ["depthReached"] = ca.DepthReached,
        };
        var rows = new JsonArray();
        var more = true;
        var guard = 0;
        while (more && guard < 100)
        {
            guard++;
            for (var i = 0; i < ca.WinCnt; i++)
            {
                var l = ca.Lines[i];
                rows.Add(new JsonObject
                {
                    ["level"] = l.Depth,
                    ["item"] = l.Item,
                    ["revision"] = l.Rev,
                    ["description"] = l.Desc,
                    ["kind"] = l.NodeType,
                    ["supply"] = l.Supply,
                    ["basis"] = l.Basis,
                    ["usage"] = CobolNumeric.WithScale(l.Usage, 6),
                    ["unit"] = l.Uom,
                    ["required"] = CobolNumeric.WithScale(l.ReqQty, 6),
                    ["unrounded"] = CobolNumeric.WithScale(l.Unrounded, 9),
                    ["stopReason"] = l.Stop,
                    ["path"] = l.Path,
                    ["warning"] = l.Warning,
                });
            }
            more = ca.MoreLines == "Y";
            if (more)
            {
                ca.Reexplode = "N";
                await LinkLeafAsync(h, ca, fn, ct);
                r = ca.Outcome;
                if (r.ReturnCode > 4) break;
            }
        }
        data["lines"] = rows;
        return (r, data);
    }

    private async Task LinkLeafAsync(EngineHeader h, LeafArea ca, string fn, CancellationToken ct)
    {
        ca.Function = fn;
        ca.Company = h.Company;
        ca.Site = h.Site;
        ca.User = h.User;
        ca.BusinessDate = h.BusinessDate;
        ca.CorrelationId = h.CorrelationId;
        ca.ReturnCode = 0;
        ca.Reason = "";
        ca.Message = "";
        ca.MessageCount = 0;
        await leaf.CallAsync(ca, ct);
    }

    /// <summary>BomService.NettedPlan (BomService.vb:198-244).</summary>
    public async Task<(LegacyResult Outcome, JsonObject? Data)> NettedPlanAsync(EngineHeader h, string itemCode, decimal qty, int effectiveDate,
        CancellationToken ct)
    {
        var ca = new NetArea
        {
            ParentItem = CobolText.Trimmed(itemCode, 40),
            Purpose = "STANDARD",
            ReqQty = CobolNumeric.Normalize(qty, 21, 6, true),
            EffDate = effectiveDate,
            MaxDepth = 20,
            StockSource = "LIVE",
            FromLine = 0,
            WinMax = 25,
        };
        await LinkNetAsync(h, ca, "NET", ct);
        var r = ca.Outcome;
        if (r.ReturnCode > 4) return (r, null);
        var data = new JsonObject
        {
            ["maxBuildable"] = CobolNumeric.WithScale(ca.MaxBuildable, 6),
            ["shortages"] = ca.ShortageCount,
            ["completeness"] = ca.Completeness,
        };
        var rows = new JsonArray();
        var more = true;
        var guard = 0;
        while (more && guard < 100)
        {
            guard++;
            await LinkNetAsync(h, ca, "GETLINE", ct);
            r = ca.Outcome;
            if (r.ReturnCode > 4) break;
            for (var i = 0; i < ca.WinCnt; i++)
            {
                var l = ca.Lines[i];
                rows.Add(new JsonObject
                {
                    ["level"] = l.Depth,
                    ["item"] = l.Item,
                    ["description"] = l.Desc,
                    ["unit"] = l.Uom,
                    ["required"] = CobolNumeric.WithScale(l.ReqQty, 6),
                    ["stockUsed"] = CobolNumeric.WithScale(l.StockUsed, 6),
                    ["inbound"] = CobolNumeric.WithScale(l.Inbound, 6),
                    ["toMake"] = CobolNumeric.WithScale(l.ToMake, 6),
                    ["shortage"] = CobolNumeric.WithScale(l.Shortage, 6),
                    ["excess"] = CobolNumeric.WithScale(l.Excess, 6),
                    ["stopReason"] = l.Stop,
                });
            }
            more = ca.MoreLines == "Y";
        }
        data["lines"] = rows;
        return (r, data);
    }

    private async Task LinkNetAsync(EngineHeader h, NetArea ca, string fn, CancellationToken ct)
    {
        ca.Function = fn;
        ca.Company = h.Company;
        ca.Site = h.Site;
        ca.User = h.User;
        ca.BusinessDate = h.BusinessDate;
        ca.ReturnCode = 0;
        ca.Reason = "";
        ca.Message = "";
        ca.MessageCount = 0;
        await net.CallAsync(ca, ct);
    }

    /// <summary>BomService.CostedExplosion (BomService.vb:247-308).</summary>
    public async Task<(LegacyResult Outcome, JsonObject? Data)> CostedExplosionAsync(EngineHeader h, string itemCode, string revision,
        decimal qty, int effectiveDate, string costSet, CancellationToken ct)
    {
        var ca = new BomCostArea
        {
            ParentItem = CobolText.Trimmed(itemCode, 40),
            ParentRev = CobolText.Trimmed(revision, 4),
            Purpose = "STANDARD",
            ReqQty = CobolNumeric.Normalize(qty, 21, 6, true),
            EffDate = effectiveDate,
            CostDate = effectiveDate,
            MaxDepth = 0,
            CostSet = CobolText.Trimmed(costSet, 20),
            Currency = "GBP",
            Netted = "N",
            FromLine = 0,
            WinMax = 25,
        };
        var r = await LinkCostAsync(h, ca, "COST", ct);
        if (r.ReturnCode > 4) return (r, null);
        var data = new JsonObject
        {
            ["completeness"] = ca.Completeness,
            ["occurrences"] = ca.OccCount,
            ["missingRates"] = ca.MissingRates,
            ["totalCost"] = CobolNumeric.WithScale(ca.TotalCost, 2),
            ["unitCost"] = CobolNumeric.WithScale(ca.UnitCost, 6),
            ["materialCost"] = CobolNumeric.WithScale(ca.MaterialCost, 2),
            ["conversionCost"] = CobolNumeric.WithScale(ca.ConversionCost + ca.SetupCost, 2),
            ["recoveryCredit"] = CobolNumeric.WithScale(ca.RecoveryCredit, 2),
            ["errorPath"] = ca.ErrorPath,
        };
        var rows = new JsonArray();
        var more = true;
        var guard = 0;
        while (more && guard < 100)
        {
            guard++;
            r = await LinkCostAsync(h, ca, "GETLINE", ct);
            if (r.ReturnCode > 4) break;
            for (var i = 0; i < ca.WinCnt; i++)
            {
                var l = ca.Lines[i];
                rows.Add(new JsonObject
                {
                    ["level"] = l.Depth,
                    ["item"] = l.Item,
                    ["revision"] = l.Rev,
                    ["description"] = l.Desc,
                    ["kind"] = l.NodeType,
                    ["element"] = l.Element,
                    ["source"] = l.Source,
                    ["required"] = CobolNumeric.WithScale(l.ReqQty, 6),
                    ["unit"] = l.Uom,
                    ["unitCost"] = CobolNumeric.WithScale(l.UnitCost, 6),
                    ["extendedCost"] = CobolNumeric.WithScale(l.ExtCost, 2),
                    ["branchCost"] = CobolNumeric.WithScale(l.Branch, 2),
                    ["path"] = l.Path,
                    ["warning"] = l.Warning,
                });
            }
            more = ca.MoreLines == "Y";
        }
        data["lines"] = rows;
        return (r, data);
    }

    private async Task<LegacyResult> LinkCostAsync(EngineHeader h, BomCostArea ca, string fn, CancellationToken ct)
    {
        ca.Function = fn;
        ca.Company = h.Company;
        ca.Site = h.Site;
        ca.User = h.User;
        ca.BusinessDate = h.BusinessDate;
        ca.CorrelationId = h.CorrelationId;
        ca.Result = LegacyResult.Ok();
        var r = await cost.LinkAsync(ca, ct);
        // the facade's CobolResult carries RC, reason, message and count only (CobolResult.cs:8-15)
        return new LegacyResult(r.ReturnCode, r.ReasonCode, r.Message, r.MessageCount);
    }
}
