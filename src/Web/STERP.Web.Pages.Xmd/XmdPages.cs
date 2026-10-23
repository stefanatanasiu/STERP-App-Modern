using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Xmd.Pages;

namespace Sterp.Web.Pages.Xmd;

/// <summary>
/// The STERP-XMD page (STERP-WEB section 7.2; STERP-XMD.md section 7.2, FR-STERP-XMD-012): the business chain trace. The
/// other rows of the slice's menu branch open the PLATFORM report launcher (FR-STERP-XMD-002).
/// </summary>
public sealed class XmdPages : IPageModule
{
    public string Slice => "STERP-XMD";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<ChainTracePage>("frmChainTrace");
    }
}
