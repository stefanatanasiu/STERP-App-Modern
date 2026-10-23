using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>One IR-SIMILAR entry.</summary>
public sealed record SimilarDocument(string RegisterNumber, string SupplierDocRef, int DocumentDate, decimal StatedGross, string Reason);

/// <summary>The APINVREGCA fields DUPCHK reads and writes (cobol/copy/APINVREGCA.cpy).</summary>
public sealed class InvoiceRegisterArea
{
    public string Supplier { get; set; } = "";
    public string DocType { get; set; } = "";
    public string SupplierRef { get; set; } = "";
    public string RefNormalised { get; set; } = "";
    public string RegisterNumber { get; set; } = "";
    public int InvoiceId { get; set; }
    public int DateReceived { get; set; }
    public int DocumentDate { get; set; }
    public int TaxPoint { get; set; }
    public int PostingDate { get; set; }
    public string Currency { get; set; } = "";
    public decimal FxRate { get; set; }
    public decimal StatedGross { get; set; }
    public string SourceChannel { get; set; } = "";
    public string RegState { get; set; } = "";
    public string DupFound { get; set; } = "";
    public string DupRegister { get; set; } = "";
    public List<SimilarDocument> Similar { get; } = [];

    /// <summary>The ApDuplicateCheckResult response object.</summary>
    public object ToData() => new
    {
        SupplierCode = Supplier,
        DocumentType = DocType,
        SupplierDocRef = SupplierRef,
        RefNormalised,
        DocumentDate = IsoDate(DocumentDate),
        DateReceived = IsoDate(DateReceived),
        RegState,
        DupFound,
        DupRegister,
        Similar = Similar.Select(s => new
        {
            s.RegisterNumber,
            s.SupplierDocRef,
            DocumentDate = IsoDate(s.DocumentDate),
            StatedGross = CobolNumeric.WithScale(s.StatedGross, 2),
            s.Reason,
        }).ToList(),
    };

    public static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// APINVREG (cobol/online/ap/APINVREG.cob) prologue, header edits and DUPCHK (FR-STERP-AP-022, spec 4.3). Reads only; nothing
/// is written. VALIDATE/REGISTER/SETSTATE/GET (FR-STERP-AP-025) have no production route.
/// </summary>
public sealed class ApInvReg(ISqlRunner sql, ApSvc svc)
{
    private bool _fatal;

    public async Task RunAsync(CaHeader ca, InvoiceRegisterArea ir, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        ca.Program = "APINVREG";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-003";
        ir.DupFound = "N";
        ir.DupRegister = "";
        ir.Similar.Clear();
        ir.Supplier = CobolText.Upper(ir.Supplier);
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(ir.DocType)) ir.DocType = "INVOICE";
        if (CobolText.IsSpaces(ir.SourceChannel)) ir.SourceChannel = "PAPER";
        if (ir.FxRate == 0) ir.FxRate = 1;
        if (CobolText.IsSpaces(ir.Currency)) ir.Currency = "GBP";
        // 1050-NORMALISE (the service RC is not read)
        var norm = ApSvcArea.For(ca, "NORMREF");
        norm.Text3 = ir.SupplierRef;
        await svc.CallAsync(norm, ct);
        ir.RefNormalised = CobolText.Trimmed(norm.TextOut, 40);

