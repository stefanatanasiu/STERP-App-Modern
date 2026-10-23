using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Cst.Jobs;

/// <summary>
/// CSTINVAL (cobol/batch/cst/CSTINVAL.cob; FR-STERP-CST-040): inventory valuation listing of every cst.ItemCost pool by
/// valuation group, item and site with the NRV line, the open FIFO layers (DETAIL=LAYER) and the reconciliation to the
/// inventory control account (cst.usp_ValuationRecon, CST-022). Report Writer RD R-CSTINVAL: CONTROLS ARE FINAL D-GROUP,
/// PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58. Return codes: 0 reconciled, 4 not reconciled or
/// reconciliation unavailable, 12 spool or selection failure. Also STEP030 of the FIN-owned MONTHEND stream.
/// </summary>
internal sealed class CstinvalProgram(CstBatch b)
{
    public const string Job = "CSTINVAL";

    public static JobDefinition Definition() => new()
    {
        JobCode = Job,
        Slice = "STERP-CST",
        SubmitOperationId = "cst_submitCstinvalRun",
        ParameterOrder = ["CO", "ASAT", "SITE", "GROUP", "METHOD", "DETAIL", "MODE", "USER"],
        SelfRegistration = pre => CstJobs.JoblogStart(pre, Job),
        // 0500-JOB-END: FAILED above 4, COMPLETE (stored OK) for 0, WARNING otherwise; rows, spool
        EndValues = (ctx, exit) => new JobEnd(exit > 4 ? "FAILED" : exit == 0 ? "OK" : "WARNING", exit, ctx.RowsProcessed, null,
            JobService.Blank(ctx.SpoolFile)),
        Execute = ctx => new CstinvalProgram(new CstBatch(ctx)).RunAsync(),
    };

    // WS-ARGS
    private string _co = "";
    private string _site = "";
    private string _group = "";
    private string _method = "";
    private string _detail = "";
    private string _mode = "";
    private string _user = "";
    private int _asat;

    // WS-RUN
    private long _rowsOut;
    private long _layersOut;

    // WS-BAND
    private decimal _aGross, _aAllow, _btGross, _btAllow;

    // WS-DET
    private string _dGroup = "";
    private string _dItem = "";
    private string _dDesc = "";
    private string _dSite = "";
    private string _dMethod = "";
    private decimal _dQty, _dUnit, _dGross, _dNrvUnit, _dAllow, _dNet;
    private string _dFlag = "";

    // WS-LAYER
    private decimal _lSeq, _lQty, _lUnit, _lValue;
    private string _lDate = "";
    private string _lRef = "";

    // WS-RECON
    private string _rcAccount = "";
    private decimal _rcPool, _rcAllow, _rcNet, _rcLedger, _rcDiff, _rcLayerDiff;
    private string _rcState = "";

    // WS-TOT
    private decimal _tGross, _tAllow, _tNet;
    private int _tPools, _tWrittenDown;

    private GnuReportWriter? _w;
    private RwLine[] _deLine = [];
    private RwLine[] _dnLine = [];
    private RwLine[] _dlLine = [];

    private async Task<int> RunAsync()
    {
        ReadArgs();
        // 0200-JOB-START: the run was registered (SelfRegistration) before the program started
        await OpenSpoolAsync();
        if (b.Rc > 4) return b.Finish(_rowsOut);
        await ProduceReportAsync();
        await ReadReconAsync();
        CloseSpool();
        return b.Finish(_rowsOut);
    }

    /// <summary>0100-READ-ARGS.</summary>
    private void ReadArgs()
    {
        var a = b.Ctx.Args;
        _co = Pic.X(a.Co, 10);
        _site = Pic.X(a.Site, 10);
        _user = Pic.X(a.User, 20);
        _mode = Pic.X(a.Mode, 10);
        if (a.Has("GROUP")) _group = Pic.X(b.Arg("GROUP"), 20);
        if (a.Has("METHOD")) _method = Pic.X(b.Arg("METHOD"), 4);
        if (a.Has("DETAIL")) _detail = Pic.X(b.Arg("DETAIL"), 10);
        if (!Pic.Blank(a.AsAt)) _asat = CstBatch.Date8(a.AsAt);
        if (Pic.Blank(_co)) _co = "STC";
        if (Pic.Blank(_user)) _user = "COST";
        if (Pic.Blank(_mode)) _mode = "ALL";
        if (Pic.Blank(_detail)) _detail = "SUMMARY";
        if (_asat == 0) _asat = CstBatch.Today();
        b.Display("STERP-I-0100 CSTINVAL valuation for " + Pic.T(_co));
    }

