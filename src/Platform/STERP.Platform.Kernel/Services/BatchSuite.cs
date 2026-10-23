using System.Globalization;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;

namespace Sterp.Platform.Kernel.Services;

/// <summary>The FIN and XMD batch suites (FINBATPR.cpy / XMDBATPR.cpy) whose housekeeping the kernel provides (FR-STERP-PLATFORM-028 c).</summary>
public enum BatchSuiteKind
{
    /// <summary>FINBATPR / FINBATWK: defaults company STC, user GLCLERK, as-at today; MODE kept only when supplied.</summary>
    Fin,

    /// <summary>XMDBATPR / XMDBATWK: defaults company STC, user FINCTRL, MODE PREVIEW, as-at today; SITE, FROM and TO too.</summary>
    Xmd,
}

/// <summary>9500-STD-ARGS of a suite: the standard arguments with the suite's literal defaults (SME-023: defaults stay per suite).</summary>
public sealed class BatchSuiteArgs
{
    public BatchSuiteKind Suite { get; init; }

    /// <summary>WS-CO / XB-CO X(10).</summary>
    public string Co { get; init; } = "";

    /// <summary>XB-SITE X(10) (XMD only).</summary>
    public string Site { get; init; } = "";

    /// <summary>WS-USER / XB-USER X(20).</summary>
    public string User { get; init; } = "";

    /// <summary>WS-MODE X(8) (FIN, blank unless supplied) / XB-MODE X(10) (XMD, PREVIEW by default).</summary>
    public string Mode { get; init; } = "";

    /// <summary>WS-ASAT / XB-ASAT 9(8).</summary>
    public int AsAt { get; init; }

    /// <summary>XB-FROM 9(8) (XMD only).</summary>
    public int From { get; init; }

    /// <summary>XB-TO 9(8) (XMD only).</summary>
    public int To { get; init; }

    /// <summary>Only the exact value COMMIT enables writes in the XMD programs (88 XB-COMMIT).</summary>
    public bool IsCommit => Mode == "COMMIT";

    /// <summary>The caller header the suite passes to JOBLOG, PARMGET and EXCPRAIS (company, user, business date = as-at).</summary>
    public KernelCaller Caller => new(Co, Suite == BatchSuiteKind.Xmd ? Site : "", User, AsAt, "");
}

/// <summary>
/// FINBATPR / XMDBATPR housekeeping helpers (FR-STERP-PLATFORM-028): standard arguments, JOBLOG START/END values, the
/// spool console messages, the heading fill from core.Company, period resolution, parameter reading and XMD exception
/// raising. The spool path itself is RUNPATH BUILD SPOOL (report id, company, timestamp) - the job framework's
/// JobRunContext.OpenSpool / OutputPaths.
/// </summary>
public static class BatchSuite
{
    public const string SpoolPathFailure = "STERP-E-9201 cannot build the spool path";

    /// <summary>"STERP-E-9202 cannot open &lt;path&gt;" (the program then ends RC 12).</summary>
    public static string SpoolOpenFailure(string path) => "STERP-E-9202 cannot open " + K.Trim(path);

    /// <summary>9350-CLOSE-SPOOL: the final "STERP-I-SPOOL &lt;path&gt;" console line.</summary>
    public static string SpoolAnnouncement(string path) => "STERP-I-SPOOL " + K.Trim(path);

    /// <summary>"STERP-W-9701 exception refused &lt;reason X(8)&gt; &lt;document ref&gt;" (XMDBATPR 9700).</summary>
    public static string ExceptionRefused(string reasonCode, string documentRef) =>
        "STERP-W-9701 exception refused " + K.X(reasonCode, 8) + " " + K.Trim(K.X(documentRef, 40));

    /// <summary>9500-STD-ARGS: CMDARGS CMDLINE then the suite defaults (as-at today from the host clock).</summary>
    public static BatchSuiteArgs StandardArgs(BatchSuiteKind suite, CommandLineArgs args, DateTime now)
    {
        var co = K.Store(args.Co, 10);
        var user = K.Store(args.User, 20);
        var asAt = args.AsAt.Length > 0 && !CobolText.IsSpaces(args.AsAt) ? Date8(CobolNumeric.NumVal(args.AsAt)) : 0;
        var today = now.Year * 10000 + now.Month * 100 + now.Day;
        if (co.Length == 0) co = "STC";
        if (suite == BatchSuiteKind.Fin)
        {
            var mode = args.Mode.Length > 0 ? K.Store(args.Mode, 8) : "";
            if (user.Length == 0) user = "GLCLERK";
            if (asAt == 0) asAt = today;
            return new BatchSuiteArgs { Suite = suite, Co = co, User = user, Mode = mode, AsAt = asAt };
        }
        var site = K.Store(args.Site, 10);
        var from = args.From.Length > 0 ? Date8(CobolNumeric.NumVal(args.From)) : 0;
        var to = args.To.Length > 0 ? Date8(CobolNumeric.NumVal(args.To)) : 0;
        var xmode = args.Mode.Length > 0 ? K.Store(CobolText.Upper(args.Mode), 10) : "";
        if (user.Length == 0) user = "FINCTRL";
        if (xmode.Length == 0) xmode = "PREVIEW";
        if (asAt == 0) asAt = today;
        return new BatchSuiteArgs { Suite = suite, Co = co, Site = site, User = user, Mode = xmode, AsAt = asAt, From = from, To = to };
    }

