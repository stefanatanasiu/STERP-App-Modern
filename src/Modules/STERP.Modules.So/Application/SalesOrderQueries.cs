using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.So.Application;

/// <summary>
/// SalesOrderService enquiry members (SqlHelper family: one short connection per query, autocommit, '' bound as NULL):
/// HeaderSql/LineSql (FR-STERP-SO-010), OrderPickSql (F2 order picker), PickListPickSql/PicksForOrder and PickLineSql
/// (FR-STERP-SO-018) - the SQL text of dotnet/STERP.Business/Sales/SalesOrderService.vb:23-100, unchanged.
/// </summary>
public static class SalesOrderQueries
{
    /// <summary>SalesOrderService.OrderPickSql (SalesOrderService.vb:23-27).</summary>
    public const string OrderPickSql =
        "SELECT OrderNumber, CustomerCode, OrderDate, CurrencyCode, ReleaseStatus, FulfilmentStatus " +
        "FROM so.SalesOrder WHERE CompanyCode = @CompanyCode " +
        "AND (@Search IS NULL OR @Search = '' OR OrderNumber LIKE @Search + '%' OR CustomerCode LIKE @Search + '%') " +
        "ORDER BY OrderNumber";

    /// <summary>SalesOrderService.PickListPickSql (SalesOrderService.vb:35-45).</summary>
    public const string PickListPickSql =
        "SELECT p.PickListRef, p.OrderNumber, p.CustomerCode, p.Status, p.RequiredDate, p.WarehouseCode " +
        "FROM so.PickList p WHERE p.CompanyCode = @CompanyCode AND p.SiteCode = @SiteCode " +
        "AND p.Status <> 'CANCELLED' " +
        "AND (@Argument IS NULL OR @Argument = '' OR p.OrderNumber = @Argument) " +
        "AND (@Search IS NULL OR @Search = '' OR p.PickListRef LIKE @Search + '%' " +
        "OR p.OrderNumber LIKE @Search + '%' OR p.CustomerCode LIKE @Search + '%') " +
        "AND EXISTS (SELECT 1 FROM so.PickListLine l JOIN so.DeliverySchedule s " +
        "ON s.SalesOrderLineId = l.SalesOrderLineId AND s.ScheduleNo = l.ScheduleNo " +
        "WHERE l.PickListId = p.PickListId AND l.PickedQty > 0 AND s.Quantity > s.DespatchedQty " +
        "AND s.DespatchedQty < (SELECT SUM(x.PickedQty) FROM so.PickListLine x JOIN so.PickList xp " +
        "ON xp.PickListId = x.PickListId WHERE xp.Status <> 'CANCELLED' " +
        "AND x.SalesOrderLineId = l.SalesOrderLineId AND x.ScheduleNo = l.ScheduleNo)) " +
        "ORDER BY p.PickListRef";

    /// <summary>SalesOrderService.HeaderSql (SalesOrderService.vb:47-54).</summary>
    public const string HeaderSql =
        "SELECT o.OrderNumber, o.OrderType, o.SiteCode, o.CustomerCode, c.LegalName AS CustomerName, " +
        "o.CustomerOrderRef, o.OrderDate, o.CurrencyCode, o.TermsCode, o.SalespersonCode, " +
        "o.IncotermsRule, o.IncotermsNamedPlace, o.DeliveryAddressRef, o.CommercialStatus, " +
        "o.ReleaseStatus, o.FulfilmentStatus, o.BillingStatus, o.HoldReason, o.NetAmount, " +
        "o.VatAmount, o.GrossAmount FROM so.SalesOrder o LEFT JOIN ar.Customer c " +
        "ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode " +
        "WHERE o.CompanyCode = @CompanyCode AND o.OrderNumber = @OrderNumber";

    /// <summary>SalesOrderService.LineSql (SalesOrderService.vb:56-60).</summary>
    public const string LineSql =
        "SELECT LineNum, ItemCode, Description, AcceptedQty, UomCode, ConversionFactor, UnitPrice, " +
        "PricePerQty, PriceSource, DiscountPct1, DiscountPct2, NetAmount, VatCode, VatRatePct, " +
        "VatAmount, MarginPct, CostSnapshotGBP, DespatchedQty, CancelledQty, Status " +
        "FROM so.SalesOrderLine WHERE CompanyCode = @CompanyCode AND OrderNumber = @OrderNumber " +
        "ORDER BY LineNum";

