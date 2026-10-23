using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Jobs;

/// <summary>Conventions shared by the SO batch programs (cobol/batch/so/*.cob; STERP-SO.md sections 4.8-4.10 and 7.3).</summary>
internal static class SoBatch
{
    public const string Slice = "STERP-SO";

    /// <summary>
    /// POST /api/so/v1/jobs/{jobCode}/runs (D-SO-05, contracts 8.2-8.3): CO defaults to X-STERP-Company and a supplied CO must
    /// be in the caller's access list; SITE defaults to X-STERP-Site where the schema says so and a supplied SITE must pair
    /// with CO; USER is always the effective user. The run is registered as ReportService.Run did (the launcher row: program
    /// name, session company and user) and again by the program's own JOBLOG START (SME-010).
    /// </summary>
    public static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d, bool siteDefaultsToHeader)
    {
        var body = await ctx.BodyObjectAsync();
        Remove(body, "USER");
        var co = TextOf(body, "CO");
        string effectiveCo;
        if (string.IsNullOrWhiteSpace(co))
        {
            Remove(body, "CO");
            if (ctx.Session.CompanyCode.Length > 0) body["CO"] = ctx.Session.CompanyCode;
            effectiveCo = ctx.Session.CompanyCode;
        }
        else
        {
            effectiveCo = co.Trim();
            if (!ctx.Session.User.HasCompany(effectiveCo))
            {
                return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed,
                    $"{ctx.Session.UserCode} has no access to {effectiveCo.ToUpperInvariant()}.");
            }
        }
        var site = TextOf(body, "SITE");
        if (site is null)
        {
            Remove(body, "SITE");
            if (siteDefaultsToHeader && ctx.Session.SiteCode.Length > 0) body["SITE"] = ctx.Session.SiteCode;
        }
        else if (site.Trim().Length > 0 && effectiveCo.Length > 0 && !ctx.Session.User.HasCompanySite(effectiveCo, site.Trim()))
        {
            return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed,
                $"{ctx.Session.UserCode} has no access to {effectiveCo.ToUpperInvariant()}/{site.Trim().ToUpperInvariant()}.");
        }
        body["USER"] = ctx.Session.UserCode;
        var cmd = JobEndpoints.BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        var launcher = new JobRegistration(d.Program, JobService.Blank(ctx.Session.CompanyCode), ctx.Session.UserCode, null, null);
        var jobs = ctx.Get<JobService>();
        var accepted = await jobs.SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body, LauncherRow: launcher),
            ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    private static string? TextOf(JsonObject body, string key)
    {
        var hit = body.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return hit.Key is null ? null : hit.Value switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            var n => n.ToJsonString(),
        };
    }

    private static void Remove(JsonObject body, string key)
    {
        foreach (var k in body.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList()) body.Remove(k);
    }

    /// <summary>The program's JOBLOG START (JOBLOG.cob 1000-START-RUN): blank company, user, site or arguments are NULL.</summary>
    public static JobRegistration JoblogStart(string job, string co, string user, string site, string commandLine) =>
        new(job, Blank(co), Blank(user), Blank(site), Blank(BatchSuite.JobArguments(commandLine)));

    /// <summary>JOBLOG END with the saved run id: COMPLETE/WARNING/FAILED (stored OK/WARNING/FAILED), RC, rows and the spool name.</summary>
    public static JobEnd End(JobRunContext ctx, int rc) =>
        new(BatchSuite.StoredEndStatus(rc), rc, ctx.RowsProcessed ?? 0, null, JobService.Blank(ctx.SpoolFile), null);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');

    /// <summary>
    /// 0300-OPEN-SPOOL: RUNPATH BUILD SPOOL &lt;report&gt;-&lt;co&gt;-&lt;stamp&gt;.lst then OPEN OUTPUT; the program's own
    /// messages on failure (RC 12).
    /// </summary>
    public static bool TryOpenSpool(JobRunContext ctx, string reportId, string co, string pathFailure, string openFailure, out SpoolWriter spool)
    {
        spool = null!;
        var r = ctx.Paths.Build("SPOOL", reportId, Cob.X(co, 10), null, null, true, out var path);
        if (!r.IsOk)
        {
            ctx.Display(pathFailure);
            return false;
        }
        try
        {
            spool = new SpoolWriter(path);
            ctx.SpoolFiles.Add(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Display(openFailure + CobolText.Fit(path, 200));
            return false;
        }
    }

    /// <summary>0360-READ-COMPANY: core.Company name and registered number (SNGL, 100-wide slots), else the company code as the name.</summary>
    public static async Task ReadCompanyAsync(ISqlRunner sql, ReportHeading h, string co, CancellationToken ct)
    {
        try
        {
            var row = await sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(co)], Widths(30, 100), ct);
            if (row.Found)
            {
                h.CompanyName = CobolText.Trimmed(row.Raw(0), 40);
                h.RegisteredNumber = CobolText.Trimmed(row.Raw(1), 10);
                return;
            }
        }
        catch (SqlioException)
        {
            // SQLIO-OK not set
        }
        h.CompanyName = CobolText.Trimmed(co, 40);
    }

    /// <summary>n column slots of the given width (SQLIO-COL-WIDTH).</summary>
    public static int[] Widths(int count, int width) => Enumerable.Repeat(width, count).ToArray();

    /// <summary>0350-FILL-HEADINGS: STRPTWS from the program's values and FUNCTION CURRENT-DATE.</summary>
    public static ReportHeading Heading(string co, string reportId, string title, string user, DateTime now) => new()
    {
        CompanyCode = CobolText.Trimmed(co, 10),
        ReportId = CobolText.Trimmed(reportId, 12),
        Title = CobolText.Trimmed(title, 60),
        User = CobolText.Trimmed(user, 20),
        RunAt = now,
    };

    /// <summary>DISPLAY of a numeric DISPLAY item (GnuCOBOL): the digits zero padded, a trailing sign when signed.</summary>
    public static string Num(long value, int digits, bool signed = true)
    {
        var limit = 1L;
        for (var i = 0; i < digits; i++) limit *= 10;
        var a = Math.Abs(value) % limit;
        var text = a.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return signed ? text + (value < 0 && a != 0 ? "-" : "+") : text;
    }

    /// <summary>COMPUTE PIC 9(8) = FUNCTION NUMVAL(text): sign dropped, decimals and high-order digits truncated.</summary>
    public static int Date8(string? text) =>
        CobolText.IsSpaces(text) ? 0 : (int)CobolNumeric.Store(CobolNumeric.NumVal(text), 8, 0, signed: false);

    /// <summary>MOVE alphanumeric TO PIC 9(8) (GnuCOBOL: leading spaces skipped, digits right justified, stops at a non-digit).</summary>
    public static int MoveDigits8(string? text)
    {
        var s = (text ?? "").TrimStart(' ');
        var digits = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (c != ' ') break;
        }
        var d = digits.ToString();
        if (d.Length > 8) d = d[^8..];
        return d.Length == 0 ? 0 : int.Parse(d, CultureInfo.InvariantCulture);
    }

    /// <summary>dd/mm/yyyy of a PIC 9(8) date (WS-DATE-ED).</summary>
    public static string Dmy(int yyyymmdd)
    {
        var d = Cob.D8(yyyymmdd);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }
}