    /// <summary>COMPUTE 9(8) = NUMVAL(...): decimals truncated, sign dropped, low-order 8 digits kept.</summary>
    private static int Date8(decimal value) => (int)CobolNumeric.Store(value, 8, 0, signed: false);

    /// <summary>JL-ARGUMENTS of 9100-JOB-START: the 256-byte command line moved into X(200).</summary>
    public static string JobArguments(string commandLine) => K.Store(K.X(commandLine, 256), 200);

    /// <summary>9150-JOB-END status: 0 COMPLETE, 4 WARNING, any other return code FAILED.</summary>
    public static string EndStatus(int returnCode) => returnCode switch { 0 => "COMPLETE", 4 => "WARNING", _ => "FAILED" };

    /// <summary>The core.JobRun status JOBLOG stores for <see cref="EndStatus"/> (COMPLETE is stored as OK).</summary>
    public static string StoredEndStatus(int returnCode) => returnCode switch { 0 => "OK", 4 => "WARNING", _ => "FAILED" };

    /// <summary>9100-JOB-START through JOBLOG (job name = report id, arguments = the command line; JOBLOG's RC is not tested).</summary>
    public static async Task<long> JobStartAsync(IJobRunRegistry joblog, BatchSuiteArgs args, string reportId, string commandLine, CancellationToken ct = default)
    {
        var area = await joblog.StartAsync(new KernelCaller(args.Co, "", args.User, args.AsAt, ""), reportId, JobArguments(commandLine), ct);
        return area.JobRunId;
    }

    /// <summary>9150-JOB-END through JOBLOG (rows, spool; log file and message NULL; a run id of 0 is refused JOB0002 and the run stays RUNNING).</summary>
    public static Task<JoblogArea> JobEndAsync(IJobRunRegistry joblog, BatchSuiteArgs args, string reportId, long jobRunId, int returnCode,
        int rowsOut, string? spoolFile, CancellationToken ct = default) =>
        joblog.EndAsync(new KernelCaller(args.Co, "", args.User, 0, ""), reportId, jobRunId, EndStatus(returnCode), returnCode, rowsOut,
            null, spoolFile, null, ct);

