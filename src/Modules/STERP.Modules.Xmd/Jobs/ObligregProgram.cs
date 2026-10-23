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
/// OBLIGREG (cobol/batch/xmd/OBLIGREG.cob; FR-STERP-XMD-022): statutory obligations register. Due dates are derived from
/// the company profile (accounting reference date, legal form, VAT periods, payment practices and packaging flags) through
/// DATECALC, matched against core.StatutoryObligation, flagged OVERDUE / DUE SOON and escalated; COMMIT MERGEs every derived
/// obligation (autocommit per statement). Listing FL-071 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class ObligregProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class Obligation
    {
        private string _desc = "";

        public string Code = "";

        /// <summary>OB-DESC X(42): the description is held cut to 42 characters (listing, JSON and escalation message).</summary>
        public string Desc
        {
            get => _desc;
            set => _desc = CobolText.Fit(value, 42);
        }

        public string Period = "";
        public int PStart;
        public int PEnd;
        public int Due;
        public string Owner = "";
        public string Status = "";
        public string Evidence = "";
        public int Days;
    }

    private sealed class Profile
    {
        public string Ard = "03-31";
        public string Form = "LTD";
        public string Ppr = "0";
        public string Pack = "0";
        public int ArdMm = 3;
        public int ArdDd = 31;
        public int PeThis;
        public int PeLast;
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
        // 0100-READ-ARGS
        var window = (int)CobolNumeric.Store(await b.GetParmNumAsync("XMD.OBLIG.ESCALATE.DAYS", 30m), 4, 0, signed: false);
        var daysText = b.GetArg("DAYS");
        if (!CobolText.IsSpaces(daysText)) window = (int)CobolNumeric.Store(CobolNumeric.NumVal(daysText), 4, 0, signed: false);
        var sAsat = BatchSuite.FormatDate(b.AsAt);
        var sMode = CobolText.Fit(b.Mode, 10);
        b.Display("STERP-I-0100 OBLIGREG register for " + CobolText.Trim(b.Co) + " as at " + Disp.D8(b.AsAt));

        result["asAt"] = IsoDate(b.AsAt);
        result["accountingPeriodEnd"] = null;
        result["window"] = window;
        result["mode"] = sMode.TrimEnd(' ');
        var list = new JsonArray();
        result["obligations"] = list;
        int cOpen = 0, cDone = 0, cLate = 0, cSoon = 0;

        if (!await b.OpenSpoolAsync("OBLIGREG", "Statutory obligations register", "Derived due dates, owners and completion evidence") || b.Rc > 4)
        {
            return Finish(b, result, cOpen, cDone, cLate, cSoon);
        }
        var p = new Profile();
        var sArd = await ReadCompanyAsync(b, p);
        result["accountingPeriodEnd"] = p.PeThis > 0 ? IsoDate(p.PeThis) : null;
        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Open        ").Num(14, cOpen, "ZZZ9").Lit(22, "Complete    ").Num(34, cDone, "ZZZ9")
                .Lit(42, "Due within window").Num(60, cSoon, "ZZZ9").Lit(68, "Overdue     ").Num(80, cLate, "ZZZ9")
                .Lit(90, "Escalated   ").Num(102, b.Escalated, "ZZZ9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Completion requires the submission reference or")
                .Lit(50, "the filed document; a bare tick is not evidence.").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Register  ").Put(11, sAsat, 10).Lit(26, "Accounting reference date  ")
                .Put(54, sArd, 10).Lit(68, "Escalation window   ").Num(88, window, "ZZZ9").Lit(93, "days ").Put(100, sMode, 10).Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Obligation").Lit(22, "Description").Lit(65, "Period").Lit(86, "Due date")
                .Lit(97, "Days").Lit(103, "Status").Lit(116, "Owner").Text),
            RwLine.Plus1(() => new string('-', 132)));

        var obs = await DeriveAsync(b, p);
        await MatchRegisterAsync(b, obs);

        // 3000-LIST
        foreach (var ob in obs)
        {
            var dCode = CobolText.Fit(ob.Code, 20);
            var dOwner = CobolText.Fit(ob.Owner, 14);
            var dDue = BatchSuite.FormatDate(ob.Due);
            var dFlag = "";
            if (ob.Status.TrimEnd(' ') == "COMPLETE")
            {
                cDone++;
            }
            else
            {
                cOpen++;
                if (ob.Days < 0)
                {
                    cLate++;
                    dFlag = "OVERDUE";
                }
                else if (ob.Days <= window)
                {
                    cSoon++;
                    dFlag = "DUE SOON";
                }
            }
            b.RowsOut++;
            rw.Generate(RwLine.Plus1(() => new RwText().Put(1, dCode, 20).Put(22, ob.Desc, 42).Put(65, ob.Period, 20).Put(86, dDue, 10)
                .Num(97, ob.Days, "-ZZZ9").Put(103, ob.Status, 12).Put(116, dOwner, 14).Text));
            if (dFlag.Length > 0) await EscalateAsync(b, ob, dFlag);
            if (!CobolText.IsSpaces(ob.Evidence) || dFlag.Length > 0)
            {
                rw.Generate(RwLine.Plus1(() => new RwText().Lit(24, "Evidence: ").Put(34, ob.Evidence, 30).Put(66, dFlag, 12).Text));
            }
            await UpsertAsync(b, ob);
            list.Add(new JsonObject
            {
                ["code"] = dCode.TrimEnd(' '),
                ["description"] = CobolText.Fit(ob.Desc, 42).TrimEnd(' '),
                ["period"] = CobolText.Fit(ob.Period, 20).TrimEnd(' '),
                ["dueDate"] = IsoDate(ob.Due),
                ["daysToDue"] = ob.Days,
                ["status"] = CobolText.Fit(ob.Status, 12).TrimEnd(' '),
                ["owner"] = dOwner.TrimEnd(' '),
                ["evidence"] = CobolText.Fit(ob.Evidence, 30).TrimEnd(' '),
                ["flag"] = dFlag,
            });
        }
        if (cLate > 0 && b.Rc < 4) b.Rc = 4;
        b.Display("STERP-I-3001 " + Disp.Signed(obs.Count, 4) + " obligation(s), " + Disp.Signed(cOpen, 5) + " open, " +
                  Disp.Signed(cLate, 5) + " overdue, " + Disp.Signed(b.Escalated, 9) + " escalated");

        rw.Terminate();
        b.CloseSpool();
        return Finish(b, result, cOpen, cDone, cLate, cSoon);
    }

    private static int Finish(XmdBatch b, JsonObject result, int cOpen, int cDone, int cLate, int cSoon)
    {
        result["openCount"] = cOpen;
        result["completeCount"] = cDone;
        result["overdueCount"] = cLate;
        result["dueSoonCount"] = cSoon;
        result["escalatedCount"] = b.Escalated;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>0200-READ-COMPANY + 0250-PERIOD-ENDS: the profile; not on file is RC 8 and the run goes on with the defaults.</summary>
    private static async Task<string> ReadCompanyAsync(XmdBatch b, Profile p)
    {
        SqlioRow? row = null;
        try
        {
            row = await b.Sql.SingleAsync(
                "SELECT ISNULL(AccountingRefDate,'03-31'), ISNULL(AccountingFramework,'FRS102'), ISNULL(VatRegNo,''), ISNULL(VatStagger,''), " +
                "ISNULL(VatScheme,'INVOICE'), CONVERT(char(1),ISNULL(PaymentPracticesInScope,0)), CONVERT(char(1),ISNULL(PackagingInScope,0)), " +
                "ISNULL(LegalForm,'LTD') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(b.Co)], Widths[..8], b.Ct);
        }
        catch (SqlioException)
        {
        }
        if (row is null || !row.Found)
        {
            b.Rc = 8;
            b.Display("STERP-E-0201 company " + CobolText.Trim(b.Co) + " is not on file");
            return "";
        }
        p.Ard = row.Raw(0)[..5];
        p.Ppr = row.Raw(5)[..1];
        p.Pack = row.Raw(6)[..1];
        p.Form = CobolText.Fit(row.Raw(7), 10);
        p.ArdMm = (int)CobolNumeric.Store(CobolNumeric.NumVal(p.Ard[..2]), 2, 0, signed: false);
        p.ArdDd = (int)CobolNumeric.Store(CobolNumeric.NumVal(p.Ard.Substring(3, 2)), 2, 0, signed: false);
        // 0250-PERIOD-ENDS
        var year = (int)CobolNumeric.Store(CobolNumeric.NumVal(Disp.D8(b.AsAt)[..4]), 4, 0);
        p.PeThis = (int)CobolNumeric.Store(year * 10000m + p.ArdMm * 100m + p.ArdDd, 8, 0, signed: false);
        if (p.PeThis > b.AsAt) p.PeThis = (int)CobolNumeric.Store(p.PeThis - 10000m, 8, 0, signed: false);
        p.PeLast = (int)CobolNumeric.Store(p.PeThis - 10000m, 8, 0, signed: false);
        return BatchSuite.FormatDate(p.PeThis);
    }

    /// <summary>1000-DERIVE.</summary>
    private static async Task<List<Obligation>> DeriveAsync(XmdBatch b, Profile p)
    {
        var obs = new List<Obligation>();
        var cal = b.Ctx.Get<IBusinessCalendar>();
        await VatAsync(b, cal, obs);
        // 1200-ACCOUNTS
        for (var i = 1; i <= 2; i++)
        {
            var ob = Add(obs, "ANNUALACCTS", "Annual accounts filing at Companies House", "FINCTRL");
            ob.PEnd = i == 1 ? p.PeLast : p.PeThis;
            ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 12, b.Ct);
            ob.Due = await AddMonthsAsync(cal, ob.PEnd, p.Form.TrimEnd(' ') == "PLC" ? 6 : 9, b.Ct);
            PeriodText(ob);
        }
        // 1300-CORPTAX
        {
            var ob = Add(obs, "CORPTAXPAY", "Corporation tax payment (nine months and a day)", "FINCTRL");
            ob.PEnd = p.PeLast;
            ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 12, b.Ct);
            ob.Due = await AddDaysAsync(cal, await AddMonthsAsync(cal, p.PeLast, 9, b.Ct), 1, b.Ct);
            PeriodText(ob);
            ob = Add(obs, "CORPTAX", "Corporation tax return CT600 (twelve months)", "FINCTRL");
            ob.PEnd = p.PeLast;
            ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 12, b.Ct);
            ob.Due = await AddMonthsAsync(cal, p.PeLast, 12, b.Ct);
            PeriodText(ob);
        }
        // 1400-CONFSTMT
        {
            var ob = Add(obs, "CONFSTMT", "Confirmation statement (fourteen days)", "SYSADM");
            ob.PEnd = p.PeThis;
            ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 12, b.Ct);
            ob.Due = await AddDaysAsync(cal, p.PeThis, 14, b.Ct);
            PeriodText(ob);
        }
        if (p.Ppr == "1")
        {
            // 1500-PPR
            for (var i = 1; i <= 2; i++)
            {
                var ob = Add(obs, "PAYPRACTICES", "Payment practices and performance report", "FINCTRL");
                var d = p.PeThis;
                if (i == 1) d = await AddMonthsAsync(cal, d, -6, b.Ct);
                ob.PEnd = d;
                ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 6, b.Ct);
                ob.Due = await AddDaysAsync(cal, ob.PEnd, 30, b.Ct);
                PeriodText(ob);
            }
        }
        if (p.Pack == "1")
        {
            // 1600-PACKAGING
            var ob = Add(obs, "PACKAGINGDATA", "Plastic Packaging Tax and packaging EPR data", "COSTACC");
            ob.PEnd = p.PeThis;
            ob.PStart = await PeriodStartAsync(cal, ob.PEnd, 12, b.Ct);
            ob.Due = await AddMonthsAsync(cal, p.PeThis, 1, b.Ct);
            PeriodText(ob);
        }
        return obs;
    }

    /// <summary>1100-VAT: the six latest VAT periods ended on or before the as-at date (an SQL error is RC 4).</summary>
    private static async Task VatAsync(XmdBatch b, IBusinessCalendar cal, List<Obligation> obs)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT VatPeriodKey, CONVERT(char(8),StartDate,112), CONVERT(char(8),EndDate,112), CONVERT(char(8),FilingDueDate,112), Status, " +
                "ISNULL(SubmissionRef,'') FROM fin.VatPeriod WHERE CompanyCode = ? AND CONVERT(char(8),EndDate,112) <= ? ORDER BY EndDate DESC",
                [SqlioParm.Text(b.Co), SqlioParm.Text(Disp.D8(b.AsAt))], Widths[..6], b.Ct);
        }
        catch (SqlioException)
        {
            b.Rc = 4;
            return;
        }
        var j = 0;
        foreach (var row in rows)
        {
            if (j > 5) break;
            j++;
            var ob = new Obligation
            {
                Code = "VATRETURN",
                Desc = "VAT return and payment to HMRC",
                Period = CobolText.Fit(row.Raw(0), 20),
                PStart = BatchSuite.IsoToNum(row.Raw(1)),
                PEnd = BatchSuite.IsoToNum(row.Raw(2)),
                Owner = "VATOWNER",
                Evidence = CobolText.Fit(row.Raw(5), 30),
                Status = CobolText.EqualsPadded(row.Raw(4), "SUBMITTED") ? "COMPLETE" : "OPEN",
            };
            var due = BatchSuite.IsoToNum(row.Raw(3));
            if (due == 0) due = await AddDaysAsync(cal, await AddMonthsAsync(cal, ob.PEnd, 1, b.Ct), 7, b.Ct);
            ob.Due = due;
            obs.Add(ob);
        }
    }

    /// <summary>2000-MATCH-REGISTER: evidence, owner and status from core.StatutoryObligation; days to the due date.</summary>
    private static async Task MatchRegisterAsync(XmdBatch b, List<Obligation> obs)
    {
        foreach (var ob in obs)
        {
            try
            {
                var row = await b.Sql.SingleAsync(
                    "SELECT Status, ISNULL(EvidenceRef,''), ISNULL(OwnerUser,''), ISNULL(CONVERT(char(8),CompletedAt,112),'') FROM " +
                    "core.StatutoryObligation WHERE CompanyCode = ? AND ObligationCode = ? AND PeriodKey = ?",
                    [SqlioParm.Text(b.Co), SqlioParm.Text(ob.Code), SqlioParm.Text(ob.Period)], Widths[..4], b.Ct);
                if (row.Found)
                {
                    ob.Status = CobolText.Fit(row.Raw(0), 12);
                    if (!CobolText.IsSpaces(row.Raw(1))) ob.Evidence = CobolText.Fit(row.Raw(1), 30);
                    if (!CobolText.IsSpaces(row.Raw(2))) ob.Owner = CobolText.Fit(row.Raw(2), 20);
                }
            }
            catch (SqlioException)
            {
            }
            if (ob.Status.TrimEnd(' ') == "COMPLETE" && CobolText.IsSpaces(ob.Evidence)) ob.Status = "NO EVIDENCE";
            ob.Days = ob.Due == 0
                ? 0
                : (int)CobolNumeric.Store(CobolDate.IntegerOfDate(ob.Due) - (decimal)CobolDate.IntegerOfDate(b.AsAt), 5, 0);
        }
    }

    /// <summary>3100-ESCALATE: HOLD priority 1 when overdue, else WARNING priority 3, owned by the obligation owner.</summary>
    private static async Task EscalateAsync(XmdBatch b, Obligation ob, string flag)
    {
        var docRef = CobolText.Fit(CobolText.Trim(b.Co) + "/" + CobolText.Trim(ob.Code) + "/" + CobolText.Trim(ob.Period), 200)[..40];
        var text2 = CobolText.Fit(CobolText.Trim(ob.Desc) + " is " + CobolText.Trim(flag) +
                                  " and has no completion evidence. Escalated to the owner and to the financial controller.", 200);
        var overdue = flag == "OVERDUE";
        await b.RaiseAsync(new ExcpraisArea
        {
            Severity = overdue ? "HOLD" : "WARNING",
            Priority = overdue ? 1 : 3,
            Category = "STATUTORY OBLIGATION",
            DocumentType = "OBLIGATION",
            DocumentRef = docRef.TrimEnd(' '),
            MessageText = text2.TrimEnd(' '),
            OwnerRole = CobolText.Fit(ob.Owner, 20).TrimEnd(' '),
            DueAt = ob.Due,
        });
        b.Escalated++;
    }

    /// <summary>3200-UPSERT (COMMIT only): MERGE core.StatutoryObligation (failure: RC 4, STERP-W-3201).</summary>
    private static async Task UpsertAsync(XmdBatch b, Obligation ob)
    {
        if (!b.IsCommit) return;
        var co = SqlioParm.Text(b.Co);
        var code = SqlioParm.Text(ob.Code);
        var period = SqlioParm.Text(ob.Period);
        var due = SqlioParm.Text(Disp.D8(ob.Due));
        var owner = SqlioParm.Text(ob.Owner);
        var ps = SqlioParm.Text(Disp.D8(ob.PStart));
        var pe = SqlioParm.Text(Disp.D8(ob.PEnd));
        try
        {
            await b.Sql.ExecAsync(
                "MERGE core.StatutoryObligation AS t USING (SELECT ? AS CompanyCode, ? AS ObligationCode, ? AS PeriodKey) AS s ON t.CompanyCode = " +
                "s.CompanyCode AND t.ObligationCode = s.ObligationCode AND t.PeriodKey = s.PeriodKey WHEN MATCHED THEN UPDATE SET DueDate = " +
                "CONVERT(date,?,112), OwnerRole = ?, PeriodStart = CONVERT(date,?,112), PeriodEnd = CONVERT(date,?,112) WHEN NOT MATCHED THEN " +
                "INSERT (CompanyCode, ObligationCode, PeriodKey, PeriodStart, PeriodEnd, DueDate, OwnerRole, Status) VALUES (?,?,?,CONVERT(date,?,112)," +
                "CONVERT(date,?,112),CONVERT(date,?,112),?,'OPEN');",
                [co, code, period, due, owner, ps, pe, co, code, period, ps, pe, due, owner], b.Ct);
            b.Posted++;
        }
        catch (SqlioException ex)
        {
            b.Rc = 4;
            b.Display("STERP-W-3201 " + CobolText.Fit(ex.DriverText, 90));
        }
    }

    private static Obligation Add(List<Obligation> obs, string code, string desc, string owner)
    {
        var ob = new Obligation { Code = code, Desc = desc, Owner = owner, Status = "OPEN" };
        obs.Add(ob);
        return ob;
    }

    /// <summary>8300-PERIOD-TEXT: "PE" + the period end; no evidence yet.</summary>
    private static void PeriodText(Obligation ob)
    {
        ob.Period = CobolText.Fit("PE" + Disp.D8(ob.PEnd), 20);
        ob.Evidence = "";
    }

    /// <summary>8400-PERIOD-START: period end less n months plus one day.</summary>
    private static async Task<int> PeriodStartAsync(IBusinessCalendar cal, int pend, int months, CancellationToken ct) =>
        await AddDaysAsync(cal, await AddMonthsAsync(cal, pend, -months, ct), 1, ct);

    /// <summary>8100-ADD-MONTHS: DATECALC ADDMONTH; the date is kept when DATECALC refuses (RC 8 or more).</summary>
    private static async Task<int> AddMonthsAsync(IBusinessCalendar cal, int date, int months, CancellationToken ct)
    {
        var r = await cal.AddMonthsAsync(date, months, ct);
        return r.Header.ReturnCode < 8 ? r.ResultDate : date;
    }

    /// <summary>8200-ADD-DAYS: DATECALC ADDDAYS; the date is kept when DATECALC refuses.</summary>
    private static async Task<int> AddDaysAsync(IBusinessCalendar cal, int date, int days, CancellationToken ct)
    {
        var r = await cal.AddDaysAsync(date, days, ct);
        return r.Header.ReturnCode < 8 ? r.ResultDate : date;
    }

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
