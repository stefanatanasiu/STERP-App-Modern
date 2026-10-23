using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>APSUPVALCA body (cobol/copy/APSUPVALCA.cpy) as the facade fills it: SV-SUPPLIER only, every other field initial.</summary>
public sealed class SupplierValidationArea
{
    public string Supplier { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string TradingName { get; set; } = "";
    public string LegalForm { get; set; } = "";
    public string Status { get; set; } = "";
    public string BusinessSize { get; set; } = "";
    public string Currency { get; set; } = "";
    public string AllowedCurrencies { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public string VatLocation { get; set; } = "";
    public string VatRegNo { get; set; } = "";
    public string Country { get; set; } = "";
    public string Sanctions { get; set; } = "";
    public string CisFlag { get; set; } = "";
    public string EinvoiceFlag { get; set; } = "";
    public string ApControl { get; set; } = "";
    public string ExpenseAccount { get; set; } = "";
    public string DimCostCentre { get; set; } = "";
    public string OwnerUser { get; set; } = "";
    public string BuyerUser { get; set; } = "";
    public string TermsCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public int TermDays { get; set; }
    public decimal MinPayment { get; set; }
    public decimal OpenBalanceGbp { get; set; }
    public int OpenItemCount { get; set; }
    public string PurchaseHold { get; set; } = "";
    public string PaymentHold { get; set; } = "";
    public string CanPurchase { get; set; } = "";
    public string CanPay { get; set; } = "";
    public string TermException { get; set; } = "";
    public int DdOutstanding { get; set; }
    public int DdFailed { get; set; }
    public string Note { get; set; } = "";

    /// <summary>The ApSupplierValidation response object (fields the facade's ReadSupplier and the page read).</summary>
    public object ToData() => new
    {
        SupplierCode = Supplier,
        LegalName,
        TradingName,
        LegalForm,
        Status,
        BusinessSize,
        Currency,
        AllowedCurrencies,
        PaymentMethod = PayMethod,
        VatLocation,
        VatRegNo,
        Country,
        Sanctions,
        CisFlag,
        EinvoiceFlag,
        ApControlAccount = ApControl,
        ExpenseAccount,
        DimCostCentre,
        OwnerUser,
        BuyerUser,
        TermsCode,
        TermDays,
        MinPayment = CobolNumeric.WithScale(MinPayment, 2),
        OpenBalanceGbp = CobolNumeric.WithScale(OpenBalanceGbp, 2),
        OpenItemCount,
        PurchaseHold,
        PaymentHold,
        CanPurchase,
        CanPay,
        DdOutstanding,
        DdFailed,
        Note,
    };
}

/// <summary>
/// APSUPVAL (cobol/online/ap/APSUPVAL.cob) functions VALIDATE and CANPAY (FR-STERP-AP-012, -013; spec 4.1). Reads only on
/// the request's engine connection (no BEGN). HOLD/RELEASE/TERMCHK are deferred (no production route, SME-015).
/// </summary>
public sealed class ApSupVal(ISqlRunner sql)
{
    private const string SupplierSql =
        "SELECT LegalName, ISNULL(TradingName,''), LegalForm, Status, BusinessSize, CurrencyCode, AllowedCurrencies, PaymentMethod, " +
        "VatLocation, ISNULL(VatRegNo,''), CountryCode, SanctionsStatus, CAST(IsCisOrConstructionDrc AS char(1)), " +
        "CAST(EInvoiceEnabled AS char(1)), ISNULL(ApControlAccount,''), ISNULL(DefaultExpenseAccount,''), ISNULL(DimCostCentre,''), " +
        "ISNULL(ApOwnerUser,''), ISNULL(BuyerUser,''), ISNULL(TermsCode,''), CAST(PurchaseHold AS char(1)), CAST(PaymentHold AS char(1)), " +
        "ISNULL(HoldReason,''), CAST(ISNULL(MinimumPaymentGBP,0) AS varchar(24)) FROM ap.Supplier WHERE CompanyCode = @p1 AND SupplierCode = @p2";

    private static readonly int[] SupplierWidths = [120, 120, 14, 10, 8, 3, 40, 10, 3, 15, 2, 12, 1, 1, 12, 12, 20, 20, 20, 12, 1, 1, 200, 24];

    private const string BalanceSql =
        "SELECT CAST(ISNULL(SUM(ResidualAmountGBP),0) AS varchar(30)), CAST(COUNT(*) AS varchar(12)) FROM ap.OpenItem " +
        "WHERE CompanyCode = @p1 AND SupplierCode = @p2 AND SettlementStatus IN ('OPEN','PARTIAL')";

    private const string DiligenceSql =
        "SELECT CAST(SUM(CASE WHEN Outcome IN ('PENDING','EXPIRED') THEN 1 ELSE 0 END) AS varchar(12)), " +
        "CAST(SUM(CASE WHEN Outcome = 'FAIL' THEN 1 ELSE 0 END) AS varchar(12)) FROM ap.SupplierDueDiligence " +
        "WHERE CompanyCode = @p1 AND SupplierCode = @p2 AND IsMandatory = 1";

    private bool _fatal;
    private bool _found;

    public async Task RunAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        _found = false;
        ca.Program = "APSUPVAL";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-001";
        sv.Note = "";
        sv.CanPurchase = "N";
        sv.CanPay = "N";
        sv.TermException = "N";
        sv.DdOutstanding = 0;
        sv.DdFailed = 0;
        sv.OpenItemCount = 0;
        sv.Supplier = CobolText.Upper(sv.Supplier);
        ca.Company = CobolText.Upper(ca.Company);

        // 1500-VALIDATE
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APS0001";
            ca.SetMessage("A company is required for a supplier enquiry");
            Reject(ca);
            return;
        }
        if (CobolText.IsSpaces(sv.Supplier))
        {
            ca.ReasonCode = "APS0002";
            ca.SetMessage("A supplier account code is required");
            Reject(ca);
            return;
        }

        switch (ca.Function)
        {
            case "VALIDATE":
                await ReadAsync(ca, sv, ct);
                break;
            case "CANPAY":
                await CanPayAsync(ca, sv, ct);
                break;
            default:
                ca.ReasonCode = "APS0099";
                ca.SetMessage("APSUPVAL does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 2000-READ
    private async Task ReadAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        await FetchSupplierAsync(ca, sv, ct);
        if (_fatal || !_found) return;
        await FetchBalanceAsync(ca, sv, ct);
        await FetchDiligenceAsync(ca, sv, ct);
        Decide(ca, sv);
    }

    // 2100-FETCH-SUPPLIER
    private async Task FetchSupplierAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, SupplierSql, new { p1 = ca.Company, p2 = sv.Supplier }, SupplierWidths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "APS0003";
            ca.SetMessage("Supplier " + sv.Supplier.Trim() + " does not exist in company " + ca.Company.Trim());
            ca.MsgCount++;
            return;
        }
        _found = true;
        // 2150-MOVE-FIELDS
        sv.LegalName = ApSqlio.T(row[0]);
        sv.TradingName = ApSqlio.T(row[1]);
        sv.LegalForm = ApSqlio.T(row[2]);
        sv.Status = ApSqlio.T(row[3]);
        sv.BusinessSize = ApSqlio.T(row[4]);
        sv.Currency = ApSqlio.T(row[5]);
        sv.AllowedCurrencies = ApSqlio.T(row[6]);
        sv.PayMethod = ApSqlio.T(row[7]);
        sv.VatLocation = ApSqlio.T(row[8]);
        sv.VatRegNo = ApSqlio.T(row[9]);
        sv.Country = ApSqlio.T(row[10]);
        sv.Sanctions = ApSqlio.T(row[11]);
        sv.ApControl = ApSqlio.T(row[14]);
        sv.ExpenseAccount = ApSqlio.T(row[15]);
        sv.DimCostCentre = ApSqlio.T(row[16]);
        sv.OwnerUser = ApSqlio.T(row[17]);
        sv.BuyerUser = ApSqlio.T(row[18]);
        sv.TermsCode = ApSqlio.T(row[19]);
        sv.Reason = ApSqlio.T(row[22]);
        sv.CisFlag = row[12] == "1" ? "Y" : "N";
        sv.EinvoiceFlag = row[13] == "1" ? "Y" : "N";
        sv.PurchaseHold = row[20] == "1" ? "Y" : "N";
        sv.PaymentHold = row[21] == "1" ? "Y" : "N";
        sv.MinPayment = CobolNumeric.Store(ApSqlio.NumVal(row[23]), 11, 2);
    }

