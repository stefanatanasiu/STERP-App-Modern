using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Worklists;

/// <summary>
/// Approvals inbox and decision, exception worklist and resolution, audit enquiry (FR-STERP-PLATFORM-036..040): the
/// ApprovalService, ExceptionService and AuditService facades (AuditService.vb:7-66) - one procedure call each on its own
/// SqlHelper connection, no target transaction, no permission check (none on the legacy path).
/// </summary>
public static class WorklistEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_listApprovals", ListApprovalsAsync);
        ops.Handle("platform_decideApproval", DecideApprovalAsync);
        ops.Handle("platform_listExceptions", ListExceptionsAsync);
        ops.Handle("platform_resolveException", ResolveExceptionAsync);
        ops.Handle("platform_listAuditEntries", ListAuditEntriesAsync);
    }

    /// <summary>ApprovalService.Inbox: core.usp_UiApprovalInbox(@CompanyCode, @UserCode, @Status); the page default is PENDING.</summary>
    private static async Task<IResult> ListApprovalsAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiApprovalInbox", new
        {
            CompanyCode = s.CompanyCode,
            UserCode = s.UserCode,
            Status = ctx.Query("status") ?? "PENDING",
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>
    /// ApprovalService.Decide: core.usp_DecideApproval(@ApprovalId, @UserCode, @Decision, @Comment) once ('' -> NULL); THROW
    /// texts become 422 E-00nn; the decided row is returned.
    /// </summary>
    private static async Task<IResult> DecideApprovalAsync(OperationContext ctx)
    {
        var id = PathId(ctx, "approvalId");
        var body = await ctx.BodyObjectAsync();
        var decision = Text(body, "decision") ?? throw ApiProblemException.BadBody("decision is required.",
            new Dictionary<string, string[]> { ["decision"] = ["decision is required."] });
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_DecideApproval", new
        {
            ApprovalId = id,
            UserCode = ctx.Session.UserCode,
            Decision = decision,
            Comment = Text(body, "comment"),
        }, ctx.Aborted);
        if (rows.Count == 0 || rows[0] is not JsonObject r) return ctx.NotFound("E-0021", "That approval request does not exist.", null);
        return ctx.Direct(new JsonObject
        {
            ["approvalId"] = r["approvalId"]?.DeepClone(),
            ["companyCode"] = r["companyCode"]?.DeepClone(),
            ["documentType"] = r["documentType"]?.DeepClone(),
            ["documentRef"] = r["documentRef"]?.DeepClone(),
            ["approvalType"] = r["approvalType"]?.DeepClone(),
            ["amountGbp"] = r["amountGBP"]?.DeepClone(),
            ["status"] = r["status"]?.DeepClone(),
            ["requestedBy"] = r["requestedBy"]?.DeepClone(),
            ["decidedBy"] = r["decidedBy"]?.DeepClone(),
            ["decidedAt"] = r["decidedAt"]?.DeepClone(),
            ["delegationId"] = r["delegationId"]?.DeepClone(),
            ["comment"] = r["comment"]?.DeepClone(),
        });
    }

    /// <summary>ExceptionService.Worklist: core.usp_UiExceptionList(@CompanyCode, @Status, @Severity, @OwnerUser trimmed).</summary>
    private static async Task<IResult> ListExceptionsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiExceptionList", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            Status = ctx.Query("status") ?? "OPEN",
            Severity = ctx.Query("severity") ?? "ALL",
            OwnerUser = ctx.Query("ownerUser")?.Trim(),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>ExceptionService.Resolve: core.usp_ResolveException(@ExceptionId, @UserCode, @ResolutionNote), status RESOLVED.</summary>
    private static async Task<IResult> ResolveExceptionAsync(OperationContext ctx)
    {
        var id = PathId(ctx, "exceptionId");
        var body = await ctx.BodyObjectAsync();
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_ResolveException", new
        {
            ExceptionId = id,
            UserCode = ctx.Session.UserCode,
            ResolutionNote = Text(body, "resolutionNote"),
        }, ctx.Aborted);
        if (rows.Count == 0 || rows[0] is not JsonObject r) return ctx.NotFound("E-0017", "That exception does not exist.", null);
        r["consoleMessage"] = null;
        return ctx.Direct(r);
    }

    /// <summary>AuditService.Enquire: core.usp_UiAuditEnquiry(@CompanyCode, @EntityType, @EntityKey, @FromDate, @ToDate, @UserCode).</summary>
    private static async Task<IResult> ListAuditEntriesAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiAuditEnquiry", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            EntityType = ctx.Query("entityType"),
            EntityKey = ctx.Query("entityKey"),
            FromDate = ctx.QueryDate("fromDate"),
            ToDate = ctx.QueryDate("toDate"),
            UserCode = ctx.Query("userCode"),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    internal static long PathId(OperationContext ctx, string name) =>
        long.TryParse(ctx.Route(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw ApiProblemException.BadBody($"{name} must be an integer.");

    internal static string? Text(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw ApiProblemException.BadBody($"{name} must be a string."),
    };
}
