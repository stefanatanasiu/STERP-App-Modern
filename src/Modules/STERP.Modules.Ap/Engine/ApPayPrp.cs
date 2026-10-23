using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>One PP-LINE entry of APPAYPRPCA.</summary>
public sealed record ProposalLine(int LineNum, string Supplier, int ItemId, string Document, string Type, int DueDate, string Currency,
    decimal Gross, decimal GrossGbp, decimal Discount, decimal Credit, decimal Reserved, decimal NetPay, int BankId, string Excluded, string Reason);

/// <summary>APPAYPRPCA body (cobol/copy/APPAYPRPCA.cpy).</summary>
public sealed class PaymentProposalArea
{
    public string ProposalRef { get; set; } = "";
    public int ProposalId { get; set; }
    public int Version { get; set; }
    public int CutoffDate { get; set; }
    public int PayDate { get; set; }
    public int PayDateRaw { get; set; }
    public int BacsInputDate { get; set; }
    public int BacsProcDate { get; set; }
    public string Method { get; set; } = "";
    public string BankAccount { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal SingleLimit { get; set; }
    public decimal ChapsThreshold { get; set; }
    public decimal CashPrincipal { get; set; }
    public decimal CreditsApplied { get; set; }
    public decimal DiscountsTaken { get; set; }
    public decimal TotalOutflow { get; set; }
    public decimal ReservedTotal { get; set; }
    public int SelectedCount { get; set; }
    public int ExcludedCount { get; set; }
    public int SupplierCount { get; set; }
    public string Status { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string IsProcDay { get; set; } = "";
    public string DayAdjusted { get; set; } = "";
    public string MethodChanged { get; set; } = "";
    public string OverLimit { get; set; } = "";
    public string PreparedBy { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<ProposalLine> Lines { get; } = [];
    public string Note { get; set; } = "";

    /// <summary>The ApPaymentProposal response object.</summary>
    public object ToData() => new
    {
        ProposalRef,
        ProposalId,
        Version,
        CutoffDate = InvoiceRegisterArea.IsoDate(CutoffDate),
        PayDate = InvoiceRegisterArea.IsoDate(PayDate),
        PayDateRaw = InvoiceRegisterArea.IsoDate(PayDateRaw),
        BacsInputDate = InvoiceRegisterArea.IsoDate(BacsInputDate),
        BacsProcDate = InvoiceRegisterArea.IsoDate(BacsProcDate),
        Method,
        BankAccount,
        Currency,
        ChapsThreshold = CobolNumeric.WithScale(ChapsThreshold, 2),
        CashPrincipal = CobolNumeric.WithScale(CashPrincipal, 2),
        CreditsApplied = CobolNumeric.WithScale(CreditsApplied, 2),
        DiscountsTaken = CobolNumeric.WithScale(DiscountsTaken, 2),
        TotalOutflow = CobolNumeric.WithScale(TotalOutflow, 2),
        ReservedTotal = CobolNumeric.WithScale(ReservedTotal, 2),
        SelectedCount,
        ExcludedCount,
        Status,
        ContentHash,
        IsProcessingDay = IsProcDay,
        DayAdjusted,
        MethodChanged,
        OverLimit,
        PreparedBy,
        Note,
        Lines = Lines.Select(l => new
        {
            l.LineNum,
            SupplierCode = l.Supplier,
            OpenItemId = l.ItemId,
            DocumentNumber = l.Document,
            ItemType = l.Type,
            DueDate = InvoiceRegisterArea.IsoDate(l.DueDate),
            CurrencyCode = l.Currency,
            Gross = CobolNumeric.WithScale(l.Gross, 2),
            GrossGbp = CobolNumeric.WithScale(l.GrossGbp, 2),
            Discount = CobolNumeric.WithScale(l.Discount, 2),
            CreditApplied = CobolNumeric.WithScale(l.Credit, 2),
            Reserved = CobolNumeric.WithScale(l.Reserved, 2),
            NetPay = CobolNumeric.WithScale(l.NetPay, 2),
            SupplierBankId = l.BankId,
            l.Excluded,
            l.Reason,
        }).ToList(),
    };
}

/// <summary>
/// APPAYPRP (cobol/online/ap/APPAYPRP.cob; FR-STERP-AP-032..034, spec 4.6): SELECT/RESERVE/RELEASE build and TOTALS. No unit of
/// work: the NUMGEN draw, the header INSERT/UPDATE, ap.usp_ReserveProposal (own transaction) and the hash UPDATE commit
/// separately. METHOD is deferred (no production route).
/// </summary>
public sealed class ApPayPrp(ISqlRunner sql, ApSvc svc)
{
    private bool _fatal;

    public async Task RunAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        await InitAsync(ca, pp, ct);
        if (_fatal) return;
        switch (ca.Function)
        {
            case "SELECT" or "RESERVE" or "RELEASE":
                await BuildAsync(ca, pp, ct);
                break;
            case "TOTALS":
                await TotalsAsync(ca, pp, ct);
                break;
            default:
                ca.ReasonCode = "APP1099";
                ca.SetMessage("APPAYPRP does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 1000-INIT
    private async Task InitAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        _fatal = false;
        ca.Program = "APPAYPRP";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-011";
        pp.Note = "";
        pp.DayAdjusted = "N";
        pp.MethodChanged = "N";
        pp.OverLimit = "N";
        pp.Lines.Clear();
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(pp.Method)) pp.Method = "BACS";
        if (CobolText.IsSpaces(pp.Currency)) pp.Currency = "GBP";
        if (pp.Version == 0) pp.Version = 1;
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APP1001";
            ca.SetMessage("A company is required");
            Reject(ca);
            return;
        }
        if (pp.PayDate == 0) pp.PayDate = ca.BusinessDate;
        if (pp.CutoffDate == 0) pp.CutoffDate = pp.PayDate;
        if (pp.ChapsThreshold == 0)
        {
            var a = await svc.ParmNumAsync(ca, "AP.CHAPS.THRESHOLD", 250000, ct);
            pp.ChapsThreshold = CobolNumeric.Store(a.NumOut, 13, 2);
        }
    }

    // 2000-METHOD
    private async Task MethodAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        pp.PayDateRaw = pp.PayDate;
        if (pp.Method == "BACS") await BacsDatesAsync(ca, pp, ct);
        else await SameDayAsync(ca, pp, ct);
        Limits(ca, pp);
    }

    // 2100-BACS-DATES
    private async Task BacsDatesAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        var busday = ApSvcArea.For(ca, "BUSDAY");
        busday.Date1 = pp.PayDate;
        busday.Text1 = "BACS";
        await svc.CallAsync(busday, ct);
        pp.IsProcDay = CobolText.Trimmed(busday.Flag1, 1);
        if (pp.IsProcDay != "Y")
        {
            var adjust = ApSvcArea.For(ca, "ADJUST");
            adjust.Date1 = pp.PayDate;
            adjust.Text1 = "BACS";
            adjust.Text2 = "NEXT";
            await svc.CallAsync(adjust, ct);
            if (adjust.Rc > 4 || adjust.DateOut == 0)
            {
                NoCalendar(ca);
                return;
            }
            pp.PayDate = adjust.DateOut;
            pp.DayAdjusted = "Y";
            pp.IsProcDay = "Y";
        }
        var proc = ApSvcArea.For(ca, "ADDBUS");
        proc.Date1 = pp.PayDate;
        proc.Int1 = -1;
        proc.Text1 = "BACS";
        await svc.CallAsync(proc, ct);
        if (proc.Rc > 4 || proc.DateOut == 0)
        {
            NoCalendar(ca);
            return;
        }
        pp.BacsProcDate = proc.DateOut;
        var input = ApSvcArea.For(ca, "ADDBUS");
        input.Date1 = pp.PayDate;
        input.Int1 = -2;
        input.Text1 = "BACS";
        await svc.CallAsync(input, ct);
        if (input.Rc > 4 || input.DateOut == 0)
        {
            NoCalendar(ca);
            return;
        }
        pp.BacsInputDate = input.DateOut;
        if (pp.DayAdjusted == "Y")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APP1010";
            ca.SetMessage("The entry date is not a Bacs processing day; it has been moved to " + D8(pp.PayDate) +
                          " under the approved next processing day rule");
            ca.MsgCount++;
        }
    }

    // 2190-NO-CALENDAR
    private void NoCalendar(CaHeader ca)
    {
        ca.ReturnCode = 8;
        ca.ReasonCode = "APP1014";
        ca.SetMessage("The Bacs processing calendar could not supply a working entry date for this payment run");
        ca.MsgCount++;
        _fatal = true;
    }

    // 2200-SAMEDAY
    private async Task SameDayAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        var busday = ApSvcArea.For(ca, "BUSDAY");
        busday.Date1 = pp.PayDate;
        busday.Text1 = "ENG";
        await svc.CallAsync(busday, ct);
        pp.IsProcDay = CobolText.Trimmed(busday.Flag1, 1);
        pp.BacsInputDate = pp.PayDate;
        pp.BacsProcDate = pp.PayDate;
        if (pp.IsProcDay != "Y")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APP1011";
            ca.SetMessage("The chosen payment date is not a working day for this payment method");
            ca.MsgCount++;
            _fatal = true;
        }
    }

    // 2300-LIMITS
    private void Limits(CaHeader ca, PaymentProposalArea pp)
    {
        var maxSingle = pp.SingleLimit == 0 ? 1000000m : pp.SingleLimit;
        if (pp.Method == "FPS" && pp.TotalOutflow > maxSingle)
        {
            pp.OverLimit = "Y";
            ca.ReturnCode = 8;
            ca.ReasonCode = "APP1012";
            ca.SetMessage("A Faster Payment above the bank limit of GBP " + MoneyEdit(maxSingle) + " cannot be submitted on that method");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        if (pp.Method != "CHAPS" && pp.TotalOutflow > pp.ChapsThreshold)
        {
            pp.MethodChanged = "Y";
            pp.Method = "CHAPS";
            ca.ReturnCode = 4;
            ca.ReasonCode = "APP1013";
            ca.SetMessage("The run exceeds GBP " + MoneyEdit(pp.ChapsThreshold) + " and has been routed to CHAPS");
            ca.MsgCount++;
        }
    }

    // 3000-BUILD
    private async Task BuildAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        var perm = await svc.PermAsync(ca, "AP.PAYMENT.PROPOSE", ct);
        if (perm.Flag1 != "Y")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APP1002";
            ca.SetMessage(perm.Message);
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await MethodAsync(ca, pp, ct);
        if (_fatal) return;
        if (pp.ProposalId == 0)
        {
            await CreateHeaderAsync(ca, pp, ct);
            if (_fatal) return;
        }
        else
        {
            await UpdateHeaderAsync(ca, pp, ct);
        }
        await ReserveAsync(ca, pp, ct);
        if (_fatal) return;
        await TotalsAsync(ca, pp, ct);
        Limits(ca, pp);
        if (_fatal) return;
        await HashAsync(ca, pp, ct);
        if (ca.ReturnCode < 4)
        {
            ca.SetMessage("Proposal " + pp.ProposalRef.Trim(' ') + " requires GBP " + MoneyEdit(pp.TotalOutflow) + " of cash");
        }
    }

    // 3100-CREATE-HEADER
    private async Task CreateHeaderAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        var num = ApSvcArea.For(ca, "NUMBER");
        num.Text1 = "APPP";
        await svc.CallAsync(num, ct);
        if (num.Rc > 4)
        {
            ca.ReturnCode = 12;
            ca.ReasonCode = CobolText.Trimmed(num.Reason, 8);
            ca.SetMessage(num.Message);
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        pp.ProposalRef = CobolText.Trimmed(num.TextOut, 30);
        if (CobolText.IsSpaces(pp.BankAccount)) pp.BankAccount = "GBP1";
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql,
                "SET NOCOUNT ON; INSERT ap.PaymentProposal (CompanyCode, ProposalRef, Version, SelectionCutoffDate, PayDate, PaymentMethod, " +
                "BankAccountCode, CurrencyCode, IsBacsProcessingDay, BacsInputDate, Status, PreparedBy, CreatedBy) SELECT @p1, @p2, @p3, " +
                "CAST(@p4 AS date), CAST(@p5 AS date), @p6, @p7, @p8, @p9, CAST(@p10 AS date), 'DRAFT', @p11, @p12; " +
                "SELECT CAST(SCOPE_IDENTITY() AS varchar(24))",
                new
                {
                    p1 = ca.Company,
                    p2 = pp.ProposalRef,
                    p3 = ApBankVr.IntEdit(pp.Version),
                    p4 = ApBankVr.IntEdit(pp.CutoffDate),
                    p5 = ApBankVr.IntEdit(pp.PayDate),
                    p6 = pp.Method,
                    p7 = pp.BankAccount,
                    p8 = pp.Currency,
                    p9 = pp.IsProcDay == "Y" ? "1" : "0",
                    p10 = ApBankVr.IntEdit(pp.BacsInputDate),
                    p11 = ca.User,
                    p12 = ca.User,
                }, [40], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        pp.ProposalId = row is null ? 0 : (int)CobolNumeric.Store(ApSqlio.NumVal(row[0]), 9, 0);
        pp.PreparedBy = CobolText.Trimmed(ca.User, 20);
    }

    // 3150-UPDATE-HEADER (row count unread)
    private async Task UpdateHeaderAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        await ExecUpdateAsync(ca,
            "UPDATE ap.PaymentProposal SET SelectionCutoffDate = CAST(@p1 AS date), PayDate = CAST(@p2 AS date), PaymentMethod = @p3, " +
            "BankAccountCode = @p4, CurrencyCode = @p5, IsBacsProcessingDay = @p6, BacsInputDate = CAST(@p7 AS date), ModifiedBy = @p8, " +
            "ModifiedAt = SYSDATETIME() WHERE ProposalId = @p9 AND Status IN ('DRAFT','PROPOSED')",
            new
            {
                p1 = ApBankVr.IntEdit(pp.CutoffDate),
                p2 = ApBankVr.IntEdit(pp.PayDate),
                p3 = pp.Method,
                p4 = pp.BankAccount,
                p5 = pp.Currency,
                p6 = pp.IsProcDay == "Y" ? "1" : "0",
                p7 = ApBankVr.IntEdit(pp.BacsInputDate),
                p8 = ca.User,
                p9 = ApBankVr.IntEdit(pp.ProposalId),
            }, ct);
    }

    // 4000-RESERVE
    private async Task ReserveAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        var mode = ca.Function switch { "SELECT" => "SELECT", "RELEASE" => "RELEASE", _ => "RESERVE" };
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, "EXEC ap.usp_ReserveProposal @p1, @p2, @p3, @p4",
                new { p1 = ca.Company, p2 = ApBankVr.IntEdit(pp.ProposalId), p3 = ca.User, p4 = mode }, [24, 24, 24, 24, 24, 24, 24], ct);
        }
        catch (SqlioException ex)
        {
            _fatal = true;
            ca.ReturnCode = 8;
            ca.ReasonCode = "APP1014";
            ca.Message = CobolText.Trimmed(ex.DriverText, 200);
            ca.MsgCount++;
            return;
        }
        if (row is null) return;
        pp.SelectedCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[0]), 6, 0, signed: false);
        pp.ExcludedCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[1]), 6, 0, signed: false);
        pp.CashPrincipal = CobolNumeric.Store(ApSqlio.NumVal(row[2]), 13, 2);
        pp.CreditsApplied = CobolNumeric.Store(ApSqlio.NumVal(row[3]), 13, 2);
        pp.DiscountsTaken = CobolNumeric.Store(ApSqlio.NumVal(row[4]), 13, 2);
        pp.ReservedTotal = CobolNumeric.Store(ApSqlio.NumVal(row[5]), 13, 2);
        pp.TotalOutflow = CobolNumeric.Store(ApSqlio.NumVal(row[6]), 13, 2);
        if (pp.ExcludedCount > 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APP1015";
            ca.SetMessage(pp.ExcludedCount.ToString(CultureInfo.InvariantCulture) + " item(s) were excluded; each exclusion states its reason");
            ca.MsgCount++;
        }
    }

    // 5000-TOTALS (a fetch error ends the list)
    private async Task TotalsAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        pp.Lines.Clear();
        pp.SupplierCount = 0;
        List<string[]> rows;
        try
        {
            rows = await ApSqlio.RowsAsync(sql,
                "SELECT CAST(l.LineNum AS varchar(8)), l.SupplierCode, CAST(ISNULL(l.OpenItemId,0) AS varchar(24)), ISNULL(l.DocumentNumber,''), " +
                "ISNULL(oi.ItemType,'INVOICE'), ISNULL(CONVERT(char(8), oi.DueDate, 112),'00000000'), l.CurrencyCode, " +
                "CAST(l.GrossAmount AS varchar(24)), CAST(l.GrossAmountGBP AS varchar(24)), CAST(l.DiscountGBP AS varchar(24)), " +
                "CAST(l.CreditAppliedGBP AS varchar(24)), CAST(l.ReservedAmountGBP AS varchar(24)), CAST(l.NetPayGBP AS varchar(24)), " +
                "CAST(ISNULL(l.SupplierBankId,0) AS varchar(24)), CAST(l.IsExcluded AS char(1)), ISNULL(LEFT(l.ExclusionReason,60),'') " +
                "FROM ap.PaymentProposalLine l LEFT JOIN ap.OpenItem oi ON oi.OpenItemId = l.OpenItemId WHERE l.ProposalId = @p1 ORDER BY l.LineNum",
                new { p1 = ApBankVr.IntEdit(pp.ProposalId) }, [8, 20, 24, 30, 12, 8, 3, 24, 24, 24, 24, 24, 24, 24, 1, 60], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        foreach (var r in rows.Take(60))
        {
            // 5100-MOVE-LINE
            pp.Lines.Add(new ProposalLine(
                (int)CobolNumeric.Store(ApSqlio.NumVal(r[0]), 5, 0, signed: false),
                ApSqlio.T(r[1]),
                (int)CobolNumeric.Store(ApSqlio.NumVal(r[2]), 9, 0),
                ApSqlio.T(r[3]),
                ApSqlio.T(r[4]),
                (int)CobolNumeric.Store(ApSqlio.NumVal(r[5]), 8, 0, signed: false),
                ApSqlio.T(r[6]),
                CobolNumeric.Store(ApSqlio.NumVal(r[7]), 13, 2),
                CobolNumeric.Store(ApSqlio.NumVal(r[8]), 13, 2),
                CobolNumeric.Store(ApSqlio.NumVal(r[9]), 11, 2),
                CobolNumeric.Store(ApSqlio.NumVal(r[10]), 11, 2),
                CobolNumeric.Store(ApSqlio.NumVal(r[11]), 13, 2),
                CobolNumeric.Store(ApSqlio.NumVal(r[12]), 13, 2),
                (int)CobolNumeric.Store(ApSqlio.NumVal(r[13]), 9, 0),
                r[14] == "1" ? "Y" : "N",
                ApSqlio.T(r[15])));
        }
    }

    // 6000-HASH
    private async Task HashAsync(CaHeader ca, PaymentProposalArea pp, CancellationToken ct)
    {
        pp.ContentHash = ApHash.Compute(HashText(pp.ProposalRef, ca.Company, pp.Method, pp.BankAccount, pp.Currency, pp.PayDate,
            pp.TotalOutflow, pp.SelectedCount));
        await ExecUpdateAsync(ca, "UPDATE ap.PaymentProposal SET ContentHash = @p1 WHERE ProposalId = @p2",
            new { p1 = pp.ContentHash, p2 = ApBankVr.IntEdit(pp.ProposalId) }, ct);
    }

    /// <summary>The proposal content text hashed by APPAYPRP 6000-HASH and APPAYAPR 9200-BUILD-HASH.</summary>
    public static string HashText(string proposalRef, string company, string method, string bank, string currency, int payDate,
        decimal outflow, int selected) =>
        CobolText.Fit(proposalRef.Trim(' ') + "|" + company.Trim(' ') + "|" + method.Trim(' ') + "|" + bank.Trim(' ') + "|" +
                      currency.Trim(' ') + "|" + D8(payDate) + "|" + MoneyEdit(outflow) + "|" + CountEdit(selected), 300);

    // 8500-EXEC-UPDATE
    private async Task ExecUpdateAsync(CaHeader ca, string statement, object param, CancellationToken ct)
    {
        try
        {
            await ApSqlio.ExecAsync(sql, statement, param, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
    }

    private void Reject(CaHeader ca)
    {
        _fatal = true;
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    private void SqlFailed(CaHeader ca, SqlioException ex)
    {
        _fatal = true;
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in APPAYPRP" : text;
        ca.MsgCount++;
    }

    /// <summary>PIC 9(8) as text.</summary>
    public static string D8(int yyyymmdd) => (Math.Abs(yyyymmdd) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>-(11)9.99 trimmed.</summary>
    public static string MoneyEdit(decimal value)
    {
        var v = CobolNumeric.Store(value, 11, 2);
        return v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Z(5)9 trimmed (unsigned).</summary>
    public static string CountEdit(int value) => (Math.Abs(value) % 1_000_000).ToString(CultureInfo.InvariantCulture);
}
