using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// CSTJOBPR (cobol/batch/cst/CSTJOBPR.cob; FR-STERP-CST-041): job profitability and work in progress from
/// cst.usp_JobProfitability (sub jobs rooted under their parent), one block per contract with budget, actual, committed,
/// EAC and revenue, the status/margin/WIP line and the over-budget note; CUSTOMER, STATUS and MODE=OVERSPEND selection.
/// Report Writer RD R-CSTJOBPR: CONTROLS ARE FINAL D-ROOT, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56,
/// FOOTING 58. Return codes 0 and 12 (spool or procedure failure); the JOBLOG END is refused, the row stays RUNNING.
/// </summary>
internal sealed class CstjobprProgram(CstBatch b)
{
    public const string Job = "CSTJOBPR";

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-CST",
        SubmitOperationId = "cst_submitCstjobprRun",
        ParameterOrder = ["CO", "PERIOD", "JOB", "STATUS", "CUSTOMER", "MODE", "USER"],
        SelfRegistration = pre => CstJobs.JoblogStart(pre, Job),
        EndValues = CstJobs.NeverEnded,
        Execute = ctx => new CstjobprProgram(new CstBatch(ctx)).RunAsync(),
    };

    // WS-ARGS
    private string _co = "";
    private string _job = "";
    private string _status = "";
    private string _cust = "";
    private string _mode = "";
    private string _user = "";
    private string _period = "";

    private long _rowsOut;

    // WS-BAND
    private decimal _aBudget, _aActual, _aCommit, _aForecast, _aRevenue;
    private decimal _btBudget, _btActual, _btCommit, _btForecast, _btRevenue;

    // WS-DET
    private string _dRoot = "";
    private string _dJob = "";
    private int _dLevel;
    private string _dPrintRef = "";
    private string _dDesc = "";
    private string _dCust = "";
    private string _dOps = "";
    private string _dFin = "";
    private decimal _dBudget, _dActual, _dCommit, _dRevenue, _dForecast, _dMargin, _dWip;
    private string _dMarginTxt = "";
    private string _dNote = "";

    // WS-TOT
    private decimal _tBudget, _tActual, _tCommit, _tForecast, _tRevenue, _tMargin, _tWip, _tPct;
    private int _tJobs, _tOverspend, _tNoBudget;

    private GnuReportWriter? _w;
    private RwLine[] _deLine = [];
    private RwLine[] _dsLine = [];

    private async Task<int> RunAsync()
    {
        ReadArgs();
        await OpenSpoolAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await ProduceReportAsync();
        FinalMargin();
        CloseSpool();
        return b.Finish(_rowsOut);
    }

    /// <summary>0100-READ-ARGS.</summary>
    private void ReadArgs()
    {
        var a = b.Ctx.Args;
        _co = Pic.X(a.Co, 10);
        _user = Pic.X(a.User, 20);
        _mode = Pic.X(a.Mode, 12);
        _period = Pic.X(a.Period, 8);
        if (a.Has("JOB")) _job = Pic.X(b.Arg("JOB"), 20);
        if (a.Has("STATUS")) _status = Pic.X(b.Arg("STATUS"), 12);
        if (a.Has("CUSTOMER")) _cust = Pic.X(b.Arg("CUSTOMER"), 20);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "COST";
        if (Pic.Blank(_mode)) _mode = "ALL";
        b.Display("STERP-I-0100 CSTJOBPR job profitability for " + Pic.T(_co));
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS, then INITIATE.</summary>
    private async Task OpenSpoolAsync()
    {
        if (!b.BuildSpoolPath(Job, _co)) return;
        b.FillHeading(_co, Job, "Job profitability and work in progress", _user);
        b.Heading.AsAtText = Pic.Blank(_period) ? "All open periods" : Pic.Str(30, "Period ", Pic.T(_period));
        b.Heading.Subtitle = Pic.Blank(_cust) ? "All customers" : Pic.Str(60, "Customer ", Pic.T(_cust));
        b.Heading.StatusText = _mode == "OVERSPEND" ? "Overspend only" : "All jobs in scope";
        await b.ReadCompanyAsync(_co);
        if (!b.OpenSpool()) return;
        _w = b.Initiate([RootControl()], FinalFooting());
        _deLine = [DeLine()];
        _dsLine = [DsLine()];
    }

    private RwControl RootControl() => new(() => _dRoot, v => _dRoot = v, 20)
    {
        Heading =
        [
            RwLine.PlusN(2, () => new RwText().Lit(1, "Contract ").Put(10, _dRoot, 20).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Job").Lit(28, "Description").Lit(55, "Customer").Lit(68, "Budget").Lit(81, "Actual")
                .Lit(93, "Committed").Lit(106, "EAC").Lit(119, "Revenue").Text),
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 66, '-').Text),
        ],
        Footing =
        [
            RwLine.Plus1(() => new RwText().Lit(3, "Contract total").Num(65, _btBudget, "Z(8)9.99").Num(78, _btActual, "Z(8)9.99")
                .Num(91, _btCommit, "Z(8)9.99").Num(104, _btForecast, "Z(8)9.99").Num(117, _btRevenue, "Z(8)9.99").Text),
        ],
    };

    private RwLine DeLine() => RwLine.Plus1(() => new RwText().Put(3, _dPrintRef, 24).Put(28, _dDesc, 26).Put(55, _dCust, 12)
        .Num(65, _dBudget, "Z(8)9.99").Num(78, _dActual, "Z(8)9.99").Num(91, _dCommit, "Z(8)9.99").Num(104, _dForecast, "Z(8)9.99")
        .Num(117, _dRevenue, "Z(8)9.99").Text);

    private RwLine DsLine() => RwLine.Plus1(() => new RwText().Lit(8, "status").Put(18, _dOps, 10).Put(29, _dFin, 10).Lit(41, "margin")
        .Num(48, _dMargin, "Z(8)9.99-").Put(62, _dMarginTxt, 10).Lit(74, "WIP").Num(78, _dWip, "Z(8)9.99").Put(93, _dNote, 28).Text);

    private RwLine[] FinalFooting() =>
    [
        RwLine.Plus1(() => new RwText().Lit(3, "Report totals").Num(65, _tBudget, "Z(8)9.99").Num(78, _tActual, "Z(8)9.99")
            .Num(91, _tCommit, "Z(8)9.99").Num(104, _tForecast, "Z(8)9.99").Num(117, _tRevenue, "Z(8)9.99").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "Jobs reported:").Num(20, _tJobs, "Z(6)9").Lit(32, "forecast over budget:")
            .Num(60, _tOverspend, "Z(6)9").Lit(72, "without an approved budget:").Num(99, _tNoBudget, "Z(6)9").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Closing WIP carried").Num(26, _tWip, "Z(11)9.99").Lit(45, "Total margin")
            .Num(62, _tMargin, "Z(11)9.99-").Lit(82, "Margin %").Num(92, _tPct, "Z(4)9.99-").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "EAC is actual plus committed plus accrued cost.").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Sub job cost belongs to its parent once only.").Text),
    ];

    /// <summary>1000-PRODUCE-REPORT: EXEC cst.usp_JobProfitability CO, JOB (NULL when blank), 1 on cursor 1.</summary>
    private async Task ProduceReportAsync()
    {
        var rows = new List<SqlioRow>();
        var outcome = await SqlioCursor.RunAsync(b.Sql, "EXEC cst.usp_JobProfitability ?, ?, 1",
            [SqlioParm.Text(_co), Pic.Blank(_job) ? SqlioParm.Null : SqlioParm.Text(_job)], 18, row =>
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
        foreach (var row in rows) EmitJob(row);
    }

    /// <summary>1100-EMIT-JOB.</summary>
    private void EmitJob(SqlioRow row)
    {
        _dJob = Pic.X(row.Raw(0), 20);
        _dLevel = Pic.I(row.NumVal(2), 4);
        var newKey = Pic.Blank(row.Raw(1)) ? Pic.X(row.Raw(0), 20) : Pic.X(row.Raw(1), 20);
        _dDesc = Pic.X(row.Raw(3), 26);
        _dCust = Pic.X(row.Raw(4), 12);
        _dOps = Pic.X(row.Raw(5), 10);
        _dFin = Pic.X(row.Raw(6), 10);
        _dBudget = Pic.N(row.NumVal(7), 11, 2);
        _dActual = Pic.N(row.NumVal(8), 11, 2);
        _dCommit = Pic.N(row.NumVal(9), 11, 2);
        _ = Pic.N(row.NumVal(10), 11, 2); // D-ACCRUE (not printed)
        _dRevenue = Pic.N(row.NumVal(11), 11, 2);
        _dWip = Pic.N(row.NumVal(12), 11, 2);
        _dForecast = Pic.N(row.NumVal(13), 11, 2);
        _ = Pic.N(row.NumVal(14), 11, 2); // D-VARIANCE (not printed)
        _dMargin = Pic.N(row.NumVal(15), 11, 2);
        if (Skipped()) return;
        // a sub job is shown indented under its parent so that nobody adds the two together by eye
        _dPrintRef = _dLevel > 0 ? Pic.Str(24, "  ", Pic.T(_dJob)) : Pic.X(_dJob, 24);
        if (!Pic.Eq(newKey, _dRoot) && !Pic.Blank(_dRoot)) CloseContract();
        _dRoot = newKey;
        if (row.Raw(16).StartsWith("UNDEFINED", StringComparison.Ordinal))
        {
            _dMarginTxt = "n/a";
        }
        else
        {
            var calc = Pic.N(row.NumVal(17), 13, 12);
            _dMarginTxt = Pic.X(CstBatch.Edit(calc, "-(4)9.99"), 10);
        }
        _dNote = "";
        if (_dBudget == 0)
        {
            _dNote = "no approved budget";
            _tNoBudget = Pic.I(_tNoBudget + 1, 7);
        }
        else if (_dForecast > _dBudget)
        {
            _dNote = "*** forecast over budget";
            _tOverspend = Pic.I(_tOverspend + 1, 7);
        }
        _tBudget = Pic.N(_tBudget + _dBudget, 13, 2);
        _aBudget = Pic.N(_aBudget + _dBudget, 13, 2);
        _tActual = Pic.N(_tActual + _dActual, 13, 2);
        _aActual = Pic.N(_aActual + _dActual, 13, 2);
        _tCommit = Pic.N(_tCommit + _dCommit, 13, 2);
        _aCommit = Pic.N(_aCommit + _dCommit, 13, 2);
        _tForecast = Pic.N(_tForecast + _dForecast, 13, 2);
        _aForecast = Pic.N(_aForecast + _dForecast, 13, 2);
        _tRevenue = Pic.N(_tRevenue + _dRevenue, 13, 2);
        _aRevenue = Pic.N(_aRevenue + _dRevenue, 13, 2);
        _tMargin = Pic.N(_tMargin + _dMargin, 13, 2);
        _tWip = Pic.N(_tWip + _dWip, 13, 2);
        _tJobs = Pic.I(_tJobs + 1, 7);
        _rowsOut = (long)Pic.N(_rowsOut + 1, 9, 0);
        _w!.Generate(_deLine);
        _w.Generate(_dsLine);
    }

    /// <summary>1150-APPLY-SELECTION: customer (on 12 characters), operational status and MODE=OVERSPEND.</summary>
    private bool Skipped()
    {
        if (!Pic.Blank(_cust) && Pic.T(_dCust) != Pic.T(_cust)) return true;
        if (!Pic.Blank(_status) && Pic.T(_dOps) != Pic.T(_status)) return true;
        return _mode == "OVERSPEND" && (_dBudget == 0 || _dForecast <= _dBudget);
    }

    /// <summary>1160-CLOSE-CONTRACT.</summary>
    private void CloseContract()
    {
        _btBudget = _aBudget;
        _btActual = _aActual;
        _btCommit = _aCommit;
        _btForecast = _aForecast;
        _btRevenue = _aRevenue;
        _aBudget = 0;
        _aActual = 0;
        _aCommit = 0;
        _aForecast = 0;
        _aRevenue = 0;
    }

    /// <summary>1800-FINAL-MARGIN: T-PCT ROUNDED = T-MARGIN * 100 / T-REVENUE, left at zero for a zero revenue.</summary>
    private void FinalMargin()
    {
        _tPct = 0;
        if (_tRevenue != 0) _tPct = ((Cd)_tMargin * 100 / _tRevenue).Store(5, 2, rounded: true);
    }

    /// <summary>0400-CLOSE-SPOOL.</summary>
    private void CloseSpool()
    {
        CloseContract();
        _w?.Terminate();
        b.CloseSpool();
        b.Display("STERP-I-0401 " + CstBatch.Disp(_rowsOut, 9) + " job(s) reported");
        if (_tOverspend > 0) b.Display("STERP-W-0402 " + CstBatch.Disp(_tOverspend, 7) + " job(s) forecast over budget");
        b.AnnounceSpool();
    }
}
