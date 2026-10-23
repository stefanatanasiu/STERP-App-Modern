using Microsoft.AspNetCore.Http;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// The CST batch programs (spec 7.3): CSTINVAL, CSTJOBPR, CSTVARRP, CSTSETCM and CSTWIPRF, submitted through the report
/// launcher model (catalogue rows CSTRP010-CSTRP050) and CSTINVAL also as STEP030 of the FIN-owned MONTHEND stream.
/// </summary>
internal static class CstJobs
{
    private static readonly Lazy<(JobDefinition Definition, string ReportCode)[]> Definitions = new(() =>
    [
        (CstinvalProgram.Definition(), "CSTRP010"),
        (CstjobprProgram.Definition(), "CSTRP020"),
        (CstvarrpProgram.Definition(), "CSTRP030"),
        (CstsetcmProgram.Definition(), "CSTRP040"),
        (CstwiprfProgram.Definition(), "CSTRP050"),
    ]);

    public static void MapSubmissions(OperationRegistry ops)
    {
        foreach (var (d, report) in Definitions.Value)
        {
            ops.Handle(d.SubmitOperationId!, ctx => SubmitAsync(ctx, d, report));
        }
    }

    public static void MapJobs(JobCatalog jobs)
    {
        foreach (var (d, _) in Definitions.Value) jobs.Add(d);
    }

    /// <summary>
    /// A slice submission as ReportService.Run launched the program (catalogue gate by the pipeline, contracts 5.4): the
    /// launcher row (core.usp_JobRunStart with the program name, the session company and the caller), then the program with
    /// its own JOBLOG START (SME-010). D-CST-11: the company the program runs for - CO, or the program default STC - must be
    /// in the caller's access list (contracts 5.3); the parameter object is passed on unchanged (no CO is injected).
    /// </summary>
    private static async Task<IResult> SubmitAsync(OperationContext ctx, JobDefinition d, string reportCode)
    {
        var body = await ctx.BodyObjectAsync();
        var cmd = JobEndpoints.BuildSliceCommandLine(body, d, ctx.Session.UserCode);
        var co = CommandLineArgs.Parse(cmd).Co.Trim(' ');
        if (co.Length == 0) co = "STC";
        if (!ctx.Session.User.HasCompany(co))
        {
            return ctx.Forbidden(ApiReasons.CompanySiteNotAllowed, $"{ctx.Session.UserCode} has no access to {co.ToUpperInvariant()}.");
        }
        var launcher = new JobRegistration(d.Program, JobService.Blank(ctx.Session.CompanyCode), ctx.Session.UserCode, null, null);
        var accepted = await ctx.Get<JobService>().SubmitAsync(new JobSubmitRequest(JobEntryPoint.Slice, d, cmd, body, reportCode, launcher),
            ctx.Session, ctx.Services, ctx.Aborted);
        return JobEndpoints.Accepted(ctx, accepted);
    }

    /// <summary>
    /// 0200-JOB-START: JOBLOG START with CA-COMPANY = CO (default STC), CA-USER = USER (default COST), no site and the command
    /// line as JL-ARGUMENTS X(200); blank values are NULL (JOBLOG.cob START).
    /// </summary>
    public static JobRegistration JoblogStart(JobPreCheckContext pre, string jobName)
    {
        var co = Pic.Blank(pre.Args.Co) ? "STC" : pre.Args.Co;
        var user = Pic.Blank(pre.Args.User) ? "COST" : pre.Args.User;
        var args = CobolText.Fit(pre.Args.Text, 200).TrimEnd(' ');
        return new JobRegistration(jobName, co, user, null, args.Length == 0 ? null : args);
    }

    /// <summary>
    /// 0500-JOB-END of CSTJOBPR, CSTVARRP, CSTSETCM and CSTWIPRF: the area is INITIALIZEd first, so JOBLOG END carries run id 0
    /// and is refused (JOB0002); the program's own row stays RUNNING (SME-009, BC-CST-06).
    /// </summary>
    public static JobEnd NeverEnded(JobRunContext ctx, int exit) => new(null, exit);
}

/// <summary>
/// The housekeeping sections every CST batch program carries its own copy of: CMDARGS GET (9550), 0300-OPEN-SPOOL (RUNPATH
/// BUILD SPOOL, STERP-E-0301/0302), 0350/0360 headings (STRPTHDR, core.Company on SQLIO cursor 8), the Report Writer RD
/// (PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58), the "STERP-E-1001" SQL failure line and the
/// closing STERP-I-SPOOL line.
/// </summary>
internal sealed class CstBatch(JobRunContext ctx)
{
    /// <summary>SQLIO-COL-WIDTH: every program sets the 24 slots to 100 before its first statement.</summary>
    public static readonly int[] Widths = Enumerable.Repeat(100, 24).ToArray();

    public JobRunContext Ctx => ctx;

    public ISqlRunner Sql { get; } = ctx.Get<ISqlRunner>();

    public CancellationToken Ct => ctx.Cancellation;

    /// <summary>WS-RC.</summary>
    public int Rc { get; set; }

