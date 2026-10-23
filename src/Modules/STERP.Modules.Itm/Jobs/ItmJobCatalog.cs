using Sterp.Platform.Hosting.Jobs;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>The six ITM batch programs (STERP-ITM.md section 7.4).</summary>
internal static class ItmJobCatalog
{
    public static IReadOnlyList<JobDefinition> All =>
    [
        StkstatJob.Definition,
        StkvalfJob.Definition,
        LottraceJob.Definition,
        CountadjJob.Definition,
        PkgrptJob.Definition,
        OpenstkJob.Definition,
    ];
}
