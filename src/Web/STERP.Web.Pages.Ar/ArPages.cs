using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Ar.Pages;

namespace Sterp.Web.Pages.Ar;

/// <summary>
/// The four STERP-AR pages of STERP-AR.md section 7.3 (STERP-WEB section 7.2 rows of slice AR). The F2 account list is the
/// platform lookup dialog (named lookup AR.CUSTOMER) and the report buttons open the platform report launcher.
/// </summary>
public sealed class ArPages : IPageModule
{
    public string Slice => "STERP-AR";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<CustomerEnquiryPage>("frmCustomerEnquiry")
             .Add<ReceiptEntryPage>("frmReceiptEntry")
             .Add<CreditControlPage>("frmCreditControl")
             .Add<CreditOverridePage>("frmCreditOverride");
    }
}
