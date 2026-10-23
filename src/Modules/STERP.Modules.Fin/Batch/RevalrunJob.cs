using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// REVALRUN (cobol/batch/fin/REVALRUN.cob; FR-STERP-FIN-056): period-end revaluation of the foreign-currency monetary
/// positions (GL accounts outside the AR/AP controls, AR and AP open items) as at ASAT through FXREVAL - REVALUE in
/// PREVIEW, POST in COMMIT (RATE = rate type, default SPOT). A position that cannot be priced is RC 8 in COMMIT, RC 4
/// otherwise; more than 50 positions RC 4. RD: CONTROLS FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 52.
/// </summary>
internal sealed class RevalrunJob(FinBatch b)
{
    private sealed record Position(string Account, string Currency, decimal Foreign, decimal Carrying, string Kind, string SubType);

    private string _rateType = "SPOT";
    private string _mode = "";
    private int _fy;
    private int _per;
    private string _lastJnl = "";
    private readonly List<Position> _positions = [];

    private string _dAcct = "";
    private string _dCurr = "";
    private decimal _dForeign, _dCarrying, _dRevalued, _dAdjust;
    private string _dSense = "";
    private decimal _tGain, _tLoss, _tNet;
    private long _tLines;
    private string _status = "";

    public static async Task<int> RunAsync(FinBatch b) => await new RevalrunJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        await ReadArgsAsync();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("REVALRUN")) return b.End();
        var w = b.Report(60, 1, 9, 52, null,
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Put(1, "Unrealised gains       ", 24).Num(98, _tGain, Ed).Text),
            RwLine.Plus1(() => new RwText().Put(1, "Unrealised losses      ", 24).Num(98, _tLoss, Ed).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Net adjustment to the ledger").Num(98, _tNet, Ed).Text),
            RwLine.PlusN(2, () => new RwText().Put(3, "The revaluation is reversed on day one of the next", 64)
                .Put(68, "period, so settlement records the difference.", 60).Text),
        ]);
        w.Initiate();
        var note = RwLine.PlusN(2, () => new RwText().Put(3, _status, 100).Text);
        await RevalueAsync(w, note);
        Post(w, note);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>0100-READ-ARGS: RATE (6), MODE default PREVIEW, fiscal period of ASAT required.</summary>
    private async Task ReadArgsAsync()
    {
        var t = b.Arg("RATE");
        if (!CobolText.IsSpaces(t)) _rateType = CobolText.Trimmed(t, 6);
        _mode = CobolText.IsSpaces(b.Mode) ? "PREVIEW" : b.Mode;
        (_fy, _per) = await b.ResolvePeriodAsync();
        if (_fy == 0)
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 no fiscal period covers " + Disp.U(b.AsAt, 8));
            return;
        }
        b.Display("STERP-I-0100 REVALRUN " + _mode.Trim() + " for " + b.Co.Trim());
    }

    /// <summary>1000-REVALUE.</summary>
    private async Task RevalueAsync(GnuReportWriter w, RwLine note)
    {
        b.Heading.Title = "Period end currency revaluation";
        b.Heading.AsAtText = "As at " + FinBatch.UkDate(b.AsAt);
        b.Heading.Subtitle = CobolText.Trimmed("Year " + Disp.U(_fy, 6) + ", rate type " + _rateType.Trim() + ", mode " + _mode.Trim(), 60);
        w.Generate(
            RwLine.Plus1(() => new RwText().Put(1, "Account", 7).Put(16, "Currency", 8).Put(30, "Foreign balance ", 16)
                .Put(54, "Carrying value  ", 16).Put(78, "Revalued value  ", 16).Put(102, "Adjustment", 10).Put(124, "Type", 4).Text),
            RwLine.Plus1(() => new string('-', 132)));
        await LoadPositionsAsync(w, note);
        if (b.Rc > 4) return;
        var line = RwLine.Plus1(() => new RwText().Put(1, _dAcct, 12).Put(17, _dCurr, 3).Num(26, _dForeign, Ed).Num(50, _dCarrying, Ed)
            .Num(74, _dRevalued, Ed).Num(98, _dAdjust, Ed).Put(124, _dSense, 4).Text);
        foreach (var p in _positions) await OnePositionAsync(w, note, line, p);
        if (_positions.Count == 0)
        {
            _status = CobolText.Trimmed("No foreign currency balances to revalue", 20);
            w.Generate(note);
        }
        b.RowsOut = _tLines;
        b.Display("STERP-I-1002 " + Disp.S(_tLines, 5) + " position(s) revalued, " + Disp.S(b.Blocked) + " could not be priced");
    }

    /// <summary>1100-LOAD-POSITIONS / 1150-BUILD-SQL: at most 50 positions (more are counted and RC 4).</summary>
    private async Task LoadPositionsAsync(GnuReportWriter w, RwLine note)
    {
        const string sql =
            "SELECT a.AccountCode, l.CurrencyCode, CONVERT(varchar(24),SUM(l.DebitAmount-l.CreditAmount)), CONVERT(varchar(24)," +
            "SUM(l.DebitAmountGBP-l.CreditAmountGBP)), CASE WHEN k.NormalBalance = 'CR' THEN 'LIABILITY' ELSE 'ASSET' END, 'GL' FROM " +
            "fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId JOIN fin.Account a ON a.CompanyCode = l.CompanyCode AND " +
            "a.AccountCode = l.AccountCode JOIN fin.AccountClass k ON k.ClassCode = a.ClassCode WHERE l.CompanyCode = @p1 AND " +
            "j.Status = 'POSTED' AND j.PostingDate <= @p2 AND ISNULL(l.CurrencyCode,'GBP') NOT IN ('GBP','') AND " +
            "ISNULL(a.ControlType,'') NOT IN ('AR','AP') GROUP BY a.AccountCode, l.CurrencyCode, k.NormalBalance HAVING " +
            "SUM(l.DebitAmount-l.CreditAmount) <> 0 UNION ALL SELECT ISNULL((SELECT TOP 1 x.AccountCode FROM fin.Account x WHERE " +
            "x.CompanyCode = o.CompanyCode AND x.ControlType = 'AR' ORDER BY x.AccountCode),'1200'), o.CurrencyCode, CONVERT(varchar(24)," +
            "SUM(o.ResidualAmount)), CONVERT(varchar(24),SUM(o.ResidualAmountGBP)), 'ASSET', 'AR' FROM ar.OpenItem o WHERE " +
            "o.CompanyCode = @p3 AND o.CurrencyCode <> 'GBP' AND o.PostingDate <= @p4 AND o.ResidualAmount <> 0 GROUP BY " +
            "o.CompanyCode, o.CurrencyCode HAVING SUM(o.ResidualAmount) <> 0 UNION ALL SELECT ISNULL((SELECT TOP 1 x.AccountCode FROM " +
            "fin.Account x WHERE x.CompanyCode = p.CompanyCode AND x.ControlType = 'AP' ORDER BY x.AccountCode),'2000'), " +
            "p.CurrencyCode, CONVERT(varchar(24),SUM(p.ResidualAmount)), CONVERT(varchar(24),SUM(p.ResidualAmountGBP)), 'LIABILITY', " +
            "'AP' FROM ap.OpenItem p WHERE p.CompanyCode = @p5 AND p.CurrencyCode <> 'GBP' AND p.PostingDate <= @p6 AND " +
            "p.ResidualAmount <> 0 GROUP BY p.CompanyCode, p.CurrencyCode HAVING SUM(p.ResidualAmount) <> 0 ORDER BY 6, 1, 2";
        var asAt = Disp.U(b.AsAt, 8);
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(sql, 0, b.Co, asAt, b.Co, asAt, b.Co, asAt);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            _status = CobolText.Trimmed(ex.DriverText, 20);
            b.Display("STERP-E-1000 " + CobolText.Fit(ex.DriverText, 512));
            w.Generate(note);
            return;
        }
        foreach (var r in rows)
        {
            if (_positions.Count < 50)
            {
                _positions.Add(new Position(
                    CobolText.Trimmed(Slot.Text(r[0], 120), 12),
                    CobolText.Trimmed(Slot.Text(r[1], 120), 3),
                    CobolNumeric.Store(Slot.Num(r[2]), 11, 2),
                    CobolNumeric.Store(Slot.Num(r[3]), 13, 2),
                    CobolText.Trimmed(Slot.Text(r[4], 120), 10),
                    CobolText.Trimmed(Slot.Text(r[5], 120), 6)));
            }
            else
            {
                b.Skipped++;
            }
        }
        if (b.Skipped > 0)
        {
            b.Rc = 4;
            _status = CobolText.Trimmed("More than 50 positions were found - the listing is truncated", 20);
            w.Generate(note);
        }
    }

    /// <summary>1200-ONE-POSITION: FXREVAL REVALUE (POST in COMMIT) and the listing line.</summary>
    private async Task OnePositionAsync(GnuReportWriter w, RwLine note, RwLine line, Position p)
    {
        var fr = new FxRevalArea
        {
            Currency = p.Currency,
            Account = p.Account,
            AsAtDate = b.AsAt,
            FiscalYear = _fy,
            PeriodNo = _per,
            RateType = _rateType,
            Mode = _mode,
            Position = p.Kind,
            SubledgerType = p.SubType,
            ForeignAmount = p.Foreign,
            CarryingGbp = p.Carrying,
        };
        var fh = await b.Ctx.Get<FxRevaluation>().RunAsync(
            new CaRequest(_mode == "COMMIT" ? "POST" : "REVALUE", b.Co, "", b.User, b.AsAt, ""), fr, b.Ct);
        if (fh.ReturnCode > 4)
        {
            NotPriced(w, note, p, fh.Message);
            return;
        }
        _dAcct = p.Account;
        _dCurr = p.Currency;
        _dForeign = fr.ForeignAmount;
        _dCarrying = fr.CarryingGbp;
        _dRevalued = fr.RevaluedGbp;
        _dAdjust = fr.UnrealisedGbp;
        if (fr.UnrealisedGbp < 0)
        {
            _dSense = "LOSS";
            _tLoss = M(_tLoss - fr.UnrealisedGbp);
        }
        else
        {
            _dSense = "GAIN";
            _tGain = M(_tGain + fr.UnrealisedGbp);
        }
        _tNet = M(_tNet + fr.UnrealisedGbp);
        _tLines = (_tLines + 1) % 100_000;
        w.Generate(line);
        if (fr.RevalStatus == "POSTED")
        {
            b.Posted++;
            _lastJnl = CobolText.Trimmed(fr.JournalNumber, 30);
        }
    }

    /// <summary>1300-NOT-PRICED.</summary>
    private void NotPriced(GnuReportWriter w, RwLine note, Position p, string message)
    {
        b.Blocked++;
        _status = CobolText.Trimmed(p.Account.Trim() + " " + CobolText.Fit(p.Currency, 3) + " not revalued - " + message.Trim(), 20);
        b.Display("STERP-E-1001 " + _status.Trim());
        w.Generate(note);
        if (_mode == "COMMIT") b.Rc = 8;
        else if (b.Rc < 4) b.Rc = 4;
    }

    /// <summary>2000-POST: the closing note.</summary>
    private void Post(GnuReportWriter w, RwLine note)
    {
        if (_tLines == 0 && b.Blocked == 0) return;
        if (_mode != "COMMIT")
        {
            _status = CobolText.Trimmed("PREVIEW ONLY - no revaluation journal posted", 20);
            w.Generate(note);
            return;
        }
        _status = CobolText.Trimmed(b.Posted.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                    " revaluation journal(s) posted, latest " + _lastJnl.Trim() +
                                    ", each reversed on day one of the next period", 20);
        b.Display("STERP-I-2002 " + _status.Trim());
        w.Generate(note);
    }

    private const string Ed = "Z,ZZZ,ZZZ,ZZ9.99-";

    private static decimal M(decimal v) => CobolNumeric.Store(v, 13, 2);
}
