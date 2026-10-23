using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>The APMATCHCA fields MATCHALL reads and writes (cobol/copy/APMATCHCA.cpy).</summary>
public sealed class MatchArea
{
    public string RegisterNumber { get; set; } = "";
    public int InvoiceId { get; set; }
    public string Basis { get; set; } = "";
    public decimal InvPricePer { get; set; }
    public decimal OrdPricePer { get; set; }
    public decimal InvConv { get; set; }
    public decimal OrdConv { get; set; }
    public decimal TolPricePct { get; set; }
    public decimal TolPriceGbp { get; set; }
    public decimal TolChargeGbp { get; set; }
    public decimal TolVatGbp { get; set; }
    public decimal TolRoundGbp { get; set; }
    public string ZeroReference { get; set; } = "";
    public string NeedsApproval { get; set; } = "";
    public string InvoiceState { get; set; } = "";
    public int LinesMatched { get; set; }
    public int LinesVariance { get; set; }
    public int LinesUnmatched { get; set; }
    public string ToleranceNote { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>The ApMatchResult response object.</summary>
    public object ToData() => new
    {
        RegisterNumber,
        InvoiceId,
        LinesMatched,
        LinesVariance,
        LinesUnmatched,
        InvoiceState,
        Note,
    };
}

/// <summary>
/// APMATCH (cobol/online/ap/APMATCH.cob) prologue and MATCHALL (FR-STERP-AP-023, spec 4.4). Every statement autocommits; the
/// facade passes only the register number, so MT-INVOICE-ID is 0, the line cursor selects nothing and the invoice UPDATE
/// affects no row (SME-446, reproduced).
/// </summary>
public sealed class ApMatch(ISqlRunner sql, ApSvc svc)
{
    private bool _fatal;

    public async Task RunAsync(CaHeader ca, MatchArea mt, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        ca.Program = "APMATCH";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-006";
        mt.Note = "";
        mt.ToleranceNote = "";
        mt.ZeroReference = "N";
        mt.NeedsApproval = "N";
        if (CobolText.IsSpaces(mt.Basis)) mt.Basis = "QUANTITY";
        if (mt.InvPricePer == 0) mt.InvPricePer = 1;
        if (mt.OrdPricePer == 0) mt.OrdPricePer = 1;
        if (mt.InvConv == 0) mt.InvConv = 1;
        if (mt.OrdConv == 0) mt.OrdConv = 1;
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APM0001";
            ca.SetMessage("A company is required");
            Reject(ca);
        }
        // 1100-TOLERANCES
        if (mt.TolPricePct == 0) mt.TolPricePct = CobolNumeric.Store((await svc.ParmNumAsync(ca, "PO.PRICE.TOL.PCT", 2, ct)).NumOut, 5, 4);
        if (mt.TolPriceGbp == 0) mt.TolPriceGbp = CobolNumeric.Store((await svc.ParmNumAsync(ca, "PO.PRICE.TOL.GBP", 50, ct)).NumOut, 11, 2);
        if (mt.TolChargeGbp == 0) mt.TolChargeGbp = CobolNumeric.Store((await svc.ParmNumAsync(ca, "PO.FREIGHT.TOL.GBP", 25, ct)).NumOut, 11, 2);
        if (mt.TolVatGbp == 0) mt.TolVatGbp = CobolNumeric.Store((await svc.ParmNumAsync(ca, "AP.VAT.TOL.GBP", 0.05m, ct)).NumOut, 11, 2);
        if (mt.TolRoundGbp == 0) mt.TolRoundGbp = CobolNumeric.Store((await svc.ParmNumAsync(ca, "AP.INVOICE.ROUNDING.GBP", 1, ct)).NumOut, 11, 2);
        if (_fatal) return;

        switch (ca.Function)
        {
            case "MATCHALL":
                await MatchAllAsync(ca, mt, ct);
                break;
            default:
                ca.ReasonCode = "APM0099";
                ca.SetMessage("APMATCH does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 5000-MATCHALL
    private async Task MatchAllAsync(CaHeader ca, MatchArea mt, CancellationToken ct)
    {
        mt.LinesMatched = 0;
        mt.LinesVariance = 0;
        mt.LinesUnmatched = 0;
        var id = ApBankVr.IntEdit(mt.InvoiceId);
        List<string[]> lines;
        try
        {
            lines = await ApSqlio.RowsAsync(sql,
                "SELECT CAST(l.LineNum AS varchar(8)), ISNULL(l.PurchaseOrderNumber,''), CAST(ISNULL(l.PurchaseOrderLine,0) AS varchar(8)), " +
                "ISNULL(l.ReceiptRef,''), CAST(ISNULL(l.ReceiptLine,0) AS varchar(8)), ISNULL(l.ServiceAcceptanceRef,''), ISNULL(l.ItemCode,''), " +
                "CAST(ISNULL(l.Quantity,0) AS varchar(24)), CAST(l.ConversionFactor AS varchar(24)), CAST(ISNULL(l.UnitPrice,0) AS varchar(24)), " +
                "CAST(l.PricePerQty AS varchar(24)), CAST(l.ChargeAmount AS varchar(24)), CAST(l.NetAmount AS varchar(24)), " +
                "CAST(l.VatAmount AS varchar(24)), CAST(l.VatRatePct AS varchar(24)) FROM ap.PurchaseInvoiceLine l " +
                "WHERE l.PurchaseInvoiceId = @p1 ORDER BY l.LineNum",
                new { p1 = id }, [8, 30, 8, 30, 8, 30, 40, 24, 24, 24, 24, 24, 24, 24, 24], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (lines.Count > 0)
        {
            // 5100-ONE-LINE -> 4000-CONFIRM: only reachable with a non-zero MT-INVOICE-ID, which no production route supplies.
            throw new NotSupportedException("APMATCH CONFIRM is reached only with an invoice id, which no production route supplies (SME-446).");
        }
        // 4300-UPDATE-INVOICE
        try
        {
            await ApSqlio.ExecAsync(sql, 
                "UPDATE ap.PurchaseInvoice SET MatchState = CASE WHEN EXISTS (SELECT 1 FROM ap.InvoiceMatch m WHERE m.PurchaseInvoiceId = @p1 " +
                "AND m.Status IN ('PROPOSED','DISPUTED') AND m.WithinTolerance = 0) THEN 'VARIANCE' WHEN (SELECT COUNT(DISTINCT " +
                "m2.PurchaseInvoiceLineNum) FROM ap.InvoiceMatch m2 WHERE m2.PurchaseInvoiceId = @p2 AND m2.Status <> 'REVERSED') >= " +
                "(SELECT COUNT(*) FROM ap.PurchaseInvoiceLine l WHERE l.PurchaseInvoiceId = @p3) THEN 'MATCHED' ELSE 'PARTIAL' END, " +
                "PendingMatchClearingGBP = PendingMatchClearingGBP, ModifiedBy = @p4, ModifiedAt = SYSDATETIME() WHERE PurchaseInvoiceId = @p5",
                new { p1 = id, p2 = id, p3 = id, p4 = ca.User, p5 = id }, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
        if (mt.LinesVariance > 0)
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APM0016";
            ca.SetMessage(mt.LinesVariance.ToString(CultureInfo.InvariantCulture) + " line(s) are outside tolerance and need approval before payment");
            ca.MsgCount++;
        }
        else
        {
            ca.ReturnCode = 0;
            ca.SetMessage(mt.LinesMatched.ToString(CultureInfo.InvariantCulture) + " line(s) matched within tolerance");
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
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in APMATCH" : text;
        ca.MsgCount++;
    }
}