    /// <summary>
    /// 9250-FILL-HEADINGS + 9300-READ-COMPANY: company code, user, run date and time from the clock, company name and
    /// registered number from core.Company (XMD also the jurisdiction as place of registration); the company code is
    /// printed as the name when the row cannot be read.
    /// </summary>
    public static async Task<ReportHeading> FillHeadingsAsync(ISqlRunner sql, BatchSuiteArgs args, ReportHeading heading, DateTime now, CancellationToken ct = default)
    {
        heading.CompanyCode = args.Co;
        heading.User = K.Store(args.User, 20);
        heading.RunAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Unspecified);
        var statement = args.Suite == BatchSuiteKind.Fin
            ? "SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?"
            : "SELECT Name, ISNULL(RegisteredNumber,''), ISNULL(Jurisdiction,'England and Wales'), ISNULL(AccountingRefDate,'03-31'), " +
              "ISNULL(AccountingFramework,'FRS102'), ISNULL(VatRegNo,''), ISNULL(VatStagger,''), ISNULL(VatScheme,'INVOICE'), " +
              "CONVERT(char(1),ISNULL(PaymentPracticesInScope,0)), CONVERT(char(1),ISNULL(PackagingInScope,0)), ISNULL(LegalForm,'LTD') " +
              "FROM core.Company WHERE CompanyCode = ?";
        try
        {
            var row = await sql.SingleAsync(statement, [SqlioParm.Text(K.X(args.Co, 10))], Widths120(args.Suite == BatchSuiteKind.Fin ? 2 : 11), ct);
            if (row.Found)
            {
                heading.CompanyName = K.Store(row.Raw(0), 40);
                heading.RegisteredNumber = K.Store(row.Raw(1), 10);
                if (args.Suite == BatchSuiteKind.Xmd) heading.RegisteredPlace = K.Store(row.Raw(2), 24);
                return heading;
            }
        }
        catch (SqlioException)
        {
            // not SQLIO-OK: the company code is printed as the name
        }
        heading.CompanyName = K.Store(args.Co, 40);
        return heading;
    }

    /// <summary>
    /// 9600-RESOLVE-PERIOD: the fin.Period row of the company whose yyyymmdd StartDate..EndDate range contains the as-at
    /// date (no ORDER BY, first row); fiscal year and period 0 (XMD status NONE) when none is returned or SQLIO fails.
    /// </summary>
    public static async Task<(int FiscalYear, int PeriodNo, string Status)> ResolvePeriodAsync(ISqlRunner sql, BatchSuiteArgs args, CancellationToken ct = default)
    {
        try
        {
            var row = await sql.SingleAsync(
                "SELECT FiscalYear, PeriodNo, Status FROM fin.Period WHERE CompanyCode = ? AND ? BETWEEN CONVERT(char(8),StartDate,112) " +
                "AND CONVERT(char(8),EndDate,112)",
                [SqlioParm.Text(K.X(args.Co, 10)), SqlioParm.Digits(args.AsAt, 8)], Widths120(3), ct);
            if (row.Found)
            {
                var fy = (int)CobolNumeric.Store(row.NumVal(0), 4, 0, signed: false);
                var per = (int)CobolNumeric.Store(row.NumVal(1), 2, 0, signed: false);
                return (fy, per, args.Suite == BatchSuiteKind.Xmd ? K.Store(row.Raw(2), 10) : "");
            }
        }
        catch (SqlioException)
        {
        }
        return (0, 0, args.Suite == BatchSuiteKind.Xmd ? "NONE" : "");
    }

    /// <summary>9450-FORMAT-DATE: yyyymmdd to dd/mm/yyyy by position, no validation (XMD: 0 gives 00/00/0000).</summary>
    public static string FormatDate(int yyyymmdd)
    {
        var s = K.D8(yyyymmdd);
        return s.Substring(6, 2) + "/" + s.Substring(4, 2) + "/" + s[..4];
    }

    /// <summary>XMDBATPR 9470-ISO-TO-NUM: yyyy-mm-dd (byte 5 is '-') or 8 leading digits, else 0; no validation.</summary>
    public static int IsoToNum(string? text)
    {
        var t = K.X(text, 200);
        if (t[4] == '-')
        {
            var digits = t[..4] + t.Substring(5, 2) + t.Substring(8, 2);
            return digits.All(char.IsAsciiDigit) ? int.Parse(digits, CultureInfo.InvariantCulture) : (int)CobolNumeric.NumVal(digits);
        }
        return t[..8].All(char.IsAsciiDigit) ? int.Parse(t[..8], CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// XMDBATPR 9650-GET-PARM-NUM: PARMGET GETNUM (as-of the as-at date, use-default "Y" with the default edited -(14)9.99);
    /// below RC 08 and numeric the value becomes the amount truncated to 2 decimals, otherwise the default stands.
    /// </summary>
    public static async Task<decimal> GetParmNumAsync(IParameterService parameters, BatchSuiteArgs args, string paramCode, decimal defaultAmount, CancellationToken ct = default)
    {
        var amount = CobolNumeric.Store(defaultAmount, 13, 2);
        var area = await parameters.GetAsync(args.Caller with { Site = "" }, paramCode, args.AsAt, CobolEdit.Format(amount, "-(14)9.99"), numeric: true, ct);
        if (area.Header.ReturnCode < 8 && area.IsNumeric == "Y") amount = CobolNumeric.Store(area.ValueNum, 13, 2);
        return amount;
    }

    /// <summary>
    /// XMDBATPR 9700-RAISE-EXCEPTION: outside COMMIT the exception is only counted; in COMMIT EXCPRAIS RAISE with module "xmd"
    /// (upper-cased by EXCPRAIS) and the suite header; RC above 06 gives the STERP-W-9701 console line and is not counted.
    /// </summary>
    public static async Task<(bool Counted, string? ConsoleLine)> RaiseExceptionAsync(IExceptionRaiser raiser, BatchSuiteArgs args, ExcpraisArea body, CancellationToken ct = default)
    {
        if (!args.IsCommit) return (true, null);
        body.Header.Company = args.Co;
        body.Header.Site = args.Site;
        body.Header.User = args.User;
        body.Header.BusinessDate = args.AsAt;
        body.Header.Function = "RAISE";
        body.Module = "xmd";
        await raiser.ExecuteAsync(body, ct);
        return body.Header.ReturnCode > 6 ? (false, ExceptionRefused(body.Header.ReasonCode, body.DocumentRef)) : (true, null);
    }

    private static int[] Widths120(int count) => Enumerable.Repeat(120, count).ToArray();
}
