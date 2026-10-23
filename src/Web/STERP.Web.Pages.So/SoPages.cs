using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.So.Pages;

namespace Sterp.Web.Pages.So;

/// <summary>
/// The three STERP-SO pages of STERP-SO.md section 7.2 (STERP-WEB section 7.2 rows of slice SO). The F2 lists are the
/// platform lookup dialog (named lookups SO.ORDER and SO.PICKLIST); the maintenance and report menu rows of the Sales menu
/// are routed to the PLATFORM generic maintenance and report launcher pages.
/// </summary>
public sealed class SoPages : IPageModule
{
    public string Slice => "STERP-SO";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<SalesOrderEntryPage>("frmSalesOrderEntry")
             .Add<DespatchConfirmPage>("frmDespatchConfirm")
             .Add<SalesInvoiceRunPage>("frmSalesInvoiceRun");
    }
}
