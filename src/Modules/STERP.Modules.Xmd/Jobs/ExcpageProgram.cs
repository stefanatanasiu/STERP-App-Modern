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
/// EXCPAGE (cobol/batch/xmd/EXCPAGE.cob; FR-STERP-XMD-021): exception ageing, ownership and escalation plus the RPT-016
/// approvals, audit and data-quality counts. COMMIT raises one EXCEPTION ESCALATION per escalated item and stamps
/// EscalatedAt/AgeDays where they were NULL (autocommit per statement). The shared XB-DATE-ED is overwritten by every
/// 9450-FORMAT-DATE of a listed exception and then bounds the RPT-016 queries (BRF-XMDBATPR-04, reproduced).
/// Listing FL-069 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class ExcpageProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class State
    {
        public string DateEd = "00000000";
        public int B0007, B0830, B3160, B61Up, Unowned, Holds;
        public decimal V0007, V0830, V3160, V61Up;
        public int Approvals, ApprOld, Overrides, Sensitive, Dq;
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
        var days = (int)CobolNumeric.Store(await b.GetParmNumAsync("XMD.EXCEPTION.ESCALATE.DAYS", 30m), 4, 0, signed: false);
        var daysText = b.GetArg("DAYS");
        if (!CobolText.IsSpaces(daysText)) days = (int)CobolNumeric.Store(CobolNumeric.NumVal(daysText), 4, 0, signed: false);
        var owner = "ALL";
        var ownerText = b.GetArg("OWNER");
        if (!CobolText.IsSpaces(ownerText)) owner = CobolText.Fit(ownerText, 200)[..20];
        var sAsat = FormatDate(st, b.AsAt);
        st.DateEd = Disp.D8(b.AsAt);
        var sMode = CobolText.Fit(b.Mode, 10);
        b.Display("STERP-I-0100 EXCPAGE ageing for " + CobolText.Trim(b.Co) + " as at " + Disp.D8(b.AsAt));

        result["asAt"] = IsoDate(b.AsAt);
        result["owner"] = CobolText.Fit(owner, 20).TrimEnd(' ');
        result["escalationDays"] = days;
        result["mode"] = sMode.TrimEnd(' ');
        var exceptions = new JsonArray();
        var categories = new JsonArray();
        result["exceptions"] = exceptions;

        if (!await b.OpenSpoolAsync("EXCPAGE", "Exception ageing, ownership and escalation", "Audit, approvals and data quality review") || b.Rc > 4)
        {
            return Finish(b, st, result, categories);
        }
        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Ageing buckets    ").Lit(20, "0-7 days ").Num(29, st.B0007, "ZZZ9")
                .Num(35, st.V0007, "ZZZ,ZZZ,ZZ9.99-").Lit(54, "8-30 days ").Num(64, st.B0830, "ZZZ9").Num(70, st.V0830, "ZZZ,ZZZ,ZZ9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(20, "31-60 days ").Num(31, st.B3160, "ZZZ9").Num(37, st.V3160, "ZZZ,ZZZ,ZZ9.99-")
                .Lit(54, "61 days plus  ").Num(68, st.B61Up, "ZZZ9").Num(74, st.V61Up, "ZZZ,ZZZ,ZZ9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Holds outstanding ").Num(20, st.Holds, "ZZZ9").Lit(28, "Exceptions without owner")
                .Num(53, st.Unowned, "ZZZ9").Lit(60, "Escalated   ").Num(72, b.Escalated, "ZZZ9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Reassigning an exception does not reset its age")
                .Lit(50, "or erase the previous owner (XMD-006).").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "As at     ").Put(11, sAsat, 10).Lit(26, "Owner filter  ").Put(40, owner, 20)
                .Lit(64, "Escalation age    ").Num(82, days, "ZZZ9").Lit(87, "days ").Put(96, sMode, 10).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Open and acknowledged exceptions only. Age runs")
                .Lit(48, "from first detection, not from reassignment.").Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Owner").Lit(22, "Id").Lit(30, "Mod").Lit(35, "Severity").Lit(45, "Category")
                .Lit(77, "Document").Lit(92, "Raised").Lit(104, "Age").Lit(109, "Amount GBP ").Lit(124, "Flag  ").Text),
            RwLine.Plus1(() => new string('-', 132)));

        await ListExceptionsAsync(b, st, rw, days, owner, exceptions);
        await Rpt16Async(b, st, rw, categories);

        // TERMINATE: CF FINAL
        rw.Terminate();
        b.CloseSpool();
        return Finish(b, st, result, categories);
    }

    private static int Finish(XmdBatch b, State st, JsonObject result, JsonArray categories)
    {
        result["count0to7"] = st.B0007;
        result["count8to30"] = st.B0830;
        result["count31to60"] = st.B3160;
        result["count61plus"] = st.B61Up;
        result["value0to7Gbp"] = CobolNumeric.WithScale(st.V0007, 2);
        result["value8to30Gbp"] = CobolNumeric.WithScale(st.V0830, 2);
        result["value31to60Gbp"] = CobolNumeric.WithScale(st.V3160, 2);
        result["value61plusGbp"] = CobolNumeric.WithScale(st.V61Up, 2);
        result["unowned"] = st.Unowned;
        result["holds"] = st.Holds;
        result["escalated"] = b.Escalated;
        result["approvalsPending"] = st.Approvals;
        result["approvalsOlderThan5Days"] = st.ApprOld;
        result["auditedWithAuthority"] = st.Overrides;
        result["restrictedAuditRows"] = st.Sensitive;
        result["categories"] = categories;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>1000-LIST-EXCEPTIONS.</summary>
    private static async Task ListExceptionsAsync(XmdBatch b, State st, GnuReportWriter rw, int days, string owner, JsonArray exceptions)
    {
        IReadOnlyList<SqlioRow> rows;
        var asat = SqlioParm.Text(st.DateEd);
        var own = SqlioParm.Text(CobolText.Fit(owner, 20));
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT CONVERT(varchar(12),e.ExceptionId), ISNULL(NULLIF(e.OwnerUser,''),ISNULL(NULLIF(e.OwnerRole,''),'(UNOWNED)')), " +
                "e.Module, e.Severity, e.Category, ISNULL(e.DocumentType,''), ISNULL(e.DocumentRef,''), CONVERT(char(8),e.RaisedAt,112), " +
                "ISNULL(CONVERT(char(8),e.DueAt,112),''), CONVERT(varchar(24),ISNULL(e.AmountGBP,0)), e.Status, LEFT(ISNULL(e.Message,''),100), " +
                "CONVERT(varchar(12),DATEDIFF(day,e.RaisedAt,CONVERT(date,?,112))) FROM core.ExceptionItem e WHERE e.CompanyCode = ? AND " +
                "e.Status IN ('OPEN','ACKNOWLEDGED') AND CONVERT(char(8),e.RaisedAt,112) <= ? AND (? = 'ALL' OR e.OwnerUser = ? OR " +
                "e.OwnerRole = ?) ORDER BY ISNULL(NULLIF(e.OwnerUser,''),ISNULL(NULLIF(e.OwnerRole,''),'(UNOWNED)')), e.RaisedAt",
                [asat, SqlioParm.Text(b.Co), asat, own, own, own], Widths, b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        foreach (var row in rows)
        {
            if (await OneExceptionAsync(b, st, rw, days, row, exceptions)) break;
        }
        b.Display("STERP-I-1002 " + Disp.Signed(b.RowsOut, 9) + " open exception(s), " + Disp.Signed(b.Escalated, 9) + " escalated");
    }

    /// <summary>
    /// 1100-ONE-EXCEPTION; true when the escalation stamp (1300, COMMIT only) ran. 1300 issues its UPDATE and CLOS on SQLIO
    /// cursor 3 and leaves SQLIO-CURSOR = 3, so the next FTCH of the 1000 loop reads the closed cursor 3, gets NOT FOUND and
    /// ends the listing: in COMMIT the listing stops after the first escalated item (observed with EXCPAGE.exe).
    /// </summary>
    private static async Task<bool> OneExceptionAsync(XmdBatch b, State st, GnuReportWriter rw, int days, SqlioRow row, JsonArray exceptions)
    {
        var dId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0, signed: false);
        var dOwner = CobolText.Fit(row.Raw(1), 20);
        var dModule = CobolText.Fit(row.Raw(2), 6);
        var dSeverity = CobolText.Fit(row.Raw(3), 8);
        var dCategory = CobolText.Fit(row.Raw(4), 30);
        var dDocType = CobolText.Fit(row.Raw(5), 12);
        var dDocRef = CobolText.Fit(row.Raw(6), 24);
        var raisedNum = BatchSuite.IsoToNum(row.Raw(7));
        var dRaised = FormatDate(st, raisedNum);
        var dateEd2 = BatchSuite.IsoToNum(row.Raw(8));
        FormatDate(st, dateEd2);
        var dAmount = CobolNumeric.Store(row.NumVal(9), 13, 2);
        var dStatus = CobolText.Fit(row.Raw(10), 12);
        var dText = CobolText.Fit(row.Raw(11), 100);
        var dAge = (int)CobolNumeric.Store(row.NumVal(12), 5, 0);
        var dFlag = "";
        if (dOwner.TrimEnd(' ') == "(UNOWNED)")
        {
            st.Unowned++;
            dFlag = "NO OWNER";
        }
        if (dSeverity.TrimEnd(' ') == "HOLD") st.Holds++;
        if (dAge <= 7)
        {
            st.B0007++;
            st.V0007 = CobolNumeric.Store(st.V0007 + dAmount, 13, 2);
        }
        else if (dAge <= 30)
        {
            st.B0830++;
            st.V0830 = CobolNumeric.Store(st.V0830 + dAmount, 13, 2);
        }
        else if (dAge <= 60)
        {
            st.B3160++;
            st.V3160 = CobolNumeric.Store(st.V3160 + dAmount, 13, 2);
        }
        else
        {
            st.B61Up++;
            st.V61Up = CobolNumeric.Store(st.V61Up + dAmount, 13, 2);
        }
        if (dateEd2 > 0 && dateEd2 < b.AsAt) dFlag = "PAST DUE";
        if (dAge > days) dFlag = "ESCALATE";
        b.RowsOut++;
        rw.Generate(RwLine.Plus1(() => new RwText().Put(1, dOwner, 20).Num(22, dId, "ZZZZZZZZ9").Put(32, dModule, 6)
            .Put(39, dSeverity, 8).Put(48, dCategory, 28).Put(77, dDocRef, 14).Put(92, dRaised, 10).Num(103, dAge, "-ZZZ9")
            .Num(109, dAmount, "ZZZ,ZZZ,ZZ9.99-").Put(124, dFlag, 9).Text));
        if (!CobolText.IsSpaces(dText))
        {
            rw.Generate(RwLine.Plus1(() => new RwText().Put(24, dText, 100).Put(126, dStatus, 6).Text));
        }
        exceptions.Add(new JsonObject
        {
            ["owner"] = dOwner.TrimEnd(' '),
            ["exceptionId"] = dId,
            ["module"] = dModule.TrimEnd(' '),
            ["severity"] = dSeverity.TrimEnd(' '),
            ["category"] = dCategory.TrimEnd(' '),
            ["documentType"] = dDocType.TrimEnd(' '),
            ["documentRef"] = dDocRef.TrimEnd(' '),
            ["raised"] = IsoDate(raisedNum),
            ["due"] = IsoDate(dateEd2),
            ["ageDays"] = dAge,
            ["amountGbp"] = CobolNumeric.WithScale(dAmount, 2),
            ["status"] = dStatus.TrimEnd(' '),
            ["flag"] = dFlag,
            ["message"] = dText.TrimEnd(' '),
        });
        if (dFlag is "ESCALATE" or "NO OWNER")
        {
            await EscalateAsync(b, dId, dAge, dCategory, dDocRef, dOwner, dAmount);
            return b.IsCommit;
        }
        return false;
    }

    /// <summary>1200-ESCALATE + 1300-STAMP-ESCALATION.</summary>
    private static async Task EscalateAsync(XmdBatch b, long dId, int dAge, string dCategory, string dDocRef, string dOwner, decimal dAmount)
    {
        var seq = Disp.Unsigned(dId, 9);
        var text2 = CobolText.Fit(CobolText.Trim(dCategory) + " on " + CobolText.Trim(dDocRef) + " has been open " + Disp.Int(dAge) +
                                  " days with owner " + CobolText.Trim(dOwner) +
                                  ". Escalated to the financial controller; the age and the previous owner are retained.", 200);
        var ex = new ExcpraisArea
        {
            Severity = "WARNING",
            Category = "EXCEPTION ESCALATION",
            DocumentType = "EXCEPTION",
            DocumentRef = CobolText.Fit("EXC" + seq, 40).TrimEnd(' '),
            MessageText = text2.TrimEnd(' '),
            OwnerRole = "FINCTRL",
            Priority = 2,
            AmountGbp = dAmount,
            DueAt = b.AsAt,
        };
        await b.RaiseAsync(ex);
        b.Escalated++;
        if (!b.IsCommit) return;
        try
        {
            await b.Sql.ExecAsync(
                "UPDATE core.ExceptionItem SET EscalatedAt = SYSDATETIME(), AgeDays = ? WHERE ExceptionId = ? AND EscalatedAt IS NULL",
                [SqlioParm.Text(Disp.Int(dAge)), SqlioParm.Text(seq)], b.Ct);
        }
        catch (SqlioException)
        {
            // EXEC result not tested
        }
    }

    /// <summary>2000-RPT16-SECTIONS (+ 2100-AUDIT-COUNTS, 2200-DATA-QUALITY): bounded by the current XB-DATE-ED.</summary>
    private static async Task Rpt16Async(XmdBatch b, State st, GnuReportWriter rw, JsonArray categories)
    {
        rw.Generate(Sum("Audit, approvals and data quality (RPT-016)", 0, ""));
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(12),COUNT(*)), CONVERT(varchar(12),SUM(CASE WHEN DATEDIFF(day,RequestedAt,CONVERT(date,?,112)) > 5 " +
                "THEN 1 ELSE 0 END)) FROM core.Approval WHERE CompanyCode = ? AND Status = 'PENDING'",
                [SqlioParm.Text(st.DateEd), SqlioParm.Text(b.Co)], Widths[..2], b.Ct);
            if (row.Found)
            {
                st.Approvals = (int)CobolNumeric.Store(row.NumVal(0), 5, 0);
                st.ApprOld = (int)CobolNumeric.Store(row.NumVal(1), 5, 0);
            }
        }
        catch (SqlioException)
        {
        }
        rw.Generate(Sum("Approvals pending", st.Approvals, "of which older than five days: " + Disp.Int(st.ApprOld)));

        // 2100-AUDIT-COUNTS
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(12),SUM(CASE WHEN ISNULL(AuthorityRef,'') <> '' THEN 1 ELSE 0 END)), CONVERT(varchar(12),SUM(CASE WHEN " +
                "ISNULL(IsSensitive,0) = 1 THEN 1 ELSE 0 END)) FROM core.AuditLog WHERE CompanyCode = ? AND CONVERT(char(8),EventTime,112) <= ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.DateEd)], Widths[..2], b.Ct);
            if (row.Found)
            {
                st.Overrides = (int)CobolNumeric.Store(row.NumVal(0), 5, 0);
                st.Sensitive = (int)CobolNumeric.Store(row.NumVal(1), 5, 0);
            }
        }
        catch (SqlioException)
        {
        }
        rw.Generate(Sum("Audited actions carrying an authority reference", st.Overrides, "overrides and approvals evidenced in core.AuditLog"));
        rw.Generate(Sum("Restricted audit rows (authorised reviewers only)", st.Sensitive, "values withheld from this listing and its export"));

        // 2200-DATA-QUALITY: at most 16 categories
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT ISNULL(Category,'(none)'), CONVERT(varchar(12),COUNT(*)) FROM core.ExceptionItem WHERE CompanyCode = ? AND Status <> " +
                "'RESOLVED' AND CONVERT(char(8),RaisedAt,112) <= ? GROUP BY Category ORDER BY COUNT(*) DESC",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.DateEd)], Widths[..2], b.Ct);
        }
        catch (SqlioException)
        {
            return;
        }
        var j = 0;
        foreach (var row in rows)
        {
            if (j > 15) break;
            j++;
            st.Dq++;
            var label = CobolText.Fit("  by category: " + row.Raw(0)[..36], 52);
            var value = (long)CobolNumeric.Store(row.NumVal(1), 9, 0);
            rw.Generate(Sum(label, value, ""));
            categories.Add(new JsonObject { ["category"] = row.Raw(0)[..36].TrimEnd(' '), ["count"] = value });
        }
    }

    /// <summary>DE-SUM.</summary>
    private static RwLine Sum(string label, long value, string note) =>
        RwLine.Plus1(() => new RwText().Put(3, label, 52).Num(58, value, "Z(8)9").Put(70, note, 60).Text);

    /// <summary>9450-FORMAT-DATE: dd/mm/yyyy (00/00/0000 for 0); a non-zero date also overwrites the shared XB-DATE-ED.</summary>
    private static string FormatDate(State st, int yyyymmdd)
    {
        if (yyyymmdd == 0) return "00/00/0000";
        st.DateEd = Disp.D8(yyyymmdd);
        return BatchSuite.FormatDate(yyyymmdd);
    }

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
