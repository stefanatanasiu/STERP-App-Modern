using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Itm.Online;

/// <summary>
/// Stock count list, lines and counted-quantity entry (FR-STERP-ITM-017, -018): StockService.Counts, .CountLines and
/// .SaveCount (StockService.vb:124-148) through the SqlHelper family.
/// </summary>
public static class CountEndpoints
{
    public const string CountsSql =
        "SELECT CountId, CountRef, CountType, SiteCode, WarehouseCode, ScopeDescription, CutoffAt, " +
        "IsFrozen, Status, CountedBy, ApprovedBy, ApprovedAt FROM inv.StockCount WHERE CompanyCode = @c " +
        "AND (@s IS NULL OR @s = 'ALL' OR Status = @s) ORDER BY CountRef";

    public const string CountLinesSql =
        "SELECT l.CountId, l.LineNum, l.ItemCode, l.Revision, l.WarehouseCode, l.LocationCode, l.LotCode, " +
        "l.SerialNo, l.ExpectedQty, l.CountedQty, l.RecountQty, l.InterveningMovementQty, " +
        "ISNULL(l.CountedQty, 0) - l.ExpectedQty AS VarianceQty, l.AdjustmentQty, l.AdjustmentValueGBP, " +
        "l.ReasonCode, l.MovementId, l.Status FROM inv.StockCountLine l JOIN inv.StockCount h " +
        "ON h.CountId = l.CountId WHERE h.CompanyCode = @c AND h.CountRef = @r ORDER BY l.LineNum";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("itm_listStockCounts", ListStockCountsAsync);
        ops.Handle("itm_listStockCountLines", ListStockCountLinesAsync);
        ops.Handle("itm_recordCountedQuantity", RecordCountedQuantityAsync);
    }

    public static async Task<IResult> ListStockCountsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "StockService.Counts";
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(CountsSql, new { c = ctx.Session.CompanyCode, s = ctx.Query("status") }, ctx.Aborted);
        return ctx.List(rows);
    }

    public static async Task<IResult> ListStockCountLinesAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "StockService.CountLines";
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(CountLinesSql, new { c = ctx.Session.CompanyCode, r = ctx.Route("countRef") }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>
    /// itm_recordCountedQuantity: MaintService.Save("ITM.COUNTLINE", "CHANGE", {CountId, LineNum, CountedQty, Status}) ->
    /// EXEC core.usp_UiMaintSave (MaintService.vb:31-43,51-68). A procedure THROW surfaces as BusinessRuleException (422),
    /// any other failure as 500 SYS0001 (contracts 4.6).
    /// </summary>
    public static async Task<IResult> RecordCountedQuantityAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "StockService.SaveCount";
        var countId = RouteInt(ctx, "countId");
        var lineNum = RouteInt(ctx, "lineNum");
        var body = await ctx.BodyObjectAsync();
        var qty = body["countedQty"] switch
        {
            null => throw ApiProblemException.BadBody("countedQty is required."),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => throw ApiProblemException.BadBody("countedQty must be a string."),
        };
        var values = new List<KeyValuePair<string, string?>>
        {
            new("CountId", countId.ToString(CultureInfo.InvariantCulture)),
            new("LineNum", lineNum.ToString(CultureInfo.InvariantCulture)),
            new("CountedQty", qty),
            new("Status", "COUNTED"),
        };
        var sql = ctx.Get<IProcedureRunner>();
        await sql.ExecuteAsync("core.usp_UiMaintSave", new
        {
            MaintCode = "ITM.COUNTLINE",
            UserCode = ctx.Session.UserCode,
            CompanyCode = ctx.Session.CompanyCode,
            Mode = "CHANGE",
            Data = MaintXml(values),
        }, ctx.Aborted);
        return ctx.Direct(new JsonObject
        {
            ["countId"] = countId,
            ["lineNum"] = lineNum,
            ["countedQty"] = qty,
            ["status"] = "COUNTED",
        });
    }

    /// <summary>MaintService.ToXml (MaintService.vb:51-62): &lt;r&gt;&lt;c n="k" v="v" /&gt;...&lt;/r&gt;, null values as null="1".</summary>
    public static string MaintXml(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var sb = new StringBuilder("<r>");
        foreach (var (k, v) in values)
        {
            sb.Append("<c n=\"").Append(SqlText.XmlEscape(k)).Append('"');
            if (v is null) sb.Append(" null=\"1\" />");
            else sb.Append(" v=\"").Append(SqlText.XmlEscape(v)).Append("\" />");
        }
        return sb.Append("</r>").ToString();
    }

    private static int RouteInt(OperationContext ctx, string name) =>
        int.TryParse(ctx.Route(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw ApiProblemException.BadBody($"{name} must be an integer.");
}
