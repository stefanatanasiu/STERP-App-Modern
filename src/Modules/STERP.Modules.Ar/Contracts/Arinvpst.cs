using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Contracts;

/// <summary>
/// ARINVPSTCA (cobol/copy/ARINVPSTCA.cpy) - ARINVPST sales invoice posting. Functions: VALIDATE, PROFORMA, POST
/// (<see cref="InvoicePostingFunctions"/>). JSON form: OpenAPI schema ArInprocArinvpstCommarea.
/// </summary>
public sealed class ArinvpstArea : Commarea
{
    public const int MaxLines = 25;
    public const int MaxVats = 6;
    public const int MaxMissing = 10;

    [Pic("X(12)")] public string DocType { get; set; } = "";
    [Pic("X(30)")] public string InvoiceNo { get; set; } = "";
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(20)")] public string InvoiceCust { get; set; } = "";
    [Pic("9(8)", Date = true)] public int DocDate { get; set; }
    [Pic("9(8)")] public int TaxPoint { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("9(8)", Date = true)] public int DueDate { get; set; }
    [Pic("X(12)")] public string TermsCode { get; set; } = "";
    [Pic("X(100)")] public string TermsSnapshot { get; set; } = "";
    [Pic("X(20)")] public string TermsSource { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal DiscountPct { get; set; }
    [Pic("9(8)", Date = true)] public int DiscountDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("9(8)", Date = true)] public int FxRateDate { get; set; }
    [Pic("X(10)")] public string Source { get; set; } = "";
    [Pic("X(40)")] public string OrderRef { get; set; } = "";
    [Pic("X(30)")] public string SoNumber { get; set; } = "";
    [Pic("X(30)")] public string DespatchRef { get; set; } = "";
    [Pic("X(20)")] public string Salesperson { get; set; } = "";
    [Pic("X(120)")] public string BillName { get; set; } = "";
    [Pic("X(300)")] public string BillAddress { get; set; } = "";
    [Pic("X(300)")] public string DeliveryAddr { get; set; } = "";
    [Pic("X(15)")] public string CustVatNo { get; set; } = "";
    [Pic("X(15)")] public string OwnVatNo { get; set; } = "";
    [Pic("X(3)")] public string VatLocation { get; set; } = "";
    [Pic("X")] public string Simplified { get; set; } = "";
    [Pic("X(300)")] public string SpecialWording { get; set; } = "";
    [Pic("X(60)")] public string EvidenceRef { get; set; } = "";
    [Pic("S9(11)V99")] public decimal DepositApply { get; set; }
    [Pic("S9(11)V99")] public decimal Net { get; set; }
    [Pic("S9(11)V99")] public decimal Vat { get; set; }
    [Pic("S9(11)V99")] public decimal Gross { get; set; }
    [Pic("S9(11)V99")] public decimal NetGbp { get; set; }
    [Pic("S9(11)V99")] public decimal VatGbp { get; set; }
    [Pic("S9(11)V99")] public decimal GrossGbp { get; set; }
    [Pic("9(3)")] public int LineCnt { get; set; }
    [Occurs(MaxLines, "lineTab", nameof(LineCnt))] public ArinvpstLine[] Lines { get; } = CommareaCodec.Table<ArinvpstLine>(MaxLines);
    [Pic("9(2)")] public int VatCnt { get; set; }
    [Occurs(MaxVats, "vatTab", nameof(VatCnt))] public ArinvpstVat[] Vats { get; } = CommareaCodec.Table<ArinvpstVat>(MaxVats);
    [Pic("S9(9)", Binary = true)] public int InvoiceId { get; set; }
    [Pic("S9(9)", Binary = true)] public int OpenItemId { get; set; }
    [Pic("X(30)")] public string JournalNo { get; set; } = "";
    [Pic("X")] public string Duplicate { get; set; } = "";
    [Pic("9(2)")] public int MissingCnt { get; set; }
    [Occurs(MaxMissing, "missingTab", nameof(MissingCnt)), Pic("X(40)")] public string[] Missing { get; } = CommareaCodec.Texts(MaxMissing);
}

/// <summary>AI-LINES OCCURS 25.</summary>
public sealed class ArinvpstLine
{
    [Pic("X(40)")] public string LItem { get; set; } = "";
    [Pic("X(60)")] public string LDesc { get; set; } = "";
    [Pic("S9(9)V9(6)")] public decimal LQty { get; set; }
    [Pic("X(10)")] public string LUom { get; set; } = "";
    [Pic("S9(11)V9(6)")] public decimal LPrice { get; set; }
    [Pic("S9(3)V9(4)")] public decimal LDiscPct { get; set; }
    [Pic("S9(11)V99")] public decimal LCharge { get; set; }
    [Pic("S9(11)V99")] public decimal LNet { get; set; }
    [Pic("X(4)")] public string LVatCode { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal LVatRate { get; set; }
    [Pic("S9(11)V99")] public decimal LVatAmt { get; set; }
    [Pic("X(12)")] public string LRevAccount { get; set; } = "";
    [Pic("X(20)")] public string LCostCentre { get; set; } = "";
    [Pic("X(20)")] public string LProject { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int LSoLine { get; set; }
    [Pic("S9(9)V9(6)")] public decimal LAvailQty { get; set; }
    [Pic("X(80)")] public string LMessage { get; set; } = "";
}

/// <summary>AI-VATS OCCURS 6 (VAT analysis per code).</summary>
public sealed class ArinvpstVat
{
    [Pic("X(4)")] public string VCode { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal VRate { get; set; }
    [Pic("S9(11)V99")] public decimal VNetGbp { get; set; }
    [Pic("S9(11)V99")] public decimal VVatGbp { get; set; }
    [Pic("X(12)")] public string VLiability { get; set; } = "";
}

/// <summary>CA-FUNCTION values of ARINVPST.</summary>
public static class InvoicePostingFunctions
{
    public const string Validate = "VALIDATE";
    public const string ProForma = "PROFORMA";
    public const string Post = "POST";
}

/// <summary>
/// IC-AR-ARINVPST - the public in-process contract of ARINVPST consumed by STERP-SO (SOINVRUN). Mirrors
/// <c>CALL "ARINVPST" USING ARINVPSTCA</c> on the caller's unit of work: VALIDATE reads only; PROFORMA owns BEGN/COMT;
/// POST runs PERMCHK, PERIODCK, IDEMPOT and NUMGEN first, then BEGN..COMT - which commits the caller's open work too
/// (SME-013) - and IDEMPOT end afterwards (STERP-AR.md section 7.2.3).
/// </summary>
public interface IArInvoicePostingService
{
    /// <summary>CALL "ARINVPST" USING area - dispatches on <c>area.Header.Function</c>; returns the header result.</summary>
    Task<Sterp.Platform.Kernel.LegacyResult> CallAsync(ArinvpstArea area, CancellationToken ct = default);

    Task<Sterp.Platform.Kernel.LegacyResult> ValidateAsync(ArinvpstArea area, CancellationToken ct = default) =>
        Run(area, InvoicePostingFunctions.Validate, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> ProFormaAsync(ArinvpstArea area, CancellationToken ct = default) =>
        Run(area, InvoicePostingFunctions.ProForma, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> PostAsync(ArinvpstArea area, CancellationToken ct = default) =>
        Run(area, InvoicePostingFunctions.Post, ct);

    private Task<Sterp.Platform.Kernel.LegacyResult> Run(ArinvpstArea area, string function, CancellationToken ct)
    {
        area.Header.Function = function;
        return CallAsync(area, ct);
    }
}
