using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Itm.Pages;

namespace Sterp.Web.Pages.Itm;

/// <summary>The four STERP-ITM pages of STERP-WEB section 7.2 (STERP-ITM.md section 3.5, FR-STERP-ITM-040..043).</summary>
public sealed class ItmPages : IPageModule
{
    public string Slice => "STERP-ITM";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<ItemMasterPage>("frmItemMaster")
             .Add<StockEnquiryPage>("frmStockEnquiry")
             .Add<StockMovementPage>("frmStockMovement")
             .Add<StockCountPage>("frmStockCount");
    }
}
