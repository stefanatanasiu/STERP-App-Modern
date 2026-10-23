using System.Globalization;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>Conventions shared by the five Purchasing batch programs (CMDARGS, JOBLOG, RUNPATH, STRPTHDR, Report Writer).</summary>
internal static class PoBatch
{
    /// <summary>The program's own JOBLOG START (JOBLOG.cob:129-152): blank company, user, site or arguments are NULL.</summary>
    public static JobRegistration JoblogStart(string job, string co, string user, string commandLine) =>
        new(job, Blank(co), Blank(user), null, Blank(CobolText.Fit(commandLine, 200)));

    /// <summary>The program's JOBLOG END: status 0 OK, 4 WARNING, other FAILED; rows and the spool name.</summary>
    public static JobEnd JoblogEnd(int rc, int rows, string? spool) =>
        new(rc switch { 0 => "OK", 4 => "WARNING", _ => "FAILED" }, rc, rows, null, string.IsNullOrWhiteSpace(spool) ? null : spool, null);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd(' ');

    /// <summary>FUNCTION CURRENT-DATE: the clock of the run.</summary>
    public static DateTime Now() => DateTime.Now;

    /// <summary>yyyymmdd of a clock value.</summary>
    public static int Ymd(DateTime now) => CobolDate.ToYyyymmdd(now);

    /// <summary>1450-FORMAT-DATE: dd/mm/yyyy of a PIC 9(8) date, 00/00/0000 when 0 (STDATES redefinition).</summary>
    public static string DateEd(int yyyymmdd)
    {
        if (yyyymmdd == 0) return "00/00/0000";
        var d = Cob.D8(yyyymmdd);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    /// <summary>"As at dd/mm/yyyy" from the eight characters of an ASAT value (WS-ASAT(7:2) "/" (5:2) "/" (1:4)).</summary>
    public static string AsAt(string asat8)
    {
        var a = CobolText.Fit(asat8, 8);
        return "As at " + a[6..8] + "/" + a[4..6] + "/" + a[..4];
    }

    /// <summary>0350/0360: the STRPTHDR heading with the company name and registered number from core.Company.</summary>
    public static async Task<ReportHeading> HeadingAsync(PoSql sql, string co, string reportId, string title, string subtitle,
        string asAt, string user, DateTime now, CancellationToken ct)
    {
        var h = new ReportHeading
        {
            CompanyCode = co,
            ReportId = reportId,
            Title = title,
            Subtitle = subtitle,
            AsAtText = asAt,
            User = user,
            RunAt = now,
        };
        var r = await sql.Sngl("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
            [PoSql.Tx(co)], [100, 100], ct);
        if (r.Ok)
        {
            h.CompanyName = r.Row.Text(0);
            h.RegisteredNumber = r.Row.Text(1);
        }
        else
        {
            h.CompanyName = co;
        }
        return h;
    }

    /// <summary>
    /// The R-xxx report of the five programs over the open spool: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 54
    /// (FOOTING 58), the STRPTHDR page heading, CONTROLS ARE FINAL [control].
    /// </summary>
    public static GnuReportWriter Report(SpoolWriter spool, ReportHeading heading, IReadOnlyList<RwControl>? controls, RwLine[]? final) =>
        new(spool.WriteLine, heading, 60, 1, 9, 54, controls, final);

    /// <summary>A report line (COLUMN / PIC); VALUE literals go through <see cref="RwText.Lit"/>.</summary>
    public static RwText Line() => new();

    /// <summary>A line of the given character repeated over two 66-column fields (VALUE ALL "-" / "=").</summary>
    public static string Rule(char c) => new(c, 132);

    /// <summary>A numeric-edited report item as stored (full picture width).</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Field(value, picture);

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

    /// <summary>
    /// RUNPATH BUILD (SPOOL, report id, company, time stamp) and OPEN OUTPUT: null with the failure DISPLAYed by the caller
    /// when the path cannot be built or the file cannot be opened.
    /// </summary>
    public static SpoolWriter? OpenSpool(JobRunContext ctx, string reportId, string co, out bool pathFailed, out string path)
    {
        pathFailed = false;
        var r = ctx.Paths.Build("SPOOL", reportId, co, null, null, true, out path);
        if (r.ReturnCode > 4)
        {
            pathFailed = true;
            return null;
        }
        try
        {
            var spool = new SpoolWriter(path);
            ctx.SpoolFiles.Add(path);
            return spool;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PoSql Sql(JobRunContext ctx) => new(ctx.Get<ISqlRunner>());
}
