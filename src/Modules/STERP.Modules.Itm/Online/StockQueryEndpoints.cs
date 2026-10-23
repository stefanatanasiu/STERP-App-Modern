using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Itm.Online;

/// <summary>
/// Stock enquiry and movement history reads (FR-STERP-ITM-012, -014): StockService.Enquiry and StockService.Movements
/// (StockService.vb:27-31,114-121) through the SqlHelper family.
/// </summary>
public static class StockQueryEndpoints
{
    public const string MovementsSql =
        "SELECT TOP (500) MovementId, MovementDate, MovementType, SiteCode, WarehouseCode, LocationCode, " +
        "ItemCode, LotCode, SerialNo, QualityStatus, Quantity, UomCode, UnitCost, ValueGBP, SourceDocType, " +
        "SourceDocRef, ReasonCode, Reason, CreatedBy FROM inv.StockMovement WHERE CompanyCode = @c " +
        "AND (@i IS NULL OR ItemCode = @i) AND (@s IS NULL OR SiteCode = @s) " +
        "AND MovementDate >= DATEADD(day, -@d, @t) ORDER BY MovementId DESC";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("itm_listStockBalances", ListStockBalancesAsync);
        ops.Handle("itm_listStockMovements", ListStockMovementsAsync);
    }

    /// <summary>
    /// itm_listStockBalances: EXEC inv.usp_StockEnquiry @CompanyCode, @ItemCode, @SiteCode, @Condition, @IncludeZero
    /// (no @AsOf: the procedure uses the server date); the screen sends NULL for the condition ALL (frmStockEnquiry.vb:89-92).
    /// </summary>
    public static async Task<IResult> ListStockBalancesAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "StockService.Enquiry";
        var condition = ctx.Query("condition");
        if (string.Equals(condition, "ALL", StringComparison.Ordinal)) condition = null;
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync("inv.usp_StockEnquiry", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            ItemCode = ctx.Query("itemCode"),
            SiteCode = ctx.Query("siteCode"),
            Condition = condition,
            IncludeZero = ctx.QueryBool("includeZero") == true ? 1 : 0,
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>itm_listStockMovements: StockService.Movements with @t = business date; blank item/site -> NULL.</summary>
    public static async Task<IResult> ListStockMovementsAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "StockService.Movements";
        var days = ctx.QueryInt("days") ?? throw Sterp.Platform.Kernel.ApiProblemException.BadBody("Query parameter days is required.");
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(MovementsSql, new
        {
            c = ctx.Session.CompanyCode,
            i = ctx.Query("itemCode"),
            s = ctx.Query("siteCode"),
            d = days,
            t = ctx.Session.BusinessDate,
        }, ctx.Aborted);
        return ctx.List(rows);
    }
}
