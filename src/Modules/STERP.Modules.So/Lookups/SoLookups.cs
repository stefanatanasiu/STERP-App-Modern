using Sterp.Modules.So.Application;
using Sterp.Platform.Hosting.PlatformContracts;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.So.Lookups;

/// <summary>
/// Named F2 lookup SO.ORDER (STERP-PLATFORM 4.4): the Sales Order Entry order picker, frmLookup.Pick(OrderPickSql)
/// (frmSalesOrderEntry.vb:244-254; SalesOrderService.vb:23-27).
/// </summary>
public sealed class OrderLookup(IProcedureRunner sql) : ILookupSource
{
    public string LookupCode => "SO.ORDER";

    public string Slice => "STERP-SO";

    public Task<DynamicRows> FindAsync(LookupRequest request, CancellationToken ct) =>
        LookupQuery.RunAsync(sql, SalesOrderQueries.OrderPickSql, request, ct);
}

/// <summary>
/// Named F2 lookup SO.PICKLIST (STERP-PLATFORM 4.4): the Despatch Confirmation pick list picker, frmLookup.Pick(PickListPickSql)
/// with the session company and site, the search text and the order number as argument (frmDespatchConfirm.vb:99-110, 227-232;
/// SalesOrderService.vb:35-45).
/// </summary>
public sealed class PickListLookup(IProcedureRunner sql) : ILookupSource
{
    public string LookupCode => "SO.PICKLIST";

    public string Slice => "STERP-SO";

    public Task<DynamicRows> FindAsync(LookupRequest request, CancellationToken ct) =>
        LookupQuery.RunAsync(sql, SalesOrderQueries.PickListPickSql, request, ct);
}
