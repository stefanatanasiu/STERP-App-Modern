using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// TBRPT (cobol/batch/fin/TBRPT.cob; FR-STERP-FIN-050): trial balance of posted journals for a fiscal year and period range,
/// by account class, with the balance proof. Report Writer RD: CONTROLS FINAL D-CLASS, PAGE LIMIT 60, HEADING 1, FIRST
/// DETAIL 9, LAST DETAIL 52, FOOTING 58. The class accumulators are cleared before the GENERATE that breaks the class, so
/// every class footing but the last presents the next class's first account (TBRPT.cob:321-324, kept deliberately; SME-077).
/// </summary>
internal sealed class TbrptJob(FinBatch b)
{
    private int _fy;
    private int _perFrom;
    private int _perTo;
    private bool _showZero;
    private bool _first = true;

    // WS-DET
    private string _dClass = "";
    private string _dClassName = "";
    private string _dAcct = "";
    private string _dName = "";
    private decimal _dBf, _dDr, _dCr, _dCfDr, _dCfCr;

    // WS-CLS
    private decimal _cDr, _cCr, _cCfDr, _cCfCr;
    private long _cAccts;

    // WS-TOT
    private decimal _tBf, _tDr, _tCr, _tCfDr, _tCfCr, _tDiff;
    private string _statusText = "";

