using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Menu;

/// <summary>Menu bar load (FR-STERP-PLATFORM-034, -002): MenuService.Load (MenuService.vb:11-18) -> core.usp_UiMenuLoad.</summary>
public static class MenuEndpoints
{
    public static void Map(OperationRegistry ops) => ops.Handle("platform_getMenu", GetMenuAsync, answersWithoutDatabase: true);

    /// <summary>The procedure's rows unchanged; a failure gives an empty list, as MenuService.Load did ('Menu load failed').</summary>
    private static async Task<IResult> GetMenuAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        if (!s.IsResolved)
        {
            // database unavailable before the user could be resolved: core.usp_UiMenuLoad cannot run either (FR-STERP-PLATFORM-034 AC3)
            return ctx.List([]);
        }
        JsonArray rows;
        try
        {
            rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiMenuLoad", new
            {
                UserCode = s.UserCode,
                CompanyCode = s.CompanyCode,
                SiteCode = s.SiteCode,
            }, ctx.Aborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Get<ILoggerFactory>().CreateLogger("Sterp.Platform.Menu").LogError(ex, "Menu load failed");
            rows = [];
        }
        return ctx.List(rows);
    }
}
