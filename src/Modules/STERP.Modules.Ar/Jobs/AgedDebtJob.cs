using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using static Sterp.Modules.Ar.Jobs.ArReportWriter;

namespace Sterp.Modules.Ar.Jobs;

/// <summary>
/// AGEDDEBT (cobol/batch/ar/AGEDDEBT.cob, FR-STERP-AR-060) - aged receivables analysis as at a date: every open item
/// posted by ASAT with its as-at residual, aged by due date, credits and deposits unaged, customer and company footings
/// and the AR control account comparison (RC 4 STERP-W-1901 above 0.005). Autocommit reads only.
/// </summary>
public static class AgedDebtJob
{
    public const string Code = "AGEDDEBT";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runAgedDebtJob",
        ParameterOrder = ["CO", "ASAT", "CUST", "USER"],
        // 0100-READ-ARGS ends RC 8 before 0200-JOB-START when ASAT is not sensible: the program never registers
        SelfRegistration = pre => SensibleAsAt(pre.Args) ? ArBatch.SelfRegistration(pre, Code) : null,
        EndValues = (ctx, rc) => SensibleAsAt(ctx.Args) ? ArBatch.End(ctx, rc) : JobService.DefaultEnd(ctx, rc, false, null),
        Execute = RunAsync,
    };

    private static bool SensibleAsAt(CommandLineArgs args) => AsAtOf(args) is >= 19900101 and <= 20991231;

    private static int AsAtOf(CommandLineArgs args)
    {
        var asat = CobolText.IsSpaces(args.AsAt) ? 0 : ArBatch.Date8(args.AsAt);
        return asat == 0 ? ArBatch.Today() : asat;
    }

    private sealed class Det
    {
        public string Cust = "";
        public string CName = "";
        public string Type = "";
        public string Doc = "";
        public string DDate = "";
        public string Due = "";
        public int Days;
        public string Curr = "";
        public decimal B0, B1, B2, B3, B4, Cred, Depos, Disp;
        public string Note = "";
        public string Flag = "";
    }

    /// <summary>The SUM counters of a footing (sum_all_detail adds every detail's D- values).</summary>
    private sealed class Sums
    {
        public decimal B0, B1, B2, B3, B4, Cred, Depos, Disp;

        public void Add(Det d)
        {
            B0 += d.B0; B1 += d.B1; B2 += d.B2; B3 += d.B3; B4 += d.B4;
            Cred += d.Cred; Depos += d.Depos; Disp += d.Disp;
        }
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var cust = CobolText.Trimmed(ctx.Args.Get("CUST"), 20);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        var asat = AsAtOf(ctx.Args);
        if (!SensibleAsAt(ctx.Args))
        {
            ctx.Display("STERP-E-0101 ASAT is not a sensible date");
            return 8;
        }
        ctx.Display("STERP-I-0100 AGEDDEBT aged receivables for " + co);

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        var heading = ArBatch.Heading(co, Code, "Aged receivables analysis", user, DateTime.Now);
        heading.AsAtText = "As at " + ArBatch.Uk(asat);
        heading.Subtitle = cust.Length == 0
            ? "All customers - amounts in GBP"
            : CobolText.Trimmed("Customer " + cust.Trim(' ') + " - amounts in GBP", 60);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        if (!ArBatch.TryOpenSpool(ctx, Code, co, out var spool)) return 12;

        var rc = 0;
        var rowsOut = 0;
        var skipped = 0;
        decimal tDebt = 0, tCred = 0, tDepos = 0, tDisp = 0;
        var tItems = 0;
        var tCusts = 0;
        var items = new JsonArray();
        var custSums = new Sums();
        var finalSums = new Sums();
        string? lastCust = null;
        var d = new Det();
        var ctlAcct = "";
        decimal glBal = 0, diff = 0;

        using (spool)
        {
            var rw = new ArReportWriter(spool, heading, firstDetail: 9, lastDetail: 54);

            GroupLine[] chCust(Det x) =>
            [
                L(2, Cols().Put(1, "Customer ", 9).Put(10, x.Cust, 20).Put(31, x.CName, 40)),
                L(1, Cols().Put(1, "Type", 8).Put(10, "Document", 8).Put(27, "Doc date", 8).Put(38, "Due date", 8).Put(49, "Days", 4)
                    .Put(56, "Not yet due", 11).Put(73, "1-30", 4).Put(86, "31-60", 5).Put(100, "61-90", 5).Put(112, "Over 90   ", 10)
                    .Put(124, "Dis", 3)),
                L(1, new string('-', 126)),
            ];
            GroupLine[] cfCust(Sums s) =>
            [
                L(1, Cols().Put(1, "Customer total", 14).Put(55, ArBatch.M13(s.B0), 13).Put(69, ArBatch.M13(s.B1), 13)
                    .Put(83, ArBatch.M13(s.B2), 13).Put(97, ArBatch.M13(s.B3), 13).Put(111, ArBatch.M13(s.B4), 13)),
                L(1, Cols().Put(3, "of which disputed (still owed)", 34).Put(55, ArBatch.M13(s.Disp), 13).Put(70, "Unapplied cr", 13)
                    .Put(83, ArBatch.M13(s.Cred), 13).Put(97, ArBatch.M13(s.Depos), 13)),
                L(1, new string('.', 126)),
            ];

            // GENERATE DE-DEBT / DE-CRED with the D-CUST control break
            void Generate(Det x, bool credit)
            {
                if (rw.Begin())
                {
                    rw.Group(chCust(x));
                }
                else if (lastCust != x.Cust)
                {
                    rw.Group(cfCust(custSums));
                    custSums = new Sums();
                    rw.Group(chCust(x));
                }
                lastCust = x.Cust;
                custSums.Add(x);
                finalSums.Add(x);
                rw.Detail(credit
                    ? L(1, Cols().Put(1, x.Type, 8).Put(10, x.Doc, 16).Put(27, x.DDate, 10).Put(38, x.Due, 10).Put(49, x.Curr, 3)
                        .Put(55, x.Note, 22).Put(83, ArBatch.M13(x.Cred), 13).Put(97, ArBatch.M13(x.Depos), 13).Put(125, x.Flag, 3))
                    : L(1, Cols().Put(1, x.Type, 8).Put(10, x.Doc, 16).Put(27, x.DDate, 10).Put(38, x.Due, 10)
                        .Put(48, CobolEdit.Field(x.Days, "ZZZZ9-"), 6).Put(55, ArBatch.M13(x.B0), 13).Put(69, ArBatch.M13(x.B1), 13)
                        .Put(83, ArBatch.M13(x.B2), 13).Put(97, ArBatch.M13(x.B3), 13).Put(111, ArBatch.M13(x.B4), 13).Put(125, x.Flag, 3)));
                rw.End();
            }

            // 1000-PRODUCE-REPORT
            var asatText = ArBatch.D8(asat);
            var stmt =
                "SELECT o.CustomerCode, c.LegalName, o.ItemType, o.DocumentNumber, CONVERT(char(8),o.DocumentDate,112), " +
                "CONVERT(char(8),o.DueDate,112), o.CurrencyCode, CONVERT(varchar(24),o.OriginalAmountGBP), CONVERT(varchar(24), " +
                "o.OriginalAmountGBP - ISNULL((SELECT SUM(a.AppliedAmountGBP + a.DiscountAmountGBP + a.WriteOffAmountGBP) FROM " +
                "ar.Allocation a WHERE a.TargetOpenItemId = o.OpenItemId AND a.Status = 'ACTIVE' AND a.EffectiveDate <= ?),0) + " +
                "ISNULL((SELECT SUM(b.AppliedAmountGBP) FROM ar.Allocation b WHERE b.SourceOpenItemId = o.OpenItemId AND " +
                "b.Status = 'ACTIVE' AND b.EffectiveDate <= ?),0)), CASE WHEN o.IsDisputed = 1 THEN 'Y' ELSE 'N' END, " +
                "CONVERT(varchar(24),o.DisputedAmountGBP), o.CollectionStatus, ISNULL(c.ArControlAccount,''), c.Status FROM " +
                "ar.OpenItem o JOIN ar.Customer c ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode WHERE " +
                "o.CompanyCode = ? AND o.PostingDate <= ? AND o.SettlementStatus <> 'REVERSED'";
            var parms = new List<SqlioParm> { SqlioParm.Text(asatText), SqlioParm.Text(asatText), SqlioParm.Text(co), SqlioParm.Text(asatText) };
            if (cust.Length > 0)
            {
                parms.Add(SqlioParm.Text(cust));
                stmt += " AND o.CustomerCode = ?";
            }
            stmt += " ORDER BY o.CustomerCode, o.DueDate,o.DocumentNumber";
            IReadOnlyList<SqlioRow> rows = [];
            try
            {
                rows = await sql.FetchAllAsync(stmt, parms, ArBatch.Widths(30, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var row in rows)
            {
                // 1100-CLASSIFY-ITEM
                var resid = CobolNumeric.Store(row.NumVal(8), 13, 2);
                if (resid > -0.005m && resid < 0.005m)
                {
                    skipped++;
                    continue;
                }
                if (d.Cust != CobolText.Trimmed(row.Raw(0), 20)) tCusts++;
                d = new Det
                {
                    Cust = CobolText.Trimmed(row.Raw(0), 20),
                    CName = CobolText.Trimmed(row.Raw(1), 40),
                    Type = CobolText.Trimmed(row.Raw(2), 10),
                    Doc = CobolText.Trimmed(row.Raw(3), 16),
                    Curr = CobolText.Trimmed(row.Raw(6), 3),
                };
                d.DDate = ArBatch.Uk(ArBatch.Date8(row.Raw(4)[..8]));
                var dueN = ArBatch.Date8(row.Raw(5)[..8]);
                d.Due = ArBatch.Uk(dueN);
                d.Days = (int)CobolNumeric.Store(CobolDate.IntegerOfDate(asat) - CobolDate.IntegerOfDate(dueN), 5, 0);
                if (row.Raw(9).StartsWith('Y')) d.Flag = "Dis";
                if (row.Raw(13).StartsWith("RETIRED", StringComparison.Ordinal)) d.Flag = "Ret";
                tItems++;
                rowsOut++;
                var credit = resid < 0;
                if (credit)
                {
                    // 1200-CREDIT-LINE
                    if (d.Type.StartsWith("DEPOSIT", StringComparison.Ordinal))
                    {
                        d.Depos = CobolNumeric.Store(0 - resid, 11, 2);
                        tDepos += d.Depos;
                        d.Note = "Deposit held";
                    }
                    else
                    {
                        d.Cred = CobolNumeric.Store(0 - resid, 11, 2);
                        tCred += d.Cred;
                        d.Note = d.Type.StartsWith("RECEIPT", StringComparison.Ordinal) ? "Unapplied receipt" : "Unapplied credit";
                    }
                    d.Due = "";
                }
                else
                {
                    // 1300-DEBIT-LINE
                    var r = CobolNumeric.Store(resid, 11, 2);
                    if (d.Days <= 0) d.B0 = r;
                    else if (d.Days <= 30) d.B1 = r;
                    else if (d.Days <= 60) d.B2 = r;
                    else if (d.Days <= 90) d.B3 = r;
                    else d.B4 = r;
                    tDebt += resid;
                    if (d.Flag == "Dis")
                    {
                        d.Disp = CobolNumeric.Store(row.NumVal(10), 11, 2);
                        if (d.Disp > resid) d.Disp = r;
                        tDisp += d.Disp;
                    }
                }
                Generate(d, credit);
                items.Add(new JsonObject
                {
                    ["customerCode"] = d.Cust, ["customerName"] = d.CName, ["itemType"] = d.Type, ["documentNumber"] = d.Doc,
                    ["documentDate"] = d.DDate, ["dueDate"] = d.Due, ["daysOverdue"] = d.Days, ["currencyCode"] = d.Curr,
                    ["notYetDue"] = d.B0, ["days1To30"] = d.B1, ["days31To60"] = d.B2, ["days61To90"] = d.B3, ["over90"] = d.B4,
                    ["unappliedCredit"] = d.Cred, ["depositHeld"] = d.Depos, ["disputed"] = d.Disp, ["note"] = d.Note, ["flag"] = d.Flag,
                });
            }

            // 1900-CONTROL-ACCOUNT
            try
            {
                var row = await sql.SingleAsync(
                    "SELECT ISNULL(MIN(ac.AccountCode),'none'), CONVERT(varchar(24),ISNULL(SUM(l.DebitAmountGBP - l.CreditAmountGBP),0)) " +
                    "FROM fin.Account ac LEFT JOIN fin.JournalLine l ON l.CompanyCode = ac.CompanyCode AND l.AccountCode = ac.AccountCode " +
                    "LEFT JOIN fin.Journal j ON j.JournalId = l.JournalId AND j.Status = 'POSTED' AND j.PostingDate <= ? " +
                    "WHERE ac.CompanyCode = ? AND ac.ControlType = 'AR'",
                    [SqlioParm.Text(asatText), SqlioParm.Text(co)], ArBatch.Widths(30, 120), ct);
                if (row.Found)
                {
                    ctlAcct = CobolText.Trimmed(row.Raw(0), 12);
                    glBal = CobolNumeric.Store(row.NumVal(1), 13, 2);
                }
                else
                {
                    ctlAcct = "n/a";
                }
            }
            catch (SqlioException)
            {
                ctlAcct = "n/a";
            }
            diff = CobolNumeric.Store(tDebt - tCred - tDepos - glBal, 13, 2);
            if (diff is > 0.005m or < -0.005m)
            {
                rc = 4;
                ctx.Display("STERP-W-1901 ledger and control account differ - see ARRECON");
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-AGEDDEBT
            rw.Terminate(() =>
            {
                rw.Group(cfCust(custSums));
                rw.Group(
                    L(2, Cols().Put(1, "Company totals", 16).Put(55, ArBatch.M13(finalSums.B0), 13).Put(69, ArBatch.M13(finalSums.B1), 13)
                        .Put(83, ArBatch.M13(finalSums.B2), 13).Put(97, ArBatch.M13(finalSums.B3), 13).Put(111, ArBatch.M13(finalSums.B4), 13)),
                    L(1, Cols().Put(3, "Gross overdue debt", 24).Put(29, ArBatch.M17(tDebt), 17).Put(53, "Disputed included", 21)
                        .Put(76, ArBatch.M17(tDisp), 17)),
                    L(1, Cols().Put(3, "Unapplied credits", 24).Put(29, ArBatch.M17(tCred), 17).Put(53, "Deposits held", 21)
                        .Put(76, ArBatch.M17(tDepos), 17)),
                    L(2, Cols().Put(3, "GL control account", 28).Put(32, ctlAcct, 12).Put(46, ArBatch.M17(glBal), 17).Put(70, "Difference", 16)
                        .Put(88, ArBatch.M17(diff), 17)),
                    L(1, Cols().Put(3, "Items listed", 24).Put(29, CobolEdit.Field(tItems, "ZZZ,ZZ9"), 7).Put(40, "Customers", 12)
                        .Put(53, CobolEdit.Field(tCusts, "ZZZ,ZZ9"), 7).Put(64, "Settled before as at", 22)
                        .Put(88, CobolEdit.Field(skipped, "ZZZ,ZZ9"), 7)),
                    L(2, Cols().Put(3, "Credit balances are not aged as overdue debt.", 48).Put(52, "Buckets are by due date; an item is not", 44)
                        .Put(92, "overdue on its due date.", 28)));
            });
        }
        ctx.Display("STERP-I-0401 " + ArBatch.Num(rowsOut, 9) + " open item(s) aged");
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = rowsOut;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["asAt"] = ArBatch.D8(asat), ["customerCode"] = cust, ["items"] = items,
            ["itemsListed"] = tItems, ["customers"] = tCusts, ["settledBeforeAsAt"] = skipped, ["grossOverdueDebt"] = tDebt,
            ["disputedIncluded"] = tDisp, ["unappliedCredits"] = tCred, ["depositsHeld"] = tDepos, ["controlAccount"] = ctlAcct,
            ["controlAccountBalance"] = glBal, ["difference"] = diff, ["returnCode"] = rc,
        };
        return rc;
    }
}
