using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// PLRPT (cobol/batch/fin/PLRPT.cob; FR-STERP-FIN-051): FRS 102 format 1 profit and loss account of posted journals for a
/// fiscal year and period range with the comparative year; accounts without a statutory line are listed on an extra line
/// and end the run RC 4. RD: CONTROLS FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 8, LAST DETAIL 54, FOOTING 58.
/// </summary>
internal sealed class PlrptJob(FinBatch b)
{
    private int _fy;
    private int _perFrom;
    private int _perTo;
    private string _layout = "STAT";

    private string _caption = "";
    private string _detail = "";
    private decimal _this, _prior, _var;

    private decimal _sTurnover, _sCos, _sDist, _sAdmin, _sOther, _sFinance, _sTax, _sVari, _sUnmapped, _amt3;
    private decimal _pTurnover, _pCos, _pDist, _pAdmin, _pOther, _pFinance, _pTax, _pVari;
    private long _unmappedCount;

    public static async Task<int> RunAsync(FinBatch b) => await new PlrptJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        await ReadArgsAsync();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("PLRPT")) return b.End();
        var w = b.Report(60, 1, 8, 54, null,
        [
            RwLine.PlusN(2, () => new RwText().Put(3, "Prepared under FRS 102, format 1.  Sterling.", 62)
                .Put(66, "Only POSTED journals are included.", 62).Text),
        ]);
        w.Initiate();
        await GatherAsync();
        Print(w);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    private async Task ReadArgsAsync()
    {
        var t = b.Arg("FY");
        if (!CobolText.IsSpaces(t)) _fy = (int)CobolNumeric.Store(Slot.NumVal(t), 4, 0, signed: false);
        t = b.Arg("FROM");
        if (!CobolText.IsSpaces(t)) _perFrom = (int)CobolNumeric.Store(Slot.NumVal(t), 2, 0, signed: false);
        t = b.Arg("TO");
        if (!CobolText.IsSpaces(t)) _perTo = (int)CobolNumeric.Store(Slot.NumVal(t), 2, 0, signed: false);
        if (CobolText.Fit(b.Arg("LAYOUT"), 4) == "MGMT") _layout = "MGMT";
        if (_fy == 0 || _perTo == 0)
        {
            var (fy, per) = await b.ResolvePeriodAsync();
            _fy = fy;
            if (_fy == 0)
            {
                b.Rc = 8;
                b.Display("STERP-E-0101 no fiscal period covers " + Disp.U(b.AsAt, 8));
                return;
            }
            if (_perTo == 0) _perTo = per;
        }
        if (_perFrom == 0) _perFrom = 1;
        b.Display("STERP-I-0100 PLRPT profit and loss for " + b.Co.Trim());
    }

    /// <summary>1000-GATHER: this year and the comparative year per P&amp;L posting account.</summary>
    private async Task GatherAsync()
    {
        const string sql =
            "SELECT a.ClassCode, ISNULL(a.StatutoryLineCode,''), CONVERT(varchar(24),ISNULL(t.Dr,0)-ISNULL(t.Cr,0)), " +
            "CONVERT(varchar(24),ISNULL(p.Dr,0)-ISNULL(p.Cr,0)), a.AccountCode FROM fin.Account a JOIN fin.AccountClass k ON " +
            "k.ClassCode = a.ClassCode OUTER APPLY (SELECT SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) Cr FROM fin.JournalLine l " +
            "JOIN fin.Journal j ON j.JournalId = l.JournalId WHERE l.CompanyCode = a.CompanyCode AND l.AccountCode = a.AccountCode " +
            "AND j.Status = 'POSTED' AND j.FiscalYear = @p1 AND j.PeriodNo BETWEEN @p2 AND @p3) t OUTER APPLY (SELECT " +
            "SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) Cr FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = " +
            "l.JournalId WHERE l.CompanyCode = a.CompanyCode AND l.AccountCode = a.AccountCode AND j.Status = 'POSTED' AND " +
            "j.FiscalYear = @p4 AND j.PeriodNo BETWEEN @p5 AND @p6) p WHERE a.CompanyCode = @p7 AND k.Statement = 'PL' AND " +
            "a.AccountKind <> 'SUMMARY'";
        var from = Disp.U(_perFrom, 6);
        var to = Disp.U(_perTo, 6);
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, Disp.U(_fy, 6), from, to, Disp.U(_fy - 1, 6), from, to, b.Co);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        foreach (var r in rows) Accumulate(r);
    }

    /// <summary>1100-ACCUMULATE: TURN and OTHINC sign-reversed, eight class literals, other classes counted as skipped.</summary>
    private void Accumulate(object?[] r)
    {
        var amt = M(Slot.Num(r[2]));
        var amt2 = M(Slot.Num(r[3]));
        b.RowsOut++;
        if (CobolText.IsSpaces(Slot.Text(r[1], 120)))
        {
            _sUnmapped = M(_sUnmapped + amt);
            _unmappedCount = (_unmappedCount + 1) % 100_000;
        }
        switch (Slot.Text(r[0], 120))
        {
            case "TURN":
                _sTurnover = M(_sTurnover - amt);
                _pTurnover = M(_pTurnover - amt2);
                break;
            case "COS":
                _sCos = M(_sCos + amt);
                _pCos = M(_pCos + amt2);
                break;
            case "VARI":
                _sVari = M(_sVari + amt);
                _pVari = M(_pVari + amt2);
                break;
            case "DIST":
                _sDist = M(_sDist + amt);
                _pDist = M(_pDist + amt2);
                break;
            case "ADMIN":
                _sAdmin = M(_sAdmin + amt);
                _pAdmin = M(_pAdmin + amt2);
                break;
            case "OTHINC":
                _sOther = M(_sOther - amt);
                _pOther = M(_pOther - amt2);
                break;
            case "FINC":
                _sFinance = M(_sFinance + amt);
                _pFinance = M(_pFinance + amt2);
                break;
            case "TAXPL":
                _sTax = M(_sTax + amt);
                _pTax = M(_pTax + amt2);
                break;
            default:
                b.Skipped++;
                break;
        }
    }

    /// <summary>2000-PRINT.</summary>
    private void Print(GnuReportWriter w)
    {
        b.Heading.Title = "Profit and loss account";
        var subtitle = new RwText().Put(1, "Fiscal year " + Disp.U(_fy, 6) + ", periods ", 60)
            .Put(31, Disp.U(_perFrom, 6), 3).Put(35, "to", 2).Put(38, Disp.U(_perTo, 6), 3);
        b.Heading.Subtitle = subtitle.Text;
        b.Heading.AsAtText = _layout == "MGMT" ? "Management layout" : "FRS 102 statutory format";

        var deLine = RwLine.Plus1(() => new RwText().Put(3, _caption, 50).Num(60, _this, Ed).Num(82, _prior, Ed).Num(104, _var, Ed).Text);
        var deSub = new[]
        {
            RwLine.Plus1(() => new RwText().Put(1, _caption, 50).Put(58, "  ", 2).Num(60, _this, Ed).Num(82, _prior, Ed).Num(104, _var, Ed).Text),
            RwLine.Plus1(() => new RwText().Put(60, new string('-', 60), 60).Text),
        };

        _caption = "Caption";
        _detail = "This period   Comparative      Movement";
        w.Generate(RwLine.Plus1(() => new RwText().Put(1, _caption, 50).Put(56, _detail, 50).Text));
        w.Generate(RwLine.Plus1(() => new string('-', 132)));

        Emit(w, deLine, "Turnover", _sTurnover, _pTurnover);
        Emit(w, deLine, "Cost of sales", M(0 - _sCos), M(0 - _pCos));
        if (_sVari != 0 || _pVari != 0) Emit(w, deLine, "Manufacturing variances", M(0 - _sVari), M(0 - _pVari));
        _amt3 = M(_sTurnover - _sCos - _sVari);
        Emit(w, deSub, "Gross profit", _amt3, M(_pTurnover - _pCos - _pVari));
        Emit(w, deLine, "Distribution costs", M(0 - _sDist), M(0 - _pDist));
        Emit(w, deLine, "Administrative expenses", M(0 - _sAdmin), M(0 - _pAdmin));
        Emit(w, deLine, "Other operating income", _sOther, _pOther);
        Emit(w, deSub, "Operating profit", M(_amt3 - _sDist - _sAdmin + _sOther),
            M(_pTurnover - _pCos - _pVari - _pDist - _pAdmin + _pOther));
        Emit(w, deLine, "Interest payable and similar charges", M(0 - _sFinance), M(0 - _pFinance));
        Emit(w, deSub, "Profit before taxation", M(_amt3 - _sDist - _sAdmin + _sOther - _sFinance),
            M(_pTurnover - _pCos - _pVari - _pDist - _pAdmin + _pOther - _pFinance));
        Emit(w, deLine, "Tax on profit", M(0 - _sTax), M(0 - _pTax));
        Emit(w, deSub, "Profit for the financial period", M(_amt3 - _sDist - _sAdmin + _sOther - _sFinance - _sTax),
            M(_pTurnover - _pCos - _pVari - _pDist - _pAdmin + _pOther - _pFinance - _pTax));
        Unmapped(w, deLine);
    }

    private void Emit(GnuReportWriter w, RwLine line, string caption, decimal thisYear, decimal prior) => Emit(w, [line], caption, thisYear, prior);

    /// <summary>2900-EMIT / 2910-EMIT-SUB: movement = this period - comparative.</summary>
    private void Emit(GnuReportWriter w, RwLine[] group, string caption, decimal thisYear, decimal prior)
    {
        _caption = caption;
        _this = thisYear;
        _prior = prior;
        _var = M(_this - _prior);
        w.Generate(group);
    }

    /// <summary>2950-UNMAPPED: a blank line, the unmapped total and RC 4.</summary>
    private void Unmapped(GnuReportWriter w, RwLine deLine)
    {
        if (_unmappedCount == 0) return;
        w.Generate(RwLine.Plus1(() => ""));
        Emit(w, deLine, "Unmapped posting accounts (statutory line missing)", _sUnmapped, 0);
        b.Rc = 4;
        b.Display("STERP-W-2951 " + Disp.S(_unmappedCount, 5) + " account(s) have no statutory line");
    }

    private const string Ed = "Z,ZZZ,ZZZ,ZZ9.99-";

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
