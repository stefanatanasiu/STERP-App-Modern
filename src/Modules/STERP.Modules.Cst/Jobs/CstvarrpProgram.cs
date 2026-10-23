using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// CSTVARRP (cobol/batch/cst/CSTVARRP.cob; FR-STERP-CST-042): variance analysis by type and owner over cst.Variance with
/// the TYPE, JOB, ITEM and PERIOD (yyyynn split by position) filters; rows without owner and disposal are UNEXPLAINED and
/// give RC 4; MODE=UNDISPOSED omits disposed rows. Report Writer RD R-CSTVARRP: CONTROLS ARE FINAL D-TYPE, PAGE LIMIT 60,
/// HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58. Return codes 0, 4 and 12; the JOBLOG END is refused.
/// </summary>
internal sealed class CstvarrpProgram(CstBatch b)
{
    public const string Job = "CSTVARRP";

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-CST",
        SubmitOperationId = "cst_submitCstvarrpRun",
        ParameterOrder = ["CO", "PERIOD", "TYPE", "JOB", "ITEM", "MODE", "USER"],
        SelfRegistration = pre => CstJobs.JoblogStart(pre, Job),
        EndValues = CstJobs.NeverEnded,
        Execute = ctx => new CstvarrpProgram(new CstBatch(ctx)).RunAsync(),
    };

    // WS-ARGS
    private string _co = "";
    private string _type = "";
    private string _job = "";
    private string _item = "";
    private string _mode = "";
    private string _user = "";
    private string _period = "";
    private int _fy;
    private int _perNo;

    private long _rowsOut;

    // WS-BAND
    private decimal _aQtyVar, _aRateVar, _aTotalVar, _btQtyVar, _btRateVar, _btTotalVar;

    // WS-DET
    private string _dType = "";
    private string _dTypeName = "";
    private string _dSubject = "";
    private string _dDoc = "";
    private string _dPeriod = "";
    private decimal _dQtyVar, _dRateVar, _dTotalVar;
    private string _dSense = "";
    private string _dOwner = "";
    private string _dDisposal = "";
    private string _dReason = "";

    // WS-TOT
    private decimal _tAdverse, _tFavourable, _tNet, _tUnexplainedGbp;
    private int _tRows, _tUnexplained;

    private GnuReportWriter? _w;
    private RwLine[] _deLine = [];
    private RwLine[] _drLine = [];

    private async Task<int> RunAsync()
    {
        ReadArgs();
        await OpenSpoolAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await ProduceReportAsync();
        CloseSpool();
        return b.Finish(_rowsOut);
    }

    /// <summary>0100-READ-ARGS: PERIOD arrives as yyyynn and is split by position without validation.</summary>
    private void ReadArgs()
    {
        var a = b.Ctx.Args;
        _co = Pic.X(a.Co, 10);
        _user = Pic.X(a.User, 20);
        _mode = Pic.X(a.Mode, 12);
        _period = Pic.X(a.Period, 8);
        if (a.Has("TYPE")) _type = Pic.X(b.Arg("TYPE"), 16);
        if (a.Has("JOB")) _job = Pic.X(b.Arg("JOB"), 20);
        if (a.Has("ITEM")) _item = Pic.X(b.Arg("ITEM"), 40);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "COST";
        if (Pic.Blank(_mode)) _mode = "ALL";
        _fy = 0;
        _perNo = 0;
        if (!Pic.Blank(_period))
        {
            var p = CobolText.Fit(_period, 8);
            _fy = Pic.I(CobolNumeric.NumVal(p[..4]), 4);
            _perNo = Pic.I(CobolNumeric.NumVal(p[4..6]), 4);
        }
        b.Display("STERP-I-0100 CSTVARRP variance analysis for " + Pic.T(_co));
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS, then INITIATE.</summary>
    private async Task OpenSpoolAsync()
    {
        if (!b.BuildSpoolPath(Job, _co)) return;
        b.FillHeading(_co, Job, "Variance analysis by type and owner", _user);
        b.Heading.AsAtText = Pic.Blank(_period) ? "All periods" : Pic.Str(30, "Period ", Pic.T(_period));
        b.Heading.Subtitle = Pic.Blank(_type) ? "All variance types" : Pic.Str(60, "Type ", Pic.T(_type));
        b.Heading.StatusText = _mode == "UNDISPOSED" ? "Undisposed only" : "All variances";
        await b.ReadCompanyAsync(_co);
        if (!b.OpenSpool()) return;
        _w = b.Initiate([TypeControl()], FinalFooting());
        _deLine = [DeLine()];
        _drLine = [DrLine()];
    }

    private RwControl TypeControl() => new(() => _dType, v => _dType = v, 16)
    {
        Heading =
        [
            RwLine.PlusN(2, () => new RwText().Lit(1, "Variance type ").Put(15, _dType, 16).Put(33, _dTypeName, 34).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Item or job").Lit(34, "Document").Lit(61, "Period").Lit(72, "Quantity").Lit(87, "Rate")
                .Lit(102, "Total").Lit(117, "Sense").Lit(124, "Owner").Text),
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 66, '-').Text),
        ],
        Footing =
        [
            RwLine.Plus1(() => new RwText().Lit(3, "Type total").Put(18, _dType, 16).Num(69, _btQtyVar, "Z(8)9.99-")
                .Num(84, _btRateVar, "Z(8)9.99-").Num(99, _btTotalVar, "Z(8)9.99-").Text),
        ],
    };

    private RwLine DeLine() => RwLine.Plus1(() => new RwText().Put(3, _dSubject, 30).Put(34, _dDoc, 26).Put(61, _dPeriod, 8)
        .Num(69, _dQtyVar, "Z(8)9.99-").Num(84, _dRateVar, "Z(8)9.99-").Num(99, _dTotalVar, "Z(8)9.99-").Put(117, _dSense, 4)
        .Put(123, _dOwner, 10).Text);

    private RwLine DrLine() => RwLine.Plus1(() => new RwText().Lit(8, "disposal").Put(18, _dDisposal, 16).Lit(36, "reason")
        .Put(44, _dReason, 34).Text);

    private RwLine[] FinalFooting() =>
    [
        RwLine.Plus1(() => new RwText().Lit(3, "Adverse variance").Num(28, _tAdverse, "Z(11)9.99").Lit(48, "Favourable variance")
            .Num(70, _tFavourable, "Z(11)9.99-").Lit(90, "Net").Num(95, _tNet, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Variances reported:").Num(26, _tRows, "Z(6)9").Lit(38, "without owner or disposal:")
            .Num(68, _tUnexplained, "Z(6)9").Lit(80, "value").Num(89, _tUnexplainedGbp, "Z(11)9.99-").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "Adverse variances are positive.  A favourable one").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "is negative, so the column adds to the charge.").Text),
    ];

    /// <summary>1000-PRODUCE-REPORT: cst.Variance by type, year, period and id (cursor 1).</summary>
    private async Task ProduceReportAsync()
    {
        const string sql =
            "SELECT v.VarianceType, ISNULL(v.ItemCode, ISNULL(v.JobRef, ISNULL(v.KitRef,'(general)'))), " +
            "LTRIM(RTRIM(ISNULL(v.SourceDocType,'') + ' ' + ISNULL(v.SourceDocRef,''))), " +
            "ISNULL(CONVERT(varchar(4), v.FiscalYear),'') + RIGHT('0' + ISNULL(CONVERT(varchar(2), v.PeriodNo),''), 2), " +
            "v.QuantityVarianceGBP, v.RateVarianceGBP, v.TotalVarianceGBP, v.IsAdverse, ISNULL(v.OwnerUser,''), " +
            "ISNULL(v.Disposal,''), ISNULL(v.Reason, ISNULL(v.ReasonCode,'')) FROM cst.Variance v WHERE v.CompanyCode = ? " +
            "AND (? = '' OR v.VarianceType = ?) AND (? = '' OR ISNULL(v.JobRef,'') = ?) AND (? = '' OR ISNULL(v.ItemCode,'') = ?) " +
            "AND (? = 0 OR ISNULL(v.FiscalYear,0) = ?) AND (? = 0 OR ISNULL(v.PeriodNo,0) = ?) ORDER BY v.VarianceType, " +
            "v.FiscalYear, v.PeriodNo, v.VarianceId";
        // MOVE WS-FY / WS-PER-NO (S9(4)) TO SQLIO-PARM-VAL: the four digits, sign dropped
        var fy = SqlioParm.Digits(_fy, 4);
        var per = SqlioParm.Digits(_perNo, 4);
        var rows = new List<SqlioRow>();
        var outcome = await SqlioCursor.RunAsync(b.Sql, sql,
            [
                SqlioParm.Text(_co), SqlioParm.Text(_type), SqlioParm.Text(_type), SqlioParm.Text(_job), SqlioParm.Text(_job),
                SqlioParm.Text(_item), SqlioParm.Text(_item), fy, fy, per, per,
            ], 11, row =>
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
        foreach (var row in rows) EmitVariance(row);
    }

    /// <summary>1100-EMIT-VARIANCE.</summary>
    private void EmitVariance(SqlioRow row)
    {
        var newKey = Pic.X(row.Raw(0), 16);
        _dTypeName = TypeName(newKey);
        _dSubject = Pic.X(row.Raw(1), 30);
        _dDoc = Pic.X(row.Raw(2), 26);
        _dPeriod = Pic.X(row.Raw(3), 8);
        _dQtyVar = Pic.N(row.NumVal(4), 11, 2);
        _dRateVar = Pic.N(row.NumVal(5), 11, 2);
        _dTotalVar = Pic.N(row.NumVal(6), 11, 2);
        _dOwner = Pic.X(row.Raw(8), 12);
        _dDisposal = Pic.X(row.Raw(9), 16);
        _dReason = Pic.X(row.Raw(10), 34);
        _dSense = row.Raw(7).StartsWith('1') ? "ADV" : "FAV";
        if (_mode == "UNDISPOSED" && !Pic.Blank(_dDisposal)) return;
        if (!Pic.Eq(newKey, _dType) && !Pic.Blank(_dType)) CloseType();
        _dType = newKey;
        if (_dTotalVar >= 0) _tAdverse = Pic.N(_tAdverse + _dTotalVar, 13, 2);
        else _tFavourable = Pic.N(_tFavourable + _dTotalVar, 13, 2);
        _aQtyVar = Pic.N(_aQtyVar + _dQtyVar, 13, 2);
        _aRateVar = Pic.N(_aRateVar + _dRateVar, 13, 2);
        _tNet = Pic.N(_tNet + _dTotalVar, 13, 2);
        _aTotalVar = Pic.N(_aTotalVar + _dTotalVar, 13, 2);
        _tRows = Pic.I(_tRows + 1, 7);
        _rowsOut = (long)Pic.N(_rowsOut + 1, 9, 0);
        if (Pic.Blank(_dOwner) && Pic.Blank(_dDisposal))
        {
            _tUnexplained = Pic.I(_tUnexplained + 1, 7);
            _tUnexplainedGbp = Pic.N(_tUnexplainedGbp + _dTotalVar, 13, 2);
            _dDisposal = "UNEXPLAINED";
            if (Pic.Blank(_dReason)) _dReason = "*** no owner and no disposal";
        }
        _w!.Generate(_deLine);
        if (!Pic.Blank(_dDisposal) || !Pic.Blank(_dReason)) _w.Generate(_drLine);
    }

    /// <summary>1300-TYPE-NAME: the long names of the variance types (fixed by the cst.Variance check constraint).</summary>
    private static string TypeName(string key) => key.TrimEnd(' ') switch
    {
        "PPV" => "Purchase price at receipt",
        "INVOICEPRICE" => "Invoice price, incremental",
        "MATUSAGE" => "Material usage against standard",
        "MATPRICE" => "Material price on issue",
        "LABEFF" => "Labour efficiency",
        "LABRATE" => "Labour rate",
        "OVHD" => "Overhead absorption",
        "RETURNVAL" => "Return valued at standard",
        "LANDED" => "Landed cost after settlement",
        "REVALUE" => "Standard cost revaluation",
        "YIELD" => "Process yield",
        _ => "Other",
    };

    /// <summary>1400-CLOSE-TYPE.</summary>
    private void CloseType()
    {
        _btQtyVar = _aQtyVar;
        _btRateVar = _aRateVar;
        _btTotalVar = _aTotalVar;
        _aQtyVar = 0;
        _aRateVar = 0;
        _aTotalVar = 0;
    }

    /// <summary>0400-CLOSE-SPOOL: unexplained variances raise the return code to 4.</summary>
    private void CloseSpool()
    {
        CloseType();
        _w?.Terminate();
        b.CloseSpool();
        b.Display("STERP-I-0401 " + CstBatch.Disp(_rowsOut, 9) + " variance(s)");
        if (_tUnexplained > 0)
        {
            b.Display("STERP-W-0402 " + CstBatch.Disp(_tUnexplained, 7) + " variance(s) have no owner");
            if (b.Rc < 4) b.Rc = 4;
        }
        b.AnnounceSpool();
    }
}
