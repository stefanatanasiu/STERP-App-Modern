using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// BSRPT (cobol/batch/fin/BSRPT.cob; FR-STERP-FIN-052): FRS 102 balance sheet as at ASAT - balance-sheet accounts cumulative
/// to the date, P&amp;L accounts for the fiscal year of the date as the unappropriated result; net assets less funds must be
/// nil (else "DOES NOT BALANCE" and RC 8). RD: CONTROLS FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 8, LAST DETAIL 54.
/// </summary>
internal sealed class BsrptJob(FinBatch b)
{
    private int _fy;
    private string _caption = "";
    private decimal _this, _subtot;
    private decimal _fa, _fadep, _stock, _debt, _cash, _cred, _taxbs, _credlt, _eq, _pl, _other;
    private decimal _diff;
    private string _status = "";

    public static async Task<int> RunAsync(FinBatch b) => await new BsrptJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        var (fy, _) = await b.ResolvePeriodAsync();
        _fy = fy;
        if (_fy == 0)
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 no fiscal period covers " + Disp.U(b.AsAt, 8));
            return b.End();
        }
        b.Display("STERP-I-0100 BSRPT balance sheet for " + b.Co.Trim());
        if (!await b.OpenSpoolAsync("BSRPT")) return b.End();
        var w = b.Report(60, 1, 8, 54, null,
        [
            RwLine.PlusN(2, () => new RwText().Put(1, "Net assets less total funds (must be nil) ", 44).Num(46, _diff, Ed).Put(70, _status, 20).Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "Prepared under FRS 102.  Amounts in sterling.", 64)
                .Put(68, "Liabilities are shown as negative amounts.", 62).Text),
        ]);
        w.Initiate();
        await GatherAsync();
        Print(w);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>1000-GATHER.</summary>
    private async Task GatherAsync()
    {
        const string sql =
            "SELECT a.ClassCode, k.Statement, CONVERT(varchar(24),ISNULL(t.Dr,0)-ISNULL(t.Cr,0)) FROM fin.Account a JOIN " +
            "fin.AccountClass k ON k.ClassCode = a.ClassCode OUTER APPLY (SELECT SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) " +
            "Cr FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId WHERE l.CompanyCode = a.CompanyCode AND " +
            "l.AccountCode = a.AccountCode AND j.Status = 'POSTED' AND j.PostingDate <= @p1 AND (k.Statement = 'BS' OR " +
            "j.FiscalYear = @p2)) t WHERE a.CompanyCode = @p3 AND a.AccountKind <> 'SUMMARY'";
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, Disp.U(b.AsAt, 8), Disp.U(_fy, 6), b.Co);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        foreach (var r in rows) Accumulate(r);
    }

    /// <summary>1100-ACCUMULATE.</summary>
    private void Accumulate(object?[] r)
    {
        var amt = M(Slot.Num(r[2]));
        b.RowsOut++;
        if (Slot.Text(r[1], 120) == "PL")
        {
            _pl = M(_pl + amt);
            return;
        }
        switch (Slot.Text(r[0], 120))
        {
            case "FA": _fa = M(_fa + amt); break;
            case "FADEP": _fadep = M(_fadep + amt); break;
            case "STOCK": _stock = M(_stock + amt); break;
            case "DEBT": _debt = M(_debt + amt); break;
            case "CASH": _cash = M(_cash + amt); break;
            case "CRED": _cred = M(_cred + amt); break;
            case "TAXBS": _taxbs = M(_taxbs + amt); break;
            case "CREDLT": _credlt = M(_credlt + amt); break;
            case "EQ": _eq = M(_eq + amt); break;
            default: _other = M(_other + amt); break;
        }
    }

    /// <summary>2000-PRINT and 2500-RESERVES.</summary>
    private void Print(GnuReportWriter w)
    {
        b.Heading.Title = "Balance sheet";
        b.Heading.AsAtText = "As at " + FinBatch.UkDate(b.AsAt);
        b.Heading.Subtitle = "FRS 102 statutory format, sterling";

        var head = RwLine.PlusN(2, () => new RwText().Put(1, _caption, 50).Text);
        var line = RwLine.Plus1(() => new RwText().Put(5, _caption, 50).Num(70, _this, Ed).Text);
        var sub = RwLine.Plus1(() => new RwText().Put(3, _caption, 50).Num(92, _subtot, Ed).Text);
        RwLine[] tot =
        [
            RwLine.Plus1(() => new RwText().Put(92, new string('-', 20), 20).Text),
            RwLine.Plus1(() => new RwText().Put(1, _caption, 50).Num(92, _subtot, Ed).Text),
            RwLine.Plus1(() => new RwText().Put(92, new string('=', 20), 20).Text),
        ];

        Gen(w, head, "Fixed assets");
        Gen(w, line, "Tangible assets at cost", _fa);
        Gen(w, line, "Accumulated depreciation", _fadep);
        var fixedAssets = M(_fa + _fadep);
        GenSub(w, [sub], "Total fixed assets", fixedAssets);
        Gen(w, head, "Current assets");
        Gen(w, line, "Stocks", _stock);
        Gen(w, line, "Debtors", _debt);
        Gen(w, line, "Cash at bank and in hand", _cash);
        var current = M(_stock + _debt + _cash);
        GenSub(w, [sub], "Total current assets", current);
        Gen(w, head, "Creditors: amounts falling due within one year");
        Gen(w, line, "Trade and other creditors", _cred);
        Gen(w, line, "Taxation and social security", _taxbs);
        var creditors = M(_cred + _taxbs);
        GenSub(w, [sub], "Total creditors within one year", creditors);
        var netCurrent = M(current + creditors);
        GenSub(w, [sub], "Net current assets", netCurrent);
        var talcl = M(fixedAssets + netCurrent);
        GenSub(w, [sub], "Total assets less current liabilities", talcl);
        GenSub(w, [sub], "Creditors: amounts falling due after one year", _credlt);
        var netAssets = M(talcl + _credlt + _other);
        GenSub(w, tot, "Net assets", netAssets);

        Gen(w, head, "Capital and reserves");
        Gen(w, line, "Called up share capital and reserves brought forward", M(0 - _eq));
        Gen(w, line, "Profit for the financial year, not yet transferred", M(0 - _pl));
        var funds = M(0 - _eq - _pl);
        GenSub(w, tot, "Total shareholders funds", funds);
        _diff = M(netAssets - funds);
        if (_diff > 0.004m || _diff < -0.004m)
        {
            _status = "DOES NOT BALANCE";
            b.Rc = 8;
            b.Display("STERP-E-2501 balance sheet does not balance");
        }
        else
        {
            _status = "BALANCED";
        }
        b.Display("STERP-I-2502 " + Disp.S(b.RowsOut) + " account(s) read");
    }

    private void Gen(GnuReportWriter w, RwLine line, string caption, decimal? amount = null)
    {
        _caption = CobolText.Trimmed(caption, 50);
        if (amount is { } a) _this = a;
        w.Generate(line);
    }

    private void GenSub(GnuReportWriter w, RwLine[] group, string caption, decimal amount)
    {
        _caption = CobolText.Trimmed(caption, 50);
        _subtot = amount;
        w.Generate(group);
    }

    private const string Ed = "Z,ZZZ,ZZZ,ZZ9.99-";

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
