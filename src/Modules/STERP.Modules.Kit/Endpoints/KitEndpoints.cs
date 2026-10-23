using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Kit.Application;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>The online operations of STERP-KIT.openapi.yaml (spec section 7.1).</summary>
internal static class KitEndpoints
{
    // ------------------------------------------------------------------ procedure-backed lists (SqlHelper family)

    /// <summary>
    /// kit_listKitWorklist - frmKitMaint.LoadData -> KitService.Worklist (KitService.vb:36-40): an undeployed
    /// kit.usp_KitReadiness gives an empty table; otherwise the procedure with blank filters as NULL (KitArea.Opt).
    /// </summary>
    public static async Task<IResult> WorklistAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        if (!await DeployedAsync(sql, "kit.usp_KitReadiness", ctx.Aborted)) return ctx.List(new JsonArray());
        var rows = await sql.QueryJsonAsync("kit.usp_KitReadiness", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@SiteCode", ctx.Query("site")),
            ("@Status", ctx.Query("status")),
            ("@KitRef", ctx.Query("kitRef"))), ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>
    /// kit_listRecallTrace - frmKitGenealogy.RecallClicked -> KitExecutionService.GenealogyList (KitExecutionService.vb:124-129)
    /// -> kit.usp_KitGenealogy: item as sent, lot/serial blank as NULL, the depth as sent (the procedure maps 1..12).
    /// </summary>
    public static async Task<IResult> RecallListAsync(OperationContext ctx)
    {
        var item = ctx.Query("itemCode") ?? throw ApiProblemException.BadBody("Query parameter itemCode is required.");
        var maxDepth = ctx.QueryInt("maxDepth") ?? 5;
        var sql = ctx.Get<IProcedureRunner>();
        if (!await DeployedAsync(sql, "kit.usp_KitGenealogy", ctx.Aborted)) return ctx.List(new JsonArray());
        var rows = await sql.QueryJsonAsync("kit.usp_KitGenealogy", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@ItemCode", item),
            ("@LotCode", ctx.Query("lotCode")),
            ("@SerialNo", ctx.Query("serialNo")),
            ("@MaxDepth", maxDepth)), ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ServiceBase.Deployed -> SqlHelper.ObjectExists (SqlHelper.cs:135-143): OBJECT_ID not null; any failure = not deployed.</summary>
    private static async Task<bool> DeployedAsync(IProcedureRunner sql, string objectName, CancellationToken ct)
    {
        try
        {
            var o = await sql.ScalarAsync("SELECT OBJECT_ID(@n)", SqlHelperArgs.Of(("@n", objectName)), ct);
            return o is not null && o is not DBNull;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ KITCREAT

    private static string KitRef(OperationContext ctx) => Input.X(ctx.Route("kitRef"), 30);

    /// <summary>kit_getKit - KitService.Enquire: KITCREAT ENQUIRE + GETLINE drain; refusals carry the grid data (D-KIT-08).</summary>
    public static async Task<IResult> GetKitAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCREAT";
        var (r, ca, lines) = await ctx.Get<KitService>().EnquireAsync(KitSession.From(ctx.Session), KitRef(ctx), ctx.Aborted);
        return ctx.Envelope(r, KitCreatJson.Kit(ca, lines));
    }

    /// <summary>kit_reviewKit - KitService.Review -> KITCREAT REVIEW.</summary>
    public static async Task<IResult> ReviewAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCREAT";
        await ctx.BodyObjectAsync();
        var (r, ca) = await ctx.Get<KitService>().ReviewAsync(KitSession.From(ctx.Session), KitRef(ctx), ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitCreatJson.Lifecycle(ca));
    }

    /// <summary>kit_releaseKit - KitService.Release -> KITCREAT RELEASE (RC 6 KIT0009 = 202 with the kit released, D-KIT-09).</summary>
    public static async Task<IResult> ReleaseAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCREAT";
        await ctx.BodyObjectAsync();
        var (r, ca) = await ctx.Get<KitService>().ReleaseAsync(KitSession.From(ctx.Session), KitRef(ctx), ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitCreatJson.Lifecycle(ca));
    }

