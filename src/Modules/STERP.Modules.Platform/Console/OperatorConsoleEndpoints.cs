using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sterp.Modules.Platform.Diagnostics;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Platform.Console;

/// <summary>
/// Operator console reads and acknowledgement (FR-STERP-PLATFORM-050..055; STERP-PLATFORM 4.13): the STERPOPS SQLIO
/// statements (STERPOPS.cob:552-1231) on the request's engine connection in autocommit, at most 300 rows each.
/// </summary>
public static class OperatorConsoleEndpoints
{
    public const int MaxRows = 300;

    public const string CatalogueSql =
        "SELECT ReportCode, Module, ProgramName, ISNULL(ParamSpec,'') AS ParamSpec, LEFT(Title,40) AS Title FROM core.ReportDef " +
        "WHERE IsActive = 1 ORDER BY CASE WHEN ReportCode LIKE 'JOB.%' THEN 0 ELSE 1 END, Module, ReportCode";

    private const string LastRunSql =
        "SELECT TOP 1 JobRunId, Status, ISNULL(SpoolFile,ISNULL(LogFile,'')) AS SpoolOrLog FROM core.JobRun WHERE JobName = @JobName " +
        "ORDER BY JobRunId DESC";

    private const string JobRunsSql =
        "SELECT TOP 300 JobRunId, JobName, ISNULL(CompanyCode,'') AS CompanyCode, CONVERT(varchar(19),StartedAt,120) AS StartedAt, Status, " +
        "ISNULL(CONVERT(varchar(8),ReturnCode),'') AS ReturnCode, ISNULL(SpoolFile,ISNULL(LogFile,'')) AS SpoolOrLog FROM core.JobRun " +
        "ORDER BY JobRunId DESC";

    private const string ExceptionsSql =
        "SELECT ExceptionId, CompanyCode, Module, Severity, LEFT(Category,24) AS Category, LEFT(Message,60) AS Message, Status, " +
        "ISNULL(CONVERT(varchar(8),AgeDays),'') AS AgeDays FROM core.ExceptionItem WHERE Status <> 'RESOLVED' ORDER BY Priority, ExceptionId";

    private const string PeriodsSql =
        "SELECT CompanyCode, FiscalYear, PeriodNo, Name, Status, CONVERT(char(10),StartDate,103) AS StartDate, " +
        "CONVERT(char(10),EndDate,103) AS EndDate, ISNULL(ClosedBy,'') AS ClosedBy FROM fin.Period " +
        "WHERE FiscalYear >= YEAR(GETDATE()) - 1 ORDER BY CompanyCode, FiscalYear, PeriodNo";

