using Sterp.Modules.Ar.Contracts;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Domain;

/// <summary>ARVATWRTCA (cobol/copy/ARVATWRTCA.cpy): VAT code lookup and fin.VatTransaction write.</summary>
public sealed class ArvatwrtArea : Commarea
{
    [Pic("X(6)")] public string SourceModule { get; set; } = "";
    [Pic("X(20)")] public string DocType { get; set; } = "";
    [Pic("X(40)")] public string DocRef { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int LineNum { get; set; }
    [Pic("X(10)")] public string PartyType { get; set; } = "";
    [Pic("X(20)")] public string PartyCode { get; set; } = "";
    [Pic("X(15)")] public string PartyVatNo { get; set; } = "";
    [Pic("9(8)", Date = true)] public int TaxPoint { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("X(10)")] public string PeriodKey { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int JournalId { get; set; }
    [Pic("X(4)")] public string VatCode { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal RatePct { get; set; }
    [Pic("X(12)")] public string Liability { get; set; } = "";
    [Pic("X(12)")] public string Mechanism { get; set; } = "";
    [Pic("X(6)")] public string Direction { get; set; } = "";
    [Pic("X")] public string SelfAccounts { get; set; } = "";
    [Pic("X")] public string EvidenceReq { get; set; } = "";
    [Pic("X(200)")] public string Wording { get; set; } = "";
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("S9(11)V99")] public decimal Net { get; set; }
    [Pic("S9(11)V99")] public decimal Vat { get; set; }
    [Pic("S9(11)V99")] public decimal NetGbp { get; set; }
    [Pic("S9(11)V99")] public decimal VatGbp { get; set; }
    [Pic("S9(11)V99")] public decimal RecoverableGbp { get; set; }
    [Pic("9(2)")] public int VatBox { get; set; }
    [Pic("9(2)")] public int NetBox { get; set; }
    [Pic("X(16)")] public string AdjustType { get; set; } = "";
    [Pic("X")] public string LateEntry { get; set; } = "";
    [Pic("X(60)")] public string EvidenceRef { get; set; } = "";
    [Pic("X(20)")] public string PlaceOfSupply { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int TxnId { get; set; }
    [Pic("X")] public string RateFound { get; set; } = "";
}