    // ------------------------------------------------------------------ KITISSUE

    /// <summary>kit_checkComponentIssue - KitExecutionService.Issue(checkOnly = true) -> KITISSUE CHECK.</summary>
    public static Task<IResult> CheckIssueAsync(OperationContext ctx) => IssueCoreAsync(ctx, true);

    /// <summary>kit_issueComponent - KitExecutionService.Issue(checkOnly = false) -> KITISSUE ISSUE.</summary>
    public static Task<IResult> IssueAsync(OperationContext ctx) => IssueCoreAsync(ctx, false);

    private static async Task<IResult> IssueCoreAsync(OperationContext ctx, bool checkOnly)
    {
        ctx.Session.LegacyProgram = "KITISSUE";
        var body = await ctx.BodyObjectAsync();
        if (!Input.Has(body, "lineNum")) throw ApiProblemException.BadBody("Property lineNum is required.");
        var (r, ca) = await ctx.Get<KitIssueService>().IssueAsync(KitSession.From(ctx.Session), KitRef(ctx),
            Input.Int(body, "lineNum"),
            Input.X(Input.Text(body, "itemCode"), 40),
            Input.X(Input.Text(body, "lotCode"), 30),
            Input.X(Input.Text(body, "serialNo"), 40),
            Input.Num(Input.Decimal(body, "quantity"), 19, 6),
            Input.X(Input.Text(body, "location"), 20),
            Input.X(Input.Text(body, "reasonCode"), 20),
            Input.X(Input.Text(body, "reasonText"), 300),
            Input.Flag(body, "allowOver"),
            checkOnly, ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitIssueJson.Issue(ca));
    }

