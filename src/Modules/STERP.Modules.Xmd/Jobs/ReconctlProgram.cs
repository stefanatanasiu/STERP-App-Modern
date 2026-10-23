using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// RECONCTL (cobol/batch/xmd/RECONCTL.cob; FR-STERP-XMD-020): control-account reconciliation of the eight control families
/// at one cutoff. Every statement autocommits on the job's SQLIO session; PREVIEW lists and counts, COMMIT replaces the
/// OPEN rpt.ReconciliationRun rows of the cutoff, inserts one row per family and raises one WARNING exception per
/// non-agreed family. Listing FL-072 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class ReconctlProgram
{
    private sealed record Family(string Code, string Desc, int Sign, string Owner);

    /// <summary>WS-CTL-TABLE (RECONCTL.cob:66-104).</summary>
    private static readonly Family[] Families =
    [
        new("AR", "Trade debtors control", +1, "CREDMGR"),
        new("AP", "Trade creditors control", -1, "APMGR"),
        new("INVENTORY", "Inventory control", +1, "COSTACC"),
        new("WIP", "Work in progress control", +1, "COSTACC"),
        new("GRNI", "Goods received not invoiced", -1, "PURMGR"),
        new("VAT", "VAT control", -1, "VATOWNER"),
        new("BANK", "Bank and cash control", +1, "TREASURY"),
        new("ACCRUAL", "Service accruals control", -1, "FINCTRL"),
    ];

    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class Gl
    {
        public decimal Bal;
        public string Accts = "";
        public decimal Cnt;
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
        await b.ResolvePeriodAsync();
        var tolerance = CobolNumeric.Store(await b.GetParmNumAsync("XMD.RECON.TOLERANCE", 0.01m), 9, 2);
        var tolText = b.GetArg("TOL");
        if (!CobolText.IsSpaces(tolText)) tolerance = CobolNumeric.Store(CobolNumeric.NumVal(tolText), 9, 2);
        var sAsat = BatchSuite.FormatDate(b.AsAt);
        var dateEd = Disp.D8(b.AsAt);
        var sMode = CobolText.Fit(b.Mode, 10);
        var sTol = CobolText.Fit(Disp.Mon(tolerance), 12);
        string sPeriod;
        if (b.FiscalYear == 0)
        {
            sPeriod = "UNDATED";
            b.Rc = 4;
            b.Display("STERP-W-0101 no accounting period covers " + dateEd);
        }
        else
        {
            sPeriod = CobolText.Fit(Disp.Unsigned(b.FiscalYear, 9).Substring(5, 4) + "/" + Disp.Unsigned(b.PeriodNo, 2) + " " +
                                    CobolText.Fit(b.PeriodStatus, 10)[..4], 12);
        }
        b.Display("STERP-I-0100 RECONCTL cutoff " + dateEd + " company " + CobolText.Trim(b.Co) + " mode " + CobolText.Trim(b.Mode));

        result["cutoff"] = CobolDate.FromYyyymmdd(b.AsAt) is { } cut ? cut.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
        result["period"] = sPeriod.TrimEnd(' ');
        result["mode"] = sMode.TrimEnd(' ');
        result["tolerance"] = CobolNumeric.WithScale(tolerance, 2);
        var families = new JsonArray();
        result["families"] = families;
        if (b.Rc > 4) return Finish(b, result, "", 0, 0, 0);

        // 9100-JOB-START is the framework's self-registration; 9200-OPEN-SPOOL
        if (!await b.OpenSpoolAsync("RECONCTL", "Control account reconciliation", "Subsidiary ledgers against the general ledger") || b.Rc > 4)
        {
            return Finish(b, result, "", 0, 0, 0);
        }
        decimal tSub = 0, tGl = 0, tDiff = 0;
        var statusText = "";
        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Control totals").Num(62, tSub, "Z,ZZZ,ZZZ,ZZ9.99-").Num(81, tGl, "Z,ZZZ,ZZZ,ZZ9.99-")
                .Num(100, tDiff, "Z,ZZZ,ZZZ,ZZ9.99-").Put(120, statusText, 12).Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Unreconciled control families  ").Num(34, b.Diffs, "ZZ9")
                .Lit(40, "Exceptions raised or listed ").Num(68, b.Raised, "ZZ9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Each difference stays explicit and owned until it")
                .Lit(67, "is corrected at source or adjusted with a reviewer.").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Event cutoff (as at date) ").Put(28, sAsat, 10).Lit(42, "Accounting period ")
                .Put(60, sPeriod, 12).Lit(76, "Mode  ").Put(82, sMode, 10).Lit(96, "Tolerance  ").Put(107, sTol, 12).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Basis: POSTED journals only, sterling carrying")
                .Lit(48, "values, same cutoff on both sides.").Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Control").Lit(12, "Description").Lit(45, "Control accounts")
                .Lit(68, "Subsidiary  ").Lit(88, "GL balance ").Lit(106, "Difference ").Lit(120, "Status").Text),
            RwLine.Plus1(() => new string('-', 132)));

        // 1000-LOAD-GL
        var gl = Families.Select(_ => new Gl()).ToArray();
        try
        {
            var rows = await b.Sql.FetchAllAsync(
                "SELECT a.ControlType, CONVERT(varchar(24),SUM(l.DebitAmountGBP - l.CreditAmountGBP)), " +
                "CONVERT(varchar(12),COUNT(DISTINCT a.AccountCode)), MIN(a.AccountCode) + CASE WHEN COUNT(DISTINCT a.AccountCode) > 1 " +
                "THEN '..' ELSE '' END FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = l.JournalId JOIN fin.Account a ON " +
                "a.CompanyCode = l.CompanyCode AND a.AccountCode = l.AccountCode WHERE l.CompanyCode = ? AND j.Status = 'POSTED' AND " +
                "CONVERT(char(8),j.PostingDate,112) <= ? AND a.ControlType IS NOT NULL AND a.ControlType <> '' GROUP BY a.ControlType",
                [SqlioParm.Text(b.Co), SqlioParm.Text(dateEd)], Widths, b.Ct);
            foreach (var row in rows)
            {
                // 1100-STORE-GL
                for (var j = 0; j < Families.Length; j++)
                {
                    if (!CobolText.EqualsPadded(Families[j].Code, row.Raw(0))) continue;
                    gl[j].Bal = CobolNumeric.Store(row.NumVal(1), 13, 2);
                    gl[j].Cnt = CobolNumeric.Store(row.NumVal(2), 4, 0);
                    gl[j].Accts = CobolText.Fit(row.Raw(3), 40);
                }
            }
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
        }

        // 2000-COMPARE
        for (var i = 0; i < Families.Length; i++)
        {
            var f = Families[i];
            var dAccts = CobolText.Fit(gl[i].Accts, 16);
            var dGl = CobolNumeric.Store(gl[i].Bal * f.Sign, 13, 2);
            var dNote = "";
            var dStatus = "";
            var dSub = await SubledgerAsync(b, f.Code, dateEd, n => dNote = n, s => dStatus = s);
            var dDiff = CobolNumeric.Store(dSub - dGl, 13, 2);
            tSub = CobolNumeric.Store(tSub + dSub, 13, 2);
            tGl = CobolNumeric.Store(tGl + dGl, 13, 2);
            tDiff = CobolNumeric.Store(tDiff + dDiff, 13, 2);
            if (dDiff > tolerance || dDiff < 0 - tolerance)
            {
                dStatus = "DIFFERENCE";
                b.Diffs++;
            }
            else
            {
                dStatus = "AGREED";
            }
            if (gl[i].Cnt == 0)
            {
                dStatus = "NO CONTROL";
                dNote = "No account of this control type is posted ";
            }
            b.RowsOut++;
            rw.Generate(RwLine.Plus1(() => new RwText().Put(1, f.Code, 10).Put(12, f.Desc, 32).Put(45, dAccts, 16)
                .Num(62, dSub, "Z,ZZZ,ZZZ,ZZ9.99-").Num(81, dGl, "Z,ZZZ,ZZZ,ZZ9.99-").Num(100, dDiff, "Z,ZZZ,ZZZ,ZZ9.99-")
                .Put(120, dStatus, 12).Text));
            if (dStatus != "AGREED")
            {
                dNote = await RaiseAsync(b, f, dDiff, dateEd);
                rw.Generate(RwLine.Plus1(() => new RwText().Put(14, dNote, 96).Lit(112, "Owner").Put(118, f.Owner, 14).Text));
            }
            await WriteRunAsync(b, f, dateEd, dAccts, dGl, dSub, dDiff, dStatus, dNote);
            families.Add(new JsonObject
            {
                ["code"] = f.Code,
                ["description"] = f.Desc,
                ["accounts"] = dAccts.TrimEnd(' '),
                ["subsidiaryGbp"] = CobolNumeric.WithScale(dSub, 2),
                ["ledgerGbp"] = CobolNumeric.WithScale(dGl, 2),
                ["differenceGbp"] = CobolNumeric.WithScale(dDiff, 2),
                ["status"] = dStatus,
                ["ownerRole"] = f.Owner,
                ["note"] = CobolText.Fit(dNote, 96).TrimEnd(' '),
            });
        }

        // 3000-PROVE
        if (b.Diffs == 0)
        {
            statusText = "RECONCILED";
        }
        else
        {
            statusText = "UNRECONCILED";
            if (b.Rc < 4) b.Rc = 4;
        }
        b.Display("STERP-I-3001 " + Disp.Signed(b.RowsOut, 9) + " control families, " + Disp.Signed(b.Diffs, 9) + " with a difference, " +
                  Disp.Signed(b.Raised, 9) + " exception(s)");
        if (b.IsCommit) b.Display("STERP-I-3002 " + Disp.Signed(b.Posted, 9) + " reconciliation row(s) written");

        // TERMINATE: CF FINAL
        rw.Terminate();
        b.CloseSpool();
        return Finish(b, result, statusText, tSub, tGl, tDiff);
    }

    private static int Finish(XmdBatch b, JsonObject result, string statusText, decimal tSub, decimal tGl, decimal tDiff)
    {
        result["totalSubsidiaryGbp"] = CobolNumeric.WithScale(tSub, 2);
        result["totalLedgerGbp"] = CobolNumeric.WithScale(tGl, 2);
        result["netDifferenceGbp"] = CobolNumeric.WithScale(tDiff, 2);
        result["unreconciledCount"] = b.Diffs;
        result["exceptionCount"] = b.Raised;
        result["runStatus"] = statusText.Length > 0 ? statusText : null;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>2100-SUBLEDGER: the subsidiary total of one family (SQL error: 0, the 'unavailable' note and RC 4).</summary>
    private static async Task<decimal> SubledgerAsync(XmdBatch b, string code, string dateEd, Action<string> note, Action<string> status)
    {
        var co = SqlioParm.Text(b.Co);
        var dt = SqlioParm.Text(dateEd);
        string statement;
        SqlioParm[] parms;
        switch (code)
        {
            case "AR":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(t.Resid),0)) FROM (SELECT o.OriginalAmountGBP - ISNULL((SELECT SUM(a.AppliedAmountGBP + " +
                    "a.DiscountAmountGBP + a.WriteOffAmountGBP) FROM ar.Allocation a WHERE a.TargetOpenItemId = o.OpenItemId AND a.Status = " +
                    "'ACTIVE' AND a.EffectiveDate <= ?),0) + ISNULL((SELECT SUM(b.AppliedAmountGBP) FROM ar.Allocation b WHERE " +
                    "b.SourceOpenItemId = o.OpenItemId AND b.Status = 'ACTIVE' AND b.EffectiveDate <= ?),0) AS Resid FROM ar.OpenItem o " +
                    "WHERE o.CompanyCode = ? AND CONVERT(char(8),o.PostingDate,112) <= ? AND o.SettlementStatus <> 'REVERSED') t";
                parms = [dt, dt, co, dt];
                break;
            case "AP":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(o.OriginalAmountGBP),0)) FROM ap.OpenItem o WHERE o.CompanyCode = ? AND " +
                    "CONVERT(char(8),o.PostingDate,112) <= ? AND o.SettlementStatus <> 'REVERSED'";
                parms = [co, dt];
                break;
            case "INVENTORY":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(ic.CarryingValueGBP - ISNULL(ic.AllowanceGBP,0)),0)) FROM cst.ItemCost ic " +
                    "WHERE ic.CompanyCode = ? AND ? >= '00000000'";
                parms = [co, dt];
                break;
            case "WIP":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(k.WipValueGBP),0)) FROM kit.ExecutionKit k WHERE k.CompanyCode = ? AND " +
                    "k.Status NOT IN ('DRAFT','FINCLOSED','CANCELLED') AND ? >= '00000000'";
                parms = [co, dt];
                break;
            case "GRNI":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL((SELECT SUM(r.GrniValueGBP) FROM po.Receipt r WHERE r.CompanyCode = ? AND r.Status = " +
                    "'CONFIRMED' AND CONVERT(char(8),r.PostingDate,112) <= ?),0) - ISNULL((SELECT SUM(m.GrniClearedGBP) FROM ap.InvoiceMatch m " +
                    "JOIN ap.PurchaseInvoice i ON i.PurchaseInvoiceId = m.PurchaseInvoiceId WHERE i.CompanyCode = ? AND m.Status <> 'REVERSED' " +
                    "AND CONVERT(char(8),i.PostingDate,112) <= ?),0))";
                parms = [co, dt, co, dt];
                break;
            case "VAT":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(CASE WHEN v.Direction = 'OUTPUT' THEN v.VatAmountGBP ELSE " +
                    "-ISNULL(v.RecoverableVatGBP,v.VatAmountGBP) END),0)) FROM fin.VatTransaction v WHERE v.CompanyCode = ? AND " +
                    "CONVERT(char(8),v.PostingDate,112) <= ?";
                parms = [co, dt];
                break;
            case "BANK":
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(v.Bal),0)) FROM (SELECT (SELECT TOP 1 r.ClosingStatementBalance - " +
                    "ISNULL(r.OutstandingPayments,0) + ISNULL(r.OutstandingReceipts,0) FROM fin.BankRecon r WHERE r.CompanyCode = " +
                    "b.CompanyCode AND r.BankAccountCode = b.BankAccountCode AND CONVERT(char(8),r.ToDate,112) <= ? ORDER BY r.ToDate DESC) " +
                    "AS Bal FROM fin.BankAccount b WHERE b.CompanyCode = ?) v";
                parms = [dt, co];
                break;
            default:
                statement =
                    "SELECT CONVERT(varchar(24),ISNULL(SUM(ISNULL(s.AcceptedValueGBP,0) - ISNULL(s.MatchedValueGBP,0)),0)) FROM " +
                    "po.ServiceAcceptance s WHERE s.CompanyCode = ? AND ? >= '00000000'";
                parms = [co, dt];
                break;
        }
        try
        {
            var row = await b.Sql.SingleAsync(statement, parms, Widths[..1], b.Ct);
            return row.Found ? CobolNumeric.Store(row.NumVal(0), 13, 2) : 0m;
        }
        catch (SqlioException)
        {
            note("Subsidiary total unavailable - input ");
            status("INCOMPLETE");
            b.Rc = 4;
            return 0m;
        }
    }

    /// <summary>2300-RAISE: the owned WARNING exception of a non-agreed family; returns D-NOTE (the message, 96 characters).</summary>
    private static async Task<string> RaiseAsync(XmdBatch b, Family f, decimal dDiff, string dateEd)
    {
        var docRef = CobolText.Fit(CobolText.Trim(b.Co) + "/" + CobolText.Trim(f.Code) + "/" + dateEd, 200)[..40];
        var text2 = CobolText.Fit(CobolText.Trim(f.Desc) + " differs from the general ledger by GBP " + Disp.Mon(dDiff) +
                                  " at the cutoff. Correct at source or record an approved adjustment with a reviewer.", 200);
        var ex = new ExcpraisArea
        {
            Severity = "WARNING",
            Category = "RECONCILIATION DIFFERENCE",
            DocumentType = "CONTROL",
            DocumentRef = docRef.TrimEnd(' '),
            MessageText = text2.TrimEnd(' '),
            OwnerRole = f.Owner,
            Priority = 2,
            AmountGbp = dDiff,
            DueAt = b.AsAt,
        };
        await b.RaiseAsync(ex);
        return text2[..96];
    }

    /// <summary>2400-WRITE-RUN (COMMIT only): DELETE the OPEN rows of the cutoff, then INSERT the family row (failure: RC 4, STERP-W-2401).</summary>
    private static async Task WriteRunAsync(XmdBatch b, Family f, string dateEd, string dAccts, decimal dGl, decimal dSub, decimal dDiff,
        string dStatus, string dNote)
    {
        if (!b.IsCommit) return;
        try
        {
            await b.Sql.ExecAsync(
                "DELETE FROM rpt.ReconciliationRun WHERE CompanyCode = ? AND ControlType = ? AND AsOfDate = CONVERT(date,?,112) AND Status = 'OPEN'",
                [SqlioParm.Text(b.Co), SqlioParm.Text(f.Code), SqlioParm.Text(dateEd)], b.Ct);
        }
        catch (SqlioException)
        {
            // EXEC result not tested
        }
        try
        {
            await b.Sql.ExecAsync(
                "INSERT INTO rpt.ReconciliationRun (CompanyCode, ControlType, AsOfDate, FiscalYear, PeriodNo, AccountCode, ControlBalanceGBP, " +
                "DetailBalanceGBP, DifferenceGBP, ContributingRefs, OwnerUser, Status, Note, RunBy) VALUES (?,?,CONVERT(date,?,112),?,?,?,?,?,?,?,?,?,?,?)",
                [
                    SqlioParm.Text(b.Co), SqlioParm.Text(f.Code), SqlioParm.Text(dateEd),
                    SqlioParm.Text(Disp.Unsigned(b.FiscalYear, 9)), SqlioParm.Text(Disp.Unsigned(b.PeriodNo, 9)),
                    SqlioParm.Text(dAccts), SqlioParm.Text(Disp.Mon(dGl)), SqlioParm.Text(Disp.Mon(dSub)), SqlioParm.Text(Disp.Mon(dDiff)),
                    SqlioParm.Text(f.Desc), SqlioParm.Text(f.Owner), SqlioParm.Text(dStatus == "AGREED" ? "ACCEPTED" : "OPEN"),
                    SqlioParm.Text(CobolText.Fit(dNote, 96)), SqlioParm.Text(b.User),
                ], b.Ct);
            b.Posted++;
        }
        catch (SqlioException ex)
        {
            b.Rc = 4;
            b.Display("STERP-W-2401 " + CobolText.Fit(ex.DriverText, 90));
        }
    }
}