    /// <summary>0300-OPEN-SPOOL with 0350-FILL-HEADINGS, then INITIATE.</summary>
    private async Task OpenSpoolAsync()
    {
        if (!b.BuildSpoolPath(Job, _co)) return;
        b.FillHeading(_co, Job, "Inventory valuation and ledger reconciliation", _user);
        var d = _asat.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        b.Heading.AsAtText = Pic.Str(30, "As at ", d[6..8], "/", d[4..6], "/", d[..4]);
        b.Heading.Subtitle = Pic.Blank(_method) ? "All cost methods" : Pic.Str(60, "Cost method ", Pic.T(_method));
        b.Heading.StatusText = _mode == "EXCEPTION" ? "Written down pools only" : "All costed pools";
        await b.ReadCompanyAsync(_co);
        if (!b.OpenSpool()) return;
        _w = b.Initiate([GroupControl()], FinalFooting());
        _deLine = [DeLine()];
        _dnLine = [DnLine()];
        _dlLine = [DlLine()];
    }

    private RwControl GroupControl() => new(() => _dGroup, v => _dGroup = v, 20)
    {
        Heading =
        [
            RwLine.PlusN(2, () => new RwText().Lit(1, "Valuation group ").Put(18, _dGroup, 20).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Item").Lit(32, "Description").Lit(63, "Site").Lit(70, "Method").Lit(82, "Quantity")
                .Lit(97, "Unit cost").Lit(111, "Gross").Lit(121, "Allowance").Text),
            RwLine.Plus1(() => CstBatch.Rule(CstBatch.Rule(new RwText(), 3, 64, '-'), 67, 66, '-').Text),
        ],
        Footing =
        [
            RwLine.Plus1(() => new RwText().Lit(3, "Group total   ").Put(18, _dGroup, 20).Num(107, _btGross, "Z(9)9.99")
                .Num(120, _btAllow, "Z(7)9.99-").Text),
        ],
    };

    private RwLine DeLine() => RwLine.Plus1(() => new RwText().Put(3, _dItem, 28).Put(32, _dDesc, 30).Put(63, _dSite, 6).Put(70, _dMethod, 4)
        .Num(76, _dQty, "Z(10)9.999").Num(92, _dUnit, "Z(8)9.9999").Num(107, _dGross, "Z(9)9.99").Num(120, _dAllow, "Z(7)9.99-")
        .Lit(133, " ").Text);

    private RwLine DnLine() => RwLine.Plus1(() => new RwText().Lit(8, "net realisable value test").Lit(36, "unit NRV")
        .Num(46, _dNrvUnit, "Z(8)9.9999").Lit(63, "carried at").Num(74, _dNet, "Z(9)9.99").Put(89, _dFlag, 10).Text);

    private RwLine DlLine() => RwLine.Plus1(() => new RwText().Lit(8, "layer ").Num(14, _lSeq, "Z(5)9").Put(22, _lDate, 10).Put(34, _lRef, 24)
        .Num(76, _lQty, "Z(10)9.999").Num(92, _lUnit, "Z(8)9.9999").Num(107, _lValue, "Z(9)9.99").Text);

