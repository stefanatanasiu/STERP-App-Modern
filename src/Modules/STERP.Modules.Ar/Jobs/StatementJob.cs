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
/// STATEMNT (cobol/batch/ar/STATEMNT.cob, FR-STERP-AR-061) - customer statements: one statement per statement party (a
/// customer that is the statement customer of itself or of others) for FROM..TO in the TRANS or OPEN style, suppressions
/// listed, and unless PREVIEW=Y each issue or suppression recorded as a STATEMENT action on the party's open COLLECTION
/// case (opened as COLL-party). Autocommit.
/// </summary>
public static class StatementJob
{
    public const string Code = "STATEMNT";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runStatementsJob",
        ParameterOrder = ["CO", "ASAT", "TO", "FROM", "CUST", "MODE", "PREVIEW", "STYLE", "USER"],
        // 0100-READ-ARGS: FROM after TO ends RC 8 before 0200-JOB-START
        SelfRegistration = pre => Period(pre.Args).From > Period(pre.Args).To ? null : ArBatch.SelfRegistration(pre, Code),
        EndValues = (ctx, rc) => Period(ctx.Args).From > Period(ctx.Args).To
            ? JobService.DefaultEnd(ctx, rc, false, null)
            : ArBatch.End(ctx, rc),
        Execute = RunAsync,
    };

    /// <summary>WS-TO (TO, else ASAT, else today) and WS-FROM (FROM, else the first day of the TO month).</summary>
    private static (int From, int To) Period(CommandLineArgs args)
    {
        var to = CobolText.IsSpaces(args.To) ? 0 : ArBatch.Date8(args.To);
        if (to == 0 && !CobolText.IsSpaces(args.AsAt)) to = ArBatch.Date8(args.AsAt);
        var from = CobolText.IsSpaces(args.From) ? 0 : ArBatch.Date8(args.From);
        if (to == 0) to = ArBatch.Today();
        if (from == 0) from = to / 100 * 100 + 1;
        return (from, to);
    }

    private sealed class Party
    {
        public string Code = "", Name = "", Restrict = "", Status = "", Freq = "", Style = "";
        public decimal Open, MoveDr, MoveCr, Close, NotDue, B1, B2, B3, B4;
        public int Lines;
        public bool Printed;
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var mode = CobolText.Trimmed(ctx.Args.Mode, 10);
        var cust = CobolText.Trimmed(ctx.Args.Get("CUST"), 20);
        var preview = ctx.Args.Get("PREVIEW") is { } pv && pv.StartsWith('Y');
        var (from, to) = Period(ctx.Args);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        if (from > to)
        {
            ctx.Display("STERP-E-0102 FROM is after TO");
            return 8;
        }
        mode = CobolText.Trimmed(mode.ToUpperInvariant(), 10);
        if (mode.Length == 0) mode = "AUTO";

        // 0150-DEFAULT-STYLE
        string style;
        if (mode is "TRANS" or "OPEN")
        {
            style = mode;
        }
        else
        {
            var pm = new ParmgetArea { ParamCode = "AR.STMT.STYLE", AsOf = to, DefaultText = "OPEN", UseDefault = "Y" };
            pm.Header.Prepare(new KernelCaller(co, "", user, to, ""), "GET");
            await ctx.Get<IParameterService>().ExecuteAsync(pm, ct);
            style = CobolText.Fit(pm.ValueText, 5) == "TRANS" ? "TRANS" : "OPEN";
        }
        ctx.Display("STERP-I-0100 STATEMNT statement run for " + co);
        ctx.RowsProcessed = 0;

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        var toUk = ArBatch.Uk(to);
        var fromUk = ArBatch.Uk(from);
        var heading = ArBatch.Heading(co, Code, "Customer statements", user, DateTime.Now);
        heading.AsAtText = "Statement date " + toUk;
        heading.Subtitle = CobolText.Trimmed("Style " + style + " - amounts in GBP unless shown otherwise", 60);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        if (!ArBatch.TryOpenSpool(ctx, Code, co, out var spool)) return 12;

        var rc = 0;
        var rows = 0;
        int tStmts = 0, suppressed = 0;
        decimal tClose = 0;
        var parties = new JsonArray();
        var toText = ArBatch.D8(to);
        var fromText = ArBatch.D8(from);

        using (spool)
        {
            var rw = new ArReportWriter(spool, heading, firstDetail: 8, lastDetail: 52);

            void Gen(params GroupLine[] lines)
            {
                rw.Begin();
                rw.Detail(lines);
                rw.End();
            }

            void PrintHead(Party p)
            {
                // 1150-PRINT-HEAD: GENERATE DE-PHEAD
                Gen(
                    L(2, Cols().Put(1, "Statement ", 10).Put(12, p.Code, 20).Put(33, p.Name, 40).Put(76, "Style ", 6).Put(82, p.Style, 10)
                        .Put(95, "Frequency ", 10).Put(106, p.Freq, 10)),
                    L(1, Cols().Put(1, "Period from", 16).Put(18, fromUk, 10).Put(30, "to", 3).Put(34, toUk, 10).Put(50, "Opening balance GBP", 24)
                        .Put(76, ArBatch.M17(p.Open), 17)),
                    L(2, Cols().Put(1, "Date", 4).Put(12, "Type", 4).Put(21, "Document", 8).Put(40, "Due date", 8).Put(51, "Legal debtor", 12)
                        .Put(72, "Cur", 3).Put(85, "Amount", 8).Put(103, "Outstanding", 11).Put(122, "Balance", 7)),
                    L(1, new string('-', 132)));
                p.Printed = true;
            }

            async Task WriteActionAsync(Party p, string narrative, string reason)
            {
                // 1750-WRITE-ACTION (+1760-FIND-CASE)
                long caseId = 0;
                SqlioRow? row;
                try
                {
                    row = await sql.SingleAsync(
                        "SELECT TOP 1 CONVERT(varchar(24),CaseId) FROM ar.CollectionCase WHERE CompanyCode = ? AND CustomerCode = ? AND " +
                        "CaseType = 'COLLECTION' AND Status IN ('OPEN','ESCALATED') ORDER BY CaseId",
                        [SqlioParm.Text(co), SqlioParm.Text(p.Code)], ArBatch.Widths(30, 120), ct);
                }
                catch (SqlioException)
                {
                    row = null;
                }
                if (row is { Found: true })
                {
                    caseId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0);
                }
                else
                {
                    try
                    {
                        row = await sql.SingleAsync(
                            "INSERT INTO ar.CollectionCase (CompanyCode, CustomerCode, CaseRef, CaseType, Reason, OwnerUser, CollectionStep, " +
                            "Status, CreatedBy) VALUES (?, ?, ?, 'COLLECTION', 'Contact and collection history', ?, 0, 'OPEN', ?); " +
                            "SELECT CONVERT(varchar(24),SCOPE_IDENTITY())",
                            [SqlioParm.Text(co), SqlioParm.Text(p.Code), SqlioParm.Text(CobolText.Fit("COLL-" + p.Code.Trim(' '), 30)),
                                SqlioParm.Text(user), SqlioParm.Text(user)],
                            ArBatch.Widths(1, 120), ct);
                        if (row.Found) caseId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0);
                    }
                    catch (SqlioException)
                    {
                        caseId = 0;
                    }
                }
                if (caseId == 0)
                {
                    rc = 4;
                    ctx.Display("STERP-W-1752 no collection case for " + p.Code.Trim(' '));
                    return;
                }
                try
                {
                    await sql.ExecAsync(
                        "INSERT INTO ar.CollectionAction (CaseId, ActionSeq, ActionType, ActionDate, PerformedBy, Narrative, SuppressedReason) " +
                        "SELECT ?, ISNULL(MAX(ActionSeq),0) + 1, 'STATEMENT', ?, ?, ?, ? FROM ar.CollectionAction WHERE CaseId = ?",
                        [SqlioParm.Integer(caseId), SqlioParm.Text(toText), SqlioParm.Text(user), SqlioParm.Text(narrative),
                            CobolText.IsSpaces(reason) ? SqlioParm.Null : SqlioParm.Text(reason), SqlioParm.Integer(caseId)], ct);
                }
                catch (SqlioException)
                {
                    rc = 4;
                    ctx.Display("STERP-W-1751 issue history not written for " + p.Code.Trim(' '));
                }
            }

            async Task SuppressAsync(Party p, string reason)
            {
                // 1800-SUPPRESS
                suppressed++;
                Gen(L(1, Cols().Put(1, "Suppressed", 10).Put(12, p.Code, 20).Put(33, p.Name, 40).Put(75, reason, 55)));
                parties.Add(new JsonObject { ["partyCode"] = p.Code, ["name"] = p.Name, ["suppressed"] = true, ["reason"] = reason });
                if (preview) return;
                await WriteActionAsync(p, CobolText.Trimmed("Statement not issued - " + reason.Trim(' '), 200), reason);
            }

            // 1000-RUN-STATEMENTS
            var stmt = "SELECT p.CustomerCode, p.LegalName, ISNULL(p.ContactRestriction,''), p.Status, ISNULL(p.StatementFrequency,'MONTHLY') FROM " +
                "ar.Customer p WHERE p.CompanyCode = ? AND EXISTS (SELECT 1 FROM ar.Customer c WHERE c.CompanyCode = p.CompanyCode AND " +
                "ISNULL(NULLIF(c.StatementCustomerCode,''),c.CustomerCode) = p.CustomerCode)";
            var parms = new List<SqlioParm> { SqlioParm.Text(co) };
            if (cust.Length > 0)
            {
                parms.Add(SqlioParm.Text(cust));
                stmt += " AND p.CustomerCode = ?";
            }
            stmt += " ORDER BY p.CustomerCode";
            IReadOnlyList<SqlioRow> list = [];
            try
            {
                list = await sql.FetchAllAsync(stmt, parms, ArBatch.Widths(30, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var pr in list)
            {
                // 1100-ONE-PARTY
                var p = new Party
                {
                    Code = CobolText.Trimmed(pr.Raw(0), 20),
                    Name = CobolText.Trimmed(pr.Raw(1), 40),
                    Restrict = CobolText.Trimmed(pr.Raw(2), 60),
                    Status = CobolText.Trimmed(pr.Raw(3), 10),
                    Freq = CobolText.Trimmed(pr.Raw(4), 10),
                    Style = style,
                };
                if (CobolText.Fit(p.Status, 8) == "PROSPECT")
                {
                    await SuppressAsync(p, "Prospect account - no ledger statement issued");
                    continue;
                }
                if (p.Restrict.Length > 0)
                {
                    await SuppressAsync(p, CobolText.Trimmed("Contact restriction: " + p.Restrict.Trim(' '), 60));
                    continue;
                }

                // 1180-OPENING-BALANCE
                try
                {
                    var ob = await sql.SingleAsync(
                        "SELECT CONVERT(varchar(24),ISNULL(SUM(o.OriginalAmountGBP),0)) FROM ar.OpenItem o JOIN ar.Customer c ON c.CompanyCode = " +
                        "o.CompanyCode AND c.CustomerCode = o.CustomerCode WHERE o.CompanyCode = ? AND ISNULL(NULLIF(c.StatementCustomerCode,'')," +
                        "c.CustomerCode) = ? AND o.PostingDate < ?",
                        [SqlioParm.Text(co), SqlioParm.Text(p.Code), SqlioParm.Text(fromText)], ArBatch.Widths(30, 120), ct);
                    if (ob.Found) p.Open = CobolNumeric.Store(ob.NumVal(0), 13, 2);
                }
                catch (SqlioException)
                {
                    // SQLIO-OK not set: P-OPEN stays 0
                }

                // 1200-READ-MOVEMENTS
                var lines = new JsonArray();
                IReadOnlyList<SqlioRow>? moves = null;
                try
                {
                    moves = await sql.FetchAllAsync(
                        "SELECT CONVERT(char(8),o.PostingDate,112), o.ItemType, o.DocumentNumber, CONVERT(char(8),o.DueDate,112), o.CustomerCode, " +
                        "o.CurrencyCode, CONVERT(varchar(24),o.OriginalAmountGBP), CONVERT(varchar(24), o.OriginalAmountGBP - ISNULL((SELECT " +
                        "SUM(a.AppliedAmountGBP + a.DiscountAmountGBP + a.WriteOffAmountGBP) FROM ar.Allocation a WHERE a.TargetOpenItemId = " +
                        "o.OpenItemId AND a.Status = 'ACTIVE' AND a.EffectiveDate <= ?),0) + ISNULL((SELECT SUM(b.AppliedAmountGBP) FROM " +
                        "ar.Allocation b WHERE b.SourceOpenItemId = o.OpenItemId AND b.Status = 'ACTIVE' AND b.EffectiveDate <= ?),0)), CASE " +
                        "WHEN o.IsDisputed = 1 THEN 'Y' ELSE 'N' END, CONVERT(varchar(24),o.OriginalAmount) FROM ar.OpenItem o JOIN ar.Customer c " +
                        "ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode WHERE o.CompanyCode = ? AND ISNULL(NULLIF(" +
                        "c.StatementCustomerCode,''),c.CustomerCode) = ? AND o.PostingDate <= ? ORDER BY o.PostingDate, o.DocumentNumber",
                        [SqlioParm.Text(toText), SqlioParm.Text(toText), SqlioParm.Text(co), SqlioParm.Text(p.Code), SqlioParm.Text(toText)],
                        ArBatch.Widths(30, 120), ct);
                }
                catch (SqlioException ex)
                {
                    rc = 12;
                    ctx.Display("STERP-E-1201 " + CobolText.Fit(ex.DriverText, 100));
                }
                if (moves is not null)
                {
                    foreach (var m in moves)
                    {
                        // 1300-ONE-MOVEMENT
                        var postN = ArBatch.Date8(m.Raw(0)[..8]);
                        var amt = CobolNumeric.Store(m.NumVal(6), 13, 2);
                        var resid = CobolNumeric.Store(m.NumVal(7), 13, 2);
                        if (postN >= from)
                        {
                            if (amt < 0) p.MoveCr += amt;
                            else p.MoveDr += amt;
                        }
                        // 1400-AGE-RESIDUAL
                        if (resid >= 0.005m)
                        {
                            var dueAge = ArBatch.Date8(m.Raw(3)[..8]);
                            var days = CobolNumeric.Store(CobolDate.IntegerOfDate(to) - CobolDate.IntegerOfDate(dueAge), 7, 0);
                            if (days <= 0) p.NotDue += resid;
                            else if (days <= 30) p.B1 += resid;
                            else if (days <= 60) p.B2 += resid;
                            else if (days <= 90) p.B3 += resid;
                            else p.B4 += resid;
                        }
                        if (style == "TRANS" && postN < from) continue;
                        if (style == "OPEN" && resid > -0.005m && resid < 0.005m) continue;
                        if (!p.Printed) PrintHead(p);
                        var dDate = ArBatch.Uk(postN);
                        var dDue = ArBatch.Uk(ArBatch.Date8(m.Raw(3)[..8]));
                        var curr = CobolText.Trimmed(m.Raw(5), 3);
                        var dAmt = amt;
                        var bal = style == "TRANS" ? CobolNumeric.Store(p.Open + p.MoveDr + p.MoveCr, 13, 2) : 0m;
                        var flag = m.Raw(8).StartsWith('Y') ? "Dis" : "";
                        if (CobolText.Fit(curr, 3) != "GBP") dAmt = CobolNumeric.Store(m.NumVal(9), 13, 2);
                        p.Lines++;
                        rows++;
                        Gen(L(1, Cols().Put(1, dDate, 10).Put(12, m.Raw(1), 8).Put(21, m.Raw(2), 18).Put(40, dDue, 10).Put(51, m.Raw(4), 20)
                            .Put(72, curr, 3).Put(76, ArBatch.M17(dAmt), 17).Put(94, ArBatch.M17(resid), 17).Put(112, ArBatch.M17(bal), 17)
                            .Put(130, flag, 3)));
                        lines.Add(new JsonObject
                        {
                            ["date"] = dDate, ["itemType"] = CobolText.Trimmed(m.Raw(1), 8), ["documentNumber"] = CobolText.Trimmed(m.Raw(2), 18),
                            ["dueDate"] = dDue, ["legalDebtor"] = CobolText.Trimmed(m.Raw(4), 20), ["currencyCode"] = curr, ["amount"] = dAmt,
                            ["outstanding"] = resid, ["balance"] = bal, ["flag"] = flag,
                        });
                    }
                    p.Close = CobolNumeric.Store(p.Open + p.MoveDr + p.MoveCr, 13, 2);
                }
                if (rc > 4) continue;
                if (p.Lines == 0 && p.Open == 0 && p.Close == 0)
                {
                    await SuppressAsync(p, "No balance and no movement in the period");
                    continue;
                }
                if (!p.Printed) PrintHead(p);
                // GENERATE DE-PFOOT
                Gen(
                    L(1, new string('=', 132)),
                    L(1, Cols().Put(1, "Movements  debits", 22).Put(24, ArBatch.M17(p.MoveDr), 17).Put(44, "credits", 8)
                        .Put(53, ArBatch.M17(p.MoveCr), 17).Put(90, "Closing balance GBP", 20).Put(112, ArBatch.M17(p.Close), 17)),
                    L(1, Cols().Put(1, "Of the amount owed", 22).Put(24, "not yet due", 11).Put(36, ArBatch.M13(p.NotDue), 13).Put(51, "1-30", 4)
                        .Put(56, ArBatch.M13(p.B1), 13).Put(71, "31-60", 5).Put(77, ArBatch.M13(p.B2), 13).Put(92, "61-90", 5)
                        .Put(98, ArBatch.M13(p.B3), 13).Put(113, "90+", 4).Put(118, ArBatch.M13(p.B4), 13)),
                    L(1, Cols().Put(1, "Please quote the document number when paying.", 46).Put(50, "Queries to the credit control department.", 44)));
                tStmts++;
                tClose += p.Close;
                parties.Add(new JsonObject
                {
                    ["partyCode"] = p.Code, ["name"] = p.Name, ["suppressed"] = false, ["style"] = style, ["openingBalance"] = p.Open,
                    ["debits"] = p.MoveDr, ["credits"] = p.MoveCr, ["closingBalance"] = p.Close, ["notYetDue"] = p.NotDue, ["days1To30"] = p.B1,
                    ["days31To60"] = p.B2, ["days61To90"] = p.B3, ["over90"] = p.B4, ["lines"] = lines,
                });
                // 1700-RECORD-ISSUE
                if (!preview)
                {
                    await WriteActionAsync(p, CobolText.Trimmed("Statement " + style + " issued to " + p.Code.Trim(' ') +
                        " for the period ending " + toUk, 200), "");
                }
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-STATEMNT
            rw.Terminate(() => rw.Group(
                L(2, Cols().Put(1, "Statements produced", 24).Put(26, CobolEdit.Field(tStmts, "ZZZ,ZZ9"), 7).Put(40, "Suppressed", 22)
                    .Put(63, CobolEdit.Field(suppressed, "ZZZ,ZZ9"), 7).Put(80, "Total closing GBP", 22).Put(105, ArBatch.M17(tClose), 17)),
                L(1, Cols().Put(1, "A grouped statement does not consolidate the", 48).Put(50, "debts of separate legal debtors.", 46))));
        }
        ctx.Display("STERP-I-0401 " + ArBatch.Num(tStmts, 7) + " statement(s), " + ArBatch.Num(suppressed, 9) + " suppressed");
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = rows;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["from"] = fromText, ["to"] = toText, ["style"] = style, ["preview"] = preview,
            ["parties"] = parties, ["statementsProduced"] = tStmts, ["suppressed"] = suppressed, ["totalClosingGbp"] = tClose, ["returnCode"] = rc,
        };
        return rc;
    }
}
