using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// BUDGRPT (cobol/batch/fin/BUDGRPT.cob; FR-STERP-FIN-054): budget version against posted actuals per budgeted account and
/// cost centre with an optional comparison version, variance sense by class (TURN/OTHINC reversed) and a +/-0.004 band.
/// BUDG is mandatory (RC 8); an unknown budget version ends RC 16 after JOBLOG START. RD: CONTROLS FINAL D-GROUP, PAGE
/// LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 52, FOOTING 58.
/// </summary>
internal sealed class BudgrptJob(FinBatch b)
{
    private int _fy;
    private int _perFrom;
    private int _perTo;
    private string _budg = "";
    private int _ver = 1;
    private string _cmp = "";
    private int _cmpVer = 1;
    private string _budgName = "";
    private string _statusText = "";

    private string _dGroup = "";
    private string _dAcct = "";
    private string _dName = "";
    private string _dCc = "";
    private string _dSense = "";
    private decimal _dBudget, _dActual, _dVar, _dCompare, _dCmpVar;
    private decimal _tBudget, _tActual, _tVar, _tCompare, _tCmpVar;
    private long _tAdverse;

    public static async Task<int> RunAsync(FinBatch b) => await new BudgrptJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        await ReadArgsAsync();
        if (b.Rc > 4) return b.End();
        await ReadBudgetAsync();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("BUDGRPT")) return b.End();
        var w = Writer();
        w.Initiate();
        await ProduceAsync(w);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>0100-READ-ARGS: FY from ASAT when absent, periods 1..12 by default, BUDG required.</summary>
    private async Task ReadArgsAsync()
    {
        var t = b.Arg("FY");
        if (!CobolText.IsSpaces(t)) _fy = Int(t, 4);
        _budg = CobolText.Trimmed(b.Arg("BUDG"), 20);
        t = b.Arg("VER");
        if (!CobolText.IsSpaces(t)) _ver = Int(t, 4);
        _cmp = CobolText.Trimmed(b.Arg("CMP"), 20);
        t = b.Arg("CMPVER");
        if (!CobolText.IsSpaces(t)) _cmpVer = Int(t, 4);
        t = b.Arg("FROM");
        if (!CobolText.IsSpaces(t)) _perFrom = Int(t, 2);
        t = b.Arg("TO");
        if (!CobolText.IsSpaces(t)) _perTo = Int(t, 2);
        if (_fy == 0) _fy = (await b.ResolvePeriodAsync()).FiscalYear;
        if (_perFrom == 0) _perFrom = 1;
        if (_perTo == 0) _perTo = 12;
        if (CobolText.IsSpaces(_budg))
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 BUDG=<budget code> is required");
            return;
        }
        b.Display("STERP-I-0100 BUDGRPT budget comparison for " + b.Co.Trim());
    }

    /// <summary>0200-READ-BUDGET.</summary>
    private async Task ReadBudgetAsync()
    {
        object?[]? row = null;
        try
        {
            row = await b.Sql.SingleAsync("SELECT Name, Status FROM fin.Budget WHERE CompanyCode = @p1 AND BudgetCode = @p2 AND " +
                "FiscalYear = @p3 AND Version = @p4", b.Co, _budg, Disp.U(_fy, 6), Disp.U(_ver, 6));
        }
        catch (SqlioException)
        {
            row = null;
        }
        if (row is null)
        {
            b.Rc = 16;
            b.Display("STERP-E-0201 budget version not found: " + _budg.Trim());
            return;
        }
        _budgName = CobolText.Trimmed(Slot.Text(row[0], 120), 80);
        var status = CobolText.Trimmed(Slot.Text(row[1], 120), 10);
        _statusText = CobolText.Trimmed("Version status " + status.Trim(), 20);
    }

    private GnuReportWriter Writer()
    {
        var group = new RwControl(() => _dGroup, v => _dGroup = v, 30)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Put(1, _dGroup, 30).Text),
                RwLine.Plus1(() => new RwText().Put(1, "Account", 7).Put(14, "Name", 4).Put(46, "Cost centre", 11).Put(62, "Budget", 6)
                    .Put(78, "Actual", 6).Put(93, "Variance", 8).Put(104, "Sense", 5).Put(118, "Comparison", 10).Text),
                RwLine.Plus1(() => new string('-', 132)),
            ],
        };
        return b.Report(60, 1, 9, 52, [group],
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Put(1, "Report total  ", 14).Num(60, _tBudget, Big).Num(76, _tActual, Big)
                .Num(90, _tVar, "ZZZ,ZZ9.99-").Num(116, _tCompare, Big).Text),
            RwLine.PlusN(2, () => new RwText().Put(1, "Adverse variance lines: ", 24).Num(25, _tAdverse, "ZZZ,ZZ9")
                .Put(40, "Variance versus forecast", 24).Num(66, _tCmpVar, Big).Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "Adverse means worse than budget for the class.", 66).Put(70, _statusText, 60).Text),
        ]);
    }

    /// <summary>1000-PRODUCE / 1100-BUILD-SQL.</summary>
    private async Task ProduceAsync(GnuReportWriter w)
    {
        b.Heading.Title = "Budget against actual";
        b.Heading.Subtitle = CobolText.Trimmed(_budg.Trim() + " v" + Disp.U(_ver, 6) + " " + _budgName.Trim(), 60);
        b.Heading.AsAtText = CobolText.IsSpaces(_cmp)
            ? "No comparison version"
            : CobolText.Trimmed("Compared with " + _cmp.Trim() + " v" + Disp.U(_cmpVer, 6), 30);
        const string sql =
            "SELECT a.AccountCode, a.Name, k.Name, a.ClassCode, ISNULL(b.DimCostCentre,'*'), CONVERT(varchar(24),ISNULL(SUM(b.Amount),0)), " +
            "CONVERT(varchar(24),ISNULL(MAX(t.Net),0)), CONVERT(varchar(24),ISNULL(MAX(c.Amt),0)) FROM fin.BudgetLine b JOIN fin.Account a " +
            "ON a.CompanyCode = b.CompanyCode AND a.AccountCode = b.AccountCode JOIN fin.AccountClass k ON k.ClassCode = a.ClassCode " +
            "OUTER APPLY (SELECT SUM(l.DebitAmountGBP - l.CreditAmountGBP) Net FROM fin.JournalLine l JOIN fin.Journal j ON " +
            "j.JournalId = l.JournalId WHERE l.CompanyCode = b.CompanyCode AND l.AccountCode = b.AccountCode AND j.Status = 'POSTED' " +
            "AND j.FiscalYear = b.FiscalYear AND j.PeriodNo BETWEEN @p1 AND @p2) t OUTER APPLY (SELECT SUM(c2.Amount) Amt FROM " +
            "fin.BudgetLine c2 WHERE c2.CompanyCode = b.CompanyCode AND c2.BudgetCode = @p3 AND c2.Version = @p4 AND c2.FiscalYear = " +
            "b.FiscalYear AND c2.AccountCode = b.AccountCode AND c2.PeriodNo BETWEEN @p5 AND @p6) c WHERE b.CompanyCode = @p7 AND " +
            "b.BudgetCode = @p8 AND b.FiscalYear = @p9 AND b.Version = @p10 AND b.PeriodNo BETWEEN @p11 AND @p12 GROUP BY " +
            "a.AccountCode, a.Name, k.Name, a.ClassCode, b.DimCostCentre ORDER BY k.Name, a.AccountCode";
        var from = Disp.U(_perFrom, 6);
        var to = Disp.U(_perTo, 6);
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, from, to, _cmp, Disp.U(_cmpVer, 6), from, to, b.Co, _budg, Disp.U(_fy, 6),
                Disp.U(_ver, 6), from, to);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        var detail = RwLine.Plus1(() => new RwText().Put(1, _dAcct, 12).Put(14, _dName, 30).Put(45, _dCc, 14).Num(60, _dBudget, Big)
            .Num(76, _dActual, Big).Num(90, _dVar, "ZZZ,ZZ9.99-").Put(103, _dSense, 12).Num(116, _dCompare, Big).Text);
        foreach (var r in rows) OneLine(w, r, detail);
        b.Display("STERP-I-1002 " + Disp.S(b.RowsOut) + " comparison line(s)");
    }

    /// <summary>1200-ONE-LINE.</summary>
    private void OneLine(GnuReportWriter w, object?[] r, RwLine detail)
    {
        _dAcct = CobolText.Trimmed(Slot.Text(r[0], 120), 12);
        _dName = CobolText.Trimmed(Slot.Text(r[1], 120), 40);
        _dGroup = CobolText.Trimmed(Slot.Text(r[2], 120), 30);
        var cls = CobolText.Trimmed(Slot.Text(r[3], 120), 10);
        _dCc = CobolText.Trimmed(Slot.Text(r[4], 120), 20);
        _dBudget = M(Slot.Num(r[5]));
        _dActual = M(Slot.Num(r[6]));
        _dCompare = M(Slot.Num(r[7]));
        if (cls == "TURN" || cls == "OTHINC")
        {
            _dActual = M(0 - _dActual);
            _dVar = M(_dActual - _dBudget);
        }
        else
        {
            _dVar = M(_dBudget - _dActual);
        }
        _dCmpVar = M(_dCompare - _dActual);
        if (_dVar < -0.004m)
        {
            _dSense = "ADVERSE";
            _tAdverse = (_tAdverse + 1) % 100_000;
        }
        else if (_dVar > 0.004m)
        {
            _dSense = "FAVOURABLE";
        }
        else
        {
            _dSense = "ON BUDGET";
        }
        _tBudget = M(_tBudget + _dBudget);
        _tActual = M(_tActual + _dActual);
        _tVar = M(_tVar + _dVar);
        _tCompare = M(_tCompare + _dCompare);
        _tCmpVar = M(_tCmpVar + _dCmpVar);
        b.RowsOut++;
        w.Generate(detail);
    }

    private const string Big = "ZZZ,ZZZ,ZZ9.99-";

    private static int Int(string t, int digits) => (int)CobolNumeric.Store(Slot.NumVal(t), digits, 0, signed: false);

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
