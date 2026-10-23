using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Po.Pages;

namespace Sterp.Web.Pages.Po;

/// <summary>
/// The three STERP-PO pages of STERP-PO.md section 7.2 (STERP-WEB section 7.2 rows of slice PO). The Item lookups are the
/// platform master-file lookup ITM.ITEM, the goods-receipt Order lookup is the named lookup PO.OPENORDERS; the Purchasing
/// maintenance and report rows open the PLATFORM generic maintenance and report launcher pages.
/// </summary>
public sealed class PoPages : IPageModule
{
    public string Slice => "STERP-PO";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<RequisitionPage>("frmRequisition")
             .Add<PurchaseOrderPage>("frmPurchaseOrder")
             .Add<GoodsReceiptPage>("frmGoodsReceipt");
    }
}