    /// <summary>SalesOrderService.PickLineSql (SalesOrderService.vb:62-68).</summary>
    public const string PickLineSql =
        "SELECT l.LineNum, l.ItemCode, l.RequiredQty, l.PickedQty, l.ShortQty, l.WarehouseCode, " +
        "l.LocationCode, l.LotCode, l.SerialNo, l.ScheduleNo, l.Status, h.OrderNumber, h.CustomerCode, " +
        "o.LineNum AS OrderLineNum " +
        "FROM so.PickListLine l JOIN so.PickList h ON h.PickListId = l.PickListId " +
        "LEFT JOIN so.SalesOrderLine o ON o.SalesOrderLineId = l.SalesOrderLineId " +
        "WHERE h.CompanyCode = @CompanyCode AND h.PickListRef = @PickListRef ORDER BY l.LineNum";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("so_listSalesOrders", ListSalesOrdersAsync);
        ops.Handle("so_getSalesOrder", GetSalesOrderAsync);
        ops.Handle("so_listDespatchablePickLists", ListDespatchablePickListsAsync);
        ops.Handle("so_getPickListLines", GetPickListLinesAsync);
    }

    /// <summary>so_listSalesOrders: frmLookup.Pick(OrderPickSql) with the search text (empty = NULL = all orders).</summary>
    public static async Task<IResult> ListSalesOrdersAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(OrderPickSql,
            new { CompanyCode = ctx.Session.CompanyCode, Search = ctx.Query("search") }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>
    /// so_getSalesOrder: SalesOrderService.Header then Lines (two autocommit queries); no header row = the screen text
    /// "Order &lt;n&gt; is not on file in &lt;company&gt;." (frmSalesOrderEntry.vb:61-66).
    /// </summary>
    public static async Task<IResult> GetSalesOrderAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var order = ctx.Route("orderNumber");
        var p = new { CompanyCode = ctx.Session.CompanyCode, OrderNumber = order };
        var header = await sql.QueryJsonAsync(HeaderSql, p, ctx.Aborted);
        if (header.Count == 0)
        {
            return ProblemResults.Create(404, ProblemKinds.NotFound, $"Order {order} is not on file in {ctx.Session.CompanyCode}.",
                ctx.Session, null, null, "NOT FOUND", 1);
        }
        var lines = await sql.QueryJsonAsync(LineSql, p, ctx.Aborted);
        var shaped = new JsonArray();
        foreach (var l in lines)
        {
            // OpenAPI SoSalesOrderLine names the CostSnapshotGBP column costSnapshotGbp
            var o = new JsonObject();
            foreach (var (k, v) in l!.AsObject()) o[k == "costSnapshotGBP" ? "costSnapshotGbp" : k] = v?.DeepClone();
            shaped.Add(o);
        }
        return ctx.Direct(new JsonObject { ["header"] = header[0]!.DeepClone(), ["lines"] = shaped });
    }

    /// <summary>
    /// so_listDespatchablePickLists: PickListPickSql with the session company and site; orderNumber = @Argument
    /// (PicksForOrder), search = @Search (F2).
    /// </summary>
    public static async Task<IResult> ListDespatchablePickListsAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(PickListPickSql, new
        {
            CompanyCode = ctx.Session.CompanyCode,
            SiteCode = ctx.Session.SiteCode,
            Search = ctx.Query("search"),
            Argument = ctx.Query("orderNumber"),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>so_getPickListLines: PickLineSql for the session company; an unknown pick list gives an empty list.</summary>
    public static async Task<IResult> GetPickListLinesAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(PickLineSql,
            new { CompanyCode = ctx.Session.CompanyCode, PickListRef = ctx.Route("pickListRef") }, ctx.Aborted);
        return ctx.List(rows);
    }
}
