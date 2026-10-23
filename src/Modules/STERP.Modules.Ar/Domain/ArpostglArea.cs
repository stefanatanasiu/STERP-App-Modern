using Sterp.Platform.Kernel.Services;
using Sterp.Modules.Ar.Contracts;

namespace Sterp.Modules.Ar.Domain;

/// <summary>ARPOSTGLCA (cobol/copy/ARPOSTGLCA.cpy) - ARPOSTGL, the sales ledger route into the general ledger.</summary>
public sealed class ArpostglArea : Commarea
{
    public const int MaxLegs = 30;

    [Pic("X(30)")] public string EventCode { get; set; } = "";
    [Pic("X(12)")] public string JournalType { get; set; } = "";
    [Pic("X(200)")] public string Description { get; set; } = "";
    [Pic("X(6)")] public string SourceModule { get; set; } = "";
    [Pic("X(40)")] public string SourceRef { get; set; } = "";
    [Pic("9(8)", Date = true)] public int DocDate { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("X(120)")] public string EvidenceRef { get; set; } = "";
    [Pic("X(20)")] public string SeriesCode { get; set; } = "";
    [Pic("X")] public string OwnTran { get; set; } = "";
    [Pic("X(12)")] public string LookupLeg { get; set; } = "";
    [Pic("X(12)")] public string LookupAccount { get; set; } = "";
    [Pic("9(3)")] public int LegCnt { get; set; }
    [Occurs(MaxLegs, "legTab", nameof(LegCnt))] public ArpostglLeg[] Legs { get; } = CommareaCodec.Table<ArpostglLeg>(MaxLegs);
    [Pic("X(30)")] public string JournalNo { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int JournalId { get; set; }
    [Pic("S9(11)V99")] public decimal TotalDebit { get; set; }
    [Pic("S9(11)V99")] public decimal TotalCredit { get; set; }
    [Pic("X")] public string WasDuplicate { get; set; } = "";
}

/// <summary>PG-LEGS OCCURS 30.</summary>
public sealed class ArpostglLeg
{
    [Pic("X(12)")] public string GLegCode { get; set; } = "";
    [Pic("X(12)")] public string GAccount { get; set; } = "";
    [Pic("S9(11)V99")] public decimal GDebit { get; set; }
    [Pic("S9(11)V99")] public decimal GCredit { get; set; }
    [Pic("X(100)")] public string GNarrative { get; set; } = "";
    [Pic("X(4)")] public string GVatCode { get; set; } = "";
    [Pic("X(20)")] public string GCostCentre { get; set; } = "";
    [Pic("X(20)")] public string GProject { get; set; } = "";
    [Pic("X(6)")] public string GSubledger { get; set; } = "";
    [Pic("X(40)")] public string GSubref { get; set; } = "";
    [Pic("X(20)")] public string GBankAcct { get; set; } = "";
}
