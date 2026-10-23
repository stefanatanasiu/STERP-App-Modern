using Sterp.Web.Framework.Registry;

namespace Sterp.Web.Client;

/// <summary>
/// The slice page libraries composed into the shell (STERP-WEB section 6), in wave order. A slice adds its library here
/// and as a ProjectReference of STERP.Web.Client.csproj (docs/DEVELOPER-GUIDE.md section 10).
/// </summary>
public static class PageModules
{
    public static IPageModule[] All() =>
    [
        new Sterp.Web.Pages.Platform.PlatformPages(),
        new Sterp.Web.Pages.Itm.ItmPages(),
        new Sterp.Web.Pages.Ar.ArPages(),
        new Sterp.Web.Pages.So.SoPages(),
        new Sterp.Web.Pages.Xmd.XmdPages(),
        new Sterp.Web.Pages.Fin.FinPages(),
        new Sterp.Web.Pages.Po.PoPages(),
        new Sterp.Web.Pages.Ap.ApPages(),
        new Sterp.Web.Pages.Bom.BomPages(),
        new Sterp.Web.Pages.Kit.KitPages(),
        new Sterp.Web.Pages.Cst.CstPages(),
    ];
}
