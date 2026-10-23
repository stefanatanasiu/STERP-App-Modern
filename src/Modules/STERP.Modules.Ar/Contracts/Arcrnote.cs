using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Contracts;

/// <summary>
/// ARCRNOTECA (cobol/copy/ARCRNOTECA.cpy) - ARCRNOTE sales credit notes. Functions: CHECK, POST
/// (<see cref="CreditNoteFunctions"/>). JSON form: OpenAPI schema ArInprocArcrnoteCommarea.
/// </summary>
public sealed class ArcrnoteArea : Commarea
{
    public const int MaxLines = 20;

    [Pic("X(30)")] public string CreditNo { get; set; } = "";
    [Pic("X(30)")] public string OrigInvoice { get; set; } = "";
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(10)")] public string CreditType { get; set; } = "";
    [Pic("X(20)")] public string ReasonCode { get; set; } = "";
    [Pic("X(200)")] public string Reason { get; set; } = "";
    [Pic("9(8)", Date = true)] public int DocDate { get; set; }
    [Pic("9(8)")] public int TaxPoint { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("X")] public string ReturnStock { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ApprovalId { get; set; }
    [Pic("X(20)")] public string ApprovedBy { get; set; } = "";
    [Pic("S9(11)V99")] public decimal OrigNetGbp { get; set; }
    [Pic("S9(11)V99")] public decimal OrigVatGbp { get; set; }
    [Pic("S9(11)V99")] public decimal OrigGrossGbp { get; set; }
    [Pic("S9(11)V99")] public decimal PriorCredit { get; set; }
    [Pic("S9(11)V99")] public decimal CreditableRem { get; set; }
    [Pic("S9(9)", Binary = true)] public int OrigInvoiceId { get; set; }
    [Pic("S9(11)V99")] public decimal Net { get; set; }
    [Pic("S9(11)V99")] public decimal Vat { get; set; }
    [Pic("S9(11)V99")] public decimal Gross { get; set; }
    [Pic("S9(11)V99")] public decimal NetGbp { get; set; }
    [Pic("S9(11)V99")] public decimal VatGbp { get; set; }
    [Pic("S9(11)V99")] public decimal GrossGbp { get; set; }
    [Pic("9(3)")] public int LineCnt { get; set; }
    [Occurs(MaxLines, "lineTab", nameof(LineCnt))] public ArcrnoteLine[] Lines { get; } = CommareaCodec.Table<ArcrnoteLine>(MaxLines);
    [Pic("S9(9)", Binary = true)] public int CreditId { get; set; }
    [Pic("S9(9)", Binary = true)] public int OpenItemId { get; set; }
    [Pic("X(30)")] public string JournalNo { get; set; } = "";
}

/// <summary>AN-LINES OCCURS 20.</summary>
public sealed class ArcrnoteLine
{
    [Pic("S9(9)", Binary = true)] public int LOrigLine { get; set; }
    [Pic("X(40)")] public string LItem { get; set; } = "";
    [Pic("X(60)")] public string LDesc { get; set; } = "";
    [Pic("S9(9)V9(6)")] public decimal LQty { get; set; }
    [Pic("S9(11)V99")] public decimal LNet { get; set; }
    [Pic("X(4)")] public string LVatCode { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal LVatRate { get; set; }
    [Pic("S9(11)V99")] public decimal LVatAmt { get; set; }
    [Pic("X(12)")] public string LRevAccount { get; set; } = "";
    [Pic("S9(11)V99")] public decimal LOrigNet { get; set; }
    [Pic("S9(11)V99")] public decimal LOrigCredit { get; set; }
    [Pic("S9(11)V99")] public decimal LRemaining { get; set; }
    [Pic("X(80)")] public string LMessage { get; set; } = "";
}

/// <summary>CA-FUNCTION values of ARCRNOTE.</summary>
public static class CreditNoteFunctions
{
    public const string Check = "CHECK";
    public const string Post = "POST";
}

/// <summary>
/// IC-AR-ARCRNOTE - the public in-process contract of ARCRNOTE consumed by STERP-SO (SOCREDIT, REBATE). Mirrors
/// <c>CALL "ARCRNOTE" USING ARCRNOTECA</c> on the caller's unit of work: CHECK reads only; POST runs PERMCHK, PERIODCK,
/// IDEMPOT and NUMGEN (SCRN) first, then BEGN..COMT - which commits the caller's open work too (SME-013)
/// (STERP-AR.md section 7.2.4).
/// </summary>
public interface IArCreditNoteService
{
    /// <summary>CALL "ARCRNOTE" USING area - dispatches on <c>area.Header.Function</c>; returns the header result.</summary>
    Task<Sterp.Platform.Kernel.LegacyResult> CallAsync(ArcrnoteArea area, CancellationToken ct = default);

    Task<Sterp.Platform.Kernel.LegacyResult> CheckAsync(ArcrnoteArea area, CancellationToken ct = default) =>
        Run(area, CreditNoteFunctions.Check, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> PostAsync(ArcrnoteArea area, CancellationToken ct = default) =>
        Run(area, CreditNoteFunctions.Post, ct);

    private Task<Sterp.Platform.Kernel.LegacyResult> Run(ArcrnoteArea area, string function, CancellationToken ct)
    {
        area.Header.Function = function;
        return CallAsync(area, ct);
    }
}
