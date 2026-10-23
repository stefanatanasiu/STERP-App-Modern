using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Platform.Console;
using Sterp.Web.Pages.Platform.Pages;

namespace Sterp.Web.Pages.Platform;

/// <summary>The twelve PLATFORM pages of STERP-PLATFORM.md section 7.2 (the sign-on and lookup pages are dialogs).</summary>
public sealed class PlatformPages : IPageModule
{
    public string Slice => "STERP-PLATFORM";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<ApprovalsPage>("frmApprovals")
             .Add<ExceptionsPage>("frmExceptions")
             .Add<AuditPage>("frmAudit")
             .Add<MaintLauncherPage>("frmMaintLauncher")
             .Add<GenericMaintPage>("frmGenericMaint")
             .Add<ReportLauncherPage>("frmReportLauncher")
             .Add<SpoolViewerPage>("frmSpoolViewer")
             .Add<JobMonitorPage>("frmJobMonitor")
             .Add<ServiceDiagnosticsPage>("frmEngineDiag")
             .Add<OperatorConsolePage>(GuiRoutes.OperatorConsole)
             .Add<SignOnDialog>("frmLogin")
             .Add(GuiRoutes.Find("frmLookup")!.FormClass, typeof(Sterp.Web.Framework.Dialogs.LookupDialog));
    }
}
