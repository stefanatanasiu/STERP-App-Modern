using Sterp.Modules.Fin.Common;
using Sterp.Modules.Fin.Journals;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// YEAREND (cobol/batch/fin/YEAREND.cob; FR-STERP-FIN-059): clears the posted P&amp;L balances of a fiscal year to the RETAINED
/// reserve. PREVIEW lists; COMMIT stages each line and the reserve under YEAREND-&lt;CO&gt;-&lt;FY 6 digits&gt;, posts through JNLPOST
/// (type YEAREND at the year end) and closes fin.FiscalYear with the journal number. A recorded year-end journal only
/// reports (RC 4); periods not HARD closed warn (RC 4). Legacy quirk kept (SME-085): the staging INSERT leaves SQLIO on
/// cursor 2, so in COMMIT the gather loop's next FTCH reads the closed cursor and the gather stops after the first non-zero
/// account. RD: CONTROLS FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 52. No unit of work of its own.
/// </summary>
internal sealed class YearendJob(FinBatch b)
{
    private string _mode = "";
    private int _fy;
    private string _yearStatus = "";
    private string _yearJnl = "";
    private int _yearEndDate;
    private long _openPeriods;
    private string _retained = "";
    private int _lineNo;

    private string _dAcct = "";
    private string _dName = "";
    private string _dClass = "";
    private decimal _dBalance, _dDr, _dCr;
    private decimal _tNet, _tDr, _tCr, _tResult;
    private long _tAccts;
    private string _status = "";

    public static async Task<int> RunAsync(FinBatch b) => await new YearendJob(b).MainAsync();

    private string Correl => CobolText.Trimmed("YEAREND-" + b.Co.Trim() + "-" + Disp.U(_fy, 6), 36);

