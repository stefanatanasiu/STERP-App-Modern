using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Ap.Pages;

namespace Sterp.Web.Pages.Ap;

/// <summary>The three STERP-AP pages of STERP-WEB section 7.2 (STERP-AP.md section 7.2, FR-STERP-AP-018, -024, -038).</summary>
public sealed class ApPages : IPageModule
{
    public string Slice => "STERP-AP";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<SupplierVerifyPage>("frmSupplierVerify")
             .Add<InvoiceRegisterPage>("frmInvoiceRegister")
             .Add<PaymentRunPage>("frmPaymentRun");
    }
}
