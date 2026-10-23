using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// CASHFCST (cobol/batch/xmd/CASHFCST.cob; FR-STERP-XMD-027): dated cash flow forecast. Opening funds from the active
/// sterling bank accounts, then contractual (AR/AP open items not covered by an in-flight instruction), confirmed (in-flight
/// payment instructions) and expected (VAT payments, CORPTAXPAY obligations) flows sorted by date (stable ordinal sort,
/// D-XMD-12) with day breaks and the STERP-I-0404 proof line. Read only. The 9450-FORMAT-DATE of a bank statement date
/// overwrites XB-DATE-ED, the lower bound of the VAT and statutory queries (SME-1987, reproduced).
/// Listing FL-063 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class CashfcstProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class Flow
    {
        public int Date;
        public int Seq;
        public string Ref = "";
        public string Src = "";
        public string Party = "";
        public string Cat = "";
        public string Dir = "";
        public decimal Amt;
        public string Note = "";
        public byte[] SortRef = [];
    }

    private sealed class State
    {
        public string DateEd = "00000000";
        public string DateEd2 = "00000000";
        public string Bank = "";
        public string Scen = "";
        public int Slip;
        public int ArSlip;
        public int ApSlip;
        public decimal CorpTax;
        public int Deferred;
        public decimal OpenBal, RunBal, LowBal;
        public int LowDate;
        public int Accts;
        public decimal DayIn, DayOut, DayContr, DayConf, DayExp;
        public decimal ContrIn, ContrOut, ConfOut, ExpIn, ExpOut, TIn, TOut, TClose, ApOnce, SuppAmt;
        public int ApItems, ConfItems, SuppItems, ArItems, ExpItems;
        public List<Flow> Flows = [];
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var b = XmdBatch.Start(ctx);
        var result = new JsonObject();
        try
        {
            return await RunAsync(b, result);
        }
        finally
        {
            ctx.Result = result;
        }
    }

    private static async Task<int> RunAsync(XmdBatch b, JsonObject result)
    {
        var st = new State();
        // 0100-READ-ARGS
        if (b.Args.From == 0) b.SetDates(from: b.AsAt);
        if (b.Args.To == 0) b.SetDates(to: AddDays(b.Args.From, 90));
        if (b.Args.To < b.Args.From)
        {
            b.Display("STERP-W-0401 TO before FROM - window reset");
            b.SetDates(to: b.Args.From);
            b.Rc = 4;
        }
        if (CobolText.IsSpaces(b.Ctx.Args.AsAt)) b.SetDates(asAt: b.Args.From);
        var bankText = b.GetArg("BANK");
        if (!CobolText.IsSpaces(bankText)) st.Bank = CobolText.Upper(CobolText.Fit(bankText, 200)[..10]).TrimEnd(' ');
        var scenText = b.GetArg("SCENARIO");
        if (!CobolText.IsSpaces(scenText)) st.Scen = CobolText.Upper(CobolText.Fit(scenText, 200)[..10]).TrimEnd(' ');
        if (st.Scen.Length == 0) st.Scen = "BASE";
        if (st.Scen is not ("BASE" or "UPSIDE" or "DOWNSIDE"))
        {
            b.Display("STERP-W-0402 unknown scenario " + CobolText.Trim(st.Scen) + " - BASE assumed");
            st.Scen = "BASE";
            b.Rc = 4;
        }
        st.Slip = (int)CobolNumeric.Store(await b.GetParmNumAsync("XMD.CASH.SLIPDAYS", 14m), 4, 0);
        st.CorpTax = CobolNumeric.Store(await b.GetParmNumAsync("XMD.CASH.CORPTAX", 0m), 13, 2);
        // 0200-SCENARIO
        if (st.Scen == "DOWNSIDE") st.ArSlip = st.Slip;
        else if (st.Scen == "UPSIDE") st.ApSlip = st.Slip;
        await b.ResolvePeriodAsync();
        var sFrom = FormatDate(st, b.Args.From);
        var sTo = FormatDate(st, b.Args.To);
        st.DateEd = Disp.D8(b.Args.From);
        st.DateEd2 = Disp.D8(b.Args.To);
        var sScen = CobolText.Fit(st.Scen, 10);
        var sBank = CobolText.Fit(st.Bank.Length == 0 ? "ALL ACTIVE" : st.Bank, 12);
        var sPeriod = b.FiscalYear == 0
            ? "UNDATED"
            : CobolText.Fit(Disp.Unsigned(b.FiscalYear, 9).Substring(5, 4) + "/" + Disp.Unsigned(b.PeriodNo, 2) + " " +
                            CobolText.Fit(b.PeriodStatus, 10)[..4], 12);
        var sSlip = CobolText.Fit(Disp.Int(st.Slip) + " day(s)", 10);
        b.Display("STERP-I-0400 CASHFCST company " + CobolText.Trim(b.Co) + " window " + st.DateEd + " to " + st.DateEd2 + " scenario " +
                  CobolText.Trim(st.Scen));

        result["from"] = IsoDate(b.Args.From);
        result["to"] = IsoDate(b.Args.To);
        result["scenario"] = sScen.TrimEnd(' ');
        result["bank"] = sBank.TrimEnd(' ');
        result["period"] = sPeriod.TrimEnd(' ');
        result["slip"] = sSlip.TrimEnd(' ');
        var accounts = new JsonArray();
        var flows = new JsonArray();
        var days = new JsonArray();
        result["accounts"] = accounts;
        result["flows"] = flows;
        result["days"] = days;
        if (b.Rc > 4) return Finish(b, st, result, "", "", 0);

        if (!await b.OpenSpoolAsync("CASHFCST", "Cash flow forecast", "Dated receipts and payments against bank funds") || b.Rc > 4)
        {
            return Finish(b, st, result, "", "", 0);
        }
        var sLowDate = "";
        var statusText = "";
        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Contractual ledger flows").Lit(65, "CONTRACTUAL ").Num(78, st.ContrIn, BigPic)
                .Num(96, st.ContrOut, BigPic).Num(114, st.ArItems, "ZZZ9").Lit(119, " in").Num(123, st.ApItems, "ZZZ9").Lit(128, " out").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Confirmed instructions").Lit(65, "CONFIRMED   ").Num(96, st.ConfOut, BigPic)
                .Num(114, st.ConfItems, "ZZZ9").Lit(119, " in flight").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Expected statutory flows").Lit(65, "EXPECTED    ").Num(78, st.ExpIn, BigPic)
                .Num(96, st.ExpOut, BigPic).Num(114, st.ExpItems, "ZZZ9").Lit(119, " estimates").Text),
            RwLine.Plus1(() => new string('-', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Forecast totals").Lit(27, "opening funds").Num(43, st.OpenBal, MidPic)
                .Num(78, st.TIn, BigPic).Num(96, st.TOut, BigPic).Num(114, st.TClose, MidPic).Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Lowest projected balance").Put(31, sLowDate, 10).Num(43, st.LowBal, MidPic)
                .Put(65, statusText, 12).Lit(80, "Flows listed").Num(93, b.RowsOut, "ZZZ,ZZ9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Payables already carried by an in flight payment")
                .Lit(52, "instruction are counted once, as CONFIRMED:").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "open items superseded").Num(34, st.SuppItems, "ZZZ9")
                .Lit(40, "for a ledger value of").Num(63, st.SuppAmt, MidPic).Lit(80, "payables counted once").Num(107, st.ApOnce, MidPic).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Flows pushed beyond the window by the scenario").Num(49, st.Deferred, "ZZZ9").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Forecast window ").Put(17, sFrom, 10).Lit(28, "to ").Put(31, sTo, 10)
                .Lit(45, "Scenario  ").Put(55, sScen, 10).Lit(68, "Bank  ").Put(74, sBank, 12).Lit(88, "Period  ")
                .Put(96, sPeriod, 12).Lit(110, "Slip days  ").Put(121, sSlip, 10).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Basis: sterling carrying values, settled cash")
                .Lit(47, "excluded, each obligation counted once.").Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Opening funds").Text),
            RwLine.Plus1(() => new string('-', 132)));
        await OpeningFundsAsync(b, st, rw, accounts);
        rw.Generate(
            RwLine.PlusN(2, () => new RwText().Lit(1, "Date").Lit(12, "Source").Lit(21, "Reference").Lit(44, "Counterparty")
                .Lit(65, "Category").Lit(87, "Receipts").Lit(105, "Payments").Lit(114, "Note / balance").Text),
            RwLine.Plus1(() => new string('-', 132)));

        // SORT ... INPUT PROCEDURE 2000-COLLECT, OUTPUT PROCEDURE 3000-PRINT
        await CollectAsync(b, st);
        var sorted = st.Flows.OrderBy(f => f.Date).ThenBy(f => f.Seq).ThenBy(f => f.SortRef, ByteOrder.Instance).ToList();
        var prevDate = 0;
        foreach (var f in sorted)
        {
            if (prevDate != 0 && f.Date != prevDate) DayBreak(b, st, rw, prevDate, days);
            prevDate = f.Date;
            OneFlow(b, st, rw, f, flows);
        }
        if (prevDate != 0) DayBreak(b, st, rw, prevDate, days);

        // 4000-PROVE
        st.TClose = st.RunBal;
        st.ApOnce = CobolNumeric.Store(st.ContrOut + st.ConfOut, 13, 2);
        sLowDate = FormatDate(st, st.LowDate);
        statusText = st.LowBal < 0 ? "SHORTFALL" : "FUNDED";
        var proof = "STERP-I-0404 ITEMS=" + Disp.Unsigned(st.ApItems, 5) + " CONF=" + Disp.Unsigned(st.ConfItems, 5) + " SUPP=" +
                    Disp.Unsigned(st.SuppItems, 5) + " OUTTOT=" + Disp.Ed(st.ApOnce, "-(11)9.99");
        b.Display("STERP-I-0403 " + Disp.Signed(b.RowsOut, 9) + " flow(s) listed, " + Disp.Signed(b.RowsIn, 9) + " read, " +
                  Disp.Signed(st.Deferred, 5) + " outside the window");
        b.Display(proof);
        if (st.LowBal < 0) b.Display("STERP-W-0405 projected shortfall on " + Disp.D8(st.LowDate));

        rw.Terminate();
        b.CloseSpool();
        return Finish(b, st, result, statusText, proof, st.LowDate);
    }

    private const string BigPic = "Z,ZZZ,ZZZ,ZZ9.99-";

    private const string MidPic = "ZZZ,ZZZ,ZZ9.99-";

    private static int Finish(XmdBatch b, State st, JsonObject result, string statusText, string proof, int lowDate)
    {
        result["contractualInGbp"] = CobolNumeric.WithScale(st.ContrIn, 2);
        result["contractualOutGbp"] = CobolNumeric.WithScale(st.ContrOut, 2);
        result["confirmedOutGbp"] = CobolNumeric.WithScale(st.ConfOut, 2);
        result["expectedInGbp"] = CobolNumeric.WithScale(st.ExpIn, 2);
        result["expectedOutGbp"] = CobolNumeric.WithScale(st.ExpOut, 2);
        result["closingGbp"] = CobolNumeric.WithScale(st.TClose, 2);
        result["supersededItems"] = st.SuppItems;
        result["supersededGbp"] = CobolNumeric.WithScale(st.SuppAmt, 2);
        result["lowestBalanceDate"] = IsoDate(lowDate);
        result["forecastStatus"] = statusText.Length > 0 ? statusText : null;
        result["proofLine"] = proof.Length > 0 ? proof : null;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>0150-ADD-DAYS: INTEGER-OF-DATE + n, DATE-OF-INTEGER (unchanged for a zero date or zero days).</summary>
    private static int AddDays(int date, int days)
    {
        if (date == 0 || days == 0) return date;
        var i = CobolDate.IntegerOfDate(date) + (long)days;
        return (int)CobolNumeric.Store(CobolDate.DateOfIntegerYyyymmdd(i), 8, 0, signed: false);
    }

    /// <summary>1000-OPENING-FUNDS (+ 1100-ONE-ACCOUNT).</summary>
    private static async Task OpeningFundsAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray accounts)
    {
        st.OpenBal = 0;
        st.Accts = 0;
        var sql = "SELECT BankAccountCode, Name, CurrencyCode, CONVERT(varchar(24),ISNULL(StatementBalance,0)), ISNULL(CONVERT(char(8)," +
                  "StatementDate,112),'00000000'), Status FROM fin.BankAccount WHERE CompanyCode = ? AND Status = 'ACTIVE'";
        var parms = new List<SqlioParm> { SqlioParm.Text(b.Co) };
        if (st.Bank.Length > 0)
        {
            sql += " AND BankAccountCode = ?";
            parms.Add(SqlioParm.Text(st.Bank));
        }
        sql += " ORDER BY BankAccountCode";
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(sql, parms, Widths[..6], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        foreach (var row in rows)
        {
            st.Accts++;
            var code = CobolText.Fit(row.Raw(0), 10);
            var name = CobolText.Fit(row.Raw(1), 30);
            var ccy = CobolText.Fit(row.Raw(2), 3);
            var bal = CobolNumeric.Store(row.NumVal(3), 13, 2);
            var note = "";
            var stmt = "";
            var due = BatchSuite.IsoToNum(row.Raw(4));
            if (due != 0) stmt = FormatDate(st, due);
            if (ccy != "GBP")
            {
                note = "not sterling - excluded";
            }
            else
            {
                st.OpenBal = CobolNumeric.Store(st.OpenBal + bal, 13, 2);
                if (due == 0) note = "never reconciled";
                else if (due < b.Args.From) note = "statement before window";
            }
            Bank(rw, accounts, code, name, ccy, stmt, due, bal, note);
        }
        if (st.Accts == 0)
        {
            Bank(rw, accounts, "", "No active bank account on file", "", "", 0, 0, "forecast starts from nil funds");
            if (b.Rc < 4) b.Rc = 4;
        }
        st.RunBal = st.OpenBal;
        st.LowBal = st.OpenBal;
        st.LowDate = b.Args.From;
    }

    private static void Bank(GnuReportWriter rw, JsonArray accounts, string code, string name, string ccy, string stmt, int stmtNum, decimal bal, string note)
    {
        rw.Generate(RwLine.Plus1(() => new RwText().Put(3, code, 10).Put(14, name, 30).Put(46, ccy, 3).Put(52, stmt, 10).Num(64, bal, BigPic)
            .Put(84, note, 30).Text));
        accounts.Add(new JsonObject
        {
            ["code"] = CobolText.Fit(code, 10).TrimEnd(' '),
            ["name"] = CobolText.Fit(name, 30).TrimEnd(' '),
            ["currency"] = CobolText.Fit(ccy, 3).TrimEnd(' '),
            ["statementDate"] = stmt.Length == 0 ? null : IsoDate(stmtNum),
            ["balanceGbp"] = CobolNumeric.WithScale(bal, 2),
            ["note"] = CobolText.Fit(note, 30).TrimEnd(' '),
        });
    }

    /// <summary>2000-COLLECT: every dated flow; flows outside the window are deferred (2950-RELEASE).</summary>
    private static async Task CollectAsync(XmdBatch b, State st)
    {
        st.Deferred = 0;
        var co = SqlioParm.Text(b.Co);
        var to = SqlioParm.Text(st.DateEd2);
        // 2100-AR-DUE
        await FetchAsync(b, "STERP-E-2101 ",
            "SELECT CONVERT(char(8),o.DueDate,112), o.DocumentNumber, o.CustomerCode, CONVERT(varchar(24),o.ResidualAmountGBP), " +
            "o.SettlementStatus, CONVERT(char(1),o.IsDisputed) FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.SettlementStatus <> " +
            "'SETTLED' AND o.WrittenOff = 0 AND o.ResidualAmountGBP <> 0 AND CONVERT(char(8),o.DueDate,112) <= ? ORDER BY o.DueDate, " +
            "o.DocumentNumber", [co, to], 6, row =>
            {
                var f = DateRule(b, st, BatchSuite.IsoToNum(row.Raw(0)), st.ArSlip);
                f.Src = "AR";
                f.Ref = row.Raw(1);
                f.Party = row.Raw(2);
                f.Cat = "CONTRACTUAL";
                f.Seq = 1;
                f.Dir = "IN";
                f.Amt = CobolNumeric.Store(row.NumVal(3), 13, 2);
                if (row.Raw(5)[0] == '1') f.Note = "disputed";
                if (row.Raw(4)[..7] == "PARTIAL") f.Note = "part settled";
                Release(b, st, f);
            });
        // 2200-AP-DUE
        await FetchAsync(b, "STERP-E-2201 ",
            "SELECT CONVERT(char(8),o.DueDate,112), o.DocumentNumber, o.SupplierCode, CONVERT(varchar(24),o.ResidualAmountGBP), " +
            "CONVERT(char(1),o.IsBlocked), CONVERT(char(1),o.IsDisputed) FROM ap.OpenItem o WHERE o.CompanyCode = ? AND o.SettlementStatus " +
            "<> 'SETTLED' AND o.ResidualAmountGBP <> 0 AND CONVERT(char(8),o.DueDate,112) <= ? AND NOT EXISTS (SELECT 1 FROM " +
            "ap.PaymentInstruction p WHERE p.PaymentInstructionId = o.PaymentInstructionId AND p.Status NOT IN ('SETTLED','CANCELLED'," +
            "'REJECTED','RETURNED')) ORDER BY o.DueDate, o.DocumentNumber", [co, to], 6, row =>
            {
                var f = DateRule(b, st, BatchSuite.IsoToNum(row.Raw(0)), st.ApSlip);
                f.Src = "AP";
                f.Ref = row.Raw(1);
                f.Party = row.Raw(2);
                f.Cat = "CONTRACTUAL";
                f.Seq = 1;
                f.Dir = "OUT";
                f.Amt = CobolNumeric.Store(row.NumVal(3), 13, 2);
                if (row.Raw(4)[0] == '1') f.Note = "blocked";
                if (row.Raw(5)[0] == '1') f.Note = "disputed";
                Release(b, st, f);
            });
        // 2250-SUPERSEDED
        st.SuppItems = 0;
        st.SuppAmt = 0;
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(12),COUNT(*)), CONVERT(varchar(24),ISNULL(SUM(o.ResidualAmountGBP),0)) FROM ap.OpenItem o WHERE " +
                "o.CompanyCode = ? AND o.SettlementStatus <> 'SETTLED' AND CONVERT(char(8),o.DueDate,112) <= ? AND EXISTS (SELECT 1 FROM " +
                "ap.PaymentInstruction p WHERE p.PaymentInstructionId = o.PaymentInstructionId AND p.Status NOT IN ('SETTLED','CANCELLED'," +
                "'REJECTED','RETURNED'))", [co, to], Widths[..2], b.Ct);
            if (row.Found)
            {
                st.SuppItems = (int)CobolNumeric.Store(row.NumVal(0), 5, 0);
                st.SuppAmt = CobolNumeric.Store(row.NumVal(1), 13, 2);
            }
        }
        catch (SqlioException)
        {
        }
        // 2300-INSTRUCTIONS
        var sql = "SELECT CONVERT(char(8),p.ValueDate,112), p.InstructionRef, p.SupplierCode, CONVERT(varchar(24),p.AmountGBP), p.Status, " +
                  "p.PaymentMethod FROM ap.PaymentInstruction p WHERE p.CompanyCode = ? AND p.Status NOT IN ('SETTLED','CANCELLED','REJECTED'," +
                  "'RETURNED') AND p.ValueDate IS NOT NULL AND CONVERT(char(8),p.ValueDate,112) <= ?";
        var parms = new List<SqlioParm> { co, to };
        if (st.Bank.Length > 0)
        {
            sql += " AND p.BankAccountCode = ?";
            parms.Add(SqlioParm.Text(st.Bank));
        }
        sql += " ORDER BY p.ValueDate, p.InstructionRef";
        await FetchAsync(b, "STERP-E-2301 ", sql, parms, 6, row =>
        {
            var f = DateRule(b, st, BatchSuite.IsoToNum(row.Raw(0)), 0);
            f.Src = "PAYINST";
            f.Ref = row.Raw(1);
            f.Party = row.Raw(2);
            f.Cat = "CONFIRMED";
            f.Seq = 2;
            f.Dir = "OUT";
            f.Amt = CobolNumeric.Store(row.NumVal(3), 13, 2);
            f.Note = row.Raw(5)[..6] + " " + row.Raw(4)[..10];
            Release(b, st, f);
        });
        // 2400-VAT-DUE (lower bound XB-DATE-ED as left by the opening funds)
        await FetchAsync(b, "STERP-E-2401 ",
            "SELECT CONVERT(char(8),v.PaymentDueDate,112), v.VatPeriodKey, CONVERT(varchar(24),ISNULL(x.Box5,0)), v.Status FROM fin.VatPeriod v " +
            "OUTER APPLY (SELECT TOP 1 r.Box5 FROM fin.VatReturn r WHERE r.CompanyCode = v.CompanyCode AND r.VatPeriodKey = v.VatPeriodKey " +
            "ORDER BY r.Version DESC) x WHERE v.CompanyCode = ? AND v.PaymentDueDate IS NOT NULL AND CONVERT(char(8),v.PaymentDueDate,112) " +
            "BETWEEN ? AND ? ORDER BY v.PaymentDueDate", [co, SqlioParm.Text(st.DateEd), to], 4, row =>
            {
                var f = DateRule(b, st, BatchSuite.IsoToNum(row.Raw(0)), 0);
                f.Src = "VAT";
                f.Ref = "VAT " + row.Raw(1)[..12];
                f.Party = "HMRC";
                f.Cat = "EXPECTED";
                f.Seq = 3;
                var amt = CobolNumeric.Store(row.NumVal(2), 13, 2);
                if (amt < 0)
                {
                    f.Dir = "IN";
                    f.Amt = CobolNumeric.Store(0 - amt, 13, 2);
                    f.Note = "repayment due";
                }
                else
                {
                    f.Dir = "OUT";
                    f.Amt = amt;
                    f.Note = "return " + row.Raw(3)[..10];
                }
                if (amt == 0) f.Note = "no return valued";
                Release(b, st, f);
            });
        // 2500-STATUTORY
        await FetchAsync(b, "STERP-E-2501 ",
            "SELECT CONVERT(char(8),s.DueDate,112), s.ObligationCode, s.PeriodKey, s.Status, ISNULL(s.OwnerUser,'') FROM " +
            "core.StatutoryObligation s WHERE s.CompanyCode = ? AND s.ObligationCode = 'CORPTAXPAY' AND s.Status <> 'COMPLETE' AND " +
            "CONVERT(char(8),s.DueDate,112) BETWEEN ? AND ? ORDER BY s.DueDate", [co, SqlioParm.Text(st.DateEd), to], 5, row =>
            {
                var f = DateRule(b, st, BatchSuite.IsoToNum(row.Raw(0)), 0);
                f.Src = "CORPTAX";
                f.Ref = row.Raw(1)[..11] + " " + row.Raw(2)[..10];
                f.Party = "HMRC";
                f.Cat = "EXPECTED";
                f.Seq = 3;
                f.Dir = "OUT";
                f.Amt = st.CorpTax;
                f.Note = st.CorpTax == 0 ? "no estimate held" : "estimate XMD.CASH";
                Release(b, st, f);
            });
    }

    /// <summary>EXEC + FTCH loop of one collector; an SQL error is RC 12 and the program's own console line.</summary>
    private static async Task FetchAsync(XmdBatch b, string errorPrefix, string sql, IReadOnlyList<SqlioParm> parms, int cols, Action<SqlioRow> each)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(sql, parms, Widths[..cols], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display(errorPrefix + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        foreach (var row in rows) each(row);
    }

    /// <summary>2900-DATE-RULE: scenario slippage, then a date before the window is brought forward to the first day (overdue).</summary>
    private static Flow DateRule(XmdBatch b, State st, int date, int slip)
    {
        var f = new Flow();
        if (slip != 0)
        {
            date = AddDays(date, slip);
            f.Note = "slipped";
        }
        if (date < b.Args.From)
        {
            f.Date = b.Args.From;
            f.Note = "overdue";
        }
        else
        {
            f.Date = date;
        }
        return f;
    }

    /// <summary>2950-RELEASE: counted as read; released to the sort only inside the window.</summary>
    private static void Release(XmdBatch b, State st, Flow f)
    {
        b.RowsIn++;
        if (f.Date > b.Args.To || f.Date < b.Args.From)
        {
            st.Deferred++;
            return;
        }
        f.Ref = CobolText.Fit(f.Ref, 22);
        f.Src = CobolText.Fit(f.Src, 8);
        f.Party = CobolText.Fit(f.Party, 20);
        f.Cat = CobolText.Fit(f.Cat, 12);
        f.Dir = CobolText.Fit(f.Dir, 3);
        f.Note = CobolText.Fit(f.Note, 18);
        f.SortRef = CobolText.Ansi.GetBytes(f.Ref);
        st.Flows.Add(f);
    }

    /// <summary>3100-ONE-FLOW.</summary>
    private static void OneFlow(XmdBatch b, State st, GnuReportWriter rw, Flow f, JsonArray flows)
    {
        var dDate = FormatDate(st, f.Date);
        decimal dIn = 0, dOut = 0;
        var isIn = f.Dir.TrimEnd(' ') == "IN";
        if (isIn)
        {
            dIn = f.Amt;
            st.DayIn = CobolNumeric.Store(st.DayIn + f.Amt, 13, 2);
            st.TIn = CobolNumeric.Store(st.TIn + f.Amt, 13, 2);
        }
        else
        {
            dOut = f.Amt;
            st.DayOut = CobolNumeric.Store(st.DayOut + f.Amt, 13, 2);
            st.TOut = CobolNumeric.Store(st.TOut + f.Amt, 13, 2);
        }
        switch (f.Cat.TrimEnd(' '))
        {
            case "CONTRACTUAL":
                if (isIn)
                {
                    st.ContrIn = CobolNumeric.Store(st.ContrIn + f.Amt, 13, 2);
                    st.DayContr = CobolNumeric.Store(st.DayContr + f.Amt, 13, 2);
                }
                else
                {
                    st.ContrOut = CobolNumeric.Store(st.ContrOut + f.Amt, 13, 2);
                    st.DayContr = CobolNumeric.Store(st.DayContr - f.Amt, 13, 2);
                }
                break;
            case "CONFIRMED":
                st.ConfOut = CobolNumeric.Store(st.ConfOut + f.Amt, 13, 2);
                st.DayConf = CobolNumeric.Store(st.DayConf - f.Amt, 13, 2);
                break;
            default:
                if (isIn)
                {
                    st.ExpIn = CobolNumeric.Store(st.ExpIn + f.Amt, 13, 2);
                    st.DayExp = CobolNumeric.Store(st.DayExp + f.Amt, 13, 2);
                }
                else
                {
                    st.ExpOut = CobolNumeric.Store(st.ExpOut + f.Amt, 13, 2);
                    st.DayExp = CobolNumeric.Store(st.DayExp - f.Amt, 13, 2);
                }
                break;
        }
        switch (f.Src.TrimEnd(' '))
        {
            case "AR": st.ArItems++; break;
            case "AP": st.ApItems++; break;
            case "PAYINST": st.ConfItems++; break;
            default: st.ExpItems++; break;
        }
        b.RowsOut++;
        rw.Generate(RwLine.Plus1(() => new RwText().Put(1, dDate, 10).Put(12, f.Src, 8).Put(21, f.Ref, 22).Put(44, f.Party, 20).Put(65, f.Cat, 12)
            .Num(78, dIn, BigPic).Num(96, dOut, BigPic).Put(114, f.Note, 18).Text));
        flows.Add(new JsonObject
        {
            ["date"] = IsoDate(f.Date),
            ["source"] = f.Src.TrimEnd(' '),
            ["reference"] = f.Ref.TrimEnd(' '),
            ["party"] = f.Party.TrimEnd(' '),
            ["category"] = f.Cat.TrimEnd(' '),
            ["inGbp"] = CobolNumeric.WithScale(dIn, 2),
            ["outGbp"] = CobolNumeric.WithScale(dOut, 2),
            ["note"] = f.Note.TrimEnd(' '),
        });
    }

    /// <summary>3200-DAY-BREAK: the day movement in its three categories and the carried closing balance.</summary>
    private static void DayBreak(XmdBatch b, State st, GnuReportWriter rw, int date, JsonArray days)
    {
        var yDate = FormatDate(st, date);
        st.RunBal = CobolNumeric.Store(st.RunBal + st.DayIn - st.DayOut, 13, 2);
        rw.Generate(RwLine.Plus1(() => new RwText().Put(1, yDate, 10).Lit(12, "Day movement").Num(27, st.DayContr, MidPic)
            .Num(43, st.DayConf, MidPic).Num(59, st.DayExp, MidPic).Num(78, st.DayIn, BigPic).Num(96, st.DayOut, BigPic)
            .Num(114, st.RunBal, MidPic).Text));
        days.Add(new JsonObject
        {
            ["date"] = IsoDate(date),
            ["contractualGbp"] = CobolNumeric.WithScale(st.DayContr, 2),
            ["confirmedGbp"] = CobolNumeric.WithScale(st.DayConf, 2),
            ["expectedGbp"] = CobolNumeric.WithScale(st.DayExp, 2),
            ["inGbp"] = CobolNumeric.WithScale(st.DayIn, 2),
            ["outGbp"] = CobolNumeric.WithScale(st.DayOut, 2),
            ["closingGbp"] = CobolNumeric.WithScale(st.RunBal, 2),
        });
        if (st.RunBal < st.LowBal)
        {
            st.LowBal = st.RunBal;
            st.LowDate = date;
        }
        st.DayIn = st.DayOut = st.DayContr = st.DayConf = st.DayExp = 0;
    }

    /// <summary>9450-FORMAT-DATE: a non-zero date also overwrites the shared XB-DATE-ED (BRF-XMDBATPR-04).</summary>
    private static string FormatDate(State st, int yyyymmdd)
    {
        if (yyyymmdd == 0) return "00/00/0000";
        st.DateEd = Disp.D8(yyyymmdd);
        return BatchSuite.FormatDate(yyyymmdd);
    }

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Ordinal byte comparison of the sort key (the native collating sequence of GnuCOBOL SORT).</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
