using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Cst.Domain;
using Sterp.Modules.Cst.Engines;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Cst.Endpoints;

/// <summary>
/// The 13 online operations of STERP-CST.openapi.yaml (spec section 7.1), each reproducing the CostingService facade member the
/// legacy page called: the three worklists through the SqlHelper family (IProcedureRunner, one short autocommit connection,
/// '' -> NULL), the COBOL-backed operations through the CST engines on the request's SQLIO unit of work.
/// </summary>
internal static class CstEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("cst_getItemCostBases", GetItemCostBasesAsync);
        ops.Handle("cst_listCostSets", ListCostSetsAsync);
        ops.Handle("cst_listCostJobs", ListCostJobsAsync);
        ops.Handle("cst_reviewCostSet", ctx => CostSetActionAsync(ctx, "REVIEW", true));
        ops.Handle("cst_approveCostSet", ctx => CostSetActionAsync(ctx, "APPROVE", true));
        ops.Handle("cst_activateCostSet", ctx => CostSetActionAsync(ctx, "ACTIVATE", true));
        ops.Handle("cst_previewCostSetImpact", ctx => CostSetActionAsync(ctx, "IMPACT", false));
        ops.Handle("cst_compareCostSets", CompareCostSetsAsync);
        ops.Handle("cst_rollUpItemCost", RollUpAsync);
        ops.Handle("cst_runWhatIf", WhatIfAsync);
        ops.Handle("cst_enquireCostJob", ctx => JobCostAsync(ctx, "ENQUIRE"));
        ops.Handle("cst_forecastCostJob", ctx => JobCostAsync(ctx, "FORECAST"));
        ops.Handle("cst_calculateCostJobWip", WipCalcAsync);
    }

    // ------------------------------------------------------------------ worklists (SqlHelper family)

    /// <summary>CostArea.Opt: a blank filter is NULL, else the trimmed value.</summary>
    private static string? Opt(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>cst_getItemCostBases: CostingService.CostEnquiry -> cst.usp_CostEnquiry (FR-STERP-CST-010).</summary>
    private static async Task<IResult> GetItemCostBasesAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "CostingService.CostEnquiry";
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("cst.usp_CostEnquiry", new Dictionary<string, object?>
        {
            ["@CompanyCode"] = ctx.Session.CompanyCode,
            ["@ItemCode"] = ctx.Route("itemCode"),
            ["@Revision"] = ctx.Query("revision"),
            ["@SiteCode"] = Opt(ctx.Query("site")),
        }, ctx.Aborted);
        return ctx.List(GbpNames(rows));
    }

    /// <summary>cst_listCostSets: CostingService.Sets -> cst.usp_CostSetList (FR-STERP-CST-011; blank or ALL is NULL).</summary>
    private static async Task<IResult> ListCostSetsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "CostingService.Sets";
        var status = Opt(ctx.Query("status"));
        if (string.Equals(status, "ALL", StringComparison.Ordinal)) status = null;
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("cst.usp_CostSetList", new Dictionary<string, object?>
        {
            ["@CompanyCode"] = ctx.Session.CompanyCode,
            ["@Status"] = status,
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>cst_listCostJobs: CostingService.Jobs -> cst.usp_JobList (FR-STERP-CST-019).</summary>
    private static async Task<IResult> ListCostJobsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "CostingService.Jobs";
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("cst.usp_JobList", new Dictionary<string, object?>
        {
            ["@CompanyCode"] = ctx.Session.CompanyCode,
            ["@JobRef"] = Opt(ctx.Query("jobRef")),
            ["@Status"] = Opt(ctx.Query("status")),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>Contracts 4.2 names of the *GBP columns: CarryingValueGBP -> carryingValueGbp.</summary>
    private static JsonArray GbpNames(JsonArray rows)
    {
        var result = new JsonArray();
        foreach (var node in rows)
        {
            if (node is not JsonObject row)
            {
                result.Add(node?.DeepClone());
                continue;
            }
            var copy = new JsonObject();
            foreach (var (k, v) in row) copy[k.EndsWith("GBP", StringComparison.Ordinal) ? k[..^3] + "Gbp" : k] = v?.DeepClone();
            result.Add(copy);
        }
        return result;
    }

    // ------------------------------------------------------------------ STDCOST

    /// <summary>CostingService.SetArea: set code, compare set, business date, commit flag (CostingService.vb:157-165).</summary>
    private static StdCostArea SetArea(OperationContext ctx, string function, string costSet, string compareTo, bool commit) => new()
    {
        Ca = CaHeader.FromSession(ctx.Session, "STDCOST", function),
        CostSet = Input.Commarea(costSet, 20),
        CompareSet = Input.Commarea(compareTo, 20),
        EffDate = Input.Yyyymmdd(ctx.Session.BusinessDate),
        Commit = commit ? "Y" : "N",
    };

    /// <summary>cst_reviewCostSet / cst_approveCostSet / cst_activateCostSet / cst_previewCostSetImpact (FR-STERP-CST-012..015).</summary>
    private static async Task<IResult> CostSetActionAsync(OperationContext ctx, string function, bool commit)
    {
        ctx.Session.LegacyProgram = "STDCOST/" + function;
        _ = await ctx.BodyObjectAsync();
        var a = SetArea(ctx, function, ctx.Route("costSet"), "", commit);
        await ctx.Get<StdCostEngine>().LinkAsync(a, ctx.Aborted);
        return ctx.Envelope(a.Ca.Result, a.Ca.ReturnCode >= 8 ? null : new JsonObject
        {
            ["costSet"] = a.CostSet,
            ["compareSet"] = a.CompareSet,
            ["status"] = a.Status,
            ["purpose"] = a.Purpose,
            ["preparedBy"] = a.PreparedBy,
            ["approvedBy"] = a.ApprovedBy,
            ["itemCount"] = a.ItemCount,
            ["revalQty"] = Pic.S(a.RevalQty, 6),
            ["revalGbp"] = Pic.S(a.RevalGbp, 2),
            ["adverse"] = a.Adverse,
            ["completeness"] = a.Completeness,
            ["warning"] = a.Warning,
        });
    }

    /// <summary>cst_compareCostSets: CostingService.Compare -> STDCOST COMPARE, first window only (FR-STERP-CST-016).</summary>
    private static async Task<IResult> CompareCostSetsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "STDCOST/COMPARE";
        var body = await ctx.BodyObjectAsync();
        var a = SetArea(ctx, "COMPARE", ctx.Route("costSet"), Input.Text(body, "compareTo"), false);
        a.FromLine = 0;
        await ctx.Get<StdCostEngine>().LinkAsync(a, ctx.Aborted);
        if (a.Ca.ReturnCode >= 8) return ctx.Envelope(a.Ca.Result);
        var lines = new JsonArray();
        if (a.Ca.Result.Succeeded)
        {
            for (var i = 0; i < a.LineCnt; i++)
            {
                var l = a.Lines[i];
                lines.Add(new JsonObject
                {
                    ["item"] = l.Item,
                    ["revision"] = l.Rev,
                    ["oldCost"] = Pic.S(l.OldCost, 6),
                    ["newCost"] = Pic.S(l.NewCost, 6),
                    ["difference"] = Pic.S(l.Diff, 6),
                    ["differencePct"] = Pic.S(l.Pct, 4),
                    ["qtyHeld"] = Pic.S(l.QtyHeld, 6),
                    ["revaluation"] = Pic.S(l.Reval, 2),
                    ["note"] = l.Note,
                });
            }
        }
        return ctx.Envelope(a.Ca.Result, new JsonObject
        {
            ["costSet"] = a.CostSet,
            ["compareSet"] = a.CompareSet,
            ["status"] = a.Status,
            ["purpose"] = a.Purpose,
            ["preparedBy"] = a.PreparedBy,
            ["approvedBy"] = a.ApprovedBy,
            ["itemCount"] = a.ItemCount,
            ["revalGbp"] = Pic.S(a.RevalGbp, 2),
            ["lineCnt"] = a.LineCnt,
            ["moreLines"] = a.MoreLines,
            ["lines"] = lines,
        });
    }

    // ------------------------------------------------------------------ COSTROLL

    /// <summary>SterpSession.BaseCurrency: the access row's base currency, GBP when blank (SterpSession.cs:73).</summary>
    private static string BaseCurrency(OperationContext ctx)
    {
        var c = ctx.Session.CompanyAccess?.BaseCurrency;
        return string.IsNullOrEmpty(c) ? "GBP" : c;
    }

    /// <summary>
    /// cst_rollUpItemCost: CostingService.Rollup (CostingService.vb:212-243) - ROLLUP, then while RC &lt;= 4 the costed tree paged
    /// with GETLINE from 1 + lines read (at most 25 windows, a failed GETLINE stops silently); the summary is the COMMAREA after
    /// the last call (F-COSTROLL-01, BC-CST-01 rejected).
    /// </summary>
    private static async Task<IResult> RollUpAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "COSTROLL/ROLLUP";
        var body = await ctx.BodyObjectAsync();
        var date = Input.Yyyymmdd(ctx.Session.BusinessDate);
        var boundary = Input.Text(body, "boundary");
        var a = new CostRollArea
        {
            Ca = CaHeader.FromSession(ctx.Session, "COSTROLL", "ROLLUP"),
            ItemCode = Input.Commarea(ctx.Route("itemCode"), 40),
            Revision = Input.Commarea(Input.Text(body, "revision"), 4),
            CostSet = Input.Commarea(Input.Text(body, "costSet"), 20),
            TargetSet = "",
            EffDate = date,
            CostDate = date,
            LotSize = Input.Commarea(Input.Number(body, "lotSize") ?? 0m, 19, 6),
            Currency = Input.Commarea(BaseCurrency(ctx), 3),
            Boundary = Input.Commarea(boundary, 10),
            MaxDepth = 10,
            FromLine = 0,
            WinMax = 20,
        };
        var engine = ctx.Get<CostRollEngine>();
        await engine.LinkAsync(a, ctx.Aborted);
        var result = a.Ca.Result;
        var lines = new JsonArray();
        if (result.Succeeded)
        {
            var from = 1;
            for (var page = 1; page <= 25; page++)
            {
                var n = a.LineCnt;
                for (var i = 0; i < n; i++) lines.Add(TreeLine(a.Lines[i]));
                from += n;
                if (n == 0 || a.MoreLines != "Y") break;
                a.FromLine = from;
                a.Ca.Function = "GETLINE";
                await engine.LinkAsync(a, ctx.Aborted);
                if (!a.Ca.Result.Succeeded) break;
            }
        }
        if (result.ReturnCode >= 8) return ctx.Envelope(result);
        return ctx.Envelope(result, new JsonObject
        {
            ["itemCode"] = a.ItemCode,
            ["revision"] = a.Revision,
            ["costSet"] = a.CostSet,
            ["lotSize"] = Pic.S(a.LotSize, 6),
            ["boundary"] = a.Boundary,
            ["grossStarts"] = Pic.S(a.GrossStarts, 6),
            ["goodOutput"] = Pic.S(a.GoodOutput, 6),
            ["materialCost"] = Pic.S(a.MaterialCost, 2),
            ["labourCost"] = Pic.S(a.LabourCost, 2),
            ["machineCost"] = Pic.S(a.MachineCost, 2),
            ["setupCost"] = Pic.S(a.SetupCost, 2),
            ["subconCost"] = Pic.S(a.SubconCost, 2),
            ["overheadCost"] = Pic.S(a.OverheadCost, 2),
            ["otherCost"] = Pic.S(a.OtherCost, 2),
            ["recoveryCredit"] = Pic.S(a.RecoveryCredit, 2),
            ["totalCost"] = Pic.S(a.TotalCost, 2),
            ["unitCost"] = Pic.S(a.UnitCost, 6),
            ["branchValue"] = Pic.S(a.BranchValue, 2),
            ["occCount"] = a.OccCount,
            ["missingCount"] = a.MissingCount,
            ["completeness"] = a.Completeness,
            ["errorPath"] = a.ErrorPath,
            ["warning"] = a.Warning,
            ["moreLines"] = a.MoreLines,
            ["lines"] = lines,
        });
    }

    private static JsonObject TreeLine(CostRollLine l) => new()
    {
        ["seq"] = l.Seq,
        ["depth"] = l.Depth,
        ["item"] = l.Item,
        ["revision"] = l.Rev,
        ["description"] = l.Desc,
        ["element"] = l.Element,
        ["source"] = l.Source,
        ["boundary"] = l.Boundary,
        ["requiredQty"] = Pic.S(l.ReqQty, 6),
        ["unitCost"] = Pic.S(l.UnitCost, 6),
        ["extendedCost"] = Pic.S(l.ExtCost, 2),
        ["contributes"] = l.Contrib,
        ["warning"] = l.Warning,
    };

    // ------------------------------------------------------------------ WHATIF

    /// <summary>
    /// cst_runWhatIf: CostingService.Scenario (CostingService.vb:250-284) - the base case and the three fixed variants, MARGINWI
    /// when the target price is positive, else SCENARIO; the variants are returned when RC &lt;= 4.
    /// </summary>
    private static async Task<IResult> WhatIfAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "WHATIF";
        var body = await ctx.BodyObjectAsync();
        var bd = ctx.Session.BusinessDate;
        var costSet = Input.Text(body, "costSet");
        var lotSize = Input.Number(body, "lotSize") ?? 0m;
        var targetPrice = Input.Number(body, "targetPrice") ?? 0m;
        var material = Input.Number(body, "materialFactor") ?? 1m;
        var rate = Input.Number(body, "rateFactor") ?? 1m;
        var function = targetPrice > 0m ? "MARGINWI" : "SCENARIO";
        ctx.Session.LegacyProgram = "WHATIF/" + function;
        var a = new WhatIfArea
        {
            Ca = CaHeader.FromSession(ctx.Session, "WHATIF", function),
            ItemCode = Input.Commarea(ctx.Route("itemCode"), 40),
            Revision = Input.Commarea(Input.Text(body, "revision"), 4),
            ScenarioRef = Input.Commarea("SCR-" + bd.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), 30),
            EffDate = Input.Yyyymmdd(bd),
            BaseCostSet = Input.Commarea(costSet, 20),
            BaseLotSize = Input.Commarea(lotSize, 19, 6),
            TargetPrice = Input.Commarea(targetPrice, 17, 6),
            SideEffects = "N",
            VariantCnt = 3,
        };
        SetVariant(a, 1, "AS ENTERED", costSet, lotSize, material, rate, "STOP");
        SetVariant(a, 2, "MATERIAL -5%", costSet, lotSize, material * 0.95m, rate, "STOP");
        SetVariant(a, 3, "LOT X2 RATE +10%", costSet, lotSize * 2m, material, rate * 1.1m, "EXPAND");
        await ctx.Get<WhatIfEngine>().LinkAsync(a, ctx.Aborted);
        var result = a.Ca.Result;
        if (result.ReturnCode >= 8) return ctx.Envelope(result);
        var variants = new JsonArray();
        if (result.Succeeded)
        {
            for (var i = 0; i < a.VariantCnt && i < 6; i++)
            {
                var v = a.Variants[i];
                variants.Add(new JsonObject
                {
                    ["label"] = v.Label,
                    ["costSet"] = v.CostSet,
                    ["lotSize"] = Pic.S(v.LotSize, 6),
                    ["material"] = Pic.S(v.Material, 2),
                    ["conversion"] = Pic.S(v.Conversion, 2),
                    ["overhead"] = Pic.S(v.Overhead, 2),
                    ["total"] = Pic.S(v.Total, 2),
                    ["unit"] = Pic.S(v.Unit, 6),
                    ["difference"] = Pic.S(v.Diff, 2),
                    ["differencePct"] = Pic.S(v.DiffPct, 4),
                    ["marginPct"] = Pic.S(v.MarginPct, 4),
                    ["completeness"] = v.Complete,
                    ["warning"] = v.Warning,
                });
            }
        }
        return ctx.Envelope(result, new JsonObject
        {
            ["itemCode"] = a.ItemCode,
            ["revision"] = a.Revision,
            ["scenarioRef"] = a.ScenarioRef,
            ["effDate"] = CobolDate.FromYyyymmdd(a.EffDate) is { } d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
            ["baseCostSet"] = a.BaseCostSet,
            ["baseLotSize"] = Pic.S(a.BaseLotSize, 6),
            ["targetPrice"] = Pic.S(a.TargetPrice, 6),
            ["sideEffects"] = a.SideEffects,
            ["baseTotal"] = Pic.S(a.BaseTotal, 2),
            ["baseUnit"] = Pic.S(a.BaseUnit, 6),
            ["baseComplete"] = a.BaseComplete,
            ["variantCnt"] = a.VariantCnt,
            ["bestVariant"] = a.BestVariant,
            ["warning"] = a.Warning,
            ["variants"] = variants,
        });
    }

    /// <summary>CostingService.SetVariant: label, cost set, lot size, material and rate factors, boundary (COMMAREA marshalling).</summary>
    private static void SetVariant(WhatIfArea a, int i, string label, string costSet, decimal lotSize, decimal material, decimal rate, string boundary)
    {
        var v = a.Variants[i - 1];
        v.Label = Input.Commarea(label, 30);
        v.CostSet = Input.Commarea(costSet, 20);
        v.LotSize = Input.Commarea(lotSize, 19, 6);
        v.MatFactor = Input.Commarea(material, 9, 4);
        v.RateFactor = Input.Commarea(rate, 9, 4);
        v.Boundary = Input.Commarea(boundary, 8);
    }

    // ------------------------------------------------------------------ JOBCOST / WIPCALC

    /// <summary>The page's Sub-jobs choice: Y unless the request names another value (INCLUDED = Y, THIS JOB ONLY = N).</summary>
    private static string IncludeSubjobs(JsonObject body) => Input.Has(body, "includeSubJobs") ? Input.Commarea(Input.Text(body, "includeSubJobs"), 1) : "Y";

    /// <summary>cst_enquireCostJob / cst_forecastCostJob: CostingService.JobEnquire / Forecast (FR-STERP-CST-020, -021).</summary>
    private static async Task<IResult> JobCostAsync(OperationContext ctx, string function)
    {
        ctx.Session.LegacyProgram = "JOBCOST/" + function;
        var body = await ctx.BodyObjectAsync();
        var a = new JobCostArea
        {
            Ca = CaHeader.FromSession(ctx.Session, "JOBCOST", function),
            JobRef = Input.Commarea(ctx.Route("jobRef"), 20),
            IncludeSubjobs = IncludeSubjobs(body),
            Commit = "N",
            FromLine = 0,
        };
        await ctx.Get<JobCostEngine>().LinkAsync(a, ctx.Aborted);
        var result = a.Ca.Result;
        if (result.ReturnCode >= 8) return ctx.Envelope(result);
        var data = new JsonObject
        {
            ["jobRef"] = a.JobRef,
            ["parentJob"] = a.ParentJob,
            ["jobLevel"] = a.JobLevel,
            ["description"] = a.Description,
            ["operStatus"] = a.OperStatus,
            ["finStatus"] = a.FinStatus,
            ["originalBudget"] = Pic.S(a.OriginalBudget, 2),
            ["approvedBudget"] = Pic.S(a.ApprovedBudget, 2),
            ["actualGbp"] = Pic.S(a.ActualGbp, 2),
            ["accruedGbp"] = Pic.S(a.AccruedGbp, 2),
            ["committedGbp"] = Pic.S(a.CommittedGbp, 2),
            ["etcGbp"] = Pic.S(a.EtcGbp, 2),
            ["revenueGbp"] = Pic.S(a.RevenueGbp, 2),
            ["wipGbp"] = Pic.S(a.WipGbp, 2),
            ["subjobCount"] = a.SubjobCount,
            ["txnCount"] = a.TxnCount,
            ["disposition"] = a.Disposition,
            ["warning"] = a.Warning,
        };
        if (function == "FORECAST")
        {
            data["eacGbp"] = Pic.S(a.EacGbp, 2);
            data["fcstVarGbp"] = Pic.S(a.FcstVarGbp, 2);
            data["fcstAdverse"] = a.FcstAdverse;
        }
        else
        {
            var lines = new JsonArray();
            if (result.Succeeded)
            {
                for (var i = 0; i < a.LineCnt; i++)
                {
                    var l = a.Lines[i];
                    lines.Add(new JsonObject
                    {
                        ["key"] = l.Key,
                        ["description"] = l.Desc,
                        ["element"] = l.Element,
                        ["budget"] = Pic.S(l.Budget, 2),
                        ["actual"] = Pic.S(l.Actual, 2),
                        ["committed"] = Pic.S(l.Committed, 2),
                        ["etc"] = Pic.S(l.Etc, 2),
                        ["eac"] = Pic.S(l.Eac, 2),
                        ["variance"] = Pic.S(l.Variance, 2),
                        ["adverse"] = l.Adverse,
                    });
                }
            }
            data["budgetTotal"] = Pic.S(a.BudgetTotal, 2);
            data["lineCnt"] = a.LineCnt;
            data["moreLines"] = a.MoreLines;
            data["lines"] = lines;
        }
        return ctx.Envelope(result, data);
    }

    /// <summary>
    /// cst_calculateCostJobWip: CostingService.Wip (CostingService.vb:315-324) - WIPCALC CALC for the requested year/period, or
    /// the session period of the business date (LoginService.LoadPeriod, decision D-CST-09).
    /// </summary>
    private static async Task<IResult> WipCalcAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "WIPCALC/CALC";
        var body = await ctx.BodyObjectAsync();
        var year = Input.Number(body, "fiscalYear");
        var period = Input.Number(body, "periodNo");
        if (year is null || period is null)
        {
            var (fy, pn) = await SessionPeriodAsync(ctx);
            year ??= fy;
            period ??= pn;
        }
        var a = new WipCalcArea
        {
            Ca = CaHeader.FromSession(ctx.Session, "WIPCALC", "CALC"),
            JobRef = Input.Commarea(ctx.Route("jobRef"), 20),
            FiscalYear = (int)Input.Commarea(year.Value, 4, 0, signed: false),
            PeriodNo = (int)Input.Commarea(period.Value, 2, 0, signed: false),
            IncludeSubjobs = IncludeSubjobs(body),
            Commit = "N",
        };
        await ctx.Get<WipCalcEngine>().LinkAsync(a, ctx.Aborted);
        var result = a.Ca.Result;
        if (result.ReturnCode >= 8) return ctx.Envelope(result);
        return ctx.Envelope(result, new JsonObject
        {
            ["jobRef"] = a.JobRef,
            ["fiscalYear"] = a.FiscalYear,
            ["periodNo"] = a.PeriodNo,
            ["openingGbp"] = Pic.S(a.OpeningGbp, 2),
            ["matIssuesGbp"] = Pic.S(a.MatIssuesGbp, 2),
            ["matReturnsGbp"] = Pic.S(a.MatReturnsGbp, 2),
            ["labourGbp"] = Pic.S(a.LabourGbp, 2),
            ["machineGbp"] = Pic.S(a.MachineGbp, 2),
            ["overheadGbp"] = Pic.S(a.OverheadGbp, 2),
            ["subconGbp"] = Pic.S(a.SubconGbp, 2),
            ["adjustmentGbp"] = Pic.S(a.AdjustmentGbp, 2),
            ["transfersGbp"] = Pic.S(a.TransfersGbp, 2),
            ["scrapGbp"] = Pic.S(a.ScrapGbp, 2),
            ["writeoffGbp"] = Pic.S(a.WriteoffGbp, 2),
            ["additionsGbp"] = Pic.S(a.AdditionsGbp, 2),
            ["deductionsGbp"] = Pic.S(a.DeductionsGbp, 2),
            ["closingGbp"] = Pic.S(a.ClosingGbp, 2),
            ["residualGbp"] = Pic.S(a.ResidualGbp, 2),
            ["hasResidual"] = a.HasResidual,
            ["txnCount"] = a.TxnCount,
            ["balanced"] = a.Balanced,
            ["warning"] = a.Warning,
        });
    }

    /// <summary>
    /// LoginService.LoadPeriod (LoginService.vb:161-180): the first fin.Period row of the company containing the business date
    /// on its own SqlHelper connection; no row (or a failure, which the legacy sign-on only logged) is 0/0.
    /// </summary>
    private static async Task<(int Year, int Period)> SessionPeriodAsync(OperationContext ctx)
    {
        try
        {
            var row = await ctx.Get<IProcedureRunner>().WithReaderAsync(
                "SELECT TOP 1 FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode = @c AND @d BETWEEN StartDate AND EndDate " +
                "ORDER BY FiscalYear, PeriodNo",
                new Dictionary<string, object?> { ["@c"] = ctx.Session.CompanyCode, ["@d"] = ctx.Session.BusinessDate.ToDateTime(TimeOnly.MinValue) },
                async r => await r.ReadAsync(ctx.Aborted)
                    ? (Convert.ToInt32(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                       Convert.ToInt32(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture))
                    : (0, 0),
                ctx.Aborted);
            return row;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, 0);
        }
    }
}
