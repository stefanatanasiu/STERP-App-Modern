using Sterp.Platform.Hosting.PlatformContracts;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Lookups;

/// <summary>
/// Named F2 lookup PO.OPENORDERS (STERP-PLATFORM 4.4): the goods-receipt Order lookup (frmGoodsReceipt.vb:101-108) -
/// frmLookup.Pick(Me, "po.usp_OpenOrders", company, "Open order") -> LookupService.Find (MaintService.vb:80-84), the procedure
/// executed with @CompanyCode, @SiteCode, @Search and @Argument (= the company code the form passes). po.usp_OpenOrders
/// declares neither @Search nor @Argument, so the call fails with the SQL Server parameter error exactly as in legacy
/// (BC-PO-03 rejected, D1/HD-11: the legacy behaviour stays the specification).
/// </summary>
public sealed class OpenOrdersLookup(IProcedureRunner sql) : ILookupSource
{
    public string LookupCode => "PO.OPENORDERS";

    public string Slice => "STERP-PO";

    public Task<DynamicRows> FindAsync(LookupRequest request, CancellationToken ct) => LookupQuery.RunAsync(sql, "po.usp_OpenOrders", request, ct);
}
