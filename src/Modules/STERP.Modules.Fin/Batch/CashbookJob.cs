using Sterp.Modules.Fin.Bank;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// CASHBOOK (cobol/batch/fin/CASHBOOK.cob; FR-STERP-FIN-053): posted bank-tagged journal lines between two dates by bank,
/// date and line with receipts, payments, contra account and a running balance per bank; for one bank the BANKREC BRIDGE
/// figures at TO (RC 4 when it does not bridge or BANKREC refuses). RD: CONTROLS FINAL D-BANK, PAGE LIMIT 60, HEADING 1,
/// FIRST DETAIL 9, LAST DETAIL 52, FOOTING 58.
/// </summary>
internal sealed class CashbookJob(FinBatch b)
{
    private int _from;
    private int _to;
    private string _bank = "";

    private string _dBank = "";
    private string _dBankName = "";
    private string _dDate = "";
    private string _dJnl = "";
    private string _dNarr = "";
    private string _dContra = "";
    private decimal _dReceipt, _dPayment, _dRunning;
    private string _gCaption = "";
    private decimal _gAmount;
    private decimal _tReceipts, _tPayments, _tClosing;
    private string _status = "";

    public static async Task<int> RunAsync(FinBatch b) => await new CashbookJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        ReadArgs();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("CASHBOOK")) return b.End();
        var w = Writer();
        w.Initiate();
        await ProduceAsync(w);
        await BridgeAsync(w);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>0100-READ-ARGS: TO defaults to ASAT, FROM to TO - 100 (yyyymmdd arithmetic, SME-1313).</summary>
    private void ReadArgs()
    {
        var a = b.Ctx.Args;
        if (!CobolText.IsSpaces(a.From)) _from = Date8(Slot.NumVal(a.From));
        if (!CobolText.IsSpaces(a.To)) _to = Date8(Slot.NumVal(a.To));
        _bank = CobolText.Trimmed(b.Arg("BANK"), 20);
        if (_to == 0) _to = b.AsAt;
        if (_from == 0) _from = Date8(_to - 100);
        if (_from > _to)
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 FROM date is after TO date");
            return;
        }
        b.Display("STERP-I-0100 CASHBOOK cash analysis for " + b.Co.Trim());
    }

    private GnuReportWriter Writer()
    {
        var bank = new RwControl(() => _dBank, v => _dBank = v, 20)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Put(1, "Bank account ", 13).Put(14, _dBank, 20).Put(36, _dBankName, 40).Text),
                RwLine.Plus1(() => new RwText().Put(1, "Date", 4).Put(13, "Journal", 7).Put(35, "Narrative", 9).Lit(70, "Analysis")
                    .Put(96, "Receipts", 8).Put(110, "Payments", 8).Put(124, "Balance", 7).Text),
                RwLine.Plus1(() => new string('-', 132)),
            ],
        };
        return b.Report(60, 1, 9, 52, [bank],
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Put(1, "Cash book totals", 16).Num(94, _tReceipts, "Z,ZZZ,ZZ9.99")
                .Num(108, _tPayments, "Z,ZZZ,ZZ9.99").Num(120, _tClosing, "ZZZ,ZZZ,ZZ9.99-").Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "A bank charge is expensed, not netted off.", 62).Put(66, _status, 62).Text),
        ]);
    }

    /// <summary>1000-PRODUCE / 1100-BUILD-SQL.</summary>
    private async Task ProduceAsync(GnuReportWriter w)
    {
        b.Heading.Title = "Cash book analysis";
        b.Heading.Subtitle = "Movements from " + FinBatch.UkDate(_from);
        b.Heading.AsAtText = "to " + FinBatch.UkDate(_to);
        const string sql =
            "SELECT l.BankAccountCode, ba.Name, CONVERT(char(8),j.PostingDate,112), j.JournalNumber, ISNULL(l.Narrative,j.Description), " +
            "CONVERT(varchar(24),l.DebitAmountGBP), CONVERT(varchar(24),l.CreditAmountGBP), ISNULL((SELECT TOP 1 x.AccountCode FROM " +
            "fin.JournalLine x WHERE x.JournalId = l.JournalId AND x.JournalLineId <> l.JournalLineId ORDER BY x.LineNum),''), " +
            "CASE WHEN l.ReconId IS NULL THEN '' ELSE 'REC' END FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId " +
            "JOIN fin.BankAccount ba ON ba.CompanyCode = l.CompanyCode AND ba.BankAccountCode = l.BankAccountCode WHERE " +
            "l.CompanyCode = @p1 AND j.Status = 'POSTED' AND j.PostingDate BETWEEN @p2 AND @p3 AND l.BankAccountCode IS NOT NULL AND " +
            "(@p4 = '' OR l.BankAccountCode = @p5) ORDER BY l.BankAccountCode, j.PostingDate, l.JournalLineId";
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, b.Co, Disp.U(_from, 8), Disp.U(_to, 8), _bank, _bank);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        var detail = RwLine.Plus1(() => new RwText().Put(1, _dDate, 10).Put(13, _dJnl, 20).Put(35, _dNarr, 34).Put(70, _dContra, 12)
            .Put(83, "", 10).Num(94, _dReceipt, "Z,ZZZ,ZZ9.99").Num(108, _dPayment, "Z,ZZZ,ZZ9.99").Num(120, _dRunning, "ZZZ,ZZZ,ZZ9.99-").Text);
        foreach (var r in rows) OneMove(w, r, detail);
        b.Display("STERP-I-1002 " + Disp.S(b.RowsOut) + " cash movement(s)");
    }

    /// <summary>1200-ONE-MOVE: the running figure restarts at a new bank.</summary>
    private void OneMove(GnuReportWriter w, object?[] r, RwLine detail)
    {
        var bank = Slot.Text(r[0], 120);
        if (!CobolText.EqualsPadded(bank, _dBank)) _dRunning = 0;
        _dBank = CobolText.Trimmed(bank, 20);
        _dBankName = CobolText.Trimmed(Slot.Text(r[1], 120), 40);
        _dDate = FinBatch.UkDate(Date8(Slot.Num(r[2])));
        _dJnl = CobolText.Trimmed(Slot.Text(r[3], 120), 20);
        _dNarr = CobolText.Trimmed(Slot.Text(r[4], 120), 34);
        _dReceipt = CobolNumeric.Store(Slot.Num(r[5]), 11, 2);
        _dPayment = CobolNumeric.Store(Slot.Num(r[6]), 11, 2);
        _dContra = CobolText.Trimmed(Slot.Text(r[7], 120), 12);
        _dRunning = M(_dRunning + _dReceipt - _dPayment);
        _tReceipts = M(_tReceipts + _dReceipt);
        _tPayments = M(_tPayments + _dPayment);
        _tClosing = _dRunning;
        b.RowsOut++;
        w.Generate(detail);
    }

    /// <summary>2000-BRIDGE: BANKREC BRIDGE for a single bank at TO.</summary>
    private async Task BridgeAsync(GnuReportWriter w)
    {
        if (CobolText.IsSpaces(_bank))
        {
            _status = CobolText.Trimmed("Bridge printed only for a single bank account", 20);
            return;
        }
        var req = new CaRequest("BRIDGE", b.Co, "", b.User, _to, "");
        var br = new BankRecArea { BankAccount = _bank, AsAtDate = _to };
        var ca = await b.Ctx.Get<BankReconciliation>().RunAsync(req, br, b.Ct);
        if (ca.ReturnCode > 4)
        {
            b.Rc = 4;
            _status = CobolText.Trimmed(ca.Message, 20);
            b.Display("STERP-W-2001 " + CobolText.Fit(ca.Message, 200));
            return;
        }
        var line = RwLine.Plus1(() => new RwText().Put(5, _gCaption, 46).Num(60, _gAmount, "Z,ZZZ,ZZZ,ZZ9.99-").Text);
        Bridge(w, line, "Balance per cash book (general ledger)", br.BookBalance);
        Bridge(w, line, "Add: receipts not yet credited by the bank", br.OutReceipts);
        Bridge(w, line, "Less: payments not yet presented", M(0 - br.OutPayments));
        Bridge(w, line, "Bridged balance", br.BridgedBalance);
        Bridge(w, line, "Balance per bank statement", br.StmtBalance);
        Bridge(w, line, "Unexplained difference", br.Unexplained);
        if (br.Unexplained > 0.004m || br.Unexplained < -0.004m)
        {
            _status = CobolText.Trimmed("UNEXPLAINED DIFFERENCE - SEE BANKREC", 20);
            b.Rc = 4;
            b.Display("STERP-W-2002 cash book does not bridge");
        }
        else
        {
            _status = "Bridge complete";
        }
    }

    private void Bridge(GnuReportWriter w, RwLine line, string caption, decimal amount)
    {
        _gCaption = caption;
        _gAmount = amount;
        w.Generate(line);
    }

    private static int Date8(decimal v) => (int)CobolNumeric.Store(v, 8, 0, signed: false);

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