    public static async Task<int> RunAsync(FinBatch b) => await new TbrptJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        await ReadArgsAsync();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("TBRPT")) return b.End();

        var w = Writer();
        w.Initiate();
        await ProduceAsync(w);
        Prove();
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    private async Task ReadArgsAsync()
    {
        var t = b.Arg("FY");
        if (!CobolText.IsSpaces(t)) _fy = Num4(t);
        t = b.Arg("FROM");
        if (!CobolText.IsSpaces(t)) _perFrom = Num2(t);
        t = b.Arg("TO");
        if (!CobolText.IsSpaces(t)) _perTo = Num2(t);
        if (b.Arg("ZERO").StartsWith('Y')) _showZero = true;
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
        if (_perFrom > _perTo)
        {
            b.Rc = 8;
            b.Display("STERP-E-0102 FROM period is after TO period");
            return;
        }
        b.Display("STERP-I-0100 TBRPT trial balance for " + b.Co.Trim());
    }

    private GnuReportWriter Writer()
    {
        var dash = new string('-', 132);
        var classControl = new RwControl(() => _dClass, v => _dClass = v, 10)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Put(1, "Class ", 6).Put(7, _dClass, 10).Put(18, _dClassName, 60).Text),
                RwLine.Plus1(() => new RwText().Put(1, "Account", 7).Put(14, "Name", 4).Put(58, "Brought forward ", 16)
                    .Put(76, "Period debits ", 14).Put(92, "Period credits ", 15).Put(110, "Dr", 2).Put(124, "Cr", 2).Text),
                RwLine.Plus1(() => dash),
            ],
            Footing =
            [
                RwLine.Plus1(() => dash),
                RwLine.Plus1(() => new RwText().Put(1, "Class total   ", 14).Put(16, _dClass, 10).Num(28, _cAccts, "ZZZ,ZZ9")
                    .Put(36, "accounts", 8).Num(74, _cDr, "Z,ZZZ,ZZZ,ZZ9.99").Num(90, _cCr, "Z,ZZZ,ZZZ,ZZ9.99")
                    .Num(106, _cCfDr, "ZZZ,ZZZ,ZZ9.99").Num(120, _cCfCr, "ZZZ,ZZZ,ZZ9.99").Text),
            ],
        };
        var equals = new string('=', 132);
        RwLine[] final =
        [
            RwLine.PlusN(2, () => equals),
            RwLine.Plus1(() => new RwText().Put(1, "Trial balance total ", 20).Num(74, _tDr, "Z,ZZZ,ZZZ,ZZ9.99")
                .Num(90, _tCr, "Z,ZZZ,ZZZ,ZZ9.99").Num(106, _tCfDr, "ZZZ,ZZZ,ZZ9.99").Num(120, _tCfCr, "ZZZ,ZZZ,ZZ9.99").Text),
            RwLine.PlusN(2, () => new RwText().Put(1, "Debits less credits carried down ", 34).Num(36, _tDiff, "Z,ZZZ,ZZZ,ZZ9.99-")
                .Put(56, _statusText, 20).Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "Balances are stated in sterling at the posted", 64)
                .Put(68, "Only POSTED journals are included.", 62).Text),
        ];
        return b.Report(60, 1, 9, 52, [classControl], final);
    }

    private RwLine DetailLine() => RwLine.Plus1(() => new RwText().Put(1, _dAcct, 12).Put(14, _dName, 40)
        .Num(56, _dBf, "Z,ZZZ,ZZZ,ZZ9.99-").Num(74, _dDr, "Z,ZZZ,ZZZ,ZZ9.99").Num(90, _dCr, "Z,ZZZ,ZZZ,ZZ9.99")
        .Num(106, _dCfDr, "ZZZ,ZZZ,ZZ9.99").Num(120, _dCfCr, "ZZZ,ZZZ,ZZ9.99").Text);

    /// <summary>1000-PRODUCE / 1100-BUILD-SQL: one row per non-summary account with b/f and the period movement.</summary>
    private async Task ProduceAsync(GnuReportWriter w)
    {
        const string sql =
            "SELECT a.AccountCode, a.Name, a.ClassCode, k.Name, ISNULL(a.StatutoryLineCode,''), " +
            "CONVERT(varchar(24),ISNULL(b.Dr,0)-ISNULL(b.Cr,0)), CONVERT(varchar(24),ISNULL(m.Dr,0)), " +
            "CONVERT(varchar(24),ISNULL(m.Cr,0)) FROM fin.Account a JOIN fin.AccountClass k ON k.ClassCode = a.ClassCode " +
            "OUTER APPLY (SELECT SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) Cr FROM fin.JournalLine l JOIN fin.Journal j ON " +
            "j.JournalId = l.JournalId WHERE l.CompanyCode = a.CompanyCode AND l.AccountCode = a.AccountCode AND j.Status = 'POSTED' " +
            "AND j.FiscalYear = @p1 AND j.PeriodNo < @p2) b OUTER APPLY (SELECT SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) Cr " +
            "FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId WHERE l.CompanyCode = a.CompanyCode AND " +
            "l.AccountCode = a.AccountCode AND j.Status = 'POSTED' AND j.FiscalYear = @p3 AND j.PeriodNo BETWEEN @p4 AND @p5) m " +
            "WHERE a.CompanyCode = @p6 AND a.AccountKind <> 'SUMMARY' ORDER BY k.SortOrder, a.ClassCode, a.AccountCode";
        var fy = Disp.U(_fy, 6);
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, fy, Disp.U(_perFrom, 6), fy, Disp.U(_perFrom, 6), Disp.U(_perTo, 6), b.Co);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        var detail = DetailLine();
        foreach (var r in rows) OneAccount(w, r, detail);
    }

    /// <summary>1200-ONE-ACCOUNT.</summary>
    private void OneAccount(GnuReportWriter w, object?[] r, RwLine detail)
    {
        _dBf = Amount(r[5]);
        _dDr = Amount(r[6]);
        _dCr = Amount(r[7]);
        var amt = CobolNumeric.Store(_dBf + _dDr - _dCr, 13, 2);
        if (!_showZero && _dBf == 0 && _dDr == 0 && _dCr == 0)
        {
            b.Skipped++;
            return;
        }
        var cls = Slot.Text(r[2], 120);
        if (!CobolText.EqualsPadded(cls, _dClass) && !_first) ResetClass();
        _first = false;
        _dAcct = CobolText.Trimmed(Slot.Text(r[0], 120), 12);
        _dName = CobolText.Trimmed(Slot.Text(r[1], 120), 40);
        _dClass = CobolText.Trimmed(cls, 10);
        _dClassName = CobolText.Trimmed(Slot.Text(r[3], 120), 60);
        _dCfDr = 0;
        _dCfCr = 0;
        if (amt >= 0) _dCfDr = amt;
        else _dCfCr = CobolNumeric.Store(0 - amt, 13, 2);
        _cDr = Add(_cDr, _dDr);
        _tDr = Add(_tDr, _dDr);
        _cCr = Add(_cCr, _dCr);
        _tCr = Add(_tCr, _dCr);
        _tBf = Add(_tBf, _dBf);
        _cCfDr = Add(_cCfDr, _dCfDr);
        _tCfDr = Add(_tCfDr, _dCfDr);
        _cCfCr = Add(_cCfCr, _dCfCr);
        _tCfCr = Add(_tCfCr, _dCfCr);
        _cAccts = (_cAccts + 1) % 10_000_000;
        b.RowsOut++;
        w.Generate(detail);
    }

    /// <summary>1300-RESET-CLASS.</summary>
    private void ResetClass()
    {
        _cDr = 0;
        _cCr = 0;
        _cCfDr = 0;
        _cCfCr = 0;
        _cAccts = 0;
    }

    /// <summary>1900-PROVE: the carried-down difference beyond +/-0.004 is OUT OF BALANCE and RC 8.</summary>
    private void Prove()
    {
        _tDiff = CobolNumeric.Store(_tCfDr - _tCfCr, 13, 2);
        if (_tDiff > 0.004m || _tDiff < -0.004m)
        {
            _statusText = "OUT OF BALANCE";
            b.Rc = 8;
            b.Display("STERP-E-1901 trial balance does not balance");
        }
        else
        {
            _statusText = "IN BALANCE";
        }
        b.Display("STERP-I-1902 " + Disp.S(b.RowsOut) + " account(s) listed, " + Disp.S(b.Skipped) + " suppressed");
    }

    private static decimal Amount(object? v) => CobolNumeric.Store(Slot.Num(v), 13, 2);

    private static decimal Add(decimal acc, decimal v) => CobolNumeric.Store(acc + v, 13, 2);

    private static int Num4(string t) => (int)CobolNumeric.Store(Slot.NumVal(t), 4, 0, signed: false);

    private static int Num2(string t) => (int)CobolNumeric.Store(Slot.NumVal(t), 2, 0, signed: false);
}
