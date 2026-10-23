using Sterp.Platform.Kernel.Services;
using Sterp.Modules.Ar.Contracts;

namespace Sterp.Modules.Ar.Domain;

/// <summary>RCPTALOCCA (cobol/copy/RCPTALOCCA.cpy) - RCPTALOC receipts and allocation. JSON form: ArRcptalocCommarea.</summary>
public sealed class RcptalocArea : Commarea
{
    public const int MaxAllocs = 20;

    [Pic("X(30)")] public string ReceiptNo { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ReceiptId { get; set; }
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(20)")] public string Payer { get; set; } = "";
    [Pic("X(120)")] public string PayerName { get; set; } = "";
    [Pic("X(10)")] public string Method { get; set; } = "";
    [Pic("X(20)")] public string BankAccount { get; set; } = "";
    [Pic("9(8)", Date = true)] public int ReceiptDate { get; set; }
    [Pic("9(8)", Date = true)] public int ValueDate { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("S9(11)V99")] public decimal Gross { get; set; }
    [Pic("S9(11)V99")] public decimal GrossGbp { get; set; }
    [Pic("S9(11)V99")] public decimal BankFee { get; set; }
    [Pic("X(12)")] public string ConfirmState { get; set; } = "";
    [Pic("X(60)")] public string BankRef { get; set; } = "";
    [Pic("X(60)")] public string ExternalRef { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int DdCollectId { get; set; }
    [Pic("X")] public string Unidentified { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ExceptionId { get; set; }
    [Pic("9(8)", Date = true)] public int EffectiveDate { get; set; }
    [Pic("X(10)")] public string SourceType { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int SourceItemId { get; set; }
    [Pic("X(30)")] public string SourceDoc { get; set; } = "";
    [Pic("S9(11)V99")] public decimal SourceAvail { get; set; }
    [Pic("S9(9)V9(8)")] public decimal CrossCcyRate { get; set; }
    [Pic("9(3)")] public int AllocCnt { get; set; }
    [Occurs(MaxAllocs, "allocTab", nameof(AllocCnt))] public RcptalocAlloc[] Allocs { get; } = CommareaCodec.Table<RcptalocAlloc>(MaxAllocs);
    [Pic("S9(11)V99")] public decimal TotApplied { get; set; }
    [Pic("S9(11)V99")] public decimal TotDiscount { get; set; }
    [Pic("S9(11)V99")] public decimal TotWriteoff { get; set; }
    [Pic("S9(11)V99")] public decimal TotFx { get; set; }
    [Pic("S9(11)V99")] public decimal Residual { get; set; }
    [Pic("S9(11)V99")] public decimal Unallocated { get; set; }
    [Pic("S9(11)V99")] public decimal WriteoffLimit { get; set; }
    [Pic("S9(5)")] public int DiscGraceDays { get; set; }
    [Pic("S9(9)", Binary = true)] public int AllocId { get; set; }
    [Pic("X(200)")] public string CorrectReason { get; set; } = "";
    [Pic("9(8)", Date = true)] public int CorrectDate { get; set; }
    [Pic("9(3)")] public int ReversedCnt { get; set; }
    [Pic("X(30)")] public string JournalNo { get; set; } = "";

    /// <summary>88 RA-CONFIRMED.</summary>
    public bool Confirmed => ConfirmState == "CONFIRMED";
}

/// <summary>RA-ALLOCS OCCURS 20.</summary>
public sealed class RcptalocAlloc
{
    [Pic("S9(9)", Binary = true)] public int AItemId { get; set; }
    [Pic("X(30)")] public string ADocNo { get; set; } = "";
    [Pic("X(3)")] public string ACurrency { get; set; } = "";
    [Pic("S9(11)V99")] public decimal AApply { get; set; }
    [Pic("S9(11)V99")] public decimal ADiscount { get; set; }
    [Pic("S9(11)V99")] public decimal AWriteoff { get; set; }
    [Pic("X(100)")] public string AWoReason { get; set; } = "";
    [Pic("S9(11)V99")] public decimal AResidBefore { get; set; }
    [Pic("S9(11)V99")] public decimal AResidAfter { get; set; }
    [Pic("S9(11)V99")] public decimal AFxRealised { get; set; }
    [Pic("S9(9)", Binary = true)] public int AAllocId { get; set; }
    [Pic("X(10)")] public string AKind { get; set; } = "";
    [Pic("X(80)")] public string AMessage { get; set; } = "";
}