        EditHeader(ca, ir);
        if (_fatal) return;
        switch (ca.Function)
        {
            case "DUPCHK":
                await DuplicateAsync(ca, ir, ct);
                break;
            default:
                ca.ReasonCode = "APR0099";
                ca.SetMessage("APINVREG does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 1100-EDIT-HEADER
    private void EditHeader(CaHeader ca, InvoiceRegisterArea ir)
    {
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APR0001";
            ca.SetMessage("A company is required");
            Reject(ca);
            return;
        }
        if (ca.Function is "GET" or "SETSTATE") return;
        if (CobolText.IsSpaces(ir.Supplier))
        {
            ca.ReasonCode = "APR0002";
            ca.SetMessage("A supplier account code is required");
            Reject(ca);
            return;
        }
        if (CobolText.IsSpaces(ir.SupplierRef))
        {
            ca.ReasonCode = "APR0003";
            ca.SetMessage("The supplier's own document reference is required");
            Reject(ca);
            return;
        }
        if (ir.DocType is not ("INVOICE" or "CREDITNOTE"))
        {
            ca.ReasonCode = "APR0004";
            ca.SetMessage("The document type must be INVOICE or CREDITNOTE");
            Reject(ca);
            return;
        }
        if (ir.DateReceived == 0) ir.DateReceived = ca.BusinessDate;
        if (ir.DocumentDate == 0)
        {
            ca.ReasonCode = "APR0005";
            ca.SetMessage("The supplier's document date is required");
            Reject(ca);
            return;
        }
        if (ir.TaxPoint == 0) ir.TaxPoint = ir.DocumentDate;
        if (ir.PostingDate == 0) ir.PostingDate = ca.BusinessDate;
        if (ir.DateReceived < ir.DocumentDate)
        {
            ca.ReasonCode = "APR0006";
            ca.SetMessage("A document cannot be received before it was issued");
            Reject(ca);
        }
    }

    // 2000-DUPLICATE
    private async Task DuplicateAsync(CaHeader ca, InvoiceRegisterArea ir, CancellationToken ct)
    {
        await ExactAsync(ca, ir, ct);
        if (_fatal) return;
        if (ir.DupFound == "Y")
        {
            ir.RegState = "DUPLICATE";
            ca.ReturnCode = 8;
            ca.ReasonCode = "APR0010";
            ca.SetMessage("Supplier document " + ir.SupplierRef.Trim(' ') + " is already registered as " + ir.DupRegister.Trim(' ') +
                          "; registering it again is refused");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await SimilarAsync(ca, ir, ct);
        if (ir.Similar.Count > 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APR0011";
            ca.SetMessage("A similar document is already registered for this supplier; review it before proceeding");
            ca.MsgCount++;
        }
        else if (ca.ReturnCode == 0)
        {
            ca.SetMessage("No duplicate or similar document found");
        }
    }

    // 2100-EXACT
    private async Task ExactAsync(CaHeader ca, InvoiceRegisterArea ir, CancellationToken ct)
    {
        string[]? row;
        try
        {
            var id = ApBankVr.IntEdit(ir.InvoiceId);
            row = await ApSqlio.SingleAsync(sql,
                "SELECT TOP (1) RegisterNumber FROM ap.PurchaseInvoice WHERE CompanyCode = @p1 AND SupplierCode = @p2 AND DocumentType = @p3 " +
                "AND SupplierDocRefNormalised = @p4 AND RegistrationState <> 'REJECTED' AND (@p5 = 0 OR PurchaseInvoiceId <> @p6)",
                new { p1 = ca.Company, p2 = ir.Supplier, p3 = ir.DocType, p4 = ir.RefNormalised, p5 = id, p6 = id }, [30], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is not null)
        {
            ir.DupFound = "Y";
            ir.DupRegister = ApSqlio.T(row[0]);
        }
    }

    // 2200-SIMILAR (a fetch error ends the list)
    private async Task SimilarAsync(CaHeader ca, InvoiceRegisterArea ir, CancellationToken ct)
    {
        ir.Similar.Clear();
        var gross = CobolNumeric.SqlioNumber(ir.StatedGross);
        var date = ApBankVr.IntEdit(ir.DocumentDate);
        List<string[]> rows;
        try
        {
            rows = await ApSqlio.RowsAsync(sql,
                "SELECT TOP (10) RegisterNumber, SupplierDocRef, CONVERT(char(8), DocumentDate, 112), CAST(StatedGross AS varchar(24)), " +
                "CASE WHEN StatedGross = CAST(@p1 AS decimal(18,2)) AND DocumentDate = CAST(@p2 AS date) THEN 'Same amount and date' " +
                "WHEN StatedGross = CAST(@p3 AS decimal(18,2)) THEN 'Same amount' ELSE 'Same document date' END " +
                "FROM ap.PurchaseInvoice WHERE CompanyCode = @p4 AND SupplierCode = @p5 AND DocumentType = @p6 " +
                "AND SupplierDocRefNormalised <> @p7 AND RegistrationState <> 'REJECTED' AND " +
                "(StatedGross = CAST(@p8 AS decimal(18,2)) OR DocumentDate = CAST(@p9 AS date)) ORDER BY PurchaseInvoiceId DESC",
                new { p1 = gross, p2 = date, p3 = gross, p4 = ca.Company, p5 = ir.Supplier, p6 = ir.DocType, p7 = ir.RefNormalised, p8 = gross, p9 = date },
                [30, 40, 8, 24, 40], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        foreach (var r in rows.Take(10))
        {
            ir.Similar.Add(new SimilarDocument(ApSqlio.T(r[0]), ApSqlio.T(r[1]), (int)CobolNumeric.Store(ApSqlio.NumVal(r[2]), 8, 0, signed: false),
                CobolNumeric.Store(ApSqlio.NumVal(r[3]), 13, 2), ApSqlio.T(r[4])));
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
        ca.Message = text.Length == 0 ? "SQL failure in APINVREG" : text;
        ca.MsgCount++;
    }
}