    private async Task<int> MainAsync()
    {
        await ReadArgsAsync();
        if (b.Rc > 4) return b.End();
        await ReadYearAsync();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("YEAREND")) return b.End();
        var w = b.Report(60, 1, 9, 52, null,
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Put(1, "Accounts cleared to nil   ", 26).Num(28, _tAccts, "ZZZ,ZZ9").Num(94, _tDr, "Z,ZZZ,ZZZ,ZZ9.99")
                .Num(112, _tCr, "Z,ZZZ,ZZZ,ZZ9.99").Text),
            RwLine.Plus1(() => new RwText().Put(1, "Result carried to the profit and loss re", 40).Put(41, "serve ", 6)
                .Num(72, _tResult, "Z,ZZZ,ZZZ,ZZ9.99-").Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "A credit result is a profit; balance sheet lines", 66)
                .Put(70, "carry forward untouched.", 60).Text),
        ]);
        w.Initiate();
        var line = RwLine.Plus1(() => new RwText().Put(1, _dAcct, 12).Put(14, _dName, 44).Put(60, _dClass, 10)
            .Num(72, _dBalance, "Z,ZZZ,ZZZ,ZZ9.99-").Num(94, _dDr, "Z,ZZZ,ZZZ,ZZ9.99").Num(112, _dCr, "Z,ZZZ,ZZZ,ZZ9.99").Text);
        var note = RwLine.PlusN(2, () => new RwText().Put(3, _status, 100).Text);
        await GatherAsync(w, line);
        await TransferAsync(w, line, note);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>0100-READ-ARGS: FY (else the year of ASAT), MODE default PREVIEW.</summary>
    private async Task ReadArgsAsync()
    {
        var t = b.Arg("FY");
        if (!CobolText.IsSpaces(t)) _fy = (int)CobolNumeric.Store(Slot.NumVal(t), 4, 0, signed: false);
        _mode = CobolText.IsSpaces(b.Mode) ? "PREVIEW" : b.Mode;
        if (_fy == 0) _fy = (await b.ResolvePeriodAsync()).FiscalYear;
        if (_fy == 0)
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 FY=<fiscal year> is required");
            return;
        }
        b.Display("STERP-I-0100 YEAREND " + _mode.Trim() + " for " + b.Co.Trim() + " year " + Disp.U(_fy, 4));
    }

    /// <summary>0200-READ-YEAR: status, recorded journal, end date, periods not HARD, the RETAINED account.</summary>
    private async Task ReadYearAsync()
    {
        object?[]? row = null;
        try
        {
            row = await b.Sql.SingleAsync(
                "SELECT y.Status, ISNULL(y.YearEndJournalNo,''), CONVERT(char(8),y.EndDate,112), (SELECT COUNT(*) FROM fin.Period p WHERE " +
                "p.CompanyCode = y.CompanyCode AND p.FiscalYear = y.FiscalYear AND p.Status <> 'HARD'), ISNULL((SELECT MIN(a.AccountCode) " +
                "FROM fin.Account a WHERE a.CompanyCode = y.CompanyCode AND a.ControlType = 'RETAINED'),'') FROM fin.FiscalYear y WHERE " +
                "y.CompanyCode = @p1 AND y.FiscalYear = @p2", b.Co, Disp.U(_fy, 6));
        }
        catch (SqlioException)
        {
            row = null;
        }
        if (row is null)
        {
            b.Rc = 16;
            b.Display("STERP-E-0201 fiscal year not defined");
            return;
        }
        _yearStatus = CobolText.Trimmed(Slot.Text(row[0], 120), 8);
        _yearJnl = CobolText.Trimmed(Slot.Text(row[1], 120), 30);
        _yearEndDate = (int)CobolNumeric.Store(Slot.NumVal(Slot.Text(row[2], 120)), 8, 0, signed: false);
        _openPeriods = (long)CobolNumeric.Store(Slot.Num(row[3]), 4, 0);
        _retained = CobolText.Trimmed(Slot.Text(row[4], 120), 12);
        if (CobolText.IsSpaces(_retained))
        {
            b.Rc = 8;
            b.Display("STERP-E-0202 no account carries ControlType RETAINED");
        }
    }

    /// <summary>1000-GATHER / 1100-ONE-ACCOUNT.</summary>
    private async Task GatherAsync(GnuReportWriter w, RwLine line)
    {
        b.Heading.Title = "Year end transfer to reserves";
        b.Heading.Subtitle = CobolText.Trimmed("Fiscal year " + Disp.U(_fy, 6) + ", status " + _yearStatus.Trim(), 60);
        b.Heading.AsAtText = "Mode " + _mode.Trim();
        w.Generate(
            RwLine.Plus1(() => new RwText().Put(1, "Account", 7).Put(14, "Name", 4).Put(60, "Class", 5).Put(74, "Balance in year ", 16)
                .Put(96, "Transfer debit ", 15).Put(114, "Transfer credit ", 16).Text),
            RwLine.Plus1(() => new string('-', 132)));
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(
                "SELECT a.AccountCode, a.Name, a.ClassCode, CONVERT(varchar(24),ISNULL(t.Dr,0)-ISNULL(t.Cr,0)) FROM fin.Account a JOIN " +
                "fin.AccountClass k ON k.ClassCode = a.ClassCode OUTER APPLY (SELECT SUM(l.DebitAmountGBP) Dr, SUM(l.CreditAmountGBP) Cr " +
                "FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId WHERE l.CompanyCode = a.CompanyCode AND " +
                "l.AccountCode = a.AccountCode AND j.Status = 'POSTED' AND j.FiscalYear = @p1) t WHERE a.CompanyCode = @p2 AND " +
                "k.Statement = 'PL' AND a.AccountKind <> 'SUMMARY' ORDER BY a.AccountCode", 0, Disp.U(_fy, 6), b.Co);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        foreach (var r in rows)
        {
            if (await OneAccountAsync(w, line, r)) break;
        }
        _tResult = M(0 - _tNet);
    }

    /// <summary>1100-ONE-ACCOUNT; true when the staging INSERT moved SQLIO to cursor 2 (the gather loop then ends).</summary>
    private async Task<bool> OneAccountAsync(GnuReportWriter w, RwLine line, object?[] r)
    {
        _dBalance = M(Slot.Num(r[3]));
        if (_dBalance == 0)
        {
            b.Skipped++;
            return false;
        }
        _dAcct = CobolText.Trimmed(Slot.Text(r[0], 120), 12);
        _dName = CobolText.Trimmed(Slot.Text(r[1], 120), 44);
        _dClass = CobolText.Trimmed(Slot.Text(r[2], 120), 10);
        _dDr = 0;
        _dCr = 0;
        if (_dBalance > 0) _dCr = _dBalance;
        else _dDr = M(0 - _dBalance);
        _tNet = M(_tNet + _dBalance);
        _tDr = M(_tDr + _dDr);
        _tCr = M(_tCr + _dCr);
        _tAccts = (_tAccts + 1) % 10_000_000;
        b.RowsOut++;
        var staged = false;
        if (_mode == "COMMIT" && CobolText.IsSpaces(_yearJnl))
        {
            await StageLineAsync();
            staged = true;
        }
        w.Generate(line);
        return staged;
    }

    /// <summary>1200-STAGE-LINE.</summary>
    private async Task StageLineAsync()
    {
        _lineNo++;
        var line = Disp.U(_lineNo, 6);
        var dr = CobolEdit.Format(_dDr, "-(14)9.99");
        var cr = CobolEdit.Format(_dCr, "-(14)9.99");
        try
        {
            await b.Sql.ExecAsync(
                "INSERT fin.JournalStage (CorrelationId, LineNum, CompanyCode, AccountCode, DebitAmount, CreditAmount, CurrencyCode, " +
                "DebitAmountGBP, CreditAmountGBP, Narrative, CreatedBy) SELECT @p1, @p2, @p3, @p4, @p5, @p6, 'GBP', @p7, @p8, @p9, @p10 " +
                "WHERE NOT EXISTS (SELECT 1 FROM fin.JournalStage s WHERE s.CorrelationId = @p11 AND s.LineNum = @p12)",
                Correl, line, b.Co, _dAcct, dr, cr, dr, cr, "Year end transfer, " + _dAcct.Trim(), b.User, Correl, line);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-1201 " + CobolText.Fit(ex.DriverText, 512));
        }
    }

    /// <summary>2000-TRANSFER.</summary>
    private async Task TransferAsync(GnuReportWriter w, RwLine line, RwLine note)
    {
        _status = "";
        if (!CobolText.IsSpaces(_yearJnl))
        {
            _status = CobolText.Trimmed("Year already transferred by journal " + _yearJnl.Trim() + " - nothing posted", 20);
            b.Rc = 4;
            b.Display("STERP-W-2001 year end already run, journal " + _yearJnl.Trim());
            w.Generate(note);
            return;
        }
        if (_openPeriods > 0)
        {
            _status = "Warning: ";
            b.Rc = 4;
            b.Display("STERP-W-2002 " + Disp.S(_openPeriods, 4) + " period(s) are not hard closed");
        }
        if (_tAccts == 0)
        {
            _status = CobolText.Trimmed("No profit and loss balances to transfer", 20);
            w.Generate(note);
            return;
        }
        if (_mode != "COMMIT")
        {
            _status = CobolText.Trimmed("PREVIEW ONLY - no journal has been posted", 20);
            w.Generate(note);
            return;
        }
        await StageReserveAsync(w, line);
        await PostAsync();
        w.Generate(note);
    }

    /// <summary>2100-STAGE-RESERVE: the RETAINED account takes the net result.</summary>
    private async Task StageReserveAsync(GnuReportWriter w, RwLine line)
    {
        _dAcct = _retained;
        _dName = "Profit and loss reserve";
        _dClass = "EQ";
        _dBalance = _tNet;
        _dDr = 0;
        _dCr = 0;
        if (_tNet > 0) _dDr = _tNet;
        else _dCr = M(0 - _tNet);
        await StageLineAsync();
        w.Generate(line);
    }

    /// <summary>2200-POST / 2300-CLOSE-YEAR.</summary>
    private async Task PostAsync()
    {
        var jp = new JnlPostArea
        {
            JournalType = "YEAREND",
            Description = "Year end transfer to reserves",
            SourceModule = "FIN",
            SourceRef = Correl,
            DocumentDate = _yearEndDate,
            PostingDate = _yearEndDate,
            Currency = "GBP",
            FxRate = 1,
        };
        var jh = await b.Ctx.Get<JournalPostingProgram>().RunAsync(new CaRequest("POST", b.Co, "", b.User, _yearEndDate, Correl), jp, b.Ct);
        if (jh.ReturnCode > 4)
        {
            b.Rc = 8;
            _status = CobolText.Trimmed(jh.Message, 20);
            b.Display("STERP-E-2201 " + CobolText.Fit(jh.Message, 200));
            return;
        }
        _yearJnl = CobolText.Trimmed(jp.JournalNumber, 30);
        if (CobolText.IsSpaces(_yearJnl)) _yearJnl = CobolText.Trimmed(jp.NewJournalNo, 30);
        if (jp.WasDuplicate == "Y")
        {
            _status = CobolText.Trimmed("Transfer already existed as journal " + _yearJnl.Trim() + " - no second posting", 20);
            b.Rc = 4;
        }
        else
        {
            _status = CobolText.Trimmed("Transferred by journal " + _yearJnl.Trim(), 20);
            await CloseYearAsync();
        }
        b.Posted++;
        b.Display("STERP-I-2202 " + _status.Trim());
    }

    private async Task CloseYearAsync()
    {
        try
        {
            await b.Sql.ExecAsync(
                "UPDATE fin.FiscalYear SET Status = 'CLOSED', YearEndJournalNo = @p1, ClosedBy = @p2, ClosedAt = SYSDATETIME() WHERE " +
                "CompanyCode = @p3 AND FiscalYear = @p4 AND YearEndJournalNo IS NULL", _yearJnl, b.User, b.Co, Disp.U(_fy, 6));
        }
        catch (SqlioException ex)
        {
            b.Rc = 4;
            b.Display("STERP-W-2301 " + CobolText.Fit(ex.DriverText, 512));
        }
    }

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
