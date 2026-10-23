using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Modules.Cst.Engines;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// CSTWIPRF (cobol/batch/cst/CSTWIPRF.cob; FR-STERP-CST-044): work in progress roll-forward - every job not financially
/// closed (or JOB=, cut to 14 characters) by financial status and job, each line WIPCALC's own answer (CALC, or POST with
/// commit when MODE=POST - CMDARGS upper-cases MODE, so 'post' posts as well). A blank PERIOD reads the absent
/// core.FiscalPeriod (STERP-W-0151, year and period stay 0, SME-832). U-CST-01 (CS-07, BC-CST-04): WIPCALC re-prepares the
/// SQLIO slot of the job cursor, so the job list ends after the first job whose WIPCALC call passed its validation; jobs
/// refused by the validation (no SQL) keep the list going. Report Writer RD R-CSTWIPRF: CONTROLS ARE FINAL D-STATUS,
/// PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58. Return codes 0 and 12; the JOBLOG END is refused.
/// </summary>
internal sealed class CstwiprfProgram(CstBatch b)
{
    public const string Job = "CSTWIPRF";

    /// <summary>The WIPCALC reasons set by 0200-VALIDATE, before any SQL statement.</summary>
    private static readonly HashSet<string> ValidationReasons = ["CST0901", "CST0902", "CST0903"];

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-CST",
        SubmitOperationId = "cst_submitCstwiprfRun",
        ParameterOrder = ["CO", "PERIOD", "JOB", "MODE", "USER"],
        SelfRegistration = pre => CstJobs.JoblogStart(pre, Job),
        EndValues = CstJobs.NeverEnded,
        Execute = ctx => new CstwiprfProgram(new CstBatch(ctx)).RunAsync(),
    };

    // WS-ARGS
    private string _co = "";
    private string _job = "";
    private string _mode = "";
    private string _user = "";
    private string _period = "";
    private int _year;
    private int _per;

    // WS-RUN
    private long _rowsOut;
    private long _posted;
    private long _refused;
    private bool _write;

    // WS-BAND
    private decimal _aOpening, _aAdditions, _aDeductions, _aClosing, _btOpening, _btAdditions, _btDeductions, _btClosing;
    private int _aJobs, _btJobs;

    // WS-DET
    private string _dStatus = "";
    private string _dJob = "";
    private string _dDesc = "";
    private decimal _dOpening, _dMaterial, _dLabour, _dOverhead, _dSubcon, _dAdditions, _dReturns, _dTransfers, _dScrap, _dWriteoff, _dClosing;
    private string _dBalanced = "";

    // WS-TOT
    private decimal _tOpening, _tAdditions, _tDeductions, _tClosing;
    private int _tJobs, _tResidual;

    private GnuReportWriter? _w;
    private RwLine[] _deLine = [];
    private RwLine[] _ddLine = [];

    private async Task<int> RunAsync()
    {
        await ReadArgsAsync();
        await OpenSpoolAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await ProduceReportAsync();
        CloseSpool();
        return b.Finish(_rowsOut);
    }

    /// <summary>0100-READ-ARGS with 0150-SPLIT-PERIOD: only the literal POST sets the write flag.</summary>
    private async Task ReadArgsAsync()
    {
        var a = b.Ctx.Args;
        _co = Pic.X(a.Co, 10);
        _user = Pic.X(a.User, 20);
        _mode = Pic.X(a.Mode, 12);
        _period = Pic.X(a.Period, 8);
        if (a.Has("JOB")) _job = Pic.X(b.Arg("JOB"), 20);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "COST";
        if (Pic.Blank(_mode)) _mode = "CALC";
        if (_mode == "POST") _write = true;
        if (Pic.Blank(_period))
        {
            await CurrentPeriodAsync();
        }
        else
        {
            var p = CobolText.Fit(_period, 8);
            _year = Pic.UI(CobolNumeric.NumVal(p[..4]), 4);
            _per = Pic.UI(CobolNumeric.NumVal(p[4..6]), 2);
        }
        b.Display("STERP-I-0100 CSTWIPRF WIP roll-forward for " + Pic.T(_co));
    }

    /// <summary>0160-CURRENT-PERIOD: the first OPEN core.FiscalPeriod of the company (SNGL, cursor 8).</summary>
    private async Task CurrentPeriodAsync()
    {
        SqlioRow? row = null;
        try
        {
            row = await b.Sql.SingleAsync(
                "SELECT TOP 1 FiscalYear, PeriodNo FROM core.FiscalPeriod WHERE CompanyCode = ? AND Status = 'OPEN' ORDER BY FiscalYear, PeriodNo",
                [SqlioParm.Text(_co)], CstBatch.Widths[..2], b.Ct);
        }
        catch (SqlioException)
        {
            // not SQLIO-OK
        }
        if (row is { Found: true })
        {
            _year = Pic.UI(row.NumVal(0), 4);
            _per = Pic.UI(row.NumVal(1), 2);
            _period = Pic.Str(8, Digits(_year, 4), Digits(_per, 2));
        }
        else
        {
            b.Display("STERP-W-0151 no open period - nothing rolled");
        }
    }

    private static string Digits(int value, int n) => value.ToString(new string('0', n), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS, then INITIATE.</summary>
    private async Task OpenSpoolAsync()
    {
        if (!b.BuildSpoolPath(Job, _co)) return;
        b.FillHeading(_co, Job, "Work in progress roll-forward", _user);
        b.Heading.AsAtText = Pic.Str(30, "Period ", Digits(_year, 4), "/", Digits(_per, 2));
        b.Heading.Subtitle = Pic.Blank(_job) ? "All jobs with a balance or movement" : Pic.Str(60, "Job ", Pic.T(_job));
        b.Heading.StatusText = _write ? "Period balances posted" : "Calculation only - nothing posted";
        await b.ReadCompanyAsync(_co);
        if (!b.OpenSpool()) return;
        _w = b.Initiate([StatusControl()], FinalFooting());
        _deLine = [DeLine()];
        _ddLine = [DdLine()];
    }

    private RwControl StatusControl() => new(() => _dStatus, v => _dStatus = v, 14)
    {
        Heading =
        [
            RwLine.PlusN(2, () => new RwText().Lit(1, "Financial status ").Put(19, _dStatus, 14).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Job").Lit(18, "Description").Lit(47, "Opening").Lit(61, "Material").Lit(75, "Labour")
                .Lit(88, "Overhead").Lit(101, "Subcontr").Lit(117, "Closing").Text),
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 64, '-').Text),
        ],
        Footing =
        [
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 64, '-').Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Status total").Num(18, _btJobs, "Z(6)9").Lit(27, "job(s)").Num(43, _btOpening, "Z(8)9.99")
                .Lit(56, "additions").Num(67, _btAdditions, "Z(8)9.99").Lit(81, "deductions").Num(92, _btDeductions, "Z(8)9.99")
                .Num(108, _btClosing, "Z(8)9.99").Text),
        ],
    };

    private RwLine DeLine() => RwLine.Plus1(() => new RwText().Put(3, _dJob, 14).Put(18, _dDesc, 24).Num(43, _dOpening, "Z(8)9.99")
        .Num(56, _dMaterial, "Z(8)9.99").Num(69, _dLabour, "Z(8)9.99").Num(82, _dOverhead, "Z(8)9.99").Num(95, _dSubcon, "Z(8)9.99")
        .Num(108, _dClosing, "Z(8)9.99").Text);

    private RwLine DdLine() => RwLine.Plus1(() => new RwText().Lit(6, "deductions:").Lit(18, "returns").Num(27, _dReturns, "Z(6)9.99")
        .Lit(40, "transfers").Num(51, _dTransfers, "Z(6)9.99").Lit(64, "scrap").Num(71, _dScrap, "Z(6)9.99").Lit(84, "write off")
        .Num(95, _dWriteoff, "Z(6)9.99").Lit(108, "balanced").Put(119, _dBalanced, 3).Text);

    private RwLine[] FinalFooting() =>
    [
        RwLine.PlusN(2, () => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '='), 67, 64, '=').Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Opening work in prog").Num(26, _tOpening, "Z(11)9.99").Lit(45, "Jobs rolled")
            .Num(62, _tJobs, "Z(6)9").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Additions in period").Num(26, _tAdditions, "Z(11)9.99").Lit(45, "With residual")
            .Num(62, _tResidual, "Z(6)9").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Deductions in period").Num(26, _tDeductions, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Closing work in prog").Num(26, _tClosing, "Z(11)9.99").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "Closing = opening + additions - deductions.")
            .Lit(50, "A completion transfer takes value OUT of work").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "in progress; it is not left in both balances.").Text),
    ];

    /// <summary>1000-PRODUCE-REPORT: the jobs not financially closed (cursor 1), each rolled by WIPCALC.</summary>
    private async Task ProduceReportAsync()
    {
        var jobParm = Pic.Blank(_job) ? SqlioParm.Null : SqlioParm.Text(_job);
        var rows = new List<SqlioRow>();
        var outcome = await SqlioCursor.RunAsync(b.Sql,
            "SELECT j.JobRef, j.Description, j.FinancialStatus FROM cst.Job j WHERE j.CompanyCode = ? AND (? IS NULL OR j.JobRef = ?) " +
            "AND j.FinancialStatus <> 'CLOSED' ORDER BY j.FinancialStatus, j.JobRef",
            [SqlioParm.Text(_co), jobParm, jobParm], 3, row =>
            {
                rows.Add(row);
                return Task.FromResult(true);
            }, b.Ct);
        if (outcome.ExecFailed)
        {
            b.Rc = 12;
            b.SqlError(outcome.DriverText);
            return;
        }
        foreach (var row in rows)
        {
            // U-CST-01: a WIPCALC call that reached its SQL leaves the job cursor's slot re-prepared - the next FTCH ends the list
            if (await RollOneJobAsync(row)) break;
        }
        CloseBand();
    }

    /// <summary>1100-ROLL-ONE-JOB; returns true when WIPCALC got past its validation (SQLIO slot 1 re-prepared).</summary>
    private async Task<bool> RollOneJobAsync(SqlioRow row)
    {
        _dJob = Pic.X(row.Raw(0), 14);
        _dDesc = Pic.X(row.Raw(1), 24);
        var status = Pic.X(row.Raw(2), 14);
        if (!Pic.Eq(_dStatus, status))
        {
            CloseBand();
            _dStatus = status;
        }
        var wip = await AskWipcalcAsync();
        var reachedSql = !ValidationReasons.Contains(wip.Ca.ReasonCode.TrimEnd(' '));
        if (wip.Ca.ReturnCode > 4)
        {
            _refused = (long)Pic.N(_refused + 1, 9, 0);
            b.Display("STERP-W-1101 " + Pic.T(_dJob) + " " + Pic.Pad(wip.Ca.Message, 60));
            return reachedSql;
        }
        MoveTheNumbers(wip);
        _w!.Generate(_deLine);
        _w.Generate(_ddLine);
        _rowsOut = (long)Pic.N(_rowsOut + 1, 9, 0);
        _aJobs = Pic.I(_aJobs + 1, 7);
        _tJobs = Pic.I(_tJobs + 1, 7);
        _aOpening = Pic.N(_aOpening + _dOpening, 13, 2);
        _tOpening = Pic.N(_tOpening + _dOpening, 13, 2);
        _aAdditions = Pic.N(_aAdditions + _dAdditions, 13, 2);
        _tAdditions = Pic.N(_tAdditions + _dAdditions, 13, 2);
        var deductions = (Cd)_dReturns + _dTransfers + _dScrap + _dWriteoff;
        _aDeductions = ((Cd)_aDeductions + deductions).Store(13, 2);
        _tDeductions = ((Cd)_tDeductions + deductions).Store(13, 2);
        _aClosing = Pic.N(_aClosing + _dClosing, 13, 2);
        _tClosing = Pic.N(_tClosing + _dClosing, 13, 2);
        if (wip.HasResidual == "Y") _tResidual = Pic.I(_tResidual + 1, 7);
        return reachedSql;
    }

    /// <summary>1120-ASK-WIPCALC: POST with commit when writing, CALC otherwise, for the job (D-JOB X(14)) and period.</summary>
    private async Task<WipCalcArea> AskWipcalcAsync()
    {
        var wip = new WipCalcArea
        {
            Ca = CaHeader.Initialized(_write ? "POST" : "CALC", _co, "", _user, 0),
            Commit = _write ? "Y" : "N",
            FiscalYear = _year,
            PeriodNo = _per,
            JobRef = _dJob,
        };
        await b.Ctx.Get<WipCalcEngine>().LinkAsync(wip, b.Ct);
        if (_write && wip.Ca.ReturnCode <= 4) _posted = (long)Pic.N(_posted + 1, 9, 0);
        return wip;
    }

    /// <summary>1140-MOVE-THE-NUMBERS.</summary>
    private void MoveTheNumbers(WipCalcArea w)
    {
        _dOpening = Pic.N(w.OpeningGbp, 11, 2);
        _dMaterial = Pic.N(w.MatIssuesGbp, 11, 2);
        _dLabour = ((Cd)w.LabourGbp + w.MachineGbp).Store(11, 2);
        _dOverhead = ((Cd)w.OverheadGbp + w.AdjustmentGbp).Store(11, 2);
        _dSubcon = Pic.N(w.SubconGbp, 11, 2);
        _dAdditions = Pic.N(w.AdditionsGbp, 11, 2);
        _dReturns = Pic.N(w.MatReturnsGbp, 11, 2);
        _dTransfers = Pic.N(w.TransfersGbp, 11, 2);
        _dScrap = Pic.N(w.ScrapGbp, 11, 2);
        _dWriteoff = Pic.N(w.WriteoffGbp, 11, 2);
        _dClosing = Pic.N(w.ClosingGbp, 11, 2);
        var check = ((Cd)w.OpeningGbp + w.AdditionsGbp - w.DeductionsGbp - w.ClosingGbp).Store(13, 2);
        _dBalanced = check == 0 ? "yes" : "NO";
    }

    /// <summary>1200-CLOSE-BAND: the band figures are taken before the control break prints them.</summary>
    private void CloseBand()
    {
        _btOpening = _aOpening;
        _btAdditions = _aAdditions;
        _btDeductions = _aDeductions;
        _btClosing = _aClosing;
        _btJobs = _aJobs;
        _aOpening = 0;
        _aAdditions = 0;
        _aDeductions = 0;
        _aClosing = 0;
        _aJobs = 0;
    }

    /// <summary>0400-CLOSE-SPOOL.</summary>
    private void CloseSpool()
    {
        _w?.Terminate();
        b.CloseSpool();
        b.Display("STERP-I-0401 " + CstBatch.Disp(_rowsOut, 9) + " job(s) rolled forward");
        if (_posted > 0) b.Display("STERP-I-0403 " + CstBatch.Disp(_posted, 9) + " period balance(s) posted");
        if (_refused > 0) b.Display("STERP-W-0402 " + CstBatch.Disp(_refused, 9) + " job(s) refused by WIPCALC");
        b.AnnounceSpool();
    }
}