    private RwLine[] FinalFooting() =>
    [
        RwLine.Plus1(() => new RwText().Lit(3, "Valuation totals - gross").Num(34, _tGross, "Z(11)9.99").Lit(52, "allowance")
            .Num(62, _tAllow, "Z(11)9.99-").Lit(82, "net").Num(87, _tNet, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Pools reported:").Num(20, _tPools, "Z(6)9").Lit(32, "of which written down:")
            .Num(58, _tWrittenDown, "Z(6)9").Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "RECONCILIATION TO THE GENERAL LEDGER (CST-022)").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Inventory control account").Put(34, _rcAccount, 20).Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Cost pool carrying value").Num(34, _rcPool, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Less net realisable allowance").Num(34, _rcAllow, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Net inventory per costing").Num(34, _rcNet, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Ledger balance at cut off").Num(34, _rcLedger, "Z(11)9.99").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Difference (costing - ledger)").Num(34, _rcDiff, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "FIFO layers less pool value").Num(34, _rcLayerDiff, "Z(11)9.99-").Text),
        RwLine.Plus1(() => new RwText().Put(3, _rcState, 50).Text),
        RwLine.PlusN(2, () => new RwText().Lit(3, "Stock is carried at the lower of cost and NRV.").Text),
        RwLine.Plus1(() => new RwText().Lit(3, "Clear any difference above 0.01 before the close.").Text),
    ];

    /// <summary>1000-PRODUCE-REPORT: one row per costed pool in valuation group, item and site order (cursor 1).</summary>
    private async Task ProduceReportAsync()
    {
        const string sql =
            "SELECT ISNULL(i.ValuationGroup,'UNCLASSIFIED'), ic.ItemCode, ISNULL(i.Description,''), ic.SiteCode, ic.CostMethod, " +
            "ic.QtyOnHand, ic.AverageUnitCost, ic.CarryingValueGBP, ISNULL(ic.NrvUnitValue,0), ic.AllowanceGBP, " +
            "ISNULL(ic.StandardUnitCost,0) FROM cst.ItemCost ic LEFT JOIN inv.Item i ON i.CompanyCode = ic.CompanyCode AND " +
            "i.ItemCode = ic.ItemCode WHERE ic.CompanyCode = ? AND (? = '' OR ic.SiteCode = ?) AND (? = '' OR " +
            "ISNULL(i.ValuationGroup,'') = ?) AND (? = '' OR ic.CostMethod = ?) ORDER BY " +
            "ISNULL(i.ValuationGroup,'UNCLASSIFIED'), ic.ItemCode, ic.SiteCode";
        var rows = new List<SqlioRow>();
        var outcome = await SqlioCursor.RunAsync(b.Sql, sql,
            [
                SqlioParm.Text(_co), SqlioParm.Text(_site), SqlioParm.Text(_site), SqlioParm.Text(_group), SqlioParm.Text(_group),
                SqlioParm.Text(_method), SqlioParm.Text(_method),
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
        // a FTCH failure ends the loop like the end of the data
        foreach (var row in rows) await EmitPoolAsync(row);
    }

    /// <summary>1100-EMIT-POOL.</summary>
    private async Task EmitPoolAsync(SqlioRow row)
    {
        var newKey = Pic.X(row.Raw(0), 20);
        _dItem = Pic.X(row.Raw(1), 28);
        _dDesc = Pic.X(row.Raw(2), 30);
        _dSite = Pic.X(row.Raw(3), 6);
        _dMethod = Pic.X(row.Raw(4), 4);
        _dQty = Pic.N(row.NumVal(5), 11, 3);
        _dUnit = Pic.N(row.NumVal(6), 9, 4);
        _dGross = Pic.N(row.NumVal(7), 13, 2);
        _dNrvUnit = Pic.N(row.NumVal(8), 9, 4);
        _dAllow = Pic.N(row.NumVal(9), 13, 2);
        // a standard costed pool reports its standard, not the moving average kept for information
        if (_dMethod == "STD")
        {
            var unit1 = Pic.N(row.NumVal(10), 11, 6);
            if (unit1 != 0) _dUnit = Pic.N(unit1, 9, 4);
        }
        _dNet = Pic.N(_dGross - _dAllow, 13, 2);
        if (_dAllow > 0)
        {
            _dFlag = "WRITE DOWN";
            _tWrittenDown = Pic.I(_tWrittenDown + 1, 7);
        }
        else
        {
            _dFlag = "AT COST";
        }
        if (_mode == "EXCEPTION" && _dAllow == 0) return;
        if (!Pic.Eq(newKey, _dGroup) && !Pic.Blank(_dGroup)) CloseGroup();
        _dGroup = newKey;
        _tGross = Pic.N(_tGross + _dGross, 13, 2);
        _aGross = Pic.N(_aGross + _dGross, 13, 2);
        _tAllow = Pic.N(_tAllow + _dAllow, 13, 2);
        _aAllow = Pic.N(_aAllow + _dAllow, 13, 2);
        _tNet = Pic.N(_tNet + _dNet, 13, 2);
        _tPools = Pic.I(_tPools + 1, 7);
        _rowsOut = (long)Pic.N(_rowsOut + 1, 9, 0);
        _w!.Generate(_deLine);
        if (_dAllow > 0 || _dNrvUnit > 0) _w.Generate(_dnLine);
        if (_detail == "LAYER" && _dMethod == "FIFO") await EmitLayersAsync();
    }

    /// <summary>1150-CLOSE-GROUP: the band figures for the footing the next GENERATE prints.</summary>
    private void CloseGroup()
    {
        _btGross = _aGross;
        _btAllow = _aAllow;
        _aGross = 0;
        _aAllow = 0;
    }

    /// <summary>1200-EMIT-LAYERS: the open FIFO layers of the item (cursor 3), by receipt date and layer sequence.</summary>
    private async Task EmitLayersAsync()
    {
        const string sql =
            "SELECT cl.LayerSeq, CONVERT(varchar(10), cl.ReceiptDate, 23), ISNULL(cl.SourceDocRef,''), cl.QtyRemaining, " +
            "cl.UnitCost, cl.ValueRemainingGBP FROM cst.CostLayer cl WHERE cl.CompanyCode = ? AND cl.ItemCode = ? AND " +
            "cl.IsClosed = 0 AND cl.QtyRemaining > 0 ORDER BY cl.ReceiptDate, cl.LayerSeq";
        var rows = new List<SqlioRow>();
        await SqlioCursor.RunAsync(b.Sql, sql, [SqlioParm.Text(_co), SqlioParm.Text(_dItem)], 6, row =>
        {
            rows.Add(row);
            return Task.FromResult(true);
        }, b.Ct);
        // an EXEC or FTCH failure ends the layers without a message
        foreach (var row in rows)
        {
            _lSeq = Pic.N(row.NumVal(0), 6, 0);
            _lDate = Pic.X(row.Raw(1), 10);
            _lRef = Pic.X(row.Raw(2), 24);
            _lQty = Pic.N(row.NumVal(3), 11, 3);
            _lUnit = Pic.N(row.NumVal(4), 9, 4);
            _lValue = Pic.N(row.NumVal(5), 13, 2);
            _layersOut = (long)Pic.N(_layersOut + 1, 9, 0);
            _w!.Generate(_dlLine);
        }
    }

    /// <summary>1900-READ-RECON: cst.usp_ValuationRecon at the as-at date (SNGL, cursor 4).</summary>
    private async Task ReadReconAsync()
    {
        var d = _asat.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        var dateText = Pic.Str(10, d[..4], "-", d[4..6], "-", d[6..8]);
        SqlioRow? row = null;
        try
        {
            row = await b.Sql.SingleAsync("EXEC cst.usp_ValuationRecon ?, ?, NULL", [SqlioParm.Text(_co), SqlioParm.Text(dateText)],
                CstBatch.Widths[..11], b.Ct);
        }
        catch (SqlioException)
        {
            // SQLIO-ERROR: not SQLIO-OK
        }
        if (row is not { Found: true })
        {
            _rcState = "RECONCILIATION NOT AVAILABLE";
            b.Rc = 4;
            b.Display("STERP-W-1901 reconciliation unavailable");
            return;
        }
        _rcAccount = Pic.X(row.Raw(2), 20);
        _rcPool = Pic.N(row.NumVal(3), 13, 2);
        _rcAllow = Pic.N(row.NumVal(4), 13, 2);
        _rcNet = Pic.N(row.NumVal(5), 13, 2);
        _ = Pic.N(row.NumVal(6), 13, 2); // RC-LAYER (not printed)
        _rcLedger = Pic.N(row.NumVal(7), 13, 2);
        _rcDiff = Pic.N(row.NumVal(8), 13, 2);
        _rcLayerDiff = Pic.N(row.NumVal(10), 13, 2);
        if (row.Raw(9).StartsWith('Y'))
        {
            _rcState = "RECONCILED - costing agrees to the ledger";
        }
        else
        {
            _rcState = "*** NOT RECONCILED - INVESTIGATE BEFORE CLOSE";
            if (b.Rc < 4) b.Rc = 4;
        }
    }

    /// <summary>0400-CLOSE-SPOOL.</summary>
    private void CloseSpool()
    {
        CloseGroup();
        _w?.Terminate();
        b.CloseSpool();
        b.Display("STERP-I-0401 " + CstBatch.Disp(_rowsOut, 9) + " pool(s), " + CstBatch.Disp(_layersOut, 9) + " layer line(s)");
        if (_rcState.StartsWith("***", StringComparison.Ordinal))
        {
            b.Display("STERP-W-0402 valuation does not agree to the inventory control account");
        }
        b.AnnounceSpool();
    }
}
