using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sterp.Modules.Platform.Session;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Reports;

/// <summary>One core.usp_UiReportList row (ReportCode, Module, Title, ProgramName, ParamSpec, PermissionCode).</summary>
public sealed record ReportDefinition(string ReportCode, string Module, string Title, string ProgramName, string? ParamSpec, string? PermissionCode)
{
    /// <summary>ReportService.IsJobStream (ReportService.vb:86-88): ReportCode trimmed starts 'JOB.' (case-insensitive).</summary>
    public bool IsJobStream => ReportCode.Trim().StartsWith("JOB.", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Report catalogue and parameter panel (FR-STERP-PLATFORM-043; STERP-PLATFORM 4.8): ReportService.Definitions
/// (ReportService.vb:29-45) -> core.usp_UiReportList and frmReportLauncher.BuildParameters (frmReportLauncher.vb:44-69).
/// </summary>
public static class ReportCatalogueEndpoints
{
    public const string NotInCatalogue = "API0020";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_listReportCatalogue", ListCatalogueAsync);
        ops.Handle("platform_getReportParameters", GetParametersAsync);
        ops.Handle("platform_launchCatalogueReport", LaunchAsync);
    }

    /// <summary>
    /// platform_launchCatalogueReport (FR-STERP-PLATFORM-044; frmReportLauncher.RunClicked -> ReportService.Run): the entry
    /// must be in the caller's catalogue (403 API0020, the only legacy gate); the job framework builds the command line,
    /// registers the launcher row and the program's or stream's own row and runs it on the job host (202).
    /// </summary>
    private static async Task<IResult> LaunchAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var code = ctx.Route("reportCode");
        var body = await ctx.BodyObjectAsync();
        var parameters = JobEndpoints.ParseParameters(body);
        var def = await FindInCatalogueAsync(ctx.Get<IProcedureRunner>(), s, code, ctx.Aborted);
        if (def is null) return ctx.Forbidden(NotInCatalogue, $"Report {code} is not in the report catalogue of {s.UserCode}.");
        var accepted = await ctx.Get<JobService>().LaunchFromCatalogueAsync(def.ReportCode, def.ProgramName, def.Module, parameters, s, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>core.usp_UiReportList(@UserCode, @CompanyCode, @Module) - active, permitted rows ordered by Module, Title.</summary>
    public static Task<JsonArray> ReportListAsync(IProcedureRunner sql, SterpContext s, string? module, CancellationToken ct) =>
        sql.QueryJsonAsync("core.usp_UiReportList", new Dictionary<string, object?>
        {
            ["@UserCode"] = s.UserCode,
            ["@CompanyCode"] = s.CompanyCode,
            ["@Module"] = module,
        }, ct);

    /// <summary>The caller's catalogue entry with this ReportCode (case-insensitive), or null (403 API0020 for the callers).</summary>
    public static async Task<ReportDefinition?> FindInCatalogueAsync(IProcedureRunner sql, SterpContext s, string reportCode, CancellationToken ct)
    {
        var all = await ReportListAsync(sql, s, null, ct);
        var hit = all.OfType<JsonObject>().FirstOrDefault(r =>
            string.Equals(Str(r, "reportCode").Trim(), reportCode.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : ToDefinition(hit);
    }

    public static ReportDefinition ToDefinition(JsonObject r) => new(
        Str(r, "reportCode"), Str(r, "module"), Str(r, "title"), Str(r, "programName"), StrOrNull(r, "paramSpec"), StrOrNull(r, "permissionCode"));

    /// <summary>
    /// ReportService.Definitions(argument): the full list; with an argument the single row whose ReportCode equals it
    /// (case-insensitive), else the list re-read with @Module = argument when it returns rows, else the full list.
    /// </summary>
    private static async Task<IResult> ListCatalogueAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var filter = (ctx.Query("argument") ?? "").Trim();
        var all = await ReportListAsync(sql, ctx.Session, null, ctx.Aborted);
        if (filter.Length == 0) return ctx.List(all);
        var hit = new JsonArray(all.OfType<JsonObject>()
            .Where(r => string.Equals(Str(r, "reportCode").Trim(), filter, StringComparison.OrdinalIgnoreCase))
            .Select(r => (JsonNode?)r.DeepClone()).ToArray());
        if (hit.Count > 0) return ctx.List(hit);
        var byModule = await ReportListAsync(sql, ctx.Session, filter, ctx.Aborted);
        return ctx.List(byModule.Count > 0 ? byModule : all);
    }

    /// <summary>
    /// frmReportLauncher.BuildParameters: ParamSpec split at commas, entries trimmed and upper-cased as keys (blank entries
    /// skipped; 'KEY=default' keeps the whole text as key), defaults CO, SITE, USER, ASAT/TO, FROM, PERIOD, MODE, then the
    /// caller's initial values (InitialParameters, case-insensitive) override.
    /// </summary>
    private static async Task<IResult> GetParametersAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var sql = ctx.Get<IProcedureRunner>();
        var code = ctx.Route("reportCode");
        var def = await FindInCatalogueAsync(sql, s, code, ctx.Aborted);
        if (def is null) return ctx.Forbidden(NotInCatalogue, $"Report {code} is not in the report catalogue of {s.UserCode}.");

        var initial = ParseInitial(ctx.Query("initial"));
        var log = ctx.Get<ILoggerFactory>().CreateLogger("Sterp.Platform.Reports");
        FiscalPeriod? period = null;
        var keys = (def.ParamSpec ?? "").Split(',').Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).ToList();
        if (keys.Contains("PERIOD")) period = await SessionContext.LoadPeriodAsync(sql, s.CompanyCode, s.BusinessDate, log, ctx.Aborted);
        var parameters = new JsonArray();
        foreach (var key in keys)
        {
            string? value = key switch
            {
                "CO" => s.CompanyCode,
                "SITE" => s.SiteCode,
                "USER" => s.UserCode,
                "ASAT" or "TO" => Yyyymmdd(s.BusinessDate),
                "FROM" => Yyyymmdd(new DateOnly(s.BusinessDate.Year, 1, 1)),
                "PERIOD" => (period?.FiscalYear ?? 0).ToString("0000", CultureInfo.InvariantCulture) +
                            (period?.PeriodNo ?? 0).ToString("00", CultureInfo.InvariantCulture),
                "MODE" => "PREVIEW",
                _ => null,
            };
            if (initial.TryGetValue(key, out var iv)) value = iv;
            parameters.Add(new JsonObject { ["key"] = key, ["value"] = string.IsNullOrEmpty(value) ? null : value });
        }
        return ctx.Direct(new JsonObject
        {
            ["reportCode"] = def.ReportCode,
            ["programName"] = def.ProgramName,
            ["isJobStream"] = def.IsJobStream,
            ["parameters"] = parameters,
            ["message"] = keys.Count == 0 ? "(this report takes no parameters)" : null,
        });
    }

    /// <summary>InitialParameters: KEY=VALUE pairs separated by '&amp;' (keys case-insensitive).</summary>
    private static Dictionary<string, string> ParseInitial(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (var part in text.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq].Trim()] = part[(eq + 1)..];
        }
        return map;
    }

    public static string Yyyymmdd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Str(JsonObject r, string name) => StrOrNull(r, name) ?? "";

    private static string? StrOrNull(JsonObject r, string name) => r[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