    /// <summary>kit_backflushKit - KitExecutionService.Backflush -> KITISSUE BACKFLSH (no dry run).</summary>
    public static async Task<IResult> BackflushAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITISSUE";
        var body = await ctx.BodyObjectAsync();
        var goodQty = Input.Num(Input.Decimal(body, "goodQty"), 19, 6);
        var (r, ca) = await ctx.Get<KitIssueService>().BackflushAsync(KitSession.From(ctx.Session), KitRef(ctx), goodQty, ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitIssueJson.Backflush(ca));
    }

    // ------------------------------------------------------------------ KITTRIAL

    /// <summary>kit_runTrialKitting - KitService.Trial: KITTRIAL TRIAL + GETLINE drain; a refusal carries the summary (D-KIT-08).</summary>
    public static async Task<IResult> TrialAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITTRIAL";
        var body = await ctx.BodyObjectAsync();
        var (r, ca, lines) = await ctx.Get<KitTrialService>().TrialAsync(KitSession.From(ctx.Session),
            Input.X(Input.Text(body, "kitRef"), 30),
            Input.X(Input.Text(body, "parentItem"), 40),
            Input.X(Input.Text(body, "parentRev"), 4),
            Input.X(Input.Text(body, "site"), 10),
            Input.Num(Input.Decimal(body, "requestedQty"), 19, 6),
            Pic.Date(Input.Date(body, "reqDate")),
            Input.Flag(body, "useInbound"),
            Input.Flag(body, "useSubassy"), ctx.Aborted);
        return ctx.Envelope(r, KitTrialJson.Result(ca, r.IsFailed ? [] : lines));
    }

    /// <summary>kit_runMaxBuildTrial - KitService.MaxBuild: KITTRIAL MAXBUILD with the caller's quantity (the page sends 0).</summary>
    public static async Task<IResult> MaxBuildAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITTRIAL";
        var body = await ctx.BodyObjectAsync();
        var (r, ca) = await ctx.Get<KitTrialService>().MaxBuildAsync(KitSession.From(ctx.Session),
            Input.X(Input.Text(body, "kitRef"), 30),
            Input.X(Input.Text(body, "parentItem"), 40),
            Input.X(Input.Text(body, "parentRev"), 4),
            Input.X(Input.Text(body, "site"), 10),
            Input.Num(Input.Decimal(body, "requestedQty"), 19, 6),
            Pic.Date(Input.Date(body, "reqDate")),
            Input.Flag(body, "useInbound"), ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitTrialJson.Summary(ca));
    }

    // ------------------------------------------------------------------ KITCOMM

    /// <summary>
    /// kit_checkCommercialKitSets - KitExecutionService.Commercial SETS as frmKitMaint sends it; refusals carry the answer
    /// with no lines (D-KIT-08).
    /// </summary>
    public static async Task<IResult> CommercialSetsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCOMM";
        var (r, ca, lines) = await ctx.Get<KitCommService>().SetsAsync(KitSession.From(ctx.Session),
            Input.X(ctx.Route("kitItem"), 40),
            Input.X(ctx.Query("kitVersion"), 4),
            Input.X(ctx.Query("orderRef"), 40),
            ctx.QueryInt("orderLine") ?? 0,
            Input.Num(ctx.QueryDecimal("parentQty") ?? 1m, 19, 6), ctx.Aborted);
        return ctx.Envelope(r, KitCommJson.Sets(ca, lines));
    }

    // ------------------------------------------------------------------ GENEALOG

    /// <summary>
    /// kit_traceGenealogy - KitExecutionService.Genealogy: GENEALOG FORWARD/BACKWARD/WHEREUSD re-run per window (1-based
    /// FROM-LINE, the row at each window edge repeats); refusals carry the summary and the lines collected (D-KIT-08).
    /// </summary>
    public static async Task<IResult> GenealogyAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "GENEALOG";
        var (r, ca, lines) = await ctx.Get<GenealogService>().TraceAsync(KitSession.From(ctx.Session),
            Input.X(ctx.Query("direction") ?? "FORWARD", 10),
            Input.X(ctx.Query("kitRef"), 30),
            Input.X(ctx.Query("itemCode"), 40),
            Input.X(ctx.Query("lotCode"), 30),
            Input.X(ctx.Query("serialNo"), 40),
            (int)Input.Num(ctx.QueryInt("maxDepth") ?? 10, 4, 0), ctx.Aborted);
        return ctx.Envelope(r, GenealogJson.Trace(ca, lines));
    }

    // ------------------------------------------------------------------ KITCOST

    /// <summary>
    /// kit_getKitCost - KitExecutionService.Cost: KITCOST COST (VARIANCE when varianceOnly) then GETELEM; refusals carry the
    /// position with no elements (D-KIT-08).
    /// </summary>
    public static async Task<IResult> CostAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCOST";
        var costSet = Input.X(ctx.Query("costSet") ?? "STANDARD", 20);
        var varianceOnly = string.Equals((ctx.Query("varianceOnly") ?? "N").Trim(), "Y", StringComparison.OrdinalIgnoreCase);
        var (r, ca, elements) = await ctx.Get<KitCostService>().CostAsync(KitSession.From(ctx.Session), KitRef(ctx), costSet, varianceOnly, ctx.Aborted);
        return ctx.Envelope(r, KitCostJson.Position(ca, elements));
    }

    /// <summary>kit_checkKitKind - KitService.KindCheck -> KITCREAT KINDCHK.</summary>
    public static async Task<IResult> KindCheckAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "KITCREAT";
        var body = await ctx.BodyObjectAsync();
        var kind = Input.X(Input.Text(body, "kitKind"), 12);
        var parent = Input.X(Input.Text(body, "parentItem"), 40);
        var (r, ca) = await ctx.Get<KitService>().KindCheckAsync(KitSession.From(ctx.Session), KitRef(ctx), kind, parent, ctx.Aborted);
        return ctx.Envelope(r, r.IsFailed ? null : KitCreatJson.KindCheck(ca));
    }
}
