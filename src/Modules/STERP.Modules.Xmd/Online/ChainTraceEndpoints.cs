using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Xmd.Services;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Xmd.Online;

/// <summary>
/// The business chain enquiry of frmChainTrace (FR-STERP-XMD-010, -011): XmdService.ChainTrace (CHAINTRC TRACE LINK) and
/// XmdService.ChainTraceList (rpt.usp_ChainTrace through SqlHelper).
/// </summary>
public static class ChainTraceEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("xmd_traceBusinessChain", TraceAsync);
        ops.Handle("xmd_listBusinessChainSteps", ListStepsAsync);
    }

    /// <summary>
    /// XmdService.ChainTrace (XmdService.vb:54-68): CHAINTRCCA with company, CT-DOC-TYPE, CT-DOC-REF (CT-MAX-STEPS 24), LINK
    /// CHAINTRC TRACE; the chain is returned only when the call succeeded (RC 0/4).
    /// </summary>
    public static async Task<IResult> TraceAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "CHAINTRC";
        var body = await ctx.BodyObjectAsync();
        var ca = new ChainTraceArea
        {
            Company = ctx.Session.CompanyCode,
            DocType = Text(body, "docType"),
            DocRef = Text(body, "docRef"),
            MaxSteps = 24,
        };
        var result = await ctx.Get<IChainTraceService>().ExecuteAsync(ca, ctx.Aborted);
        return ctx.Envelope(result, result.Succeeded ? ca.ToJson() : null);
    }

    /// <summary>
    /// XmdService.ChainTraceList (XmdService.vb:75-79): ServiceBase.Deployed probe (SqlHelper.ObjectExists), then
    /// rpt.usp_ChainTrace @CompanyCode, @DocType, @Reference only; table 0 of the result.
    /// </summary>
    public static async Task<IResult> ListStepsAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var sql = ctx.Get<IProcedureRunner>();
        if (!await ObjectExistsAsync(sql, "rpt.usp_ChainTrace", ctx.Aborted))
        {
            return ctx.Direct(new JsonObject { ["items"] = new JsonArray(), ["count"] = 0, ["deployed"] = false });
        }
        var sets = await sql.QueryJsonSetsAsync("rpt.usp_ChainTrace", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            DocType = TextOrNull(body, "docType"),
            Reference = TextOrNull(body, "reference"),
        }, ctx.Aborted);
        var items = sets.Count > 0 ? sets[0] : [];
        return ctx.Direct(new JsonObject { ["items"] = items, ["count"] = items.Count, ["deployed"] = true });
    }

    /// <summary>SqlHelper.ObjectExists: SELECT OBJECT_ID(@n) on its own connection; any failure is false.</summary>
    private static async Task<bool> ObjectExistsAsync(IProcedureRunner sql, string name, CancellationToken ct)
    {
        try
        {
            var o = await sql.ScalarAsync("SELECT OBJECT_ID(@n)", new { n = name }, ct);
            return o is not null && o is not DBNull;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>A body property as text ("" when absent): the COMMAREA field is set from it (absent = spaces).</summary>
    private static string Text(JsonObject body, string name) => TextOrNull(body, name) ?? "";

    private static string? TextOrNull(JsonObject body, string name)
    {
        JsonNode? node = null;
        if (!body.TryGetPropertyValue(name, out node))
        {
            foreach (var (k, v) in body)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                {
                    node = v;
                    break;
                }
            }
        }
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }
}
