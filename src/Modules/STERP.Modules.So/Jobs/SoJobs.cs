using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.So.Jobs;

/// <summary>The SO batch programs (STERP-SO.md section 7.3) and their submissions (POST /api/so/v1/jobs/{jobCode}/runs).</summary>
internal static class SoJobs
{
    /// <summary>(definition, SITE defaults to X-STERP-Site) - SOINVRUN and SOACKDOC default the site; SOANAL and SOBACKLG read blank as all sites.</summary>
    public static IReadOnlyList<(JobDefinition Definition, bool SiteDefault)> Programs() =>
    [
        (SobacklgJob.Definition, false),
        (SoanalJob.Definition, false),
        (SoackdocJob.Definition, true),
        (SoinvrunJob.Definition, true),
    ];

    public static void Map(JobCatalog jobs)
    {
        foreach (var (d, _) in Programs()) jobs.Add(d);
    }

    public static void MapSubmissions(OperationRegistry operations)
    {
        foreach (var (d, siteDefault) in Programs())
        {
            var definition = d;
            var defaultSite = siteDefault;
            operations.Handle(d.SubmitOperationId!, ctx => SoBatch.SubmitAsync(ctx, definition, defaultSite));
        }
    }
}
