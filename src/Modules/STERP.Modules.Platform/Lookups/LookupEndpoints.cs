using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Hosting.PlatformContracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Lookups;

/// <summary>
/// F2 lookups (FR-STERP-PLATFORM-035; STERP-PLATFORM 4.4): frmLookup.Search -> LookupService.Find (MaintService.vb:80-84) with
/// @CompanyCode, @SiteCode, @Search (trimmed Find text) and @Argument; no SQL text or procedure name comes from the client.
/// </summary>
public static class LookupEndpoints
{
    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_lookupMasterFile", MasterFileAsync);
        ops.Handle("platform_lookupNamed", NamedAsync);
    }

    /// <summary>core.usp_UiLookup with @Argument = the MaintCode (-> core.usp_UiMaintList @ForLookup = 1, @Top = 200).</summary>
    private static async Task<IResult> MasterFileAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var request = new LookupRequest(s.CompanyCode, s.SiteCode, ctx.Query("search")?.Trim(), ctx.Route("maintCode"), s);
        var rows = await LookupQuery.RunAsync(ctx.Get<IProcedureRunner>(), "core.usp_UiLookup", request, ctx.Aborted);
        return ctx.Direct(rows.ToJson());
    }

    /// <summary>One of the four named lookups through the ILookupSource of its owning slice (501 API0501 while undelivered).</summary>
    private static async Task<IResult> NamedAsync(OperationContext ctx)
    {
        var code = ctx.Route("lookupCode").Trim();
        if (!NamedLookups.Owners.TryGetValue(code, out var owner))
        {
            return ctx.NotFound("API0404", $"{code} is not a named lookup.", null);
        }
        var source = ctx.Services.GetServices<ILookupSource>().FirstOrDefault(x => string.Equals(x.LookupCode, code, StringComparison.Ordinal));
        var slice = source?.Slice ?? owner;
        if (source is null || !ctx.Get<JobService>().IsDelivered(slice))
        {
            var wave = ContractCatalog.Waves.GetValueOrDefault(slice, "");
            return ProblemResults.Create(501, ProblemKinds.NotImplemented,
                $"Lookup {code} belongs to slice {slice} (wave {wave}), which is not yet delivered in the modernized application - use the legacy client.",
                ctx.Session, ApiReasons.NotDelivered);
        }
        var s = ctx.Session;
        var rows = await source.FindAsync(new LookupRequest(s.CompanyCode, s.SiteCode, ctx.Query("search")?.Trim(), ctx.Query("argument"), s), ctx.Aborted);
        return ctx.Direct(rows.ToJson());
    }
}
