
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
/// DDCOLL (cobol/batch/ar/DDCOLL.cob, FR-STERP-AR-063/070) - Bacs Direct Debit collection run: the processing date is
/// DUEDATE adjusted to the next Bacs day, the submission date one Bacs day earlier; one collection per active mandate
/// with overdue undisputed items whose advance notice has expired, written to a Bacs Standard 18 file (100-byte records
/// with CR LF) and, in COMMIT, recorded as SUBMITTED. The Bacs file is the run's log file (JOBLOG END). Autocommit.
/// </summary>
public static class DdcollJob
{
    public const string Code = "DDCOLL";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runDirectDebitCollectionJob",
        ParameterOrder = ["CO", "MODE", "DUEDATE", "BANK", "ASAT", "SUN", "USER"],
        // 0100-READ-ARGS: a MODE other than PREVIEW/COMMIT ends RC 8 before 0200-JOB-START
        SelfRegistration = pre => ValidMode(pre.Args) ? ArBatch.SelfRegistration(pre, Code) : null,
        EndValues = (ctx, rc) => ValidMode(ctx.Args)
            ? ArBatch.End(ctx, rc) with { LogFile = ctx.SpoolFiles.FirstOrDefault(IsBacsFile) }
            : JobService.DefaultEnd(ctx, rc, false, null),
        Execute = RunAsync,
    };

    private static bool IsBacsFile(string path) => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static string ModeOf(CommandLineArgs args)
    {
        var mode = CobolText.Trimmed(args.Mode.ToUpperInvariant(), 10);
        return mode.Length == 0 ? "PREVIEW" : mode;
    }

    private static bool ValidMode(CommandLineArgs args) => ModeOf(args) is "PREVIEW" or "COMMIT";

    /// <summary>8300-JULIAN: " yyddd" (leading space, two-digit year, day of the year).</summary>
    private static string Julian(int yyyymmdd)
    {
        var days = CobolDate.IntegerOfDate(yyyymmdd);
        var ddd = days > 0 ? CobolDate.DateOfInteger(days).DayOfYear : 0;
        return " " + ArBatch.D8(yyyymmdd)[2..4] + (ddd % 1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A fixed 100-byte Standard 18 record built by position (WS-BACREC).</summary>
    private sealed class BacsRecord
    {
        private readonly char[] _c = Enumerable.Repeat(' ', 100).ToArray();

        public BacsRecord At(int pos, string value, int len)
        {
            var v = CobolText.Fit(value, len);
            for (var i = 0; i < len; i++) _c[pos - 1 + i] = v[i];
            return this;
        }

        public override string ToString() => new(_c);
    }

    private static string Digits(decimal value, int width)
    {
        var limit = 1m;
        for (var i = 0; i < width; i++) limit *= 10m;
        return (decimal.Truncate(Math.Abs(value)) % limit).ToString(new string('0', width), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>B-PAY: one Standard 18 payment (or contra) record.</summary>
    private static string Payment(string destSort, string destAcct, string txn, string origSort, string origAcct, decimal pence, string origName,
        string reference, string destName) =>
        CobolText.Fit(destSort, 6) + CobolText.Fit(destAcct, 8) + "0" + CobolText.Fit(txn, 2) + CobolText.Fit(origSort, 6) +
        CobolText.Fit(origAcct, 8) + "0000" + Digits(pence, 11) + CobolText.Fit(origName, 18) + CobolText.Fit(reference, 18) +
        CobolText.Fit(destName, 18);

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var cal = ctx.Get<IBusinessCalendar>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var mode = ModeOf(ctx.Args);
        var due = ctx.Args.Get("DUEDATE") is { } dd ? ArBatch.Date8(dd) : 0;
        var bankCode = CobolText.Trimmed(ctx.Args.Get("BANK"), 20);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        if (due == 0) due = ArBatch.Today();
        if (!ValidMode(ctx.Args))
        {
            ctx.Display("STERP-E-0103 MODE must be PREVIEW or COMMIT");
            return 8;
        }
        var commit = mode == "COMMIT";
        ctx.Display("STERP-I-0100 DDCOLL direct debit run " + mode);
        ctx.RowsProcessed = 0;
        var rc = 0;

        // 0250-BACS-DATES
        int procDate, subDate;
        var dc = new DatecalcArea { Date1 = due, Calendar = "BACS", Direction = "NEXT" };
        dc.Header.Prepare(new KernelCaller(co, "", user, 0, ""), "ADJUST");
        await cal.ExecuteAsync(dc, ct);
        if (dc.Header.ReturnCode > 4)
        {
            procDate = due;
            rc = 4;
            ctx.Display("STERP-W-0251 Bacs calendar unavailable");
        }
        else
        {
            procDate = dc.ResultDate;
        }
        dc = new DatecalcArea { Date1 = procDate, Calendar = "BACS", Number = -1 };
        dc.Header.Prepare(new KernelCaller(co, "", user, 0, ""), "ADDBUS");
        await cal.ExecuteAsync(dc, ct);
        subDate = dc.Header.ReturnCode > 4 ? procDate : dc.ResultDate;
        var julProc = Julian(procDate);
        var julToday = Julian(ArBatch.Today());

        // 0260-ORIGINATOR
        var stmt = "SELECT TOP 1 BankAccountCode, ISNULL(SortCode,'000000'), ISNULL(AccountNumber,'00000000'), " +
            "ISNULL(BacsServiceUserNumber,'000000'), Name FROM fin.BankAccount WHERE CompanyCode = ? AND SupportsBacs = 1 AND " +
            "Status = 'ACTIVE' AND CurrencyCode = 'GBP'";
        var parms = new List<SqlioParm> { SqlioParm.Text(co) };
        if (bankCode.Length > 0)
        {
            parms.Add(SqlioParm.Text(bankCode));
            stmt += " AND BankAccountCode = ?";
        }
        stmt += " ORDER BY BankAccountCode";
        SqlioRow? orig;
        try
        {
            orig = await sql.SingleAsync(stmt, parms, ArBatch.Widths(20, 120), ct);
        }
        catch (SqlioException)
        {
            orig = null;
        }
        if (orig is not { Found: true })
        {
            ctx.Display("STERP-E-0261 no Bacs capable bank account");
            return 8;
        }
        var oSort = CobolText.Fit(orig.Raw(1), 6);
        var oAcct = CobolText.Fit(orig.Raw(2), 8);
        var oSun = CobolText.Fit(orig.Raw(3), 6);
        var oName = CobolText.Fit(orig.Raw(4), 18);

        // 0300-OPEN-FILES: the listing and the Bacs file share the RUNPATH timestamp
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        ctx.Paths.Build("SPOOL", Code, co, null, stamp, true, out var spoolName);
        ctx.Paths.Build("BACS", Code, co, null, stamp, true, out var bacsName);
        ctx.SpoolFiles.Add(bacsName);
        var heading = ArBatch.Heading(co, Code, "Bacs Direct Debit collection run", user, DateTime.Now);
        heading.AsAtText = "Processing " + ArBatch.Uk(procDate);
        heading.Subtitle = CobolText.Trimmed("Submit " + ArBatch.Uk(subDate) + " - mode " + mode + " - Standard 18 file", 60);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        SpoolWriter spool;
        FileStream bacs;
        try
        {
            spool = new SpoolWriter(spoolName);
            ctx.SpoolFiles.Add(spoolName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Display("STERP-E-0302 cannot open " + CobolText.Fit(spoolName, 200));
            return 12;
        }
        try
        {
            bacs = new FileStream(bacsName, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            spool.Dispose();
            ctx.Display("STERP-E-0303 cannot open " + CobolText.Fit(bacsName, 200));
            return 12;
        }

        var rows = 0;
        int tSelected = 0, tCollected = 0, tRejected = 0, seq = 0;
        decimal tValue = 0, totPence = 0;
        var mandates = new JsonArray();

        using (spool)
        using (bacs)
        {
            void PutBacs(string record)
            {
                // 8900-PUT-BACS: BAC-DATA X(100) + BAC-EOL X"0D0A"
                var bytes = CobolText.Ansi.GetBytes(CobolText.Fit(record, 100) + "\r\n");
                bacs.Write(bytes, 0, bytes.Length);
            }

            var rw = new ArReportWriter(spool, heading, firstDetail: 8, lastDetail: 54);

            // 0380-BACS-HEADER
            PutBacs(new BacsRecord().At(1, "VOL1", 4).At(5, oSun, 6).At(42, oSun, 6).At(80, "1", 1).ToString());
            PutBacs(new BacsRecord().At(1, "HDR1", 4).At(5, "A", 1).At(6, oSun, 6).At(12, "S", 1).At(13, oSun, 6).At(19, "0001", 4)
                .At(23, "0001", 4).At(27, "0001", 4).At(31, "01", 2).At(33, julToday, 6).At(39, julProc, 6).At(46, "000000", 6)
                .At(52, "BACS       ", 11).ToString());
            PutBacs(new BacsRecord().At(1, "HDR2", 4).At(5, "F", 1).At(6, "02000", 5).At(11, "00100", 5).ToString());
            PutBacs(new BacsRecord().At(1, "UHL1", 4).At(5, julProc, 6).At(11, "999999", 6).At(17, "0001", 4).At(21, "DD  ", 4)
                .At(44, "00000", 5).ToString());

            // 1000-SELECT-COLLECTIONS
            var dueText = ArBatch.D8(due);
            IReadOnlyList<SqlioRow> found = [];
            try
            {
                found = await sql.FetchAllAsync(
                    "SELECT o.CustomerCode, MAX(c.LegalName), m.MandateReference, m.SortCode, m.AccountNumber, m.PayerName, m.Status, " +
                    "CONVERT(char(8),COALESCE((SELECT MAX(d2.AdvanceNoticeSentAt) FROM ar.DirectDebitCollection d2 WHERE d2.MandateId = " +
                    "m.MandateId AND d2.DueDate = ?), CASE WHEN m.AdvanceNoticeAgreedInWriting = 1 THEN m.LodgedDate ELSE NULL END," +
                    "'19000101'),112), CONVERT(varchar(12),m.AdvanceNoticeDays), CONVERT(varchar(12),CONVERT(int," +
                    "m.AdvanceNoticeAgreedInWriting)), CONVERT(varchar(24),SUM(o.ResidualAmountGBP)), CONVERT(varchar(12),COUNT(*)), " +
                    "LEFT(ISNULL(STRING_AGG(CONVERT(varchar(20),o.OpenItemId),','),''),300), CONVERT(varchar(12),(SELECT COUNT(*) FROM " +
                    "ar.DirectDebitCollection d WHERE d.MandateId = m.MandateId AND d.Status IN ('SUBMITTED','COLLECTED'))), " +
                    "CONVERT(varchar(12),m.MandateId), CONVERT(varchar(12),(SELECT COUNT(*) FROM ar.DirectDebitCollection d3 WHERE " +
                    "d3.MandateId = m.MandateId AND d3.DueDate = ? AND d3.Status IN ('PLANNED','SUBMITTED','COLLECTED'))) FROM " +
                    "ar.OpenItem o JOIN ar.Customer c ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode JOIN " +
                    "ar.DirectDebitMandate m ON m.CompanyCode = o.CompanyCode AND m.CustomerCode = o.CustomerCode WHERE o.CompanyCode = ? " +
                    "AND o.DueDate <= ? AND o.ResidualAmountGBP > 0 AND o.SettlementStatus IN ('OPEN','PARTIAL') AND o.IsDisputed = 0 AND " +
                    "o.WrittenOff = 0 AND c.CollectionMethod = 'DD' AND m.Status <> 'CANCELLED' GROUP BY o.CustomerCode, m.MandateId, " +
                    "m.MandateReference, m.SortCode, m.AccountNumber, m.PayerName, m.Status, m.LodgedDate, m.AdvanceNoticeDays, " +
                    "m.AdvanceNoticeAgreedInWriting ORDER BY o.CustomerCode",
                    [SqlioParm.Text(dueText), SqlioParm.Text(dueText), SqlioParm.Text(co), SqlioParm.Text(dueText)], ArBatch.Widths(20, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var r in found)
            {
                // 1100-ONE-MANDATE
                tSelected++;
                rows++;
                var cust = CobolText.Trimmed(r.Raw(0), 20);
                var name = CobolText.Trimmed(r.Raw(1), 28);
                var mandate = CobolText.Trimmed(r.Raw(2), 18);
                var sort = CobolText.Trimmed(r.Raw(3), 6);
                var acct = CobolText.Trimmed(r.Raw(4), 8);
                var payer = CobolText.Trimmed(r.Raw(5), 18);
                var amt = CobolNumeric.Store(r.NumVal(10), 11, 2);
                var items = (int)CobolNumeric.Store(r.NumVal(11), 5, 0);
                var itemIds = CobolText.Trimmed(r.Raw(12), 120);
                var why = "";
                var txn = "";
                var state = "COLLECT";

                // 1200-CHECK-ELIGIBLE
                if (CobolText.Fit(r.Raw(6), 6) != "ACTIVE")
                {
                    state = "NO MANDATE";
                    why = CobolText.Trimmed("Mandate status " + r.Raw(6).Trim(' ') + " - not collectable", 34);
                }
                else if (CobolNumeric.Store(r.NumVal(15), 9, 0) > 0)
                {
                    state = "DUPLICATE";
                    why = "Already submitted for this date";
                }
                else if (amt < 0.01m)
                {
                    state = "NIL VALUE";
                    why = "Nothing outstanding to collect";
                }
                else
                {
                    var notice = ArBatch.Date8(r.Raw(7)[..8]);
                    if (notice < 19900101)
                    {
                        state = "NO NOTICE";
                        why = "Advance notice has not been sent";
                    }
                    else
                    {
                        var days = (int)CobolNumeric.Store(r.NumVal(8), 7, 0);
                        if (days <= 0) days = 10;
                        var nc = new DatecalcArea { Date1 = notice, Calendar = "BACS", Number = days };
                        nc.Header.Prepare(new KernelCaller(co, "", user, 0, ""), "ADDBUS");
                        await cal.ExecuteAsync(nc, ct);
                        var noticeOk = nc.Header.ReturnCode > 4
                            ? CobolDate.DateOfIntegerYyyymmdd(CobolDate.IntegerOfDate(notice) + days)
                            : nc.ResultDate;
                        if (procDate < noticeOk)
                        {
                            state = "TOO EARLY";
                            why = "Advance notice expires " + ArBatch.Uk(noticeOk);
                        }
                    }
                }

                var entry = new JsonObject
                {
                    ["customerCode"] = cust, ["name"] = name, ["mandateReference"] = mandate, ["sortCode"] = sort, ["accountNumber"] = acct,
                    ["items"] = items, ["amountGbp"] = amt, ["outcome"] = state, ["reason"] = why, ["openItemIds"] = itemIds,
                };
                GroupLine[] Lines() =>
                [
                    L(1, Cols().Put(1, cust, 20).Put(22, name, 28).Put(51, mandate, 18).Put(70, sort, 6).Put(80, acct, 8)
                        .Put(90, CobolEdit.Field(items, "ZZZZ9"), 5).Put(96, ArBatch.M13(amt), 13).Put(113, txn, 2).Put(117, state, 10)),
                    L(1, Cols().Put(22, why, 34).Put(60, "Open items", 10).Put(71, itemIds, 60)),
                ];
                if (state != "COLLECT")
                {
                    tRejected++;
                    rw.Begin();
                    rw.Detail(Lines());
                    rw.End();
                    mandates.Add(entry);
                    continue;
                }

                // 1300-TXN-CODE / 1400-WRITE-PAYMENT
                txn = CobolNumeric.Store(r.NumVal(13), 9, 0) == 0 ? "01" : "17";
                var pence = CobolNumeric.Store(amt * 100, 13, 0, signed: false);
                PutBacs(Payment(sort, acct, txn, oSort, oAcct, pence, oName, mandate, payer));
                totPence += pence;
                tCollected++;
                tValue += amt;
                rw.Begin();
                rw.Detail(Lines());
                rw.End();
                entry["transactionCode"] = txn;

                // 1500-RECORD-COLLECTION
                if (commit)
                {
                    seq = (seq + 1) % 10000;
                    var collRef = "DDC" + dueText[2..8] + "-" + seq.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                    try
                    {
                        await sql.ExecAsync(
                            "INSERT INTO ar.DirectDebitCollection (MandateId, CompanyCode, CustomerCode, CollectionRef, AdvanceNoticeSentAt, " +
                            "SubmissionDate, ProcessingDate, DueDate, AmountGBP, TransactionCode, BacsFileRef, Status, OpenItemIds, CreatedBy) " +
                            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SUBMITTED', ?, ?)",
                            [SqlioParm.Text(r.Raw(14)), SqlioParm.Text(co), SqlioParm.Text(cust), SqlioParm.Text(collRef), SqlioParm.Text(r.Raw(7)),
                                SqlioParm.Text(ArBatch.D8(subDate)), SqlioParm.Text(ArBatch.D8(procDate)), SqlioParm.Text(dueText),
                                SqlioParm.Number(amt), SqlioParm.Text(txn), SqlioParm.Text(CobolText.Fit(bacsName, 200)[..60]),
                                SqlioParm.Text(itemIds), SqlioParm.Text(user)], ct);
                        entry["collectionRef"] = collRef;
                    }
                    catch (SqlioException)
                    {
                        rc = 4;
                        ctx.Display("STERP-W-1501 collection not recorded for " + cust.Trim(' '));
                    }
                }
                mandates.Add(entry);
            }

            // 0390-BACS-TRAILER
            if (tCollected > 0) PutBacs(Payment(oSort, oAcct, "99", oSort, oAcct, totPence, oName, "CONTRA", oName));
            PutBacs(new BacsRecord().At(1, "UTL1" + Digits(totPence, 13) + Digits(totPence, 13) + Digits(tCollected, 7) +
                Digits(tCollected > 0 ? 1 : 0, 7), 44).ToString());
            PutBacs(new BacsRecord().At(1, "EOF1", 4).At(5, "A", 1).At(6, oSun, 6).At(12, "S", 1).At(13, oSun, 6).At(19, "0001", 4)
                .At(23, "0001", 4).At(27, "0001", 4).At(31, "01", 2).At(33, julToday, 6).At(39, julProc, 6).At(46, "000000", 6)
                .At(52, "BACS       ", 11).ToString());
            PutBacs(new BacsRecord().At(1, "EOF2", 4).At(5, "F", 1).At(6, "02000", 5).At(11, "00100", 5).ToString());

            // 0400-CLOSE-FILES: TERMINATE R-DDCOLL
            rw.Terminate(() => rw.Group(
                L(2, Cols().Put(1, "Mandates considered", 22).Put(24, CobolEdit.Field(tSelected, "ZZZ,ZZ9"), 7).Put(36, "Collected", 20)
                    .Put(57, CobolEdit.Field(tCollected, "ZZZ,ZZ9"), 7).Put(69, "Not collected", 20).Put(90, CobolEdit.Field(tRejected, "ZZZ,ZZ9"), 7)),
                L(1, Cols().Put(1, "Submission value GBP", 22).Put(24, ArBatch.M17(tValue), 17).Put(47, "Service user", 16).Put(64, oSun, 6)
                    .Put(74, "Originator", 14).Put(89, oName, 18)),
                L(2, Cols().Put(1, "No collection is made without an active mandate", 50)
                    .Put(52, "or before the advance notice has expired.", 48))));
        }
        ctx.Display("STERP-I-0401 " + ArBatch.Num(tCollected, 7) + " collection(s), " + ArBatch.Num(tRejected, 7) + " not collected");
        ctx.Display("STERP-I-0402 Bacs file " + bacsName);
        ctx.AnnounceSpool(spoolName);
        ctx.RowsProcessed = rows;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["mode"] = mode, ["dueDate"] = ArBatch.D8(due), ["processingDate"] = ArBatch.D8(procDate),
            ["submissionDate"] = ArBatch.D8(subDate), ["mandates"] = mandates, ["mandatesConsidered"] = tSelected, ["collected"] = tCollected,
            ["notCollected"] = tRejected, ["submissionValueGbp"] = tValue, ["serviceUserNumber"] = oSun, ["bacsFile"] = bacsName, ["returnCode"] = rc,
        };
        return rc;
    }
}
