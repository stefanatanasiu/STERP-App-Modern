using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// The XMD batch suite of one run (XMDBATWK work fields + XMDBATPR paragraphs, FR-STERP-XMD-013): the standard arguments,
/// the run counters, the spool file with the STRPTHDR heading, period resolution, numeric parameters and exception raising.
/// The program's own JOBLOG START/END are the job framework's self-registration (<see cref="XmdJobs"/>): END carries the
/// return code, XB-ROWS-OUT and XB-SPOOL-NAME stored here by <see cref="Finish"/>.
/// </summary>
internal sealed class XmdBatch
{
    private XmdBatch(JobRunContext ctx, BatchSuiteArgs args)
    {
        Ctx = ctx;
        Args = args;
        Sql = ctx.Get<ISqlRunner>();
    }

    public JobRunContext Ctx { get; }

    public ISqlRunner Sql { get; }

    public CancellationToken Ct => Ctx.Cancellation;

    /// <summary>XB-ARGS after 9500-STD-ARGS.</summary>
    public BatchSuiteArgs Args { get; private set; }

    public string Co => Args.Co;
    public string User => Args.User;
    public string Mode => Args.Mode;
    public int AsAt => Args.AsAt;
    public bool IsCommit => Args.IsCommit;

    /// <summary>XB-RC S9(4).</summary>
    public int Rc { get; set; }

    public int RowsIn { get; set; }
    public int RowsOut { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int Duplicate { get; set; }
    public int Skipped { get; set; }
    public int Raised { get; set; }
    public int Escalated { get; set; }
    public int Posted { get; set; }
    public int Diffs { get; set; }

    /// <summary>XB-FY 9(4), XB-PER 9(2), XB-PERIOD-STATUS X(10) after 9600-RESOLVE-PERIOD.</summary>
    public int FiscalYear { get; private set; }
    public int PeriodNo { get; private set; }
    public string PeriodStatus { get; private set; } = "";

    /// <summary>XB-SPOOL-NAME X(200): the RUNPATH path (kept even when the OPEN fails).</summary>
    public string SpoolName { get; private set; } = "";

    public ReportHeading Heading { get; } = new();

    public SpoolWriter? Spool { get; private set; }

    /// <summary>9500-STD-ARGS.</summary>
    public static XmdBatch Start(JobRunContext ctx) =>
        new(ctx, BatchSuite.StandardArgs(BatchSuiteKind.Xmd, ctx.Args, DateTime.Now));

    /// <summary>Replaces XB-ASAT / XB-FROM / XB-TO after program-specific defaulting.</summary>
    public void SetDates(int? asAt = null, int? from = null, int? to = null) => Args = new BatchSuiteArgs
    {
        Suite = Args.Suite, Co = Args.Co, Site = Args.Site, User = Args.User, Mode = Args.Mode,
        AsAt = asAt ?? Args.AsAt, From = from ?? Args.From, To = to ?? Args.To,
    };

    /// <summary>DISPLAY to the run console (SYSOUT).</summary>
    public void Display(string line) => Ctx.Display(line);

    /// <summary>9550-GET-ARG: the value of the key (first 12 characters) or "" when not supplied.</summary>
    public string GetArg(string key)
    {
        var k = CobolText.Fit(key, 12).TrimEnd(' ');
        return Ctx.Args.Get(k) ?? "";
    }

    /// <summary>9600-RESOLVE-PERIOD.</summary>
    public async Task ResolvePeriodAsync()
    {
        var (fy, per, status) = await BatchSuite.ResolvePeriodAsync(Sql, Args, Ct);
        FiscalYear = fy;
        PeriodNo = per;
        PeriodStatus = status;
    }

    /// <summary>9650-GET-PARM-NUM.</summary>
    public Task<decimal> GetParmNumAsync(string code, decimal defaultAmount) =>
        BatchSuite.GetParmNumAsync(Ctx.Get<IParameterService>(), Args, code, defaultAmount, Ct);

    /// <summary>
    /// 9200-OPEN-SPOOL (RUNPATH BUILD SPOOL &lt;report&gt;-&lt;CO&gt;-&lt;stamp&gt;.lst, 9250-FILL-HEADINGS, OPEN OUTPUT): false with RC 12
    /// and the STERP-E-9201 / STERP-E-9202 console line when the path or the file fails.
    /// </summary>
    public async Task<bool> OpenSpoolAsync(string reportId, string title, string subtitle)
    {
        Heading.ReportId = reportId;
        Heading.Title = title;
        Heading.Subtitle = subtitle;
        var r = Ctx.Paths.Build("SPOOL", reportId, Co, null, null, true, out var path);
        if (r.ReturnCode > 4)
        {
            Rc = 12;
            Display(BatchSuite.SpoolPathFailure);
            return false;
        }
        SpoolName = CobolText.Fit(path, 200).TrimEnd(' ');
        await BatchSuite.FillHeadingsAsync(Sql, Args, Heading, DateTime.Now, Ct);
        try
        {
            Spool = new SpoolWriter(path);
            Ctx.SpoolFiles.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Spool = null;
            Rc = 12;
            Display(BatchSuite.SpoolOpenFailure(SpoolName));
            return false;
        }
        return true;
    }

    /// <summary>
    /// The program's report on the open spool: every XMD RD is CONTROLS ARE FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 8,
    /// LAST DETAIL 52, FOOTING 58 with the STRPTHDR page heading; <paramref name="finalFooting"/> is CF-FINAL (its SOURCE
    /// fields are read at TERMINATE).
    /// </summary>
    public GnuReportWriter Report(RwLine[] finalFooting) => new(Spool!.WriteLine, Heading, 60, 1, 8, 52, null, finalFooting);

    /// <summary>9350-CLOSE-SPOOL: CLOSE and the STERP-I-SPOOL console line.</summary>
    public void CloseSpool()
    {
        Spool?.Dispose();
        Ctx.AnnounceSpool(SpoolName);
    }

    /// <summary>9700-RAISE-EXCEPTION: counted only outside COMMIT; COMMIT raises through EXCPRAIS (STERP-W-9701 when refused).</summary>
    public async Task RaiseAsync(ExcpraisArea body)
    {
        var (counted, console) = await BatchSuite.RaiseExceptionAsync(Ctx.Get<IExceptionRaiser>(), Args, body, Ct);
        if (counted) Raised++;
        if (console is not null) Display(console);
    }

    /// <summary>0000-FINISH: the JOBLOG END values (rows = XB-ROWS-OUT, spool = XB-SPOOL-NAME) and RETURN-CODE = XB-RC.</summary>
    public int Finish()
    {
        Spool?.Dispose();
        Ctx.RowsProcessed = RowsOut;
        Ctx.SpoolFile = SpoolName.Length > 0 ? SpoolName : null;
        return Rc;
    }
}
