using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Jobs;

/// <summary>Conventions shared by the AR batch programs (cobol/batch/ar/*.cob, STERP-AR.md section 3.7 and 7.4).</summary>
public static class ArBatch
{
    public const string Slice = "STERP-AR";

    /// <summary>
    /// The program's own JOBLOG START (JOBLOG.cob 1000-START-RUN): job name, CO (default STC), USER (default ARCLERK1),
    /// no site, CM-COMMAND-LINE as JL-ARGUMENTS X(200).
    /// </summary>
    public static JobRegistration SelfRegistration(JobPreCheckContext pre, string jobName, string defaultUser = "ARCLERK1")
    {
        var co = CobolText.Trimmed(pre.Args.Co, 10);
        var user = CobolText.Trimmed(pre.Args.User, 20);
        var args = BatchSuite.JobArguments(pre.CommandLine);
        return new JobRegistration(jobName, co.Length == 0 ? "STC" : co, user.Length == 0 ? defaultUser : user, null,
            args.Length == 0 ? null : args);
    }

    /// <summary>JOBLOG END: COMPLETE/WARNING/FAILED (stored OK/WARNING/FAILED), RC, rows and the spool name (no log file).</summary>
    public static JobEnd End(JobRunContext ctx, int rc) =>
        new(BatchSuite.StoredEndStatus(rc), rc, ctx.RowsProcessed ?? 0, null, JobService.Blank(ctx.SpoolFile), null);

    /// <summary>
    /// POST /api/ar/v1/jobs/{jobCode}/runs: CO defaults to the X-STERP-Company header (STERP-AR.md 7.4) and USER to the
    /// caller (contracts 8.2) before the platform builds the KEY=VALUE command line in schema order.
    /// </summary>
    public static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition definition)
    {
        var body = await ctx.BodyObjectAsync();
        var co = body.FirstOrDefault(p => string.Equals(p.Key, "CO", StringComparison.OrdinalIgnoreCase));
        var coText = co.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(coText) && ctx.Session.CompanyCode.Length > 0)
        {
            if (co.Key is not null) body.Remove(co.Key);
            body["CO"] = ctx.Session.CompanyCode;
        }
        var cmd = JobEndpoints.BuildSliceCommandLine(body, definition, ctx.Session.UserCode);
        var jobs = ctx.Get<JobService>();
        var accepted = await jobs.SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, definition, cmd, body), ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>COMPUTE PIC 9(8) = FUNCTION NUMVAL(text): sign dropped, decimals and high-order digits truncated.</summary>
    public static int Date8(string? text) =>
        CobolText.IsSpaces(text) ? 0 : (int)CobolNumeric.Store(CobolNumeric.NumVal(text), 8, 0, signed: false);

    /// <summary>FUNCTION CURRENT-DATE(1:8) as yyyymmdd.</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    /// <summary>The eight digits of a PIC 9(8) date (AR-DATE-ED).</summary>
    public static string D8(int yyyymmdd) => Domain.ArEdit.D8(yyyymmdd);

    /// <summary>dd/mm/yyyy of a PIC 9(8) date (8100-FORMAT-DATE into WS-UK).</summary>
    public static string Uk(int yyyymmdd) => Domain.ArEdit.Dmy(yyyymmdd);

    /// <summary>DISPLAY of a numeric DISPLAY item (GnuCOBOL): the digits zero padded, a trailing sign when signed.</summary>
    public static string Num(long value, int digits, bool signed = true)
    {
        var limit = 1L;
        for (var i = 0; i < digits; i++) limit *= 10;
        var a = Math.Abs(value) % limit;
        var text = a.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return signed ? text + (value < 0 && a != 0 ? "-" : "+") : text;
    }

    /// <summary>0300-OPEN-SPOOL: RUNPATH BUILD SPOOL report-company-timestamp, OPEN OUTPUT (RC 12 STERP-E-0302 on failure).</summary>
    public static bool TryOpenSpool(JobRunContext ctx, string reportId, string co, out SpoolWriter spool, string folder = "SPOOL", string? extension = null)
    {
        try
        {
            spool = ctx.OpenSpool(reportId, co, extension, folder);
            return true;
        }
        catch (LegacyOutcomeException)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Display("STERP-E-0302 cannot open " + ex.Message);
        }
        spool = null!;
        return false;
    }

    /// <summary>0360-READ-COMPANY: core.Company name and registered number (SNGL), else the company code as the name.</summary>
    public static async Task ReadCompanyAsync(ISqlRunner sql, ReportHeading h, string co, CancellationToken ct)
    {
        try
        {
            var row = await sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(co)], Widths(30, 120), ct);
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

    /// <summary>8000-SET-WIDTHS: n column slots of the given width.</summary>
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

    /// <summary>PIC Z,ZZZ,ZZ9.99- (13 positions).</summary>
    public static string M13(decimal v) => CobolEdit.Field(v, "Z,ZZZ,ZZ9.99-");

    /// <summary>PIC Z,ZZZ,ZZZ,ZZ9.99- (17 positions).</summary>
    public static string M17(decimal v) => CobolEdit.Field(v, "Z,ZZZ,ZZZ,ZZ9.99-");
}
