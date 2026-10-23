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
/// DUNNING (cobol/batch/ar/DUNNING.cob, FR-STERP-AR-062) - collection worklist and reminder run: one row per customer with
/// overdue items, the step from the oldest overdue age (7/21/30/45/75 days), suppression reasons, reminder letters (DUNNLTR),
/// in COMMIT the case, contact action and item stamps, and an escalation exception from FINAL DEMAND. Autocommit; the
/// letter file is the run's log file (JOBLOG END JL-LOG-FILE).
/// </summary>
public static class DunningJob
{
    public const string Code = "DUNNING";
    private const int MaxAccounts = 300;

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = ArBatch.Slice,
        SubmitOperationId = "ar_runDunningJob",
        ParameterOrder = ["CO", "ASAT", "MODE", "STAGE", "OWNER", "USER"],
        // 0100-READ-ARGS: a MODE other than PREVIEW/COMMIT ends RC 8 before 0200-JOB-START
        SelfRegistration = pre => ValidMode(pre.Args) ? ArBatch.SelfRegistration(pre, Code) : null,
        EndValues = (ctx, rc) => ValidMode(ctx.Args)
            ? ArBatch.End(ctx, rc) with { LogFile = ctx.SpoolFiles.FirstOrDefault(IsLetterFile) }
            : JobService.DefaultEnd(ctx, rc, false, null),
        Execute = RunAsync,
    };

    private static bool IsLetterFile(string path) => Path.GetFileName(path).StartsWith("DUNNLTR-", StringComparison.OrdinalIgnoreCase);

    private static string ModeOf(CommandLineArgs args)
    {
        var mode = CobolText.Trimmed(args.Mode.ToUpperInvariant(), 10);
        return mode.Length == 0 ? "PREVIEW" : mode;
    }

    private static bool ValidMode(CommandLineArgs args) => ModeOf(args) is "PREVIEW" or "COMMIT";

    private sealed class Det
    {
        public string Cust = "", Name = "", Legal = "", Oldest = "", Step = "", Next = "", Why = "", Owner = "";
        public int Days, Items;
        public decimal Owed, Disputed, Chase;
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;

        // 0100-READ-ARGS
        var co = CobolText.Trimmed(ctx.Args.Co, 10);
        var user = CobolText.Trimmed(ctx.Args.User, 20);
        var mode = ModeOf(ctx.Args);
        var asat = CobolText.IsSpaces(ctx.Args.AsAt) ? 0 : ArBatch.Date8(ctx.Args.AsAt);
        if (co.Length == 0) co = "STC";
        if (user.Length == 0) user = "ARCLERK1";
        if (asat == 0) asat = ArBatch.Today();
        if (!ValidMode(ctx.Args))
        {
            ctx.Display("STERP-E-0103 MODE must be PREVIEW or COMMIT");
            return 8;
        }
        var commit = mode == "COMMIT";
        ctx.Display("STERP-I-0100 DUNNING collection run " + mode);
        ctx.RowsProcessed = 0;

        // 0300-OPEN-SPOOL: the listing and the letter file share the RUNPATH timestamp
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        ctx.Paths.Build("SPOOL", Code, co, null, stamp, true, out var spoolName);
        ctx.Paths.Build("SPOOL", "DUNNLTR", co, null, stamp, true, out var letterName);
        // WS-LETTER-NAME is JOBLOG END's JL-LOG-FILE
        ctx.SpoolFiles.Add(letterName);
        var uk = ArBatch.Uk(asat);
        var heading = ArBatch.Heading(co, Code, "Collection worklist and reminder run", user, DateTime.Now);
        heading.AsAtText = "As at " + uk;
        heading.Subtitle = CobolText.Trimmed("Mode " + mode + " - baseline sequence 7/21/30/45/75 days", 60);
        await ArBatch.ReadCompanyAsync(sql, heading, co, ct);
        SpoolWriter spool, letters;
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
            letters = new SpoolWriter(letterName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            spool.Dispose();
            ctx.Display("STERP-E-0303 cannot open " + CobolText.Fit(letterName, 200));
            return 12;
        }

        var rc = 0;
        var rows = 0;
        int tAccounts = 0, tSuppressed = 0, nLetters = 0, nEscalated = 0;
        decimal tOwed = 0, tChase = 0, tDisputed = 0;
        var accounts = new JsonArray();

        using (spool)
        using (letters)
        {
            var rw = new ArReportWriter(spool, heading, firstDetail: 8, lastDetail: 54);

            void Generate(Det d)
            {
                rw.Begin();
                rw.Detail(
                    L(1, Cols().Put(1, d.Cust, 20).Put(22, d.Name, 34).Put(57, d.Oldest, 10).Put(68, CobolEdit.Field(d.Days, "ZZZZ9"), 5)
                        .Put(75, CobolEdit.Field(d.Items, "ZZZZ9"), 5).Put(81, ArBatch.M13(d.Owed), 13).Put(96, ArBatch.M13(d.Chase), 13)
                        .Put(112, d.Step, 14).Put(127, d.Legal, 6)),
                    L(1, Cols().Put(5, "Disputed", 9).Put(15, ArBatch.M13(d.Disputed), 13).Put(32, "Next due", 10).Put(43, d.Next, 10)
                        .Put(55, "Owner", 6).Put(62, d.Owner, 20).Put(85, d.Why, 42)));
                rw.End();
            }

            // 1000-COLLECTION-RUN
            var asatText = ArBatch.D8(asat);
            IReadOnlyList<SqlioRow> found = [];
            try
            {
                found = await sql.FetchAllAsync(
                    "SELECT o.CustomerCode, MAX(c.LegalName), CONVERT(char(8),MIN(o.DueDate),112), CONVERT(varchar(24),SUM(o.ResidualAmountGBP)), " +
                    "CONVERT(varchar(24),SUM(CASE WHEN o.IsDisputed = 1 THEN o.DisputedAmountGBP ELSE 0 END)), CONVERT(varchar(12),COUNT(*)), " +
                    "MAX(ISNULL(c.ContactRestriction,'')), MAX(ISNULL(c.InsolvencyType,'')), MAX(c.LegalForm), MAX(c.Status), " +
                    "CONVERT(varchar(12),MAX(ISNULL(o.LastReminderStep,0))), MAX(ISNULL(cr.ControllerUser,'')) FROM ar.OpenItem o JOIN " +
                    "ar.Customer c ON c.CompanyCode = o.CompanyCode AND c.CustomerCode = o.CustomerCode LEFT JOIN ar.CustomerCredit cr ON " +
                    "cr.CompanyCode = o.CompanyCode AND cr.CustomerCode = o.CustomerCode WHERE o.CompanyCode = ? AND o.DueDate < ? AND " +
                    "o.ResidualAmountGBP > 0 AND o.SettlementStatus IN ('OPEN','PARTIAL') AND o.WrittenOff = 0 GROUP BY o.CustomerCode " +
                    "ORDER BY o.CustomerCode",
                    [SqlioParm.Text(co), SqlioParm.Text(asatText)], ArBatch.Widths(30, 120), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            }
            foreach (var r in found)
            {
                // 1100-ONE-ACCOUNT
                tAccounts++;
                rows++;
                var d = new Det
                {
                    Cust = CobolText.Trimmed(r.Raw(0), 20),
                    Name = CobolText.Trimmed(r.Raw(1), 34),
                    Legal = CobolText.Trimmed(r.Raw(8), 12),
                    Owner = CobolText.Trimmed(r.Raw(11), 20),
                };
                if (d.Owner.Length == 0) d.Owner = user;
                d.Owed = CobolNumeric.Store(r.NumVal(3), 13, 2);
                d.Disputed = CobolNumeric.Store(r.NumVal(4), 13, 2);
                d.Items = (int)CobolNumeric.Store(r.NumVal(5), 5, 0);
                var stepLast = (int)CobolNumeric.Store(r.NumVal(10), 1, 0, signed: false);
                var dueN = ArBatch.Date8(r.Raw(2)[..8]);
                uk = ArBatch.Uk(dueN);
                d.Oldest = uk;
                d.Days = (int)CobolNumeric.Store(CobolDate.IntegerOfDate(asat) - CobolDate.IntegerOfDate(dueN), 5, 0);
                d.Chase = CobolNumeric.Store(d.Owed - d.Disputed, 13, 2);
                if (d.Chase < 0) d.Chase = 0;
                tOwed += d.Owed;
                tChase += d.Chase;
                tDisputed += d.Disputed;

                // 1200-DECIDE-STEP
                var step = d.Days >= 75 ? 5 : d.Days >= 45 ? 4 : d.Days >= 30 ? 3 : d.Days >= 21 ? 2 : d.Days >= 7 ? 1 : 0;
                string actionType;
                (actionType, d.Step) = step switch
                {
                    1 => ("REMINDER1", "REMINDER1"),
                    2 => ("REMINDER2", "REMINDER2"),
                    3 => ("STOP", "ACCOUNT STOP"),
                    4 => ("DEMAND", "FINAL DEMAND"),
                    5 => ("HANDOFF", "RECOVERY"),
                    _ => ("NOTE", "none due"),
                };
                if (step > 0 && step <= stepLast)
                {
                    step = 0;
                    d.Step = "already issued";
                    d.Why = "Step already issued at this stage";
                    tSuppressed++;
                }

                // 1300-CHECK-SUPPRESSION
                var nextN = 0;
                if (step != 0)
                {
                    if (d.Chase < 0.005m)
                    {
                        step = 0;
                        d.Why = "Whole balance disputed or settled";
                        tSuppressed++;
                    }
                    else if (!CobolText.IsSpaces(r.Raw(6)))
                    {
                        step = 0;
                        d.Why = CobolText.Trimmed("Contact restriction: " + r.Raw(6).Trim(' '), 42);
                        tSuppressed++;
                    }
                    else if (!CobolText.IsSpaces(r.Raw(7)))
                    {
                        step = 0;
                        d.Why = CobolText.Trimmed("Insolvency " + r.Raw(7).Trim(' ') + " - office holder route", 42);
                        tSuppressed++;
                    }
                    else
                    {
                        // 1350-CHECK-PROMISE
                        SqlioRow? p = null;
                        try
                        {
                            p = await sql.SingleAsync(
                                "SELECT TOP 1 CONVERT(char(8),a.PromiseDate,112), CONVERT(varchar(24),ISNULL(a.PromiseAmountGBP,0)) FROM " +
                                "ar.CollectionAction a JOIN ar.CollectionCase k ON k.CaseId = a.CaseId WHERE k.CompanyCode = ? AND " +
                                "k.CustomerCode = ? AND a.ActionType = 'PROMISE' AND a.PromiseStatus = 'OPEN' AND a.PromiseDate >= ? " +
                                "ORDER BY a.PromiseDate",
                                [SqlioParm.Text(co), SqlioParm.Text(d.Cust), SqlioParm.Text(asatText)], ArBatch.Widths(30, 120), ct);
                        }
                        catch (SqlioException)
                        {
                            p = null;
                        }
                        if (p is { Found: true })
                        {
                            nextN = ArBatch.Date8(p.Raw(0)[..8]);
                            uk = ArBatch.Uk(nextN);
                            d.Why = "Promise to pay open until " + uk;
                            step = 0;
                            tSuppressed++;
                        }
                    }
                }
                var entry = new JsonObject
                {
                    ["customerCode"] = d.Cust, ["name"] = d.Name, ["oldestDue"] = d.Oldest, ["daysOverdue"] = d.Days, ["items"] = d.Items,
                    ["owedGbp"] = d.Owed, ["disputedGbp"] = d.Disputed, ["chaseableGbp"] = d.Chase, ["owner"] = d.Owner,
                };
                if (step == 0)
                {
                    Generate(d);
                }
                else
                {
                    // 1400-NEXT-ACTION-DATE
                    nextN = CobolDate.DateOfIntegerYyyymmdd(CobolDate.IntegerOfDate(asat) + (step == 4 ? 30 : 14));
                    uk = ArBatch.Uk(nextN);
                    d.Next = uk;
                    Generate(d);

                    // 1500-WRITE-LETTER
                    if (step != 3)
                    {
                        nLetters++;
                        uk = ArBatch.Uk(asat);
                        var money = Domain.ArEdit.MoneyUk(d.Chase);
                        letters.WriteLine(new string('=', 80));
                        letters.WriteLine(Ltr(heading.CompanyName.Trim(' ') + " - credit control"));
                        letters.WriteLine(Ltr("Our reference " + d.Cust.Trim(' ') + "    Date " + uk));
                        letters.WriteLine(Ltr(d.Name));
                        letters.WriteLine("");
                        letters.WriteLine(Ltr(step switch
                        {
                            1 => "Our records show GBP " + money + " overdue.  Please arrange payment.",
                            2 => "GBP " + money + " remains overdue.  We may claim statutory interest and compensation.",
                            4 => "Final demand: GBP " + money + " is now due in full.",
                            _ => "Your account of GBP " + money + " is being passed for external recovery.",
                        }));
                        // 1550-LETTER-TAIL
                        letters.WriteLine(Ltr("Oldest item due " + CobolText.Fit(d.Oldest, 10) + ", disputed amounts are excluded from this letter."));
                        if (step == 4 && CobolText.Fit(d.Legal, 10) == "SOLETRADER")
                        {
                            letters.WriteLine("Letter of claim under the Pre-Action Protocol for Debt Claims.  Information sheet");
                            letters.WriteLine("and reply form enclosed.  You have 30 days from the date of this letter to respond.");
                        }
                        letters.WriteLine(Ltr("Please contact " + d.Owner.Trim(' ') + " if the amount is wrong or already paid."));
                        letters.WriteLine("");
                    }

                    // 1600-UPDATE-WORKLIST
                    if (commit && !await UpdateWorklistAsync(sql, co, user, asat, nextN, step, actionType, d, ct)) rc = 4;

                    // 1700-RAISE-ESCALATION
                    if (step is 4 or 5)
                    {
                        nEscalated++;
                        var ex = new ExcpraisArea
                        {
                            Module = "AR",
                            Severity = "WARNING",
                            Category = "COLLECTION ESCALATION",
                            DocumentType = "CUSTOMER",
                            DocumentRef = d.Cust,
                            OwnerUser = d.Owner,
                            OwnerRole = "CREDITCTL",
                            Priority = 2,
                            DueAt = nextN,
                            AmountGbp = d.Chase,
                            MessageText = CobolText.Trimmed(d.Step.Trim(' ') + " reached for " + d.Cust.Trim(' ') + " - GBP " +
                                Domain.ArEdit.MoneyUk(d.Chase) + " chaseable, oldest item due " + CobolText.Fit(d.Oldest, 10), 200),
                        };
                        ex.Header.Prepare(new KernelCaller(co, "", user, asat, ""), "RAISE");
                        await ctx.Get<IExceptionRaiser>().ExecuteAsync(ex, ct);
                        if (ex.Header.ReturnCode > 6)
                        {
                            rc = 4;
                            ctx.Display("STERP-W-1701 exception not raised for " + d.Cust.Trim(' '));
                        }
                    }
                }
                entry["step"] = d.Step;
                entry["nextDue"] = d.Next;
                entry["reason"] = d.Why;
                accounts.Add(entry);
                if (tAccounts >= MaxAccounts)
                {
                    rc = 4;
                    ctx.Display("STERP-W-1002 account limit reached");
                    break;
                }
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-DUNNING
            rw.Terminate(() => rw.Group(
                L(2, Cols().Put(1, "Accounts considered", 24).Put(26, CobolEdit.Field(tAccounts, "ZZZ,ZZ9"), 7).Put(40, "Steps suppressed", 20)
                    .Put(61, CobolEdit.Field(tSuppressed, "ZZZ,ZZ9"), 7).Put(75, "Escalations", 16).Put(92, CobolEdit.Field(nEscalated, "ZZZ,ZZ9"), 7)),
                L(1, Cols().Put(1, "Overdue debt GBP", 24).Put(24, ArBatch.M17(tOwed), 17).Put(45, "Chaseable", 16).Put(62, ArBatch.M17(tChase), 17)
                    .Put(85, "Disputed", 10).Put(96, ArBatch.M17(tDisputed), 17)),
                L(1, Cols().Put(1, "Letters written", 16).Put(18, CobolEdit.Field(nLetters, "ZZZ,ZZ9"), 7).Put(32, "Run mode", 10).Put(43, mode, 10)),
                L(2, Cols().Put(1, "A dispute classifies the debt; it does not erase", 50)
                    .Lit(52, "it.  Collection continues on the undisputed part."))));
        }
        ctx.Display("STERP-I-0401 " + ArBatch.Num(tAccounts, 7) + " overdue account(s), " + ArBatch.Num(nLetters, 7) + " letter(s)");
        ctx.Display("STERP-I-0402 letters " + letterName);
        ctx.AnnounceSpool(spoolName);
        ctx.RowsProcessed = rows;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = co, ["mode"] = mode, ["asAt"] = ArBatch.D8(asat), ["accounts"] = accounts,
            ["accountsConsidered"] = tAccounts, ["stepsSuppressed"] = tSuppressed, ["escalations"] = nEscalated, ["lettersWritten"] = nLetters,
            ["overdueGbp"] = tOwed, ["chaseableGbp"] = tChase, ["disputedGbp"] = tDisputed, ["letterFile"] = letterName, ["returnCode"] = rc,
        };
        return rc;
    }

    /// <summary>STRING ... DELIMITED BY SIZE INTO LTR-REC (PIC X(132)).</summary>
    private static string Ltr(string text) => CobolText.Trimmed(text, 132);

    /// <summary>1600-UPDATE-WORKLIST (+1650 case, 1680 action, 1690 item stamps); false when no case could be found or opened.</summary>
    private static async Task<bool> UpdateWorklistAsync(ISqlRunner sql, string co, string user, int asat, int nextN, int step, string actionType,
        Det d, CancellationToken ct)
    {
        var asatText = ArBatch.D8(asat);
        // 1650-FIND-CASE
        long caseId = 0;
        SqlioRow? row = null;
        try
        {
            row = await sql.SingleAsync(
                "SELECT TOP 1 CONVERT(varchar(24),CaseId) FROM ar.CollectionCase WHERE CompanyCode = ? AND CustomerCode = ? AND " +
                "CaseType = 'COLLECTION' AND Status IN ('OPEN','ESCALATED') ORDER BY CaseId",
                [SqlioParm.Text(co), SqlioParm.Text(d.Cust)], ArBatch.Widths(30, 120), ct);
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
                    "NextActionDate, Status, CreatedBy) VALUES (?, ?, ?, 'COLLECTION', 'Opened by the collection run', ?, 0, ?, 'OPEN', ?); " +
                    "SELECT CONVERT(varchar(24),SCOPE_IDENTITY())",
                    [SqlioParm.Text(co), SqlioParm.Text(d.Cust), SqlioParm.Text(CobolText.Fit("COLL-" + d.Cust.Trim(' '), 30)),
                        SqlioParm.Text(d.Owner), SqlioParm.Text(ArBatch.D8(nextN)), SqlioParm.Text(user)],
                    ArBatch.Widths(1, 120), ct);
                if (row.Found) caseId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0);
            }
            catch (SqlioException)
            {
                caseId = 0;
            }
        }
        if (caseId == 0) return false;

        await IgnoreErrorsAsync(() => sql.ExecAsync(
            "UPDATE ar.CollectionCase SET CollectionStep = ?, NextActionDate = ?, NextAction = ?, OwnerUser = ISNULL(OwnerUser,?), " +
            "Status = CASE WHEN ? >= 4 THEN 'ESCALATED' ELSE Status END, PreActionProtocolSentAt = CASE WHEN ? = 4 THEN ? ELSE " +
            "PreActionProtocolSentAt END, ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE CaseId = ?",
            [SqlioParm.Integer(step), SqlioParm.Text(ArBatch.D8(nextN)), SqlioParm.Text("Next step after " + d.Step.Trim(' ')),
                SqlioParm.Text(d.Owner), SqlioParm.Integer(step), SqlioParm.Integer(step), SqlioParm.Text(asatText), SqlioParm.Text(user),
                SqlioParm.Integer(caseId)], ct));
        // 1680-WRITE-ACTION
        await IgnoreErrorsAsync(() => sql.ExecAsync(
            "INSERT INTO ar.CollectionAction (CaseId, ActionSeq, ActionType, ActionDate, PerformedBy, Narrative) SELECT ?, " +
            "ISNULL(MAX(ActionSeq),0) + 1, ?, ?, ?, ? FROM ar.CollectionAction WHERE CaseId = ?",
            [SqlioParm.Integer(caseId), SqlioParm.Text(actionType), SqlioParm.Text(asatText), SqlioParm.Text(user),
                SqlioParm.Text("Step " + d.Step.Trim(' ') + " issued for GBP " + Domain.ArEdit.Money(d.Chase) + " chaseable"),
                SqlioParm.Integer(caseId)], ct));
        // 1690-STAMP-ITEMS
        await IgnoreErrorsAsync(() => sql.ExecAsync(
            "UPDATE ar.OpenItem SET LastReminderStep = ?, LastReminderDate = ?, CollectionStatus = ?, ModifiedBy = ?, ModifiedAt = " +
            "SYSDATETIME() WHERE CompanyCode = ? AND CustomerCode = ? AND DueDate < ? AND ResidualAmountGBP > 0 AND IsDisputed = 0 AND " +
            "SettlementStatus IN ('OPEN','PARTIAL')",
            [SqlioParm.Integer(step), SqlioParm.Text(asatText), SqlioParm.Text(step >= 4 ? "ESCALATED" : "REMINDED"), SqlioParm.Text(user),
                SqlioParm.Text(co), SqlioParm.Text(d.Cust), SqlioParm.Text(asatText)], ct));
        return true;
    }

    /// <summary>An EXEC whose SQLIO return code the program never tests.</summary>
    internal static async Task IgnoreErrorsAsync(Func<Task<SqlioExecResult>> exec)
    {
        try
        {
            await exec();
        }
        catch (SqlioException)
        {
            // not tested by the legacy program
        }
    }
}
