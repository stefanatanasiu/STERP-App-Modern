using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Kit.Pages;

namespace Sterp.Web.Pages.Kit;

/// <summary>The four STERP-KIT pages of STERP-KIT.md section 7.2 (the KIT forms open no child forms of their own).</summary>
public sealed class KitPages : IPageModule
{
    public string Slice => "STERP-KIT";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<KitMaintPage>("frmKitMaint")
             .Add<KitExecutionPage>("frmKitExecution")
             .Add<KitTrialPage>("frmKitTrial")
             .Add<KitGenealogyPage>("frmKitGenealogy");
    }
}
