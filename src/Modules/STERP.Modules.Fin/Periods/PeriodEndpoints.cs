using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Periods;

public sealed class FinCloseStepRequest
{
    public string? EvidenceRef { get; set; }
}

public sealed class FinPeriodReopenRequest
{
    public string? Reason { get; set; }
    public string? Reviewer { get; set; }
}

/// <summary>Period close checklist operations (PeriodService.Status / Checklist / CompleteStep / Close).</summary>
public static class PeriodEndpoints
{
    /// <summary>PeriodService.Area: CL-FISCAL-YEAR PIC 9(4) and CL-PERIOD-NO PIC 9(2), unsigned (contracts 4.4).</summary>
    private static PerCloseArea Area(OperationContext ctx) => new()
    {
        FiscalYear = (int)In.N(RouteLong(ctx, "fiscalYear"), 4, 0, signed: false),
        PeriodNo = (int)In.N(RouteLong(ctx, "periodNo"), 2, 0, signed: false),
    };

    public static long RouteLong(OperationContext ctx, string name)
    {
        var text = ctx.Route(name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw ApiProblemException.BadBody($"Path parameter {name} must be an integer.");
    }

    private static async Task<IResult> RunAsync(OperationContext ctx, string function, PerCloseArea cl)
    {
        ctx.Session.LegacyProgram = "PERCLOSE";
        var ca = await ctx.Get<PeriodCloseProgram>().RunAsync(CaRequest.From(ctx.Session, function), cl, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), cl.ToJson());
    }

    /// <summary>fin_getPeriodCloseStatus: PeriodService.Status -> PERCLOSE STATUS.</summary>
    public static Task<IResult> StatusAsync(OperationContext ctx) => RunAsync(ctx, "STATUS", Area(ctx));

    /// <summary>fin_completeCloseStep: PeriodService.CompleteStep(year, period, step, "DONE", evidence, 0) -> PERCLOSE STEP.</summary>
    public static async Task<IResult> CompleteStepAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinCloseStepRequest>();
        var cl = Area(ctx);
        cl.StepCode = In.X(ctx.Route("stepCode"), 20);
        cl.StepAction = "DONE";
        cl.EvidenceRef = In.X(body.EvidenceRef, 200);
        cl.Difference = 0m;
        return await RunAsync(ctx, "STEP", cl);
    }

    /// <summary>fin_softClosePeriod: PeriodService.Close("SOFT", year, period, "", "", False).</summary>
    public static Task<IResult> SoftCloseAsync(OperationContext ctx) => CloseAsync(ctx, "SOFT");

    /// <summary>fin_hardClosePeriod: PeriodService.Close("HARD", year, period, "", "", False).</summary>
    public static Task<IResult> HardCloseAsync(OperationContext ctx) => CloseAsync(ctx, "HARD");

    private static Task<IResult> CloseAsync(OperationContext ctx, string function)
    {
        var cl = Area(ctx);
        cl.Force = "N";
        return RunAsync(ctx, function, cl);
    }

    /// <summary>fin_reopenPeriod: PeriodService.Close("REOPEN", year, period, reason, approver, False).</summary>
    public static async Task<IResult> ReopenAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinPeriodReopenRequest>();
        var cl = Area(ctx);
        cl.Reason = In.X(body.Reason, 200);
        cl.Reviewer = In.X(body.Reviewer, 20);
        cl.Force = "N";
        return await RunAsync(ctx, "REOPEN", cl);
    }

    /// <summary>fin_getCloseChecklist: PeriodService.Checklist (PeriodService.vb:72-77) - result set 2 of fin.usp_CloseStatus.</summary>
    public static async Task<IResult> ChecklistAsync(OperationContext ctx)
    {
        var sets = await ctx.Get<IProcedureRunner>().SetsAsync("fin.usp_CloseStatus", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            FiscalYear = RouteLong(ctx, "fiscalYear"),
            PeriodNo = RouteLong(ctx, "periodNo"),
        }, ctx.Aborted);
        return ctx.List(sets.Count < 2 ? new JsonArray() : sets[1]);
    }
}
