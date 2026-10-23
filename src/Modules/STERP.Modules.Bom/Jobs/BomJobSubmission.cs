using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Bom.Data;
using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Bom.Jobs;

/// <summary>
/// The BOM job submissions (bom_submitBomrp010Run, bom_submitBomrp030Run; spec 7.1, 7.3): the legacy chain is the report
/// launcher (frmReportLauncher -> ReportService.Run), so the catalogue row named by <c>reportCode</c> must be in the
/// caller's report catalogue (its PermissionCode, core.usp_UiReportList), the run is registered as ReportService.Run did
/// (core.usp_JobRunStart with the program name, company and user; ended with the launcher status, return code, log and the
/// spool of the last STERP-I-SPOOL line) and by the program's own JOBLOG START, whose END is refused because the area still
/// carries the START status RUNNING (JOBLOG JOB0003), so that row stays RUNNING (SME-009, SME-010).
/// </summary>
internal static class BomJobSubmission
{
    public static void Map(OperationRegistry ops, JobDefinition d, string defaultReport, IReadOnlyList<string> reports)
    {
        ops.Handle(d.SubmitOperationId!, ctx => SubmitAsync(ctx, d, defaultReport, reports));
    }

    private static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d, string defaultReport, IReadOnlyList<string> reports)
    {
        var body = await ctx.BodyObjectAsync();
        var key = body.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "reportCode", StringComparison.OrdinalIgnoreCase));
        var reportCode = defaultReport;
        if (key is not null)
        {
            reportCode = body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
            body.Remove(key);
        }
        if (!reports.Contains(reportCode, StringComparer.OrdinalIgnoreCase))
        {
            throw ApiProblemException.BadBody($"reportCode must be one of {string.Join(", ", reports)}.");
        }
        if (!await InReportCatalogueAsync(ctx, reportCode))
        {
            return ctx.Forbidden(JobService.NotInCatalogue, $"Report {reportCode.ToUpperInvariant()} is not in the report catalogue of {ctx.Session.UserCode}.");
        }
        var cmd = JobEndpoints.BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        // ReportService.JobStart(programName): @JobName, @CompanyCode, @SubmittedBy (ReportService.vb:195-199)
        var launcher = new JobRegistration(d.Program, JobService.Blank(ctx.Session.CompanyCode), ctx.Session.UserCode, null, null);
        var jobs = ctx.Get<JobService>();
        var accepted = await jobs.SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body, reportCode.ToUpperInvariant(), launcher),
            ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>The caller's report catalogue (core.usp_UiReportList, every module) holds the report code.</summary>
    private static async Task<bool> InReportCatalogueAsync(OperationContext ctx, string reportCode)
    {
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync("core.usp_UiReportList", SqlHelperArgs.Of(
            ("@UserCode", ctx.Session.UserCode),
            ("@CompanyCode", ctx.Session.CompanyCode),
            ("@Module", null)), ctx.Aborted);
        return rows.OfType<JsonObject>().Any(r =>
            r["reportCode"] is JsonValue v && v.TryGetValue<string>(out var code) &&
            string.Equals(code.Trim(), reportCode, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 0200-JOB-START: JOBLOG START with CA-COMPANY = CO (default STC), CA-USER = USER (default ENGINEER), no site and the
    /// command line as JL-ARGUMENTS X(200) (JOBLOG.cob:124-176: blank values are NULL).
    /// </summary>
    public static JobRegistration JoblogStart(JobPreCheckContext pre, string jobName)
    {
        var co = Pic.Blank(pre.Args.Co) ? "STC" : pre.Args.Co;
        var user = Pic.Blank(pre.Args.User) ? "ENGINEER" : pre.Args.User;
        var args = CobolText.Fit(pre.Args.Text, 200).TrimEnd(' ');
        return new JobRegistration(jobName, co, user, null, args.Length == 0 ? null : args);
    }

    /// <summary>0500-JOB-END: JL-STATUS still holds RUNNING from START, so JOBLOG refuses the END (JOB0003).</summary>
    public static JobEnd NeverEnded(JobRunContext ctx, int exit) => new(null, exit);
}

/// <summary>Batch helpers shared by BOMRP010 and BOMRP030.</summary>
internal static class BomBatch
{
    /// <summary>DISPLAY of a PIC S9(n) DISPLAY item: the digits zero padded and a trailing sign.</summary>
    public static string Disp(long value, int digits)
    {
        var v = Math.Abs(value) % (long)Math.Pow(10, digits);
        return v.ToString(new string('0', digits), CultureInfo.InvariantCulture) + (value < 0 ? "-" : "+");
    }

    /// <summary>A PIC X(n) item DISPLAYed as stored (trailing spaces kept).</summary>
    public static string Field(string? value, int n) => CobolText.Fit(value, n);

    /// <summary>FUNCTION NUMVAL of a CMDARGS value (0 when not numeric).</summary>
    public static decimal NumVal(string? text) => BomSql.NumVal(text);

    /// <summary>FUNCTION CURRENT-DATE(1:8) as a number.</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    /// <summary>MOVE of a NUMVAL to PIC 9(8).</summary>
    public static int Date8(decimal value) => (int)(Math.Abs(decimal.Truncate(value)) % 100_000_000m);

    /// <summary>"dd/mm/yyyy" of Ws-Date-R (the REDEFINES of a PIC 9(8) date).</summary>
    public static string Dmy(int yyyymmdd)
    {
        var d = yyyymmdd.ToString("D8", CultureInfo.InvariantCulture);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    /// <summary>0350-FILL-HEADINGS / 0360-READ-COMPANY: the STRPTHDR heading with name and registered number from core.Company.</summary>
    public static async Task<ReportHeading> HeadingAsync(ISqlRunner sql, string co, string reportId, string title, string subtitle,
        string asAtText, string user, CancellationToken ct)
    {
        var h = new ReportHeading
        {
            CompanyCode = co,
            ReportId = reportId,
            Title = title,
            Subtitle = subtitle,
            AsAtText = asAtText,
            User = user,
            RunAt = DateTime.Now,
        };
        try
        {
            var row = await sql.WithReaderAsync(BomSql.Q("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?"),
                BomSql.Args(Pic.X(co, 10)), async r => await r.ReadAsync(ct) ? new[] { BomSql.Text(r, 0, 100), BomSql.Text(r, 1, 100) } : null, ct);
            if (row is not null)
            {
                h.CompanyName = row[0];
                h.RegisteredNumber = row[1];
                return h;
            }
        }
        catch (SqlioException)
        {
            // SNGL failure: the company code is printed instead
        }
        h.CompanyName = co;
        return h;
    }

    /// <summary>RUNPATH BUILD (SPOOL, report id, company, time stamp) then OPEN OUTPUT; a null writer when the file cannot be opened.</summary>
    public static LegacyResult OpenSpool(JobRunContext ctx, string reportId, string co, out SpoolWriter? spool, out string path)
    {
        spool = null;
        var r = ctx.Paths.Build("SPOOL", reportId, co, null, null, true, out path);
        if (!r.IsOk) return r;
        try
        {
            spool = new SpoolWriter(path);
            ctx.SpoolFiles.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            spool = null;
        }
        return r;
    }

    /// <summary>A numeric-edited report field.</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Field(value, picture);
}
