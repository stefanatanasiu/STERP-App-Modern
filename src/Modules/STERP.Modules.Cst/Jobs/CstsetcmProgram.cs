using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// CSTSETCM (cobol/batch/cst/CSTSETCM.cob; FR-STERP-CST-043): comparison of a proposed cost set with its base (COMPARETO,
/// else the set's ComparedToSetCode, else the latest ACTIVE STANDARD set) item by item (FULL JOIN of cst.StandardCost) with
/// the revaluation impact on the quantity held, banded by the materiality THRESHOLD (default 5, also for 0); MODE=EXCEPTION
/// omits changes under the threshold. The sets are validated after the JOBLOG START and before the spool is opened
/// (STERP-E-0151..0154, RC 12, no listing). Report Writer RD R-CSTSETCM: CONTROLS ARE FINAL D-BAND, PAGE LIMIT 60,
/// HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58. Return codes 0 and 12; the JOBLOG END is refused.
/// </summary>
internal sealed class CstsetcmProgram(CstBatch b)
{
    public const string Job = "CSTSETCM";

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-CST",
        SubmitOperationId = "cst_submitCstsetcmRun",
        ParameterOrder = ["CO", "COSTSET", "COMPARETO", "THRESHOLD", "MODE", "USER"],
        SelfRegistration = pre => CstJobs.JoblogStart(pre, Job),
        EndValues = CstJobs.NeverEnded,
        Execute = ctx => new CstsetcmProgram(new CstBatch(ctx)).RunAsync(),
    };

    // WS-ARGS
    private string _co = "";
    private string _set = "";
    private string _base = "";
    private string _mode = "";
    private string _user = "";
    private decimal _threshold;

    // WS-SETINFO
    private string _sStatus = "";
    private string _sPurpose = "";
    private string _bStatus = "";

    private long _rowsOut;

    // WS-BAND
    private decimal _aImpact, _btImpact;

    // WS-DET
    private string _dBand = "";
    private string _dItem = "";
    private string _dRev = "";
    private string _dDesc = "";
    private decimal _dOld, _dNew, _dChange, _dPct, _dQty, _dImpact;
    private string _dNote = "";

    // WS-TOT
    private decimal _tImpact, _tUp, _tDown;
    private int _tItems, _tOver, _tNew, _tDropped;

    private GnuReportWriter? _w;
    private RwLine[] _deLine = [];
    private RwLine[] _dqLine = [];

    private async Task<int> RunAsync()
    {
        ReadArgs();
        await ReadSetsAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await OpenSpoolAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await ProduceReportAsync();
        CloseSpool();
        return b.Finish(_rowsOut);
    }

    /// <summary>0100-READ-ARGS: THRESHOLD is NUMVAL of its first 10 characters; absent or 0 is 5 per cent.</summary>
    private void ReadArgs()
    {
        var a = b.Ctx.Args;
        _co = Pic.X(a.Co, 10);
        _user = Pic.X(a.User, 20);
        _mode = Pic.X(a.Mode, 12);
        if (a.Has("COSTSET")) _set = Pic.X(b.Arg("COSTSET"), 20);
        if (a.Has("COMPARETO")) _base = Pic.X(b.Arg("COMPARETO"), 20);
        if (a.Has("THRESHOLD"))
        {
            var text = CobolText.Fit(b.Arg("THRESHOLD"), 10);
            _threshold = Pic.N(CobolNumeric.NumVal(text), 5, 2);
        }
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "COST";
        if (Pic.Blank(_mode)) _mode = "ALL";
        if (_threshold == 0) _threshold = 5.00m;
        b.Display("STERP-I-0100 CSTSETCM cost set comparison for " + Pic.T(_co));
    }

    /// <summary>0150-READ-SETS: both sets must exist before a page is printed (CST0401, CST0402, CST0410).</summary>
    private async Task ReadSetsAsync()
    {
        if (Pic.Blank(_set))
        {
            b.Rc = 12;
            b.Display("STERP-E-0151 CST0401 COSTSET= is required");
            return;
        }
        var set = await LookupSetAsync(_set);
        if (set is null)
        {
            b.Rc = 12;
            b.Display("STERP-E-0152 CST0402 cost set " + Pic.T(_set) + " not found");
            return;
        }
        _sStatus = Pic.X(set.Raw(1), 10);
        _sPurpose = Pic.X(set.Raw(2), 12);
        // no COMPARETO given - the set the proposal was prepared against, then the live standard
        if (Pic.Blank(_base)) _base = Pic.X(set.Raw(3), 20);
        if (Pic.Blank(_base)) await FindActiveAsync();
        if (Pic.Blank(_base))
        {
            b.Rc = 12;
            b.Display("STERP-E-0153 CST0410 no comparison set");
            return;
        }
        var baseSet = await LookupSetAsync(_base);
        if (baseSet is null)
        {
            b.Rc = 12;
            b.Display("STERP-E-0154 CST0410 comparison set " + Pic.T(_base) + " not found");
            return;
        }
        _bStatus = Pic.X(baseSet.Raw(1), 10);
    }

    /// <summary>0160-LOOKUP-SET (SNGL, cursor 5): the row, or null when not SQLIO-OK (not found or failed).</summary>
    private async Task<SqlioRow?> LookupSetAsync(string code)
    {
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT Name, Status, Purpose, ISNULL(ComparedToSetCode,'') FROM cst.CostSet WHERE CompanyCode = ? AND CostSetCode = ?",
                [SqlioParm.Text(_co), SqlioParm.Text(code)], CstBatch.Widths[..4], b.Ct);
            return row.Found ? row : null;
        }
        catch (SqlioException)
        {
            return null;
        }
    }

    /// <summary>0170-FIND-ACTIVE: the latest ACTIVE STANDARD set other than the proposal (SNGL, cursor 5).</summary>
    private async Task FindActiveAsync()
    {
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT TOP (1) CostSetCode FROM cst.CostSet WHERE CompanyCode = ? AND Status = 'ACTIVE' AND Purpose = 'STANDARD' " +
                "AND CostSetCode <> ? ORDER BY EffectiveFrom DESC", [SqlioParm.Text(_co), SqlioParm.Text(_set)], CstBatch.Widths[..1], b.Ct);
            if (row.Found) _base = Pic.X(row.Raw(0), 20);
        }
        catch (SqlioException)
        {
            // not SQLIO-OK: WS-BASE stays blank
        }
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS, then INITIATE.</summary>
    private async Task OpenSpoolAsync()
    {
        if (!b.BuildSpoolPath(Job, _co)) return;
        b.FillHeading(_co, Job, "Cost set comparison and revaluation impact", _user);
        b.Heading.Subtitle = Pic.Str(60, Pic.T(_set), " (", Pic.T(_sStatus), ") against ", Pic.T(_base), " (", Pic.T(_bStatus), ")");
        b.Heading.AsAtText = Pic.Str(30, "Threshold ", Pic.T(CstBatch.Edit(_threshold, "-(4)9.99")), " per cent");
        b.Heading.StatusText = _sPurpose;
        await b.ReadCompanyAsync(_co);
        if (!b.OpenSpool()) return;
        _w = b.Initiate([BandControl()], FinalFooting());
        _deLine = [DeLine()];
        _dqLine = [DqLine()];
    }

    private RwControl BandControl() => new(() => _dBand, v => _dBand = v, 28)
    {
        Heading =
        [
            RwLine.PlusN(2, () => new RwText().Put(1, _dBand, 28).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Item").Lit(32, "Rev").Lit(37, "Description").Lit(66, "Previous").Lit(81, "Proposed")
                .Lit(96, "Change").Lit(106, "Pct").Lit(117, "Impact on stock").Text),
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 66, '-').Text),
        ],
        Footing =
        [
            RwLine.Plus1(() => new RwText().Lit(3, "Band total").Put(14, _dBand, 28).Num(116, _btImpact, "Z(8)9.99-").Text),
        ],
    };

    private RwLine DeLine() => RwLine.Plus1(() => new RwText().Put(3, _dItem, 28).Put(32, _dRev, 4).Put(37, _dDesc, 26)
        .Num(64, _dOld, "Z(7)9.9999").Num(79, _dNew, "Z(7)9.9999").Num(92, _dChange, "Z(5)9.9999-").Num(105, _dPct, "Z(4)9.99-")
        .Num(116, _dImpact, "Z(8)9.99-").Text);

    private RwLine DqLine() => RwLine.Plus1(() => new RwText().Lit(8, "quantity on hand").Num(26, _dQty, "Z(10)9.999").Put(45, _dNote, 18).Text);

    private RwLine[] FinalFooting() =>
    [
        RwLine.Plus1(() => new RwText().Lit(3, "Revaluation impact if activated").Num(36, _tImpact, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "   of which increases stock by").Num(36, _tUp, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "   of which reduces stock by").Num(36, _tDown, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Items compared:").Num(21, _tItems, "Z(6)9").Lit(33, "over threshold:").Num(51, _tOver, "Z(6)9")
            .Lit(63, "new:").Num(69, _tNew, "Z(6)9").Lit(81, "dropped:").Num(91, _tDropped, "Z(6)9").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "This report changes nothing.  Activation is a").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "separate act, and the approver is not the preparer.").Text),
    ];

    /// <summary>1000-PRODUCE-REPORT: proposal FULL JOIN base by item and revision with the company's quantity held (cursor 1).</summary>
    private async Task ProduceReportAsync()
    {
        const string sql =
            "SELECT ISNULL(n.ItemCode, o.ItemCode), ISNULL(n.Revision, o.Revision), ISNULL(i.Description,''), " +
            "ISNULL(o.TotalUnitCost, 0), ISNULL(n.TotalUnitCost, 0), CASE WHEN n.ItemCode IS NULL THEN 'DROPPED' WHEN o.ItemCode IS " +
            "NULL THEN 'NEW' ELSE 'BOTH' END, ISNULL((SELECT SUM(ic.QtyOnHand) FROM cst.ItemCost ic WHERE ic.CompanyCode = ? AND " +
            "ic.ItemCode = ISNULL(n.ItemCode, o.ItemCode)), 0) FROM (SELECT ItemCode, Revision, TotalUnitCost FROM cst.StandardCost " +
            "WHERE CompanyCode = ? AND CostSetCode = ?) n FULL JOIN (SELECT ItemCode, Revision, TotalUnitCost FROM cst.StandardCost " +
            "WHERE CompanyCode = ? AND CostSetCode = ?) o ON o.ItemCode = n.ItemCode AND o.Revision = n.Revision LEFT JOIN inv.Item i " +
            "ON i.CompanyCode = ? AND i.ItemCode = ISNULL(n.ItemCode, o.ItemCode) ORDER BY ISNULL(n.ItemCode, o.ItemCode), " +
            "ISNULL(n.Revision, o.Revision)";
        var rows = new List<SqlioRow>();
        var outcome = await SqlioCursor.RunAsync(b.Sql, sql,
            [
                SqlioParm.Text(_co), SqlioParm.Text(_co), SqlioParm.Text(_set), SqlioParm.Text(_co), SqlioParm.Text(_base),
                SqlioParm.Text(_co),
            ], 7, row =>
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
        foreach (var row in rows) EmitItem(row);
    }

    /// <summary>1100-EMIT-ITEM: change, percentage and impact, then the band of the line.</summary>
    private void EmitItem(SqlioRow row)
    {
        _dItem = Pic.X(row.Raw(0), 28);
        _dRev = Pic.X(row.Raw(1), 4);
        _dDesc = Pic.X(row.Raw(2), 26);
        _dOld = Pic.N(row.NumVal(3), 9, 4);
        _dNew = Pic.N(row.NumVal(4), 9, 4);
        _dQty = Pic.N(row.NumVal(6), 11, 3);
        _dChange = ((Cd)_dNew - _dOld).Store(9, 4);
        _dPct = 0;
        if (_dOld != 0) _dPct = ((Cd)_dChange * 100 / _dOld).Store(5, 2, rounded: true, previous: _dPct);
        _dImpact = ((Cd)_dChange * _dQty).Store(11, 2, rounded: true);
        _dNote = "";
        string newKey;
        switch (CobolText.Fit(row.Raw(5), 7))
        {
            case "NEW    ":
                newKey = "NOT IN THE PREVIOUS SET";
                _dNote = "new cost line";
                _tNew = Pic.I(_tNew + 1, 7);
                break;
            case "DROPPED":
                newKey = "DROPPED FROM THE PROPOSAL";
                _dNote = "no proposed cost";
                _tDropped = Pic.I(_tDropped + 1, 7);
                break;
            default:
                if (Math.Abs(_dPct) >= _threshold)
                {
                    newKey = "CHANGE AT OR OVER THRESHOLD";
                    _tOver = Pic.I(_tOver + 1, 7);
                }
                else
                {
                    newKey = "CHANGE UNDER THRESHOLD";
                }
                break;
        }
        if (_mode == "EXCEPTION" && newKey == "CHANGE UNDER THRESHOLD") return;
        if (!Pic.Eq(newKey, _dBand) && !Pic.Blank(_dBand)) CloseBand();
        _dBand = newKey;
        _tImpact = Pic.N(_tImpact + _dImpact, 13, 2);
        _aImpact = Pic.N(_aImpact + _dImpact, 13, 2);
        if (_dImpact >= 0) _tUp = Pic.N(_tUp + _dImpact, 13, 2);
        else _tDown = Pic.N(_tDown + _dImpact, 13, 2);
        _tItems = Pic.I(_tItems + 1, 7);
        _rowsOut = (long)Pic.N(_rowsOut + 1, 9, 0);
        _w!.Generate(_deLine);
        if (_dQty != 0 || !Pic.Blank(_dNote)) _w.Generate(_dqLine);
    }

    /// <summary>1150-CLOSE-BAND.</summary>
    private void CloseBand()
    {
        _btImpact = _aImpact;
        _aImpact = 0;
    }

    /// <summary>0400-CLOSE-SPOOL.</summary>
    private void CloseSpool()
    {
        CloseBand();
        _w?.Terminate();
        b.CloseSpool();
        b.Display("STERP-I-0401 " + CstBatch.Disp(_rowsOut, 9) + " item(s) compared");
        b.Display("STERP-I-0403 no cost has been changed by this run");
        b.AnnounceSpool();
    }
}
