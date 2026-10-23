using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Ap.Engine;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// APVAT6M - six-month unpaid input VAT repayment and reclaim (cobol/batch/ap/APVAT6M.cob; FR-STERP-AP-045, -052; spec 4.10,
/// 4.14). Pass 1 (unpaid, latest evidence VALID/RELEASED/RECLAIMED/none) applies AP6MDATE REPAY; pass 2 (REPAID evidence on a
/// settled or part-paid invoice) applies AP6MDATE RECLAIM; only MODE=COMMIT marks the evidence and posts through APPOST (the
/// evidence write and each posting commit on their own; a repeated correlation is APP0007 and counts as posted). In COMMIT the
/// evidence write leaves SQLIO-COL-CNT 0, so every later fetch of the same pass re-processes the last row moved (HD-11,
/// oracle-verified: duplicate evidence rows, replayed postings, repeated listing lines). RD R-APVAT6M
/// with CH-SUPP; RC 12 spool or pass-1 failure, 4 evidence or posting failure, else 0; JOBLOG END re-initialised (SME-009).
/// </summary>
internal static class ApVat6mJob
{
    public static JobDefinition Definition => new()
    {
        JobCode = "APVAT6M",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitApvat6mRun",
        ParameterOrder = ["CO", "ASAT", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = ApBatchArgs.From(pre.Args);
            return ApJobSubmission.JoblogStart("APVAT6M", a.Co, a.User, pre.Args.Text);
        },
        EndValues = ApJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private static readonly int[] Widths = Enumerable.Repeat(100, 24).ToArray();

    /// <summary>WS-DET: the invoice being processed and its listing line.</summary>
    private sealed class Det
    {
        public string Supplier = "";
        public string SuppName = "";
        public string Register = "";
        public string SuppRef = "";
        public string TaxPoint = "";
        public string Due = "";
        public string Trigger = "";
        public int Days;
        public decimal Gross;
        public decimal Residual;
        public decimal Claimed;
        public decimal Adjust;
        public string Action = "";
        public string Period = "";
        public long InvId;
        public long EvidId;
        public decimal Already;
    }

    private sealed class State
    {
        public int Rc;
        public string Co = "";
        public string User = "";
        public string Mode = "";
        public int AsAt;
        public string AsAtText = "";
        public string Pass = "";
        public bool SqlFailed;
        public decimal TRepay;
        public decimal TReclaim;
        public long TRepayCnt;
        public long TReclaimCnt;
        public long TScanned;
        public long TPosted;
        public long Written;
        public readonly SixMonthArea Sm = new();
        public readonly JsonArray Lines = [];

        /// <summary>
        /// SQLIO-COL-CNT of the program's one SQLIO-CA: FTCH copies that many columns into AP-ROW (SQLIO.cob 3000-FETCH), and
        /// 1500-MARK-EVIDENCE / 2400-MARK-RECLAIMED leave it 0, so later fetches of the same pass keep the last row moved.
        /// </summary>
        public int ColCnt;

        /// <summary>AP-ROW: the last row a fetch copied.</summary>
        public SqlioRow? Row;
    }

    /// <summary>1200-FETCH-UNPAID / 2200-FETCH-PAID: the fetched row reaches AP-ROW only while SQLIO-COL-CNT is not 0.</summary>
    private static SqlioRow Fetched(State s, SqlioRow row)
    {
        if (s.ColCnt > 0 || s.Row is null) s.Row = row;
        return s.Row;
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;
        var s = new State();

        // 0100-READ-ARGS
        var a = ApBatchArgs.From(ctx.Args);
        s.Co = a.Co;
        s.User = a.User;
        if (!CobolText.IsSpaces(a.AsAtText)) s.AsAt = (int)CobolNumeric.Store(CobolNumeric.NumVal(a.AsAtText), 8, 0, signed: false);
        s.Mode = a.Mode == "COMMIT" ? "COMMIT" : "PREVIEW";
        var today = CobolDate.ToYyyymmdd(DateTime.Now);
        if (s.AsAt == 0) s.AsAt = today;
        s.AsAtText = Nine(s.AsAt / 10000, 4) + "-" + Nine(s.AsAt / 100, 2) + "-" + Nine(s.AsAt, 2);
        ctx.Display("STERP-I-0100 APVAT6M six month input VAT " + s.Mode.Trim() + " for " + s.Co.Trim());

        // 0200-JOB-START is the program's self-registration made by the job service.

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        SpoolWriter? spool = null;
        var spoolName = "";
        var heading = new ReportHeading
        {
            CompanyCode = s.Co,
            ReportId = "APVAT6M",
            Title = "Six month unpaid input VAT",
            User = s.User,
            RunAt = DateTime.Now,
            AsAtText = "As at " + ApBatchArgs.Ddmmyyyy(s.AsAt),
            Subtitle = s.Mode == "COMMIT" ? "Repayment and reclaim committed to the return" : "Preview - nothing is repaid or reclaimed",
        };
        try
        {
            spool = ctx.OpenSpool("APVAT6M", s.Co);
            spoolName = spool.Path;
        }
        catch (LegacyOutcomeException)
        {
            s.Rc = 12;
            ctx.Display("STERP-E-0301 cannot build the spool path");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s.Rc = 12;
            ctx.Display("STERP-E-0302 cannot open the spool file");
        }

        if (s.Rc < 8 && spool is not null)
        {
            await ApBatchArgs.FillCompanyAsync(sql, heading, s.Co, ct);
            var listing = new Vat6mListing(spool, heading, s);
            var post = new ApPost(ctx.Get<IGlPostingGateway>());
            await RepayPassAsync(ctx, sql, post, s, listing, ct);
            await ReclaimPassAsync(ctx, sql, post, s, listing, ct);
            // 0400-CLOSE-SPOOL
            listing.Terminate();
            spool.Dispose();
            ctx.Result = new JsonObject
            {
                ["lines"] = s.Lines,
                ["scanned"] = s.TScanned,
                ["repayments"] = s.TRepayCnt,
                ["repaidGbp"] = CobolNumeric.WithScale(s.TRepay, 2),
                ["reclaims"] = s.TReclaimCnt,
                ["reclaimedGbp"] = CobolNumeric.WithScale(s.TReclaim, 2),
                ["posted"] = s.TPosted,
            };
        }

        // 0500-JOB-END (the JOBLOG END is refused: run id 0)
        ctx.RowsProcessed = (int)s.Written;
        ctx.Display("STERP-I-0500 APVAT6M " + CobolEdit.Field(s.TRepayCnt, "Z(8)9") + " repayments, " + SignedDisplay(s.TReclaimCnt, 7) + " reclaims");
        ctx.Display("STERP-I-SPOOL " + spoolName.Trim());
        return s.Rc;
    }

    // 1000-REPAY-PASS
    private static async Task RepayPassAsync(JobRunContext ctx, ISqlRunner sql, ApPost post, State s, Vat6mListing listing, CancellationToken ct)
    {
        s.Pass = "REPAY";
        IReadOnlyList<SqlioRow> rows;
        try
        {
            // 1100-OPEN-UNPAID
            s.ColCnt = 12;
            rows = await sql.FetchAllAsync(
                "SELECT i.SupplierCode, s.LegalName, i.RegisterNumber, i.SupplierDocRef, CONVERT(varchar(8), i.TaxPointDate, 112), " +
                "CONVERT(varchar(8), ISNULL(i.DueDate, i.TaxPointDate), 112), CONVERT(varchar(30), i.GrossAmountGBP), " +
                "CONVERT(varchar(30), ISNULL(v.AllowedVatGBP, i.RecoverableVatGBP)), CONVERT(varchar(30), ISNULL(o.ResidualAmountGBP, " +
                "i.GrossAmountGBP)), CONVERT(varchar(20), i.PurchaseInvoiceId), CONVERT(varchar(20), ISNULL(v.EvidenceId, 0)), " +
                "ISNULL(v.Outcome, 'VALID') FROM ap.PurchaseInvoice i JOIN ap.Supplier s ON s.CompanyCode = i.CompanyCode AND " +
                "s.SupplierCode = i.SupplierCode LEFT JOIN ap.OpenItem o ON o.CompanyCode = i.CompanyCode AND o.PurchaseInvoiceId = " +
                "i.PurchaseInvoiceId AND o.ItemType = 'INVOICE' OUTER APPLY (SELECT TOP 1 e.EvidenceId, e.Outcome, e.AllowedVatGBP FROM " +
                "ap.VatEvidence e WHERE e.CompanyCode = i.CompanyCode AND e.PurchaseInvoiceId = i.PurchaseInvoiceId ORDER BY " +
                "e.EvidenceId DESC) v WHERE i.CompanyCode = ? AND i.DocumentType = 'INVOICE' AND i.AccountingState IN " +
                "('POSTED','PARTREVERSED') AND i.VatEvidenceValid = 1 AND i.RecoverableVatGBP > 0 AND i.SettlementState IN " +
                "('UNPAID','PARTPAID') AND ISNULL(v.Outcome,'VALID') IN ('VALID','RELEASED','RECLAIMED') AND i.TaxPointDate <= ? " +
                "ORDER BY i.SupplierCode, i.RegisterNumber",
                [SqlioParm.Text(s.Co), SqlioParm.Text(s.AsAtText)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            s.SqlFailed = true;
            ctx.Display("STERP-E-1101 " + CobolText.Fit(ex.DriverText, 512));
            s.Rc = 12;
            return;
        }
        foreach (var row in rows)
        {
            s.TScanned++;
            var d = MoveRow(Fetched(s, row), s.Sm);
            await ApplyRuleAsync(ctx, sql, post, s, d, listing, ct);
        }
    }

    // 1250-MOVE-ROW
    private static Det MoveRow(SqlioRow row, SixMonthArea sm)
    {
        var d = new Det
        {
            Supplier = CobolText.Trimmed(row.Raw(0), 20),
            SuppName = CobolText.Trimmed(row.Raw(1), 34),
            Register = CobolText.Trimmed(row.Raw(2), 20),
            SuppRef = CobolText.Trimmed(row.Raw(3), 20),
        };
        var taxPoint = (int)CobolNumeric.Store(row.NumVal(4), 8, 0, signed: false);
        sm.SupplyDate = taxPoint;
        d.TaxPoint = Ddmmyyyy(taxPoint);
        var due = (int)CobolNumeric.Store(row.NumVal(5), 8, 0, signed: false);
        sm.DueDate = due;
        d.Due = Ddmmyyyy(due);
        d.Gross = CobolNumeric.Store(row.NumVal(6), 13, 2);
        d.Claimed = CobolNumeric.Store(row.NumVal(7), 13, 2);
        d.Residual = CobolNumeric.Store(row.NumVal(8), 13, 2);
        d.InvId = (long)CobolNumeric.Store(row.NumVal(9), 9, 0);
        d.EvidId = (long)CobolNumeric.Store(row.NumVal(10), 9, 0);
        return d;
    }

    // 1300-APPLY-RULE
    private static async Task ApplyRuleAsync(JobRunContext ctx, ISqlRunner sql, ApPost post, State s, Det d, Vat6mListing listing, CancellationToken ct)
    {
        var sm = s.Sm;
        sm.Func = "REPAY";
        sm.Months = 6;
        sm.AsAt = s.AsAt;
        sm.ClaimedVat = d.Claimed;
        sm.Gross = d.Gross;
        sm.Residual = d.Residual;
        sm.AlreadyRepaid = 0;
        sm.PaidDate = 0;
        Ap6mDate.Call(sm);
        d.Trigger = Ddmmyyyy(sm.TriggerDate);
        d.Days = sm.DaysOver;
        d.Period = sm.PeriodKey;
        if (sm.DueForRepay != "Y") return;
        d.Adjust = sm.RepayVat;
        d.Action = "REPAY";
        s.TRepayCnt++;
        s.TRepay = CobolNumeric.Store(s.TRepay + d.Adjust, 15, 2);
        if (s.Mode == "COMMIT")
        {
            // 1400-COMMIT-REPAY
            await MarkEvidenceAsync(ctx, sql, s, d, ct);
            await PostAdjustmentAsync(ctx, post, s, d, ct);
        }
        s.Written++;
        listing.Generate(d);
        s.Lines.Add(Line(d));
    }

    // 1500-MARK-EVIDENCE: the evidence row carries the audit trail; a missing row is created
    private static async Task MarkEvidenceAsync(JobRunContext ctx, ISqlRunner sql, State s, Det d, CancellationToken ct)
    {
        var sm = s.Sm;
        s.ColCnt = 0;
        try
        {
            if (d.EvidId > 0)
            {
                await sql.ExecAsync(
                    "UPDATE ap.VatEvidence SET Outcome = 'REPAID', RepaidVatPeriodKey = ?, RepaidAt = ?, FailureReason = ? WHERE EvidenceId = ?",
                    [
                        SqlioParm.Text(CobolText.Trimmed(sm.PeriodKey, 10)), SqlioParm.Text(s.AsAtText),
                        SqlioParm.Text("Unpaid six months after " + d.Trigger.Trim()), SqlioParm.Text(IntEdit(d.EvidId)),
                    ], ct);
            }
            else
            {
                var claimed = CobolEdit.Field(d.Claimed, "-(11)9.99").Trim();
                await sql.ExecAsync(
                    "INSERT INTO ap.VatEvidence (CompanyCode, PurchaseInvoiceId, RegisterNumber, SupplierCode, EvidenceType, ClaimedVatGBP, " +
                    "AllowedVatGBP, Outcome, RepaidVatPeriodKey, RepaidAt, RecordedBy) VALUES (?, ?, ?, ?, 'VATINVOICE', ?, ?, 'REPAID', ?, ?, ?)",
                    [
                        SqlioParm.Text(s.Co), SqlioParm.Text(IntEdit(d.InvId)), SqlioParm.Text(d.Register), SqlioParm.Text(d.Supplier),
                        SqlioParm.Text(claimed), SqlioParm.Text(claimed), SqlioParm.Text(CobolText.Trimmed(sm.PeriodKey, 10)),
                        SqlioParm.Text(s.AsAtText), SqlioParm.Text(s.User),
                    ], ct);
            }
        }
        catch (SqlioException ex)
        {
            s.Rc = 4;
            ctx.Display("STERP-E-1501 " + CobolText.Fit(ex.DriverText, 512));
        }
    }

    // 1600-POST-ADJUSTMENT: the same correlation id posted twice returns the original journal
    private static async Task PostAdjustmentAsync(JobRunContext ctx, ApPost post, State s, Det d, CancellationToken ct)
    {
        var pj = new PostingArea
        {
            Func = "POST",
            Company = s.Co,
            User = s.User,
            BusinessDate = s.AsAt,
            PostingDate = s.AsAt,
            DocumentDate = s.AsAt,
            Currency = "GBP",
            FxRate = 1,
            JournalType = "JOURNAL",
            SubledgerType = "AP",
            SubledgerRef = d.Register,
            SourceRef = d.Register,
            LineCount = 2,
            SeriesCode = CobolText.Trimmed(s.Sm.PeriodKey, 20),
        };
        if (s.Pass == "REPAY")
        {
            pj.EventCode = "AP.VATREPAY";
            pj.Correlation = CobolText.Trimmed("AP6M-REPAY-" + s.Co.Trim() + "-" + d.Register.Trim(), 64);
            pj.Description = CobolText.Trimmed("Input VAT repaid, unpaid six months - " + d.Register.Trim(), 200);
            pj.Lines.Add(new PostingLine { Account = "2050", Debit = d.Adjust, Narrative = "VAT control, input tax repaid" });
            pj.Lines.Add(new PostingLine { Account = "1250", Credit = d.Adjust, Narrative = "VAT recoverable, claim reversed" });
        }
        else
        {
            pj.EventCode = "AP.VATRECLAIM";
            pj.Correlation = CobolText.Trimmed("AP6M-RECLAIM-" + s.Co.Trim() + "-" + d.Register.Trim(), 64);
            pj.Description = CobolText.Trimmed("Input VAT reclaimed once paid - " + d.Register.Trim(), 200);
            pj.Lines.Add(new PostingLine { Account = "1250", Debit = d.Adjust, Narrative = "VAT recoverable, claimed again" });
            pj.Lines.Add(new PostingLine { Account = "2050", Credit = d.Adjust, Narrative = "VAT control, return in progress" });
        }
        await post.CallAsync(pj, ct);
        if (pj.Rc < 8)
        {
            s.TPosted++;
        }
        else
        {
            s.Rc = 4;
            ctx.Display("STERP-E-1601 " + pj.Message.Trim());
        }
    }

    // 2000-RECLAIM-PASS: invoices repaid earlier that have since been settled
    private static async Task ReclaimPassAsync(JobRunContext ctx, ISqlRunner sql, ApPost post, State s, Vat6mListing listing, CancellationToken ct)
    {
        s.Pass = "RECLAIM";
        IReadOnlyList<SqlioRow>? rows = null;
        try
        {
            // 2100-OPEN-PAID
            s.ColCnt = 12;
            rows = await sql.FetchAllAsync(
                "SELECT i.SupplierCode, s.LegalName, i.RegisterNumber, i.SupplierDocRef, CONVERT(varchar(8), i.TaxPointDate, 112), " +
                "CONVERT(varchar(8), ISNULL(i.DueDate, i.TaxPointDate), 112), CONVERT(varchar(30), i.GrossAmountGBP), " +
                "CONVERT(varchar(30), v.AllowedVatGBP), CONVERT(varchar(30), ISNULL(o.ResidualAmountGBP, 0)), " +
                "CONVERT(varchar(20), i.PurchaseInvoiceId), CONVERT(varchar(20), v.EvidenceId), v.Outcome FROM ap.PurchaseInvoice i " +
                "JOIN ap.Supplier s ON s.CompanyCode = i.CompanyCode AND s.SupplierCode = i.SupplierCode JOIN ap.VatEvidence v ON " +
                "v.CompanyCode = i.CompanyCode AND v.PurchaseInvoiceId = i.PurchaseInvoiceId AND v.Outcome = 'REPAID' LEFT JOIN " +
                "ap.OpenItem o ON o.CompanyCode = i.CompanyCode AND o.PurchaseInvoiceId = i.PurchaseInvoiceId AND o.ItemType = 'INVOICE' " +
                "WHERE i.CompanyCode = ? AND i.SettlementState IN ('SETTLED','PARTPAID') AND ISNULL(o.ResidualAmountGBP, 0) < " +
                "i.GrossAmountGBP ORDER BY i.SupplierCode, i.RegisterNumber",
                [SqlioParm.Text(s.Co)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            s.SqlFailed = true;
            ctx.Display("STERP-E-2101 " + CobolText.Fit(ex.DriverText, 512));
        }
        if (s.SqlFailed || rows is null) return;
        foreach (var row in rows)
        {
            s.TScanned++;
            // 2200-FETCH-PAID
            var r = Fetched(s, row);
            var d = MoveRow(r, s.Sm);
            d.Already = CobolNumeric.Store(r.NumVal(7), 13, 2);
            await ReclaimOneAsync(ctx, sql, post, s, d, listing, ct);
        }
    }

    // 2300-RECLAIM-ONE
    private static async Task ReclaimOneAsync(JobRunContext ctx, ISqlRunner sql, ApPost post, State s, Det d, Vat6mListing listing, CancellationToken ct)
    {
        var sm = s.Sm;
        sm.Func = "RECLAIM";
        sm.Months = 6;
        sm.AsAt = s.AsAt;
        sm.PaidDate = s.AsAt;
        sm.ClaimedVat = d.Claimed;
        sm.Gross = d.Gross;
        sm.Residual = d.Residual;
        sm.AlreadyRepaid = d.Already;
        Ap6mDate.Call(sm);
        if (sm.ReclaimVat <= 0) return;
        d.Trigger = "";
        d.Days = 0;
        d.Adjust = CobolNumeric.Store(0 - sm.ReclaimVat, 13, 2);
        d.Action = "RECLAIM";
        d.Period = sm.PeriodKey;
        s.TReclaimCnt++;
        s.TReclaim = CobolNumeric.Store(s.TReclaim + sm.ReclaimVat, 15, 2);
        if (s.Mode == "COMMIT")
        {
            d.Adjust = sm.ReclaimVat;
            await MarkReclaimedAsync(ctx, sql, s, d, ct);
            await PostAdjustmentAsync(ctx, post, s, d, ct);
            d.Adjust = CobolNumeric.Store(0 - sm.ReclaimVat, 13, 2);
        }
        s.Written++;
        listing.Generate(d);
        s.Lines.Add(Line(d));
    }

    // 2400-MARK-RECLAIMED
    private static async Task MarkReclaimedAsync(JobRunContext ctx, ISqlRunner sql, State s, Det d, CancellationToken ct)
    {
        s.ColCnt = 0;
        try
        {
            await sql.ExecAsync(
                "UPDATE ap.VatEvidence SET Outcome = 'RECLAIMED', ReclaimedVatPeriodKey = ?, ReclaimedAt = ? WHERE EvidenceId = ? AND Outcome = 'REPAID'",
                [SqlioParm.Text(CobolText.Trimmed(s.Sm.PeriodKey, 10)), SqlioParm.Text(s.AsAtText), SqlioParm.Text(IntEdit(d.EvidId))], ct);
        }
        catch (SqlioException ex)
        {
            s.Rc = 4;
            ctx.Display("STERP-E-2401 " + CobolText.Fit(ex.DriverText, 512));
        }
    }

    private static JsonObject Line(Det d) => new()
    {
        ["supplierCode"] = d.Supplier,
        ["registerNumber"] = d.Register,
        ["action"] = d.Action,
        ["adjustmentGbp"] = CobolNumeric.WithScale(d.Adjust, 2),
        ["periodKey"] = d.Period,
    };

    /// <summary>"dd/mm/yyyy" by position of a yyyymmdd number (STRING WS-DATE-DD "/" WS-DATE-MM "/" WS-DATE-YYYY).</summary>
    private static string Ddmmyyyy(int yyyymmdd) => ApBatchArgs.Ddmmyyyy(yyyymmdd);

    /// <summary>WS-INT-ED -(9)9 trimmed.</summary>
    private static string IntEdit(long value) => CobolEdit.Field(value, "-(9)9").Trim();

    private static string Nine(long value, int digits) =>
        (Math.Abs(value) % (long)Math.Pow(10, digits)).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>DISPLAY of a PIC S9(n) DISPLAY field as the GnuCOBOL runtime shows it: n digits followed by the sign.</summary>
    private static string SignedDisplay(long value, int digits) => Nine(value, digits) + (value < 0 ? "-" : "+");

    /// <summary>
    /// R-APVAT6M (APVAT6M.cob:93-166: CONTROLS FINAL D-SUPPLIER, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 54,
    /// FOOTING 56): CH-SUPP, DE-LINE, CF-SUPP (SUM D-ADJUST) and CF-FINAL (SUM D-ADJUST and the run totals).
    /// </summary>
    private sealed class Vat6mListing
    {
        private readonly GnuReportWriter _rw;
        private readonly RwLine _detail;
        private bool _generated;

        // WS-DET as the report groups read it
        private string _dSupplier = "";
        private string _dSuppName = "";
        private string _dRegister = "";
        private string _dSuppRef = "";
        private string _dTaxPoint = "";
        private string _dDue = "";
        private string _dTrigger = "";
        private decimal _dDays, _dResidual, _dClaimed, _dAdjust;
        private string _dAction = "";

        // SUM counters of CF-SUPP and CF-FINAL
        private decimal _sumSupp;
        private decimal _sumFinal;

        public Vat6mListing(SpoolWriter spool, ReportHeading heading, State s)
        {
            var supplier = new RwControl(() => _dSupplier, v => _dSupplier = v, 20)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Supplier ").Src(11, _dSupplier, 20).Src(33, _dSuppName, 34).Text),
                    RwLine.Plus1(() => new RwText().Lit(3, "Register            ").Lit(24, "Supplier reference  ").Lit(45, "Tax point ")
                        .Lit(56, "Due date  ").Lit(67, "Six months").Lit(78, "Days ").Lit(84, "  Outstanding").Lit(98, "   Input VAT ")
                        .Lit(112, "  Adjustment ").Lit(126, "Action ").Text),
                    RwLine.Plus1(() => ApRw.Rule),
                ],
                Footing =
                [
                    RwLine.Plus1(() => new RwText().Lit(84, "Supplier VAT adjustment    ").Ed(112, _sumSupp, "Z(9)9.99-").Text),
                ],
            };
            RwLine[] final =
            [
                RwLine.PlusN(2, () => new RwText().Lit(84, "Net VAT adjustment         ").Ed(112, _sumFinal, "Z(9)9.99-").Text),
                RwLine.PlusN(2, () => new RwText().Lit(3, "SIX MONTH UNPAID INPUT VAT (AP-029)").Text),
                RwLine.Plus1(() => new RwText().Lit(5, "Invoices scanned                 ").Ed(40, s.TScanned, "Z(6)9").Text),
                RwLine.Plus1(() => new RwText().Lit(5, "Repayments identified            ").Ed(40, s.TRepayCnt, "Z(6)9").Ed(50, s.TRepay, "Z(11)9.99-").Text),
                RwLine.Plus1(() => new RwText().Lit(5, "Reclaims on invoices since paid  ").Ed(40, s.TReclaimCnt, "Z(6)9").Ed(50, s.TReclaim, "Z(11)9.99-").Text),
                RwLine.Plus1(() => new RwText().Lit(5, "Journals staged and posted       ").Ed(40, s.TPosted, "Z(6)9").Text),
                RwLine.PlusN(2, () => new RwText().Lit(3, "The adjustment belongs to the return covering").Lit(49, "the six month date. ").Text),
                RwLine.Plus1(() => new RwText().Lit(3, "PREVIEW identifies only; COMMIT repays and   ").Lit(49, "reclaims once per invoice.    ").Text),
            ];
            _detail = RwLine.Plus1(() => new RwText().Src(3, _dRegister, 20).Src(24, _dSuppRef, 20).Src(45, _dTaxPoint, 10).Src(56, _dDue, 10)
                .Src(67, _dTrigger, 10).Ed(78, _dDays, "Z(4)9-").Ed(84, _dResidual, "Z(9)9.99-").Ed(98, _dClaimed, "Z(9)9.99-")
                .Ed(112, _dAdjust, "Z(9)9.99-").Src(126, _dAction, 7).Text);
            _rw = ApRw.Writer(spool, heading, 60, 1, 9, 54, [supplier], final);
            // INITIATE R-APVAT6M
            _rw.Initiate();
        }

        /// <summary>GENERATE DE-LINE: a supplier break presents CF-SUPP with its sum, which is then zeroed.</summary>
        public void Generate(Det d)
        {
            var broke = _generated && CobolText.Fit(_dSupplier, 20) != CobolText.Fit(d.Supplier, 20);
            _dSupplier = d.Supplier;
            _dSuppName = d.SuppName;
            _dRegister = d.Register;
            _dSuppRef = d.SuppRef;
            _dTaxPoint = d.TaxPoint;
            _dDue = d.Due;
            _dTrigger = d.Trigger;
            _dDays = d.Days;
            _dResidual = d.Residual;
            _dClaimed = d.Claimed;
            _dAdjust = d.Adjust;
            _dAction = d.Action;
            _rw.Generate(_detail);
            if (broke) _sumSupp = 0;
            _sumSupp = CobolNumeric.Store(_sumSupp + d.Adjust, 13, 2);
            _sumFinal = CobolNumeric.Store(_sumFinal + d.Adjust, 13, 2);
            _generated = true;
        }

        /// <summary>TERMINATE R-APVAT6M (nothing printed when no GENERATE ran).</summary>
        public void Terminate() => _rw.Terminate();
    }
}
