using Sterp.Platform.Hosting;

namespace Sterp.Modules.Composition;

/// <summary>The slice modules composed into both hosts, in wave order (contracts section 3).</summary>
public static class SterpModules
{
    public static ISterpModule[] All() =>
    [
        new Sterp.Modules.Platform.PlatformModule(),
        new Sterp.Modules.Itm.ItmModule(),
        new Sterp.Modules.Ar.ArModule(),
        new Sterp.Modules.So.SoModule(),
        new Sterp.Modules.Xmd.XmdModule(),
        new Sterp.Modules.Fin.FinModule(),
        new Sterp.Modules.Po.PoModule(),
        new Sterp.Modules.Ap.ApModule(),
        new Sterp.Modules.Bom.BomModule(),
        new Sterp.Modules.Kit.KitModule(),
        new Sterp.Modules.Cst.CstModule(),
    ];
}
