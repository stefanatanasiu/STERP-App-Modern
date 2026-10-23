using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Services;
using static Sterp.Modules.Ar.Jobs.ArReportWriter;

namespace Sterp.Modules.Ar.Jobs;

/// <summary>
/// ARRECON (cobol/batch/ar/ARRECON.cob, FR-STERP-AR-064) - sales ledger control account proof as at a date (PERIOD end date
/// or ASAT): ledger by item type, the AR control accounts, the proof and the adjustments; a difference above 0.005 sets
/// RC 4 and raises a priority-1 WARNING exception for FINCTRL after the listing is closed. Autocommit.
/// </summary>
public static class ArreconJob
{
    public const string Code = "ARRECON";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runDebtorsReconciliationJob",
        ParameterOrder = ["CO", "PERIOD", "ASAT", "USER"],
        // 0100-READ-ARGS: neither PERIOD nor ASAT ends RC 8 before 0200-JOB-START (no registration)
        SelfRegistration = pre => HasBasis(pre.Args) ? ArBatch.SelfRegistration(pre, Code) : null,
        EndValues = (ctx, rc) => HasBasis(ctx.Args) ? ArBatch.End(ctx, rc) : JobService.DefaultEnd(ctx, rc, false, null),
        Execute = RunAsync,
    };

    private static bool HasBasis(CommandLineArgs args) =>
        !CobolText.IsSpaces(args.Period) || (!CobolText.IsSpaces(args.AsAt) && ArBatch.Date8(args.AsAt) != 0);

    /// <summary>WS-DET: the detail line source fields.</summary>
    private sealed class Det
    {
        public string Label = "", Ref = "", Date = "", Name = "";
        public decimal Amt1, Amt2;

        public void Clear()
        {
            Label = Ref = Date = Name = "";
        }
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var period = CobolText.Trimmed(ctx.Args.Period, 6);
        var asat = CobolText.IsSpaces(ctx.Args.AsAt) ? 0 : ArBatch.Date8(ctx.Args.AsAt);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        if (period.Length == 0 && asat == 0)
        {
            ctx.Display("STERP-E-0105 give PERIOD=yyyypp or ASAT=");
            return 8;
        }
        ctx.Display("STERP-I-0100 ARRECON control account proof for " + co);
        ctx.RowsProcessed = 0;

        // 0150-RESOLVE-DATE
        var pstat = "n/a";
        if (period.Length > 0)
        {
            var fy = (long)CobolNumeric.Store(CobolNumeric.NumVal(CobolText.Fit(period, 6)[..4]), 4, 0, signed: false);
            var pp = (long)CobolNumeric.Store(CobolNumeric.NumVal(CobolText.Fit(period, 6)[4..6]), 2, 0, signed: false);
            SqlioRow? row = null;
            try
            {
                row = await sql.SingleAsync("SELECT CONVERT(char(8),EndDate,112), Status FROM fin.Period WHERE CompanyCode = ? AND FiscalYear = ? AND PeriodNo = ?",
                    [SqlioParm.Text(co), SqlioParm.Integer(fy), SqlioParm.Integer(pp)], ArBatch.Widths(20, 120), ct);
            }
            catch (SqlioException)
            {
                row = null;
            }
            if (row is not { Found: true })
            {
                ctx.Display("STERP-E-0151 period not found");
                return 8;
            }
            asat = ArBatch.Date8(row.Raw(0)[..8]);
            pstat = CobolText.Trimmed(row.Raw(1), 8);
        }

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS (8100-FORMAT-DATE leaves WS-UK set; 4100 changes it again)
        var uk = ArBatch.Uk(asat);
        var heading = ArBatch.Heading(co, Code, "Sales ledger control account reconciliation", user, DateTime.Now);
        heading.AsAtText = "As at " + uk;
        heading.Subtitle = period.Length == 0
            ? "Date basis - amounts in GBP"
            : CobolText.Trimmed("Period " + period.Trim(' ') + " (" + pstat.Trim(' ') + ") - amounts in GBP", 60);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        if (!ArBatch.TryOpenSpool(ctx, Code, co, out var spool)) return 12;

        var rc = 0;
        var rows = 0;
        decimal kDebt = 0, kCredit = 0, kCash = 0, kDeposit = 0, kAllow = 0, kLedger = 0, kGl = 0, kDiff = 0, direct = 0;
        long kItems = 0;
        var glCnt = 0;
        var adjCnt = 0;
        var x = new Det();
        var types = new JsonArray();
        var accounts = new JsonArray();
        var adjustments = new JsonArray();
        var asatText = ArBatch.D8(asat);

        using (spool)
        {
            var rw = new ArReportWriter(spool, heading, firstDetail: 8, lastDetail: 54);

            void Gen(string text)
            {
                rw.Begin();
                rw.Detail(L(1, text));
                rw.End();
            }
            void DeLine() => Gen(Cols().Put(1, x.Label, 38).Put(40, x.Ref, 20).Put(61, x.Date, 10).Put(72, x.Name, 24)
                .Put(97, ArBatch.M17(x.Amt1), 17).Put(115, ArBatch.M17(x.Amt2), 17).Text);
            void PutText()
            {
                Gen(Cols().Put(1, x.Label, 38).Put(40, x.Ref, 20).Put(61, x.Date, 10).Put(72, x.Name, 24).Text);
                x.Clear();
            }
            void PutRule() => Gen(new string('-', 132));
            void PutGap() => Gen("");

            // 1000-SUBLEDGER
            x.Label = "Sales ledger detail by item type";
            x.Ref = "Posted to date";
            x.Name = "Outstanding";
            PutText();
            PutRule();
            IReadOnlyList<SqlioRow>? found = null;
            try
            {
                found = await sql.FetchAllAsync(
                    "SELECT t.ItemType, CONVERT(varchar(24),SUM(t.Orig)), CONVERT(varchar(24),SUM(t.Resid)), CONVERT(varchar(12),COUNT(*)) FROM " +
                    "(SELECT o.ItemType, o.OriginalAmountGBP AS Orig, o.OriginalAmountGBP - ISNULL((SELECT SUM(a.AppliedAmountGBP + " +
                    "a.DiscountAmountGBP + a.WriteOffAmountGBP) FROM ar.Allocation a WHERE a.TargetOpenItemId = o.OpenItemId AND a.Status = " +
                    "'ACTIVE' AND a.EffectiveDate <= ?),0) + ISNULL((SELECT SUM(b.AppliedAmountGBP) FROM ar.Allocation b WHERE " +
                    "b.SourceOpenItemId = o.OpenItemId AND b.Status = 'ACTIVE' AND b.EffectiveDate <= ?),0) AS Resid FROM ar.OpenItem o WHERE " +
                    "o.CompanyCode = ? AND o.PostingDate <= ? AND o.SettlementStatus <> 'REVERSED') t GROUP BY t.ItemType ORDER BY t.ItemType",
                    [SqlioParm.Text(asatText), SqlioParm.Text(asatText), SqlioParm.Text(co), SqlioParm.Text(asatText)], ArBatch.Widths(20, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            if (found is not null)
            {
                foreach (var r in found)
                {
                    // 1100-ONE-TYPE
                    x.Clear();
                    x.Label = CobolText.Trimmed(r.Raw(0), 38);
                    x.Amt1 = CobolNumeric.Store(r.NumVal(1), 13, 2);
                    x.Amt2 = CobolNumeric.Store(r.NumVal(2), 13, 2);
                    var n = (long)CobolNumeric.Store(r.NumVal(3), 9, 0);
                    x.Ref = Domain.ArEdit.Int(n);
                    kItems += n;
                    kLedger += x.Amt2;
                    switch (CobolText.Fit(r.Raw(0), 10))
                    {
                        case "INVOICE   ":
                        case "INTEREST  ":
                            kDebt += x.Amt2;
                            break;
                        case "CREDIT    ":
                            kCredit += x.Amt2;
                            break;
                        case "RECEIPT   ":
                            kCash += x.Amt2;
                            break;
                        case "DEPOSIT   ":
                            kDeposit += x.Amt2;
                            break;
                        default:
                            kAllow += x.Amt2;
                            break;
                    }
                    rows++;
                    types.Add(new JsonObject { ["itemType"] = x.Label, ["items"] = n, ["postedToDate"] = x.Amt1, ["outstanding"] = x.Amt2 });
                    DeLine();
                }
                PutRule();
            }

            // 2000-CONTROL-ACCOUNTS
            PutGap();
            x.Label = "General ledger AR control accounts";
            x.Ref = "Account";
            x.Name = "Balance / direct";
            PutText();
            PutRule();
            found = null;
            try
            {
                found = await sql.FetchAllAsync(
                    "SELECT ac.AccountCode, ac.Name, CONVERT(varchar(24),ISNULL(SUM(l.DebitAmountGBP - l.CreditAmountGBP),0)), " +
                    "CONVERT(varchar(24),ISNULL(SUM(CASE WHEN ISNULL(l.SubledgerType,'') <> 'AR' THEN l.DebitAmountGBP - l.CreditAmountGBP ELSE 0 " +
                    "END),0)) FROM fin.Account ac LEFT JOIN fin.JournalLine l ON l.CompanyCode = ac.CompanyCode AND l.AccountCode = ac.AccountCode " +
                    "LEFT JOIN fin.Journal j ON j.JournalId = l.JournalId AND j.Status = 'POSTED' AND j.PostingDate <= ? WHERE ac.CompanyCode = ? AND " +
                    "ac.ControlType = 'AR' GROUP BY ac.AccountCode, ac.Name ORDER BY ac.AccountCode",
                    [SqlioParm.Text(asatText), SqlioParm.Text(co)], ArBatch.Widths(20, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-2001 " + CobolText.Fit(ex.DriverText, 100));
            }
            if (found is not null)
            {
                foreach (var r in found)
                {
                    // 2100-ONE-ACCOUNT
                    glCnt++;
                    x.Clear();
                    x.Label = CobolText.Trimmed(r.Raw(1), 38);
                    x.Ref = CobolText.Trimmed(r.Raw(0), 20);
                    x.Amt1 = CobolNumeric.Store(r.NumVal(2), 13, 2);
                    x.Amt2 = CobolNumeric.Store(r.NumVal(3), 13, 2);
                    kGl += x.Amt1;
                    direct += x.Amt2;
                    if (x.Amt2 != 0) x.Name = "direct journals";
                    accounts.Add(new JsonObject { ["accountCode"] = x.Ref, ["name"] = x.Label, ["balance"] = x.Amt1, ["directJournals"] = x.Amt2 });
                    DeLine();
                }
                PutRule();
                if (glCnt == 0)
                {
                    rc = 4;
                    x.Label = "No AR control account is designated";
                    x.Ref = x.Date = x.Name = "";
                    PutText();
                }
            }

            // 3000-PROOF
            PutGap();
            x.Label = "Reconciliation proof";
            x.Ref = x.Date = x.Name = "";
            PutText();
            PutRule();
            x.Clear();
            x.Label = "Outstanding invoices and debit items";
            x.Amt1 = kDebt;
            x.Amt2 = 0;
            DeLine();
            x.Label = "less unapplied credit notes";
            x.Amt1 = kCredit;
            DeLine();
            x.Label = "less cash on account (unapplied)";
            x.Amt1 = kCash;
            DeLine();
            x.Label = "less customer deposits held";
            x.Amt1 = kDeposit;
            DeLine();
            x.Label = "adjustments, allowances and write offs";
            x.Amt1 = kAllow;
            DeLine();
            PutRule();
            x.Label = "Sales ledger balance";
            x.Amt1 = 0;
            x.Amt2 = kLedger;
            DeLine();
            x.Label = "General ledger control account balance";
            x.Amt2 = kGl;
            DeLine();
            kDiff = CobolNumeric.Store(kLedger - kGl, 13, 2);
            x.Clear();
            x.Label = "Difference (ledger less control)";
            x.Amt1 = 0;
            x.Amt2 = kDiff;
            DeLine();
            PutRule();
            if (kDiff is > 0.005m or < -0.005m)
            {
                rc = 4;
                x.Clear();
                x.Label = "OUT OF BALANCE - an exception has been";
                x.Ref = "raised for the owner";
                PutText();
                if (direct != 0)
                {
                    x.Clear();
                    x.Label = "Journals posted direct to a control";
                    x.Ref = "account";
                    x.Amt2 = direct;
                    x.Amt1 = 0;
                    DeLine();
                }
            }
            else
            {
                x.Clear();
                x.Label = "Sales ledger agrees with the general";
                x.Ref = "ledger control account";
                PutText();
            }

            // 4000-ADJUSTMENTS
            PutGap();
            x.Label = "Adjustments and write offs to the date";
            x.Ref = "Document";
            x.Date = "Posted";
            x.Name = "Customer";
            PutText();
            PutRule();
            found = null;
            try
            {
                found = await sql.FetchAllAsync(
                    "SELECT TOP 200 o.DocumentNumber, o.ItemType, o.CustomerCode, CONVERT(char(8),o.PostingDate,112), " +
                    "CONVERT(varchar(24),o.OriginalAmountGBP), ISNULL(o.CreatedBy,'') FROM ar.OpenItem o WHERE o.CompanyCode = ? AND " +
                    "o.ItemType IN ('ADJUSTMENT','WRITEOFF','INTEREST') AND o.PostingDate <= ? ORDER BY o.PostingDate DESC, o.DocumentNumber",
                    [SqlioParm.Text(co), SqlioParm.Text(asatText)], ArBatch.Widths(20, 120), ct);
            }
            catch (SqlioException)
            {
                found = null;
            }
            if (found is not null)
            {
                foreach (var r in found)
                {
                    // 4100-ONE-ADJUSTMENT
                    adjCnt++;
                    x.Clear();
                    x.Label = CobolText.Trimmed(r.Raw(1), 38);
                    x.Ref = CobolText.Trimmed(r.Raw(0), 20);
                    uk = ArBatch.Uk(ArBatch.Date8(r.Raw(3)[..8]));
                    x.Date = uk;
                    x.Name = CobolText.Trimmed(r.Raw(2), 24);
                    x.Amt1 = CobolNumeric.Store(r.NumVal(4), 13, 2);
                    x.Amt2 = 0;
                    adjustments.Add(new JsonObject
                    {
                        ["itemType"] = x.Label, ["documentNumber"] = x.Ref, ["postingDate"] = x.Date, ["customerCode"] = x.Name, ["amount"] = x.Amt1,
                    });
                    DeLine();
                }
                if (adjCnt == 0)
                {
                    x.Clear();
                    x.Label = "No adjustments or write offs recorded";
                    PutText();
                }
                PutRule();
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-ARRECON
            rw.Terminate(() => rw.Group(
                L(2, Cols().Put(1, "Open items reconstructed as at the date", 40).Put(42, CobolEdit.Field(kItems, "ZZZ,ZZZ,ZZ9"), 11)
                    .Put(60, "Control accounts proved", 30).Put(92, CobolEdit.Field(glCnt, "ZZZZ9"), 5)),
                L(1, Cols().Put(1, "Balances are derived from posted documents only.", 48)
                    .Put(50, "A customer balance is never edited directly.", 44))));
        }
        ctx.Display("STERP-I-0401 difference GBP " + Domain.ArEdit.Money(kDiff));
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = rows;

        if (kDiff is > 0.005m or < -0.005m)
        {
            // 5000-RAISE-DIFFERENCE (WS-UK is the last date 8100-FORMAT-DATE formatted)
            var ex = new ExcpraisArea
            {
                Module = "AR",
                Severity = "WARNING",
                Category = "CONTROL ACCOUNT DIFFERENCE",
                DocumentType = "RECONCILIATION",
                DocumentRef = period.Length == 0 ? asatText : period,
                OwnerUser = user,
                OwnerRole = "FINCTRL",
                Priority = 1,
                DueAt = asat,
                AmountGbp = kDiff,
                MessageText = CobolText.Trimmed("Sales ledger and AR control account differ by GBP " + Domain.ArEdit.MoneyUk(kDiff) + " as at " + uk +
                    " - see the ARRECON proof", 200),
            };
            ex.Header.Prepare(new KernelCaller(co, "", user, asat, ""), "RAISE");
            await ctx.Get<IExceptionRaiser>().ExecuteAsync(ex, ct);
            if (ex.Header.ReturnCode > 6) ctx.Display("STERP-W-5001 exception not raised");
        }

        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["period"] = period, ["periodStatus"] = pstat, ["asAt"] = asatText,
            ["itemTypes"] = types, ["controlAccounts"] = accounts, ["openItems"] = kItems, ["outstandingDebit"] = kDebt,
            ["unappliedCredits"] = kCredit, ["unappliedCash"] = kCash, ["depositsHeld"] = kDeposit, ["adjustments"] = kAllow,
            ["ledgerBalance"] = kLedger, ["controlBalance"] = kGl, ["difference"] = kDiff, ["directJournals"] = direct,
            ["adjustmentItems"] = adjustments, ["returnCode"] = rc,
        };
        return rc;
    }
}