    /// <summary>WS-SPOOL-NAME X(200).</summary>
    public string SpoolName { get; private set; } = "";

    /// <summary>RPT-HEAD-WORK (STRPTWS).</summary>
    public ReportHeading Heading { get; } = new();

    private SpoolWriter? _spool;

    public void Display(string line) => ctx.Display(line);

    /// <summary>CMDARGS GET: CM-LOOKUP-VALUE X(60) of the first occurrence of the key; spaces when not supplied.</summary>
    public string Arg(string key) => ctx.Args.Get(key) ?? "";

    /// <summary>"STERP-E-1001 " SQLIO-ERRMSG(1:100).</summary>
    public void SqlError(string driverText) => Display("STERP-E-1001 " + Pic.Pad(driverText, 100));

    /// <summary>0300-OPEN-SPOOL, first half: RUNPATH BUILD SPOOL &lt;report&gt;-&lt;CO&gt;-&lt;stamp&gt;.lst; RC 12 and STERP-E-0301 on failure.</summary>
    public bool BuildSpoolPath(string reportId, string co)
    {
        var r = ctx.Paths.Build("SPOOL", reportId, co, null, null, true, out var path);
        if (r.ReturnCode > 4)
        {
            Rc = 12;
            Display("STERP-E-0301 cannot build the spool path");
            return false;
        }
        SpoolName = path;
        return true;
    }

    /// <summary>0350-FILL-HEADINGS: the fields every program moves (company, report id, title, user, run date and time).</summary>
    public void FillHeading(string co, string reportId, string title, string user)
    {
        Heading.CompanyCode = co;
        Heading.ReportId = reportId;
        Heading.Title = title;
        Heading.User = user;
        Heading.RunAt = DateTime.Now;
    }

    /// <summary>0360-READ-COMPANY: name and registered number from core.Company (SNGL, cursor 8); the company code otherwise.</summary>
    public async Task ReadCompanyAsync(string co)
    {
        try
        {
            var row = await Sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(co)], Widths[..2], Ct);
            if (row.Found)
            {
                Heading.CompanyName = Pic.X(row.Raw(0), 40);
                Heading.RegisteredNumber = Pic.X(row.Raw(1), 10);
                return;
            }
        }
        catch (SqlioException)
        {
            // SQLIO-ERROR: the company code is printed instead
        }
        Heading.CompanyName = co;
    }

    /// <summary>0300-OPEN-SPOOL, second half: OPEN OUTPUT RPT-FILE; RC 12 and STERP-E-0302 with the X(200) path on failure.</summary>
    public bool OpenSpool()
    {
        try
        {
            _spool = new SpoolWriter(SpoolName);
            ctx.SpoolFiles.Add(SpoolName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Rc = 12;
            Display("STERP-E-0302 cannot open " + Pic.Pad(SpoolName, 200));
            return false;
        }
    }

    /// <summary>INITIATE of the program's RD over the open spool.</summary>
    public GnuReportWriter Initiate(IReadOnlyList<RwControl> controls, RwLine[] finalFooting)
    {
        var w = new GnuReportWriter(_spool!.WriteLine, Heading, 60, 1, 9, 56, controls, finalFooting);
        w.Initiate();
        return w;
    }

    /// <summary>CLOSE RPT-FILE.</summary>
    public void CloseSpool()
    {
        _spool?.Dispose();
        _spool = null;
    }

    /// <summary>DISPLAY "STERP-I-SPOOL " FUNCTION TRIM(WS-SPOOL-NAME).</summary>
    public void AnnounceSpool() => ctx.AnnounceSpool(SpoolName.Trim(' '));

    /// <summary>STOP RUN: the spool is closed; JOBLOG END values (WS-ROWS-OUT, WS-SPOOL-NAME); RETURN-CODE = WS-RC.</summary>
    public int Finish(long rowsOut)
    {
        CloseSpool();
        ctx.RowsProcessed = (int)Math.Clamp(rowsOut, int.MinValue, int.MaxValue);
        ctx.SpoolFile = SpoolName.Trim(' ').Length == 0 ? null : SpoolName.Trim(' ');
        return Rc;
    }

    /// <summary>DISPLAY of a PIC S9(n) item: the zero-padded digits and a trailing sign.</summary>
    public static string Disp(decimal value, int digits) => Pic.DispSigned(value, digits);

    /// <summary>MOVE to a numeric-edited working-storage item (e.g. WS-PCT-ED PIC -(4)9.99).</summary>
    public static string Edit(decimal value, string picture) => CobolEdit.Field(value, picture);

    /// <summary>COMPUTE PIC 9(8) = FUNCTION NUMVAL(text).</summary>
    public static int Date8(string text) => Pic.UI(CobolNumeric.NumVal(text), 8);

    /// <summary>FUNCTION CURRENT-DATE(1:8) as a number.</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    /// <summary>A run of a character (VALUE ALL "-") at a column.</summary>
    public static RwText Rule(RwText line, int column, int width, char c) => line.Put(column, new string(c, width), width);
}
