using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// The ITM job submissions (contracts 8.2-8.3; STERP-ITM.md section 3.4): the run is registered as ReportService.Run did
/// (core.usp_JobRunStart with the program name, company and user; ended with the launcher status, return code, log and
/// spool) and by the program's own JOBLOG START, whose END carries run id 0 and so never closes its row (SME-009, SME-010).
/// </summary>
internal static class ItmJobSubmission
{
    public static void Map(OperationRegistry ops, IEnumerable<JobDefinition> definitions)
    {
        foreach (var d in definitions)
        {
            var definition = d;
            ops.Handle(d.SubmitOperationId!, ctx => SubmitAsync(ctx, definition));
        }
    }

    public static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d)
    {
        var body = await ctx.BodyObjectAsync();
        var co = body.FirstOrDefault(p => string.Equals(p.Key, "CO", StringComparison.OrdinalIgnoreCase));
        var coText = co.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(coText))
        {
            // CO defaults to X-STERP-Company (contracts 8.2)
            if (co.Key is not null) body.Remove(co.Key);
            if (ctx.Session.CompanyCode.Length > 0) body["CO"] = ctx.Session.CompanyCode;
        }
        else if (!ctx.Session.User.HasCompany(coText))
        {
            // a supplied CO is checked against the caller's access list like the header (contracts 5.3)
            return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed, $"{ctx.Session.UserCode} has no access to {coText.ToUpperInvariant()}.");
        }
        var cmd = JobEndpoints.BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        var launcher = new JobRegistration(d.Program, JobService.Blank(ctx.Session.CompanyCode), ctx.Session.UserCode, null, null);
        var jobs = ctx.Get<JobService>();
        var accepted = await jobs.SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body, LauncherRow: launcher),
            ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>JOBLOG END with run id 0 updates nothing: the program's own row stays RUNNING (SME-009).</summary>
    public static JobEnd NeverEnded(JobRunContext ctx, int exit) => new(null, exit);

    /// <summary>The program's JOBLOG START (JOBLOG.cob:129-152): blank company, user, site or arguments are NULL.</summary>
    public static JobRegistration JoblogStart(string job, string co, string user, string site, string arguments) =>
        new(job, Blank(co), Blank(user), Blank(site), Blank(CobolText.Fit(arguments, 200)));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');
}

/// <summary>Batch program helpers shared by the ITM jobs.</summary>
internal static class Batch
{
    /// <summary>DISPLAY of a numeric DISPLAY item: the digits zero padded to the picture, a trailing sign when signed.</summary>
    public static string Disp(decimal value, int digits, bool signed = true)
    {
        var v = Math.Abs(decimal.Truncate(value));
        var limit = 1m;
        for (var i = 0; i < digits; i++) limit *= 10m;
        if (v >= limit) v %= limit;
        var text = v.ToString(new string('0', digits), CultureInfo.InvariantCulture);
        return signed ? text + (value < 0 && v != 0 ? "-" : "+") : text;
    }

    /// <summary>A PIC X(n) field DISPLAYed untrimmed.</summary>
    public static string Field(string? value, int n) => CobolText.Fit(value, n);

    /// <summary>FUNCTION NUMVAL of a CMDARGS value into PIC 9(8) (0 when blank or not numeric).</summary>
    public static int Date8(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var n = Col.NumVal(text);
        return (int)(Math.Abs(decimal.Truncate(n)) % 100_000_000m);
    }

    /// <summary>FUNCTION CURRENT-DATE(1:8).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    /// <summary>"As at dd/mm/yyyy" from a PIC 9(8) date (WS-DATE-R).</summary>
    public static string AsAtText(int yyyymmdd)
    {
        var d = yyyymmdd.ToString("D8", CultureInfo.InvariantCulture);
        return "As at " + d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    /// <summary>0360-READ-COMPANY: name and registered number from core.Company (the company code when not found).</summary>
    public static async Task<ReportHeading> HeadingAsync(Sqlio sql, string co, string reportId, string title, string subtitle, string asAt,
        string user, CancellationToken ct)
    {
        var h = new ReportHeading
        {
            CompanyCode = co,
            ReportId = reportId,
            Title = title,
            Subtitle = subtitle,
            AsAtText = asAt,
            User = user,
            RunAt = DateTime.Now,
        };
        var r = await sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = @p1", [co], ct);
        if (r.SqlCode == 0)
        {
            h.CompanyName = Col.Text(r.Row, 0);
            h.RegisteredNumber = Col.Text(r.Row, 1);
        }
        else
        {
            h.CompanyName = co;
        }
        return h;
    }

    /// <summary>
    /// RUNPATH BUILD (SPOOL folder, time-stamped) then OPEN OUTPUT: the RUNPATH outcome, with a null writer and the built path
    /// when the file cannot be opened.
    /// </summary>
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

    public static CommandLineArgs Args(JobRunContext ctx) => ctx.Args;
}
