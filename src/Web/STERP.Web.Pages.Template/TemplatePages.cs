using Sterp.Web.Framework.Registry;

namespace Sterp.Web.Pages.Template;

/// <summary>
/// Empty example of a slice page library (docs/DEVELOPER-GUIDE.md section 10). Copy this project to
/// <c>src/Web/STERP.Web.Pages.&lt;Slice&gt;</c>, rename it, register each page for its legacy FormClass (a row of
/// contracts/gui-routes.json) and add the module to <c>STERP.Web.Client/PageModules.cs</c>.
/// </summary>
public sealed class TemplatePages : IPageModule
{
    /// <summary>Target application id of the slice; its pages open only while the API hosts report it delivered.</summary>
    public string Slice => "STERP-TEMPLATE";

    public void Register(PageRegistryBuilder pages)
    {
        // pages.Add<StockEnquiryPage>("frmStockEnquiry");
    }
}
