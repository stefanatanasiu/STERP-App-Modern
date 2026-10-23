using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// The AP job submissions (contracts 8.2-8.3; STERP-AP.md section 7.3): a slice submission registers the run exactly as the
/// report launcher did (ReportService.Run: core.usp_JobRunStart with the program name, the session company and user, ended
/// with the launcher status, return code, log and the parsed spool path - ReportService.vb:116-188) and the program then
/// registers itself through JOBLOG START; APAGED, APREMIT, APPPR and APVAT6M send their JOBLOG END after re-initialising the
/// area (run id 0), so their own row stays RUNNING (SME-009, SME-010). CO defaults to X-STERP-Company, USER to the caller.
/// The PAYRUN stream registers only its SETENV row and ends it through JOBEND (the 202 run is the stream row).
/// </summary>
internal static class ApJobSubmission
{
    public static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d)
    {
        var body = await ctx.BodyObjectAsync();
        var co = body.FirstOrDefault(p => string.Equals(p.Key, "CO", StringComparison.OrdinalIgnoreCase));
        var coText = co.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(coText))
        {
            if (co.Key is not null) body.Remove(co.Key);
            if (ctx.Session.CompanyCode.Length > 0) body["CO"] = ctx.Session.CompanyCode;
        }
        var cmd = JobEndpoints.BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        // a stream registers its own SETENV row (contracts 8.4); a program registers like the report launcher plus its JOBLOG row
        var launcher = d.IsStream ? null : new JobRegistration(d.Program, JobService.Blank(ctx.Session.CompanyCode), ctx.Session.UserCode, null, null);
        var accepted = await ctx.Get<JobService>().SubmitAsync(
            new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body, LauncherRow: launcher), ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>JOBLOG END with run id 0 updates nothing: the program's own row stays RUNNING (SME-009).</summary>
    public static JobEnd NeverEnded(JobRunContext ctx, int exit) => new(null, exit);

    /// <summary>The program's JOBLOG START (JOBLOG.cob:129-152): blank company, user, site or arguments are NULL.</summary>
    public static JobRegistration JoblogStart(string job, string co, string user, string commandLine) =>
        new(job, Blank(co), Blank(user), null, Blank(BatchSuite.JobArguments(commandLine)));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');
}

/// <summary>The common batch arguments of the AP programs (0100-READ-ARGS: CMDARGS CMDLINE, program defaults STC / APMGR).</summary>
internal sealed record ApBatchArgs(string Co, string User, string Mode, string AsAtText, string Run)
{
    public static ApBatchArgs From(Sterp.Platform.Kernel.Jobs.CommandLineArgs a) => new(
        CobolText.Trimmed(a.Co, 10) is { Length: > 0 } co ? co : "STC",
        CobolText.Trimmed(a.User, 20) is { Length: > 0 } user ? user : "APMGR",
        CobolText.Trimmed(a.Mode, 10),
        a.AsAt,
        a.Get("RUN") ?? "");

    /// <summary>The company heading of the batch suite (0360-READ-COMPANY: name and registered number, company code when not found).</summary>
    public static async Task FillCompanyAsync(ISqlRunner sql, ReportHeading h, string co, CancellationToken ct)
    {
        try
        {
            var row = await sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(co)], Enumerable.Repeat(100, 24).ToArray(), ct);
            if (row.Found)
            {
                h.CompanyName = CobolText.Trimmed(row.Raw(0), 40);
                h.RegisteredNumber = CobolText.Trimmed(row.Raw(1), 10);
                return;
            }
        }
        catch (Sterp.Platform.Kernel.SqlioException)
        {
            // SQLIO-OK is false: the heading carries the company code
        }
        h.CompanyName = CobolText.Trimmed(co, 40);
    }

    /// <summary>"dd/mm/yyyy" from a yyyymmdd number by position (no validation).</summary>
    public static string Ddmmyyyy(int yyyymmdd)
    {
        var t = ((long)Math.Abs(yyyymmdd) % 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return t[6..8] + "/" + t[4..6] + "/" + t[..4];
    }
}
