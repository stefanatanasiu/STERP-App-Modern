using System.Globalization;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// The FIN batch suite housekeeping of one program run (cobol/copy/FINBATPR.cpy with FINBATWK.cpy): standard arguments and
/// program keys (9500/9550), RUNPATH spool and headings (9200/9250/9300), the closing STERP-I-SPOOL line (9350), the period
/// of the as-at date (9600) and the JOBLOG END values (9150: WS-RC, WS-ROWS-OUT, WS-SPOOL-NAME).
/// </summary>
internal sealed class FinBatch
{
    public FinBatch(JobRunContext ctx)
    {
        Ctx = ctx;
        Std = BatchSuite.StandardArgs(BatchSuiteKind.Fin, ctx.Args, DateTime.Now);
        Sql = ctx.Get<ISqlRunner>();
    }

    public JobRunContext Ctx { get; }

    public BatchSuiteArgs Std { get; }

    public ISqlRunner Sql { get; }

    public CancellationToken Ct => Ctx.Cancellation;

    /// <summary>WS-CO.</summary>
    public string Co => Std.Co;

    /// <summary>WS-USER.</summary>
    public string User => Std.User;

    /// <summary>WS-MODE (X(8)).</summary>
    public string Mode => Std.Mode;

    /// <summary>WS-ASAT (9(8)).</summary>
    public int AsAt => Std.AsAt;

    /// <summary>WS-RC.</summary>
    public int Rc { get; set; }

    /// <summary>WS-ROWS-OUT.</summary>
    public long RowsOut { get; set; }

    /// <summary>WS-SKIPPED.</summary>
    public long Skipped { get; set; }

    /// <summary>WS-POSTED.</summary>
    public long Posted { get; set; }

    /// <summary>WS-BLOCKED.</summary>
    public long Blocked { get; set; }

    /// <summary>WS-SPOOL-NAME.</summary>
    public string SpoolName { get; private set; } = "";

    /// <summary>RPT-HEAD-WORK (STRPTWS).</summary>
    public ReportHeading Heading { get; } = new();

    /// <summary>RPT-FILE once opened.</summary>
    public FinSpool? Spool { get; private set; }

    /// <summary>9550-GET-ARG: the CMDARGS value of the key (first 12 characters), spaces when not supplied.</summary>
    public string Arg(string key) => Ctx.Args.Get(CobolText.Trimmed(key, 12)) ?? "";

    public void Display(string line) => Ctx.Display(line);

    /// <summary>"STERP-E-1001 " SQLIO-ERRMSG: the driver text in its X(512) field (DISPLAY keeps the padding).</summary>
    public void SqlError(SqlioException ex) => Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 512));

    /// <summary>
    /// 9200-OPEN-SPOOL: RUNPATH BUILD SPOOL &lt;report&gt;-&lt;CO&gt;-&lt;stamp&gt;.lst (failure RC 12 STERP-E-9201), the headings (9250)
    /// and OPEN OUTPUT (failure RC 12 STERP-E-9202 with the path).
    /// </summary>
    public async Task<bool> OpenSpoolAsync(string reportId)
    {
        Heading.ReportId = reportId;
        var built = Ctx.Paths.Build("SPOOL", reportId, Co, null, null, true, out var path);
        if (built.ReturnCode > 4)
        {
            Rc = 12;
            Display(BatchSuite.SpoolPathFailure);
            return false;
        }
        SpoolName = path;
        await BatchSuite.FillHeadingsAsync(Sql, Std, Heading, DateTime.Now, Ct);
        try
        {
            Spool = new FinSpool(path);
            Ctx.SpoolFiles.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rc = 12;
            Display(BatchSuite.SpoolOpenFailure(path));
            return false;
        }
        return true;
    }

    /// <summary>A Report Writer over the open spool (LINE SEQUENTIAL records).</summary>
    public GnuReportWriter Report(int pageLimit, int headingLine, int firstDetail, int lastDetail, IReadOnlyList<RwControl>? controls = null,
        RwLine[]? finalFooting = null) =>
        new(Spool!.WriteLine, Heading, pageLimit, headingLine, firstDetail, lastDetail, controls, finalFooting);

    /// <summary>WS-SPOOL-NAME set by a program that builds its own output path (MTDJSON 3000).</summary>
    public void SetSpoolName(string path) => SpoolName = path;

    /// <summary>9350-CLOSE-SPOOL: CLOSE and the "STERP-I-SPOOL &lt;path&gt;" line.</summary>
    public void CloseSpool()
    {
        Spool?.Dispose();
        Ctx.AnnounceSpool(SpoolName.Trim());
    }

    /// <summary>9600-RESOLVE-PERIOD: fiscal year and period of WS-ASAT (0/0 when no period covers it).</summary>
    public async Task<(int FiscalYear, int PeriodNo)> ResolvePeriodAsync()
    {
        var (fy, per, _) = await BatchSuite.ResolvePeriodAsync(Sql, Std, Ct);
        return (fy, per);
    }

    /// <summary>9450-FORMAT-DATE: yyyymmdd as dd/mm/yyyy by position.</summary>
    public static string UkDate(long yyyymmdd) => BatchSuite.FormatDate((int)(Math.Abs(yyyymmdd) % 100_000_000));

    /// <summary>The JOBLOG END values of 9150-JOB-END (the program's exit code is WS-RC).</summary>
    public int End()
    {
        Spool?.Dispose();
        Ctx.RowsProcessed = (int)Math.Clamp(RowsOut, int.MinValue, int.MaxValue);
        Ctx.SpoolFile = SpoolName.Length == 0 ? null : SpoolName.Trim();
        return Rc;
    }
}

/// <summary>
/// A LINE SEQUENTIAL output file (RPT-FILE) in Windows-1252 with CR LF record ends. GnuCOBOL's OPEN OUTPUT truncates without
/// a sharing lock (two runs of a program in the same second write the same timestamped name and both succeed), so the
/// file is opened for shared write.
/// </summary>
internal sealed class FinSpool : IDisposable
{
    private readonly StreamWriter _writer;

    public FinSpool(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), CobolText.Ansi)
        {
            NewLine = "\r\n",
        };
    }

    public void WriteLine(string line) => _writer.WriteLine(line);

    public void Dispose() => _writer.Dispose();
}

/// <summary>DISPLAY of numeric items as the GnuCOBOL runtime shows them.</summary>
internal static class Disp
{
    /// <summary>PIC S9(n) DISPLAY: n digits with the sign after them ("000000021+").</summary>
    public static string S(long value, int digits = 9)
    {
        var d = (Math.Abs(value) % (long)Math.Pow(10, digits)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
        return d + (value < 0 ? "-" : "+");
    }

    /// <summary>PIC S9(n)V9(s) DISPLAY: digits, an implied point shown as '.', and the trailing sign.</summary>
    public static string S(decimal value, int intDigits, int scale)
    {
        var abs = Math.Abs(CobolNumeric.Truncated(value, scale));
        var whole = decimal.Truncate(abs) % (decimal)Math.Pow(10, intDigits);
        var frac = (abs - decimal.Truncate(abs)) * (decimal)Math.Pow(10, scale);
        var text = whole.ToString(new string('0', intDigits), CultureInfo.InvariantCulture) + "." +
                   decimal.Truncate(frac).ToString(new string('0', scale), CultureInfo.InvariantCulture);
        return text + (value < 0 ? "-" : "+");
    }

    /// <summary>PIC 9(n) DISPLAY: n digits.</summary>
    public static string U(long value, int digits) =>
        (Math.Abs(value) % (long)Math.Pow(10, digits)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
}
