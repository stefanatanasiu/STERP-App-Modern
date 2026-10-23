using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Bom.Pages;

namespace Sterp.Web.Pages.Bom;

/// <summary>
/// The STERP-BOM pages (STERP-WEB section 7.2; STERP-BOM.md section 7.2, FR-STERP-BOM-020..023): bill maintenance,
/// explosion enquiry (also revision comparison with the argument COMPARE), where-used enquiry and bulk change preview.
/// </summary>
public sealed class BomPages : IPageModule
{
    public string Slice => "STERP-BOM";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<BomEditorPage>("frmBomEditor")
             .Add<BomExplosionPage>("frmBomExplosion")
             .Add<BomWhereUsedPage>("frmBomWhereUsed")
             .Add<BomBulkPreviewPage>("frmBomBulkPreview");
    }
}