    // 2200-FETCH-BALANCE
    private async Task FetchBalanceAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        sv.OpenBalanceGbp = 0m;
        sv.OpenItemCount = 0;
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, BalanceSql, new { p1 = ca.Company, p2 = sv.Supplier }, [30, 12], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null) return;
        sv.OpenBalanceGbp = CobolNumeric.Store(ApSqlio.NumVal(row[0]), 13, 2);
        sv.OpenItemCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[1]), 6, 0, signed: false);
    }

    // 2300-FETCH-DILIGENCE
    private async Task FetchDiligenceAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, DiligenceSql, new { p1 = ca.Company, p2 = sv.Supplier }, [12, 12], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null) return;
        sv.DdOutstanding = (int)CobolNumeric.Store(ApSqlio.NumVal(row[0]), 3, 0, signed: false);
        sv.DdFailed = (int)CobolNumeric.Store(ApSqlio.NumVal(row[1]), 3, 0, signed: false);
    }

    // 2400-DECIDE
    private static void Decide(CaHeader ca, SupplierValidationArea sv)
    {
        sv.CanPurchase = "Y";
        sv.CanPay = "Y";
        sv.Note = "";
        if (sv.PurchaseHold == "Y")
        {
            sv.CanPurchase = "N";
            sv.Note = "New purchasing is suspended; existing payables remain due";
        }
        if (sv.PaymentHold == "Y")
        {
            sv.CanPay = "N";
            sv.Note = "Payment is on hold for this account";
        }
        if (sv.Status == "PENDING")
        {
            sv.CanPurchase = "N";
            sv.CanPay = "N";
            sv.Note = "The account is pending approval and due diligence";
        }
        if (sv.Status == "RETIRED")
        {
            sv.CanPurchase = "N";
            sv.Note = "Retired account; authorised settlement and enquiry remain available";
        }
        if (sv.Status == "SUSPENDED")
        {
            sv.CanPurchase = "N";
        }
        if (sv.Sanctions is "MATCH" or "BLOCKED")
        {
            sv.CanPurchase = "N";
            sv.CanPay = "N";
            sv.Note = "Sanctions screening blocks this account";
        }
        if (sv.DdFailed > 0)
        {
            sv.CanPurchase = "N";
        }
        if (sv.CanPurchase == "N" && sv.CanPay == "N")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APS0006";
            ca.SetMessage(sv.Note);
            ca.MsgCount++;
        }
        else if (sv.CanPurchase == "N" || sv.CanPay == "N")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APS0007";
            ca.SetMessage(sv.Note);
            ca.MsgCount++;
        }
        else if (sv.DdOutstanding > 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APS0008";
            ca.SetMessage("Due diligence evidence is outstanding for this supplier");
            ca.MsgCount++;
        }
        else
        {
            ca.ReturnCode = 0;
        }
    }

    // 2500-CANPAY
    private async Task CanPayAsync(CaHeader ca, SupplierValidationArea sv, CancellationToken ct)
    {
        await ReadAsync(ca, sv, ct);
        if (_fatal || !_found) return;
        if (sv.CanPay == "Y")
        {
            ca.ReturnCode = 0;
            ca.ReasonCode = "";
            ca.SetMessage(sv.Supplier.Trim() + " may be paid");
        }
        else
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APS0009";
            ca.SetMessage("Payment to " + sv.Supplier.Trim() + " is held: " + sv.Note.Trim());
            ca.MsgCount++;
        }
    }

    // 9000-REJECT
    private void Reject(CaHeader ca)
    {
        _fatal = true;
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    // 9300-SQL-FAILED
    private void SqlFailed(CaHeader ca, SqlioException ex)
    {
        _fatal = true;
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Fit(ex.DriverText, 200);
        ca.Message = CobolText.IsSpaces(text) ? "SQL failure in APSUPVAL" : text.TrimEnd();
        ca.MsgCount++;
    }
}
