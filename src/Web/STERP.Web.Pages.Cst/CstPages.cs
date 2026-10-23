using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Cst.Pages;

namespace Sterp.Web.Pages.Cst;

/// <summary>
/// The STERP-CST pages (STERP-CST.md 7.2; STERP-WEB 7.2 rows of slice CST): the four Costing windows of the legacy client.
/// They open no dialog or child form of their own - only the MessageBox confirmations and outcome dialogs of BaseForm.
/// </summary>
public sealed class CstPages : IPageModule
{
    public string Slice => "STERP-CST";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<CostEnquiryPage>("frmCostEnquiry")
             .Add<CostSetPage>("frmCostSet")
             .Add<CostRollupPage>("frmCostRollup")
             .Add<JobCostPage>("frmJobCost");
    }
}
