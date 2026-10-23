using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Bom.Application;
using Sterp.Modules.Bom.Data;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Endpoints;

/// <summary>The online operations of STERP-BOM.openapi.yaml (spec section 7.1).</summary>
internal static class BomEndpoints
{
    // ------------------------------------------------------------------ engine-backed (COMMAREA) operations

    /// <summary>bom_explodeBill - BomService.Explode -> BOMLEAF -> BOMEXPL (FR-STERP-BOM-010).</summary>
    public static async Task<IResult> ExplodeAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BOMLEAF";
        var body = await ctx.BodyObjectAsync();
        var item = ctx.Route("parentItemCode");
        var revision = Input.Text(body, "revision");
        var qty = Input.Decimal(body, "quantity", true);
        var eff = Input.Date(body, "effectiveDate", true)!.Value;
        var view = Input.NullableText(body, "view") ?? "INDENTED";
        var maxDepth = Input.Int(body, "maxDepth", false);
        var (outcome, data) = await ctx.Get<BomFacade>().ExplodeAsync(EngineHeader.From(ctx.Session), item, revision, qty,
            Input.Yyyymmdd(eff), view, maxDepth, ctx.Aborted);
        return ctx.Envelope(outcome, data);
    }

    /// <summary>bom_costBill - BomService.CostedExplosion -> BOMCOST (FR-STERP-BOM-011).</summary>
    public static async Task<IResult> CostAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BOMCOST";
        var body = await ctx.BodyObjectAsync();
        var item = ctx.Route("parentItemCode");
        var revision = Input.Text(body, "revision");
        var qty = Input.Decimal(body, "quantity", true);
        var eff = Input.Date(body, "effectiveDate", true)!.Value;
        var costSet = Input.Text(body, "costSet");
        var (outcome, data) = await ctx.Get<BomFacade>().CostedExplosionAsync(EngineHeader.From(ctx.Session), item, revision, qty,
            Input.Yyyymmdd(eff), costSet, ctx.Aborted);
        return ctx.Envelope(outcome, data);
    }

    /// <summary>bom_netBillRequirement - BomService.NettedPlan -> BOMNET (FR-STERP-BOM-012).</summary>
    public static async Task<IResult> NetAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BOMNET";
        var body = await ctx.BodyObjectAsync();
        var item = ctx.Route("parentItemCode");
        var qty = Input.Decimal(body, "quantity", true);
        var eff = Input.Date(body, "effectiveDate", true)!.Value;
        var (outcome, data) = await ctx.Get<BomFacade>().NettedPlanAsync(EngineHeader.From(ctx.Session), item, qty, Input.Yyyymmdd(eff), ctx.Aborted);
        return ctx.Envelope(outcome, data);
    }

    /// <summary>bom_validateBill - BomService.Validate (record false) -> BOMVALID CHECK (FR-STERP-BOM-016, FR-STERP-BOM-042).</summary>
    public static async Task<IResult> ValidateAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BOMVALID";
        var body = await ctx.BodyObjectAsync();
        var revision = Input.Text(body, "bomRevision");
        var eff = Input.Date(body, "effectiveDate", true)!.Value;
        var (outcome, data) = await ctx.Get<BomChangeFacade>().ValidateAsync(EngineHeader.From(ctx.Session), ctx.Route("bomRef"), revision,
            Input.Yyyymmdd(eff), ctx.Aborted);
        return ctx.Envelope(outcome, data);
    }

    /// <summary>
    /// bom_previewBulkChange - BomBulkService.Preview -> BOMBULK PREVIEW/GETLINE -> BOMBREV (FR-STERP-BOM-018): RC 0/4 200,
    /// RC 6 202 HOLD, RC 8 422 carrying the full preview in <c>data</c>, RC 16 404, RC 12 500.
    /// </summary>
    public static async Task<IResult> PreviewBulkChangeAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "BOMBULK";
        var body = await ctx.BodyObjectAsync();
        var oldItem = Input.Text(body, "oldItem");
        var newItem = Input.Text(body, "newItem");
        var eff = Input.Date(body, "effectiveDate", true)!.Value;
        var scope = Input.Text(body, "scope");
        var scopeItem = Input.Text(body, "scopeItem");
        var includeDraft = Input.Bool(body, "includeDraft", true);
        var (outcome, data) = await ctx.Get<BomChangeFacade>().PreviewBulkChangeAsync(EngineHeader.From(ctx.Session), ctx.Route("changeRef"),
            oldItem, newItem, Input.Yyyymmdd(eff), scope, scopeItem, includeDraft, ctx.Aborted);
        return ctx.Envelope(outcome, outcome.ReturnCode is 0 or 4 or 6 or 8 ? data : null);
    }

    // ------------------------------------------------------------------ procedure-backed (SqlHelper) operations

    private static DateOnly RequiredDate(OperationContext ctx, string name) =>
        ctx.QueryDate(name) ?? throw ApiProblemException.BadBody($"Query parameter {name} is required.");

    private static string RequiredText(OperationContext ctx, string name) =>
        ctx.Query(name) ?? throw ApiProblemException.BadBody($"Query parameter {name} is required.");

    /// <summary>bom_getWhereUsed - BomService.WhereUsed -> bom.usp_BomWhereUsed, first result set (FR-STERP-BOM-013).</summary>
    public static async Task<IResult> WhereUsedAsync(OperationContext ctx)
    {
        var asOf = RequiredDate(ctx, "asOf");
        var maxLevels = ctx.QueryInt("maxLevels") ?? throw ApiProblemException.BadBody("Query parameter maxLevels is required.");
        var includeDraft = ctx.QueryBool("includeDraft") ?? throw ApiProblemException.BadBody("Query parameter includeDraft is required.");
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("bom.usp_BomWhereUsed", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@ComponentItemCode", ctx.Route("componentItemCode")),
            ("@AsOf", asOf),
            ("@MaxLevels", maxLevels),
            ("@IncludeDraft", includeDraft ? 1 : 0)), ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>bom_compareBillRevisions - BomService.CompareRevisions -> bom.usp_BomCompareRevisions (FR-STERP-BOM-014).</summary>
    public static async Task<IResult> CompareAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("bom.usp_BomCompareRevisions", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@BomRef", ctx.Route("bomRef")),
            ("@FromRevision", RequiredText(ctx, "fromRevision")),
            ("@ToRevision", RequiredText(ctx, "toRevision")),
            ("@CostSetCode", ctx.Query("costSetCode") ?? "")), ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>BomService.ReadStructure: bom.usp_BomStructure with the session company (BomService.vb:363-369).</summary>
    private static Task<JsonArray> ReadStructureAsync(OperationContext ctx, string mode, string bomRef, string revision, string parentItem, DateOnly asOf) =>
        ctx.Get<IProcedureRunner>().QueryJsonAsync("bom.usp_BomStructure", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@BomRef", bomRef),
            ("@BomRevision", revision),
            ("@ParentItemCode", parentItem),
            ("@Mode", mode),
            ("@AsOf", asOf)), ctx.Aborted);

    /// <summary>bom_listBillRevisions - BomService.Revisions (REVISIONS).</summary>
    public static async Task<IResult> RevisionsAsync(OperationContext ctx) =>
        ctx.List(await ReadStructureAsync(ctx, "REVISIONS", ctx.Query("bomRef") ?? "", "", ctx.Query("parentItemCode") ?? "", RequiredDate(ctx, "asOf")));

    /// <summary>bom_getBillHeader - BomService.BillHeader (HEADER, 0 or 1 row, D-BOM-06).</summary>
    public static async Task<IResult> HeaderAsync(OperationContext ctx)
    {
        var rows = await ReadStructureAsync(ctx, "HEADER", ctx.Route("bomRef"), ctx.Query("revision") ?? "", "", RequiredDate(ctx, "asOf"));
        while (rows.Count > 1) rows.RemoveAt(rows.Count - 1);
        return ctx.List(rows);
    }

    /// <summary>bom_listBillComponents - BomService.BillLines (LINES).</summary>
    public static async Task<IResult> ComponentsAsync(OperationContext ctx) =>
        ctx.List(await ReadStructureAsync(ctx, "LINES", ctx.Route("bomRef"), ctx.Query("revision") ?? "", "", RequiredDate(ctx, "asOf")));

    /// <summary>bom_listBillByProducts - BomService.ByProducts (BYPRODUCT).</summary>
    public static async Task<IResult> ByProductsAsync(OperationContext ctx) =>
        ctx.List(await ReadStructureAsync(ctx, "BYPRODUCT", ctx.Route("bomRef"), ctx.Query("revision") ?? "", "", RequiredDate(ctx, "asOf")));

    /// <summary>
    /// bom_releaseBillRevision - BomService.Release -> bom.usp_ReleaseBom (FR-STERP-BOM-017). On the RELEASED path the
    /// procedure's first result set is core.usp_WriteAudit's AuditId row (usp_WriteAudit.sql:11), which SqlHelper's
    /// ExecuteDataTable would return; the binding BomReleaseOutcome schema is the procedure's outcome row, so the result
    /// set carrying Outcome is returned (finding recorded in the Agent 08 BOM work-stream report).
    /// </summary>
    public static async Task<IResult> ReleaseAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var sets = await ctx.Get<IProcedureRunner>().QueryJsonSetsAsync("bom.usp_ReleaseBom", SqlHelperArgs.Of(
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@BomRef", ctx.Route("bomRef")),
            ("@BomRevision", ctx.Route("bomRevision")),
            ("@UserCode", ctx.Session.UserCode),
            ("@ReviewerUser", Input.NullableText(body, "reviewerUser")),
            ("@EffectiveFrom", Input.Date(body, "effectiveFrom", false)),
            ("@SupersedeRevision", Input.NullableText(body, "supersedeRevision")),
            ("@Reason", Input.NullableText(body, "reason")),
            ("@CorrelationId", ctx.Session.CorrelationId)), ctx.Aborted);
        var rows = sets.FirstOrDefault(s => s.Count > 0 && s[0] is JsonObject o && o.ContainsKey("outcome")) ?? sets.FirstOrDefault() ?? [];
        return ctx.List(rows);
    }
}