    public const string AcknowledgeNote = "ACKNOWLEDGED AT THE OPERATOR CONSOLE";

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("platform_listConsoleCatalogue", ListCatalogueAsync);
        ops.Handle("platform_submitConsoleJob", SubmitJobAsync);
        ops.Handle("platform_getConsoleLastRun", GetLastRunAsync);
        ops.Handle("platform_listConsoleJobRuns", ListJobRunsAsync);
        ops.Handle("platform_listConsoleExceptions", ListExceptionsAsync);
        ops.Handle("platform_acknowledgeConsoleException", AcknowledgeAsync);
        ops.Handle("platform_listConsolePeriods", ListPeriodsAsync);
        ops.Handle("platform_runConsoleSelfTest", SelfTestAsync, answersWithoutDatabase: true);
    }

    /// <summary>
    /// platform_submitConsoleJob (FR-STERP-PLATFORM-051; STERPOPS 3200/3300/3350): the row of the console catalogue (active
    /// core.ReportDef rows), then the job framework's console entry point - no catalogue permission and no read-only
    /// restriction (SME-1172, SME-1173); the 202 JobRunId is the job's own registration.
    /// </summary>
    private static async Task<IResult> SubmitJobAsync(OperationContext ctx)
    {
        var code = ctx.Route("reportCode");
        var body = await ctx.BodyObjectAsync();
        var parameters = JobEndpoints.ParseParameters(body);
        var startText = body["sessionStartDate"] is JsonValue sv && sv.TryGetValue<string>(out var st) ? st : null;
        if (!DateOnly.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            throw ApiProblemException.BadBody("sessionStartDate is required (yyyy-MM-dd).");
        }
        var rows = await QueryAsync(ctx.Get<ISqlRunner>(), CatalogueSql, ctx.Aborted);
        var row = rows.OfType<JsonObject>().FirstOrDefault(r => string.Equals(Text(r, "reportCode").Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (row is null) return ctx.NotFound("API0404", $"Job {code} is not in the operator console catalogue.", null);
        var accepted = await ctx.Get<JobService>().SubmitFromConsoleAsync(Text(row, "reportCode"), Text(row, "programName"), Text(row, "module"),
            parameters, start, ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    private static string Text(JsonObject r, string name) => r[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // ------------------------------------------------------------------ queries shared with the self-test

    public static async Task<JsonArray> QueryAsync(ISqlRunner sql, string statement, CancellationToken ct)
    {
        var rows = await sql.QueryJsonAsync(statement, null, ct);
        while (rows.Count > MaxRows) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    /// <summary>STERPOPS 3100-LOAD-JOBS with the 3200 parameter panel of each row.</summary>
    public static async Task<JsonArray> CatalogueAsync(ISqlRunner sql, string company, string user, string site, string todayYyyymmdd, CancellationToken ct)
    {
        var rows = await QueryAsync(sql, CatalogueSql, ct);
        foreach (var r in rows.OfType<JsonObject>())
        {
            r["parameters"] = Panel(r["paramSpec"]?.GetValue<string>() ?? "", company, user, site, todayYyyymmdd);
        }
        return rows;
    }

    /// <summary>
    /// STERPOPS 3200-PROMPT-PARMS (STERPOPS.cob:619-662): ParamSpec UNSTRING at ',' into at most 8 entries of 14 characters,
    /// each trimmed, upper-cased and split at '=' into key (14) and fixed default (24); CO and USER are always the session
    /// values; SITE, ASAT (session-start date yyyymmdd) and MODE (PREVIEW) only when no default was given.
    /// </summary>
    public static JsonArray Panel(string paramSpec, string company, string user, string site, string todayYyyymmdd)
    {
        var entries = paramSpec.Split(',');
        var count = Math.Min(entries.Length, 8);
        var panel = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var text = CobolText.Trim(CobolText.Fit(entries[i], 14)).ToUpperInvariant();
            var parts = text.Split('=');
            var key = CobolText.Trimmed(parts[0], 14);
            var value = parts.Length > 1 ? CobolText.Trimmed(parts[1], 24) : "";
            switch (key)
            {
                case "CO": value = CobolText.Trimmed(company, 10); break;
                case "USER": value = CobolText.Trimmed(user, 20); break;
                case "SITE": if (value.Length == 0) value = CobolText.Trimmed(site, 10); break;
                case "ASAT": if (value.Length == 0) value = todayYyyymmdd; break;
                case "MODE": if (value.Length == 0) value = "PREVIEW"; break;
            }
            panel.Add(new JsonObject { ["key"] = key, ["value"] = value.Length == 0 ? null : value });
        }
        return panel;
    }

    // ------------------------------------------------------------------ operations

    private static async Task<IResult> ListCatalogueAsync(OperationContext ctx)
    {
        var start = ctx.QueryDate("sessionStartDate") ?? throw ApiProblemException.BadBody("sessionStartDate is required (yyyy-MM-dd).");
        var s = ctx.Session;
        var rows = await CatalogueAsync(ctx.Get<ISqlRunner>(), s.CompanyCode, s.UserCode, s.SiteCode, start.ToString("yyyyMMdd", CultureInfo.InvariantCulture), ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>
    /// STERPOPS 3400-LAST-RUN: the newest core.JobRun row of the program name (any user, company or time); status cut to 12
    /// and path to 108 characters; no row or a failed read gives UNKNOWN and ''.
    /// </summary>
    private static async Task<IResult> GetLastRunAsync(OperationContext ctx)
    {
        var program = CobolText.Trimmed(ctx.Query("programName") ?? throw ApiProblemException.BadBody("programName is required."), 8);
        var result = new JsonObject { ["programName"] = program, ["status"] = "UNKNOWN", ["spoolOrLog"] = "", ["jobRunId"] = null };
        try
        {
            var rows = await ctx.Get<ISqlRunner>().QueryJsonAsync(LastRunSql, new { JobName = program }, ctx.Aborted);
            if (rows.Count > 0 && rows[0] is JsonObject r)
            {
                result["status"] = CobolText.Trimmed(r["status"]?.GetValue<string>(), 12);
                result["spoolOrLog"] = CobolText.Trimmed(r["spoolOrLog"]?.GetValue<string>(), 108);
                result["jobRunId"] = r["jobRunId"]?.DeepClone();
            }
        }
        catch (SqlioException)
        {
            // the console kept UNKNOWN and an empty path when the read failed
        }
        return ctx.Direct(result);
    }

    private static async Task<IResult> ListJobRunsAsync(OperationContext ctx) =>
        ctx.List(await QueryAsync(ctx.Get<ISqlRunner>(), JobRunsSql, ctx.Aborted));

    private static async Task<IResult> ListExceptionsAsync(OperationContext ctx) =>
        ctx.List(await QueryAsync(ctx.Get<ISqlRunner>(), ExceptionsSql, ctx.Aborted));

    private static async Task<IResult> ListPeriodsAsync(OperationContext ctx) =>
        ctx.List(await QueryAsync(ctx.Get<ISqlRunner>(), PeriodsSql, ctx.Aborted));

    /// <summary>
    /// STERPOPS 5200-ACKNOWLEDGE: a read-only user is refused STERP-E-5002; otherwise EXEC core.usp_ResolveException ?, ?, ?, ?
    /// with the console user, the console note and status ACKNOWLEDGED (SQLIO autocommit), answered with STERP-I-5001.
    /// </summary>
    private static async Task<IResult> AcknowledgeAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        if (s.User.IsReadOnly)
        {
            return ctx.Forbidden("STERP-E-5002", "STERP-E-5002 READ ONLY USER MAY NOT ACKNOWLEDGE");
        }
        var id = ctx.Route("exceptionId").Trim();
        if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw ApiProblemException.BadBody("exceptionId must be an integer.");
        JsonArray rows;
        try
        {
            rows = await ctx.Get<ISqlRunner>().QueryJsonAsync("EXEC core.usp_ResolveException @p1, @p2, @p3, @p4",
                new { p1 = id, p2 = s.UserCode, p3 = AcknowledgeNote, p4 = "ACKNOWLEDGED" }, ctx.Aborted);
        }
        catch (SqlioException ex) when (BusinessRuleException.MatchText(ex.ServerMessage) is { Success: true } m)
        {
            return ctx.BusinessRule(m.Groups["s"].Value + "-" + m.Groups["c"].Value, m.Groups["t"].Value.Trim(), null);
        }
        var row = rows.Count > 0 ? rows[0] as JsonObject : null;
        var result = row ?? new JsonObject { ["exceptionId"] = long.Parse(id, CultureInfo.InvariantCulture), ["status"] = "ACKNOWLEDGED" };
        result["consoleMessage"] = "STERP-I-5001 EXCEPTION " + id + " ACKNOWLEDGED BY " + s.UserCode.Trim();
        return ctx.Direct(result);
    }

    // ------------------------------------------------------------------ SMOKE

    private sealed class SignOn
    {
        public string User { get; set; } = "";
        public string Name { get; set; } = "";
        public string Company { get; set; } = "";
        public string Site { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// platform_runConsoleSelfTest - STERPOPS 8000-SMOKE (STERPOPS.cob:1134-1231): sign on the requested user (default OPS)
    /// with the requested company, falling back to the first active user after STERP-W-8001; the first 12 catalogue, 10
    /// monitor, 10 exception and 12 period rows with their counts and the PING health; returnCode 0, or 8 when no user, a
    /// list query failure (STERP-E-9009) or an unhealthy probe. Nothing is submitted or acknowledged.
    /// </summary>
    private static async Task<IResult> SelfTestAsync(OperationContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var now = DateTime.Now;
        var messages = new JsonArray();
        var rc = 0;
        // 0080-CONNECT: one connection at start; without it the sign-on and the four panels read nothing (WS-DB-OK guards)
        var dbOk = true;
        try
        {
            await sql.UnitOfWork.ConnectionAsync(ctx.Aborted);
        }
        catch (SqlException ex)
        {
            dbOk = false;
            messages.Add("STERP-E-9001 DATABASE UNAVAILABLE - " + CobolText.Trimmed(sql.DriverText(ex), 40));
            rc = 12;
        }
        messages.Add("STERP-I-8000 STERPOPS SMOKE TEST " + now.ToString("dd/MM/yyyy  HH:mm", CultureInfo.InvariantCulture));
        var requested = CobolText.Trimmed((ctx.Query("userCode") ?? "").ToUpperInvariant(), 20);
        var company = CobolText.Trimmed((ctx.Query("companyCode") ?? "").ToUpperInvariant(), 10);
        var signOn = new SignOn { Company = company };
        await CheckUserAsync(sql, requested.Length == 0 ? "OPS" : requested, company, signOn, dbOk, ctx.Aborted);
        if (signOn.Name.Length == 0)
        {
            messages.Add("STERP-W-8001 " + signOn.Message.Trim());
            try
            {
                // 8100-ANY-USER has no WS-DB-OK guard: it reads even when the start-up connection failed
                var any = await sql.ScalarAsync<string>("SELECT TOP 1 UserCode FROM core.AppUser WHERE IsActive = 1 ORDER BY UserCode", null, ctx.Aborted);
                if (any is not null) await CheckUserAsync(sql, CobolText.Trimmed(any, 20), company, signOn, dbOk, ctx.Aborted);
            }
            catch (SqlioException)
            {
                // 8100-ANY-USER leaves the console without a user when the read fails
            }
        }
        if (signOn.Name.Length == 0) rc = 8;
        else messages.Add(signOn.Message);

        var today = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        async Task<JsonArray> Load(Func<Task<JsonArray>> read)
        {
            if (!dbOk) return []; // 3100/4100/5100/6100: no database connection, nothing read, "(no rows)"
            try
            {
                return await read();
            }
            catch (SqlioException ex)
            {
                messages.Add(Sql9009(ex));
                rc = 8;
                return [];
            }
        }
        var catalogue = await Load(() => CatalogueAsync(sql, signOn.Company, signOn.User, signOn.Site, today, ctx.Aborted));
        var runs = await Load(() => QueryAsync(sql, JobRunsSql, ctx.Aborted));
        var exceptions = await Load(() => QueryAsync(sql, ExceptionsSql, ctx.Aborted));
        var periods = await Load(() => QueryAsync(sql, PeriodsSql, ctx.Aborted));
        var probe = await DatabaseProbe.RunAsync(sql, ctx.Get<IOptions<SterpDatabaseOptions>>(), ctx.Aborted);
        messages.Add(DiagnosticsEndpoints.HealthText(probe));
        if (!probe.IsOk) rc = 8;
        messages.Add("STERP-I-8009 SMOKE TEST COMPLETE RC=" + rc.ToString(CultureInfo.InvariantCulture));
        return ctx.Direct(new JsonObject
        {
            ["returnCode"] = rc,
            ["userCode"] = signOn.Name.Length == 0 ? null : signOn.User,
            ["companyCode"] = signOn.Company.Length == 0 ? null : signOn.Company,
            ["messages"] = messages,
            ["catalogue"] = First(catalogue, 12),
            ["catalogueCount"] = catalogue.Count,
            ["jobRuns"] = First(runs, 10),
            ["jobRunCount"] = runs.Count,
            ["exceptions"] = First(exceptions, 10),
            ["exceptionCount"] = exceptions.Count,
            ["periods"] = First(periods, 12),
            ["periodCount"] = periods.Count,
            ["health"] = probe.ToJson(),
        });
    }

    /// <summary>STERPOPS 1100-CHECK-USER: the user must exist and be active; company = the typed one, else the user's default.</summary>
    private static async Task CheckUserAsync(ISqlRunner sql, string user, string company, SignOn signOn, bool dbOk, CancellationToken ct)
    {
        signOn.Name = "";
        if (!dbOk)
        {
            signOn.Message = "STERP-E-1009 NO DATABASE - SIGN ON IMPOSSIBLE";
            return;
        }
        try
        {
            var rows = await sql.QueryJsonAsync(
                "SELECT DisplayName, ISNULL(DefaultCompanyCode,'') AS DefaultCompanyCode, ISNULL(DefaultSiteCode,'') AS DefaultSiteCode, " +
                "CONVERT(char(1),IsReadOnly) AS IsReadOnly FROM core.AppUser WHERE UserCode = @UserCode AND IsActive = 1",
                new { UserCode = user }, ct);
            if (rows.Count == 0 || rows[0] is not JsonObject r)
            {
                signOn.Message = "STERP-E-1002 UNKNOWN OR INACTIVE USER CODE";
                return;
            }
            signOn.User = user;
            signOn.Name = CobolText.Trimmed(r["displayName"]?.GetValue<string>(), 40);
            signOn.Company = company.Length > 0 ? company : CobolText.Trimmed(r["defaultCompanyCode"]?.GetValue<string>(), 10);
            signOn.Site = CobolText.Trimmed(r["defaultSiteCode"]?.GetValue<string>(), 10);
            signOn.Message = "STERP-I-1000 WELCOME " + signOn.Name.Trim();
        }
        catch (SqlioException ex)
        {
            signOn.Message = "STERP-E-1008 " + CobolText.Fit(ex.DriverText, 60);
        }
    }

    /// <summary>9600-SQL-ERROR: 'STERP-E-9009 SQL &lt;SQLSTATE&gt; &lt;first 50 characters of the message&gt;'.</summary>
    public static string Sql9009(SqlioException ex) => ("STERP-E-9009 SQL " + ex.SqlState + " " + CobolText.Fit(ex.DriverText, 50)).TrimEnd();

    private static JsonArray First(JsonArray rows, int n) => new(rows.Take(n).Select(r => r?.DeepClone()).ToArray());
}
