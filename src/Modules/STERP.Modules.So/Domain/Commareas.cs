// Generated from the binding OpenAPI components of STERP-SO (x-sterp-legacy-field of the COMMAREA result schemas).
// Field order, JSON names and pictures are binding (contracts 4.3/4.4); behaviour lives in the program classes.
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>PRICINGCA body (cobol/copy/PRICINGCA.cpy; OpenAPI SoPricingResult).</summary>
public sealed partial class PricingArea : Commarea
{
    /// <summary>PR-ITEM-CODE PIC X(40).</summary>
    [F("itemCode", "X(40)")] public string ItemCode { get; set; } = "";
    /// <summary>PR-REVISION PIC X(4).</summary>
    [F("revision", "X(4)")] public string Revision { get; set; } = "";
    /// <summary>PR-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>PR-PRICE-GROUP PIC X(20).</summary>
    [F("priceGroup", "X(20)")] public string PriceGroup { get; set; } = "";
    /// <summary>PR-CONTRACT-REF PIC X(40).</summary>
    [F("contractRef", "X(40)")] public string ContractRef { get; set; } = "";
    /// <summary>PR-UOM-CODE PIC X(10).</summary>
    [F("uomCode", "X(10)")] public string UomCode { get; set; } = "";
    /// <summary>PR-CURRENCY PIC X(3).</summary>
    [F("currency", "X(3)")] public string Currency { get; set; } = "";
    /// <summary>PR-QUANTITY PIC S9(13)V9(6).</summary>
    [F("quantity", "S9(13)V9(6)")] public decimal Quantity { get; set; }
    /// <summary>PR-ORDER-DATE PIC 9(8).</summary>
    [F("orderDate", "9(8)", Date = true)] public int OrderDate { get; set; }
    /// <summary>PR-PRICE-DATE PIC 9(8).</summary>
    [F("priceDate", "9(8)", Date = true)] public int PriceDate { get; set; }
    /// <summary>PR-DOC-TYPE PIC X(20).</summary>
    [F("docType", "X(20)")] public string DocType { get; set; } = "";
    /// <summary>PR-DOC-REF PIC X(40).</summary>
    [F("docRef", "X(40)")] public string DocRef { get; set; } = "";
    /// <summary>PR-DOC-VERSION PIC S9(4).</summary>
    [F("docVersion", "S9(4)")] public int DocVersion { get; set; }
    /// <summary>PR-MANUAL-PRICE PIC S9(11)V9(6).</summary>
    [F("manualPrice", "S9(11)V9(6)")] public decimal ManualPrice { get; set; }
    /// <summary>PR-MANUAL-PER-QTY PIC S9(9)V9(6).</summary>
    [F("manualPerQty", "S9(9)V9(6)")] public decimal ManualPerQty { get; set; }
    /// <summary>PR-MANUAL-APPROVAL PIC X(20).</summary>
    [F("manualApproval", "X(20)")] public string ManualApproval { get; set; } = "";
    /// <summary>PR-MANUAL-REASON PIC X(100).</summary>
    [F("manualReason", "X(100)")] public string ManualReason { get; set; } = "";
    /// <summary>PR-LIST-CODE-IN PIC X(20).</summary>
    [F("listCodeIn", "X(20)")] public string ListCodeIn { get; set; } = "";
    /// <summary>PR-MAX-LEVEL PIC S9(4).</summary>
    [F("maxLevel", "S9(4)")] public int MaxLevel { get; set; }
    /// <summary>PR-XCURR-ALLOW PIC X.</summary>
    [F("xcurrAllow", "X")] public string XcurrAllow { get; set; } = "";
    /// <summary>PR-UNIT-PRICE PIC S9(11)V9(6).</summary>
    [F("unitPrice", "S9(11)V9(6)")] public decimal UnitPrice { get; set; }
    /// <summary>PR-PRICE-PER-QTY PIC S9(9)V9(6).</summary>
    [F("pricePerQty", "S9(9)V9(6)")] public decimal PricePerQty { get; set; }
    /// <summary>PR-DISCOUNT-PCT PIC S9(5)V9(4).</summary>
    [F("discountPct", "S9(5)V9(4)")] public decimal DiscountPct { get; set; }
    /// <summary>PR-MIN-MARGIN-PCT PIC S9(5)V9(4).</summary>
    [F("minMarginPct", "S9(5)V9(4)")] public decimal MinMarginPct { get; set; }
    /// <summary>PR-PRICE-LEVEL PIC S9(4).</summary>
    [F("priceLevel", "S9(4)")] public int PriceLevel { get; set; }
    /// <summary>PR-PRICE-SCOPE PIC X(12).</summary>
    [F("priceScope", "X(12)")] public string PriceScope { get; set; } = "";
    /// <summary>PR-PRICE-LIST-CODE PIC X(20).</summary>
    [F("priceListCode", "X(20)")] public string PriceListCode { get; set; } = "";
    /// <summary>PR-PRICE-CURRENCY PIC X(3).</summary>
    [F("priceCurrency", "X(3)")] public string PriceCurrency { get; set; } = "";
    /// <summary>PR-PRICE-UOM PIC X(10).</summary>
    [F("priceUom", "X(10)")] public string PriceUom { get; set; } = "";
    /// <summary>PR-QTY-BREAK PIC S9(13)V9(6).</summary>
    [F("qtyBreak", "S9(13)V9(6)")] public decimal QtyBreak { get; set; }
    /// <summary>PR-VALID-FROM PIC 9(8).</summary>
    [F("validFrom", "9(8)")] public int ValidFrom { get; set; }
    /// <summary>PR-VALID-TO PIC 9(8).</summary>
    [F("validTo", "9(8)")] public int ValidTo { get; set; }
    /// <summary>PR-PRICE-SOURCE PIC X(10).</summary>
    [F("priceSource", "X(10)")] public string PriceSource { get; set; } = "";
    /// <summary>PR-PRICE-FOUND PIC X.</summary>
    [F("priceFound", "X")] public string PriceFound { get; set; } = "";
    /// <summary>PR-AMBIGUOUS PIC X.</summary>
    [F("ambiguous", "X")] public string Ambiguous { get; set; } = "";
    /// <summary>PR-BLOCK-CONFIRM PIC X.</summary>
    [F("blockConfirm", "X")] public string BlockConfirm { get; set; } = "";
    /// <summary>PR-RIVAL-LIST-CODE PIC X(20).</summary>
    [F("rivalListCode", "X(20)")] public string RivalListCode { get; set; } = "";
    /// <summary>PR-EXPLANATION PIC X(200).</summary>
    [F("explanation", "X(200)")] public string Explanation { get; set; } = "";
    /// <summary>PR-CAND-CNT PIC 9(3).</summary>
    [F("candCnt", "9(3)")] public int CandCnt { get; set; }
    /// <summary>PR-CAND-TRUNC PIC X.</summary>
    [F("candTrunc", "X")] public string CandTrunc { get; set; } = "";
    /// <summary>PR-CAND OCCURS 40.</summary>
    [T("cand", 40, "candCnt")] public PricingCand[] Cand { get; } = SoCodec.Table<PricingCand>(40);
    /// <summary>PR-BREAK-CNT PIC 9(3).</summary>
    [F("breakCnt", "9(3)")] public int BreakCnt { get; set; }
    /// <summary>PR-BREAK OCCURS 20.</summary>
    [T("break", 20, "breakCnt")] public PricingBreak[] Break { get; } = SoCodec.Table<PricingBreak>(20);
}

/// <summary>One PR-CAND OCCURS 40 occurrence.</summary>
public sealed class PricingCand
{
    /// <summary>PR-C-LEVEL PIC 9(1).</summary>
    [F("cLevel", "9(1)")] public int CLevel { get; set; }
    /// <summary>PR-C-SCOPE PIC X(12).</summary>
    [F("cScope", "X(12)")] public string CScope { get; set; } = "";
    /// <summary>PR-C-LIST PIC X(20).</summary>
    [F("cList", "X(20)")] public string CList { get; set; } = "";
    /// <summary>PR-C-CURRENCY PIC X(3).</summary>
    [F("cCurrency", "X(3)")] public string CCurrency { get; set; } = "";
    /// <summary>PR-C-UOM PIC X(10).</summary>
    [F("cUom", "X(10)")] public string CUom { get; set; } = "";
    /// <summary>PR-C-QTYBRK PIC S9(13)V9(6).</summary>
    [F("cQtybrk", "S9(13)V9(6)")] public decimal CQtybrk { get; set; }
    /// <summary>PR-C-PRICE PIC S9(11)V9(6).</summary>
    [F("cPrice", "S9(11)V9(6)")] public decimal CPrice { get; set; }
    /// <summary>PR-C-PER-QTY PIC S9(9)V9(6).</summary>
    [F("cPerQty", "S9(9)V9(6)")] public decimal CPerQty { get; set; }
    /// <summary>PR-C-DISC PIC S9(5)V9(4).</summary>
    [F("cDisc", "S9(5)V9(4)")] public decimal CDisc { get; set; }
    /// <summary>PR-C-MINMGN PIC S9(5)V9(4).</summary>
    [F("cMinmgn", "S9(5)V9(4)")] public decimal CMinmgn { get; set; }
    /// <summary>PR-C-FROM PIC 9(8).</summary>
    [F("cFrom", "9(8)")] public int CFrom { get; set; }
    /// <summary>PR-C-TO PIC 9(8).</summary>
    [F("cTo", "9(8)")] public int CTo { get; set; }
    /// <summary>PR-C-RANK PIC 9(3).</summary>
    [F("cRank", "9(3)")] public int CRank { get; set; }
    /// <summary>PR-C-CHOSEN PIC X.</summary>
    [F("cChosen", "X")] public string CChosen { get; set; } = "";
    /// <summary>PR-C-WHY PIC X(70).</summary>
    [F("cWhy", "X(70)")] public string CWhy { get; set; } = "";
}

/// <summary>One PR-BREAK OCCURS 20 occurrence.</summary>
public sealed class PricingBreak
{
    /// <summary>PR-B-QTYBRK PIC S9(13)V9(6).</summary>
    [F("bQtybrk", "S9(13)V9(6)")] public decimal BQtybrk { get; set; }
    /// <summary>PR-B-PRICE PIC S9(11)V9(6).</summary>
    [F("bPrice", "S9(11)V9(6)")] public decimal BPrice { get; set; }
    /// <summary>PR-B-PER-QTY PIC S9(9)V9(6).</summary>
    [F("bPerQty", "S9(9)V9(6)")] public decimal BPerQty { get; set; }
    /// <summary>PR-B-DISC PIC S9(5)V9(4).</summary>
    [F("bDisc", "S9(5)V9(4)")] public decimal BDisc { get; set; }
    /// <summary>PR-B-CURRENCY PIC X(3).</summary>
    [F("bCurrency", "X(3)")] public string BCurrency { get; set; } = "";
    /// <summary>PR-B-UOM PIC X(10).</summary>
    [F("bUom", "X(10)")] public string BUom { get; set; } = "";
    /// <summary>PR-B-LIST PIC X(20).</summary>
    [F("bList", "X(20)")] public string BList { get; set; } = "";
    /// <summary>PR-B-FROM PIC 9(8).</summary>
    [F("bFrom", "9(8)")] public int BFrom { get; set; }
    /// <summary>PR-B-TO PIC 9(8).</summary>
    [F("bTo", "9(8)")] public int BTo { get; set; }
}

/// <summary>SODISCCA body (cobol/copy/SODISCCA.cpy; OpenAPI SoDiscountResult).</summary>
public sealed partial class SodiscArea : Commarea
{
    /// <summary>SD-DOC-TYPE PIC X(20).</summary>
    [F("docType", "X(20)")] public string DocType { get; set; } = "";
    /// <summary>SD-DOC-REF PIC X(40).</summary>
    [F("docRef", "X(40)")] public string DocRef { get; set; } = "";
    /// <summary>SD-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SD-CURRENCY PIC X(3).</summary>
    [F("currency", "X(3)")] public string Currency { get; set; } = "";
    /// <summary>SD-PRICE-DATE PIC 9(8).</summary>
    [F("priceDate", "9(8)", Date = true)] public int PriceDate { get; set; }
    /// <summary>SD-ROUND-PLACES PIC S9(4).</summary>
    [F("roundPlaces", "S9(4)")] public int RoundPlaces { get; set; }
    /// <summary>SD-DOC-ALLOWANCE PIC S9(13)V9(2) COMP-3.</summary>
    [F("docAllowance", "S9(13)V9(2)")] public decimal DocAllowance { get; set; }
    /// <summary>SD-DOC-ALLOW-DESC PIC X(40).</summary>
    [F("docAllowDesc", "X(40)")] public string DocAllowDesc { get; set; } = "";
    /// <summary>SD-DOC-CHARGE PIC S9(13)V9(2) COMP-3.</summary>
    [F("docCharge", "S9(13)V9(2)")] public decimal DocCharge { get; set; }
    /// <summary>SD-DOC-CHARGE-DESC PIC X(40).</summary>
    [F("docChargeDesc", "X(40)")] public string DocChargeDesc { get; set; } = "";
    /// <summary>SD-DOC-CHARGE-VAT PIC X(4).</summary>
    [F("docChargeVat", "X(4)")] public string DocChargeVat { get; set; } = "";
    /// <summary>SD-CHARGE-VAT-RATE PIC S9(5)V9(4) COMP-3.</summary>
    [F("chargeVatRate", "S9(5)V9(4)")] public decimal ChargeVatRate { get; set; }
    /// <summary>SD-CHARGE-VAT-BASIS PIC X(8).</summary>
    [F("chargeVatBasis", "X(8)")] public string ChargeVatBasis { get; set; } = "";
    /// <summary>SD-APPLIED-TAG PIC X(32).</summary>
    [F("appliedTag", "X(32)")] public string AppliedTag { get; set; } = "";
    /// <summary>SD-ALREADY-APPLIED PIC X.</summary>
    [F("alreadyApplied", "X")] public string AlreadyApplied { get; set; } = "";
    /// <summary>SD-LINE-CNT PIC 9(3).</summary>
    [F("lineCnt", "9(3)")] public int LineCnt { get; set; }
    /// <summary>SD-TOT-GROSS PIC S9(13)V9(2) COMP-3.</summary>
    [F("totGross", "S9(13)V9(2)")] public decimal TotGross { get; set; }
    /// <summary>SD-TOT-DISCOUNT PIC S9(13)V9(2) COMP-3.</summary>
    [F("totDiscount", "S9(13)V9(2)")] public decimal TotDiscount { get; set; }
    /// <summary>SD-TOT-ALLOWANCE PIC S9(13)V9(2) COMP-3.</summary>
    [F("totAllowance", "S9(13)V9(2)")] public decimal TotAllowance { get; set; }
    /// <summary>SD-TOT-CHARGE PIC S9(13)V9(2) COMP-3.</summary>
    [F("totCharge", "S9(13)V9(2)")] public decimal TotCharge { get; set; }
    /// <summary>SD-TOT-NET PIC S9(13)V9(2) COMP-3.</summary>
    [F("totNet", "S9(13)V9(2)")] public decimal TotNet { get; set; }
    /// <summary>SD-TOT-VAT PIC S9(13)V9(2) COMP-3.</summary>
    [F("totVat", "S9(13)V9(2)")] public decimal TotVat { get; set; }
    /// <summary>SD-TOT-GROSS-TOTAL PIC S9(13)V9(2) COMP-3.</summary>
    [F("totGrossTotal", "S9(13)V9(2)")] public decimal TotGrossTotal { get; set; }
    /// <summary>SD-TOT-COST-GBP PIC S9(13)V9(2) COMP-3.</summary>
    [F("totCostGbp", "S9(13)V9(2)")] public decimal TotCostGbp { get; set; }
    /// <summary>SD-DOC-MARGIN-PCT PIC S9(5)V9(4) COMP-3.</summary>
    [F("docMarginPct", "S9(5)V9(4)")] public decimal DocMarginPct { get; set; }
    /// <summary>SD-ALLOC-RESIDUAL PIC S9(13)V9(2) COMP-3.</summary>
    [F("allocResidual", "S9(13)V9(2)")] public decimal AllocResidual { get; set; }
    /// <summary>SD-ALLOC-BUMPED PIC S9(9) COMP-5.</summary>
    [F("allocBumped", "S9(9)", Binary = true)] public int AllocBumped { get; set; }
    /// <summary>SD-RECONCILED PIC X.</summary>
    [F("reconciled", "X")] public string Reconciled { get; set; } = "";
    /// <summary>SD-EXPLANATION PIC X(200).</summary>
    [F("explanation", "X(200)")] public string Explanation { get; set; } = "";
    /// <summary>SD-LINE OCCURS 200.</summary>
    [T("line", 200, "lineCnt")] public SodiscLine[] Line { get; } = SoCodec.Table<SodiscLine>(200);
}

/// <summary>One SD-LINE OCCURS 200 occurrence.</summary>
public sealed class SodiscLine
{
    /// <summary>SD-L-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lLineNum", "S9(9)", Binary = true)] public int LLineNum { get; set; }
    /// <summary>SD-L-LINE-REF PIC X(8).</summary>
    [F("lLineRef", "X(8)")] public string LLineRef { get; set; } = "";
    /// <summary>SD-L-ITEM-CODE PIC X(40).</summary>
    [F("lItemCode", "X(40)")] public string LItemCode { get; set; } = "";
    /// <summary>SD-L-ENT-QTY PIC S9(13)V9(6) COMP-3.</summary>
    [F("lEntQty", "S9(13)V9(6)")] public decimal LEntQty { get; set; }
    /// <summary>SD-L-ENT-UOM PIC X(10).</summary>
    [F("lEntUom", "X(10)")] public string LEntUom { get; set; } = "";
    /// <summary>SD-L-PRICE-UOM PIC X(10).</summary>
    [F("lPriceUom", "X(10)")] public string LPriceUom { get; set; } = "";
    /// <summary>SD-L-FACTOR PIC S9(9)V9(9) COMP-3.</summary>
    [F("lFactor", "S9(9)V9(9)")] public decimal LFactor { get; set; }
    /// <summary>SD-L-PRICE-QTY PIC S9(13)V9(6) COMP-3.</summary>
    [F("lPriceQty", "S9(13)V9(6)")] public decimal LPriceQty { get; set; }
    /// <summary>SD-L-UNIT-PRICE PIC S9(11)V9(6) COMP-3.</summary>
    [F("lUnitPrice", "S9(11)V9(6)")] public decimal LUnitPrice { get; set; }
    /// <summary>SD-L-PER-QTY PIC S9(9)V9(6) COMP-3.</summary>
    [F("lPerQty", "S9(9)V9(6)")] public decimal LPerQty { get; set; }
    /// <summary>SD-L-GROSS-AMT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lGrossAmt", "S9(13)V9(2)")] public decimal LGrossAmt { get; set; }
    /// <summary>SD-L-DISC-PCT1 PIC S9(5)V9(4) COMP-3.</summary>
    [F("lDiscPct1", "S9(5)V9(4)")] public decimal LDiscPct1 { get; set; }
    /// <summary>SD-L-DISC-PCT2 PIC S9(5)V9(4) COMP-3.</summary>
    [F("lDiscPct2", "S9(5)V9(4)")] public decimal LDiscPct2 { get; set; }
    /// <summary>SD-L-DISC-PCT3 PIC S9(5)V9(4) COMP-3.</summary>
    [F("lDiscPct3", "S9(5)V9(4)")] public decimal LDiscPct3 { get; set; }
    /// <summary>SD-L-DISC-AMT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lDiscAmt", "S9(13)V9(2)")] public decimal LDiscAmt { get; set; }
    /// <summary>SD-L-LINE-ALLOW PIC S9(13)V9(2) COMP-3.</summary>
    [F("lLineAllow", "S9(13)V9(2)")] public decimal LLineAllow { get; set; }
    /// <summary>SD-L-DOC-ALLOW PIC S9(13)V9(2) COMP-3.</summary>
    [F("lDocAllow", "S9(13)V9(2)")] public decimal LDocAllow { get; set; }
    /// <summary>SD-L-ALLOW-TOT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lAllowTot", "S9(13)V9(2)")] public decimal LAllowTot { get; set; }
    /// <summary>SD-L-CHARGE PIC S9(13)V9(2) COMP-3.</summary>
    [F("lCharge", "S9(13)V9(2)")] public decimal LCharge { get; set; }
    /// <summary>SD-L-DOC-CHARGE PIC S9(13)V9(2) COMP-3.</summary>
    [F("lDocCharge", "S9(13)V9(2)")] public decimal LDocCharge { get; set; }
    /// <summary>SD-L-CHARGE-VAT PIC X(4).</summary>
    [F("lChargeVat", "X(4)")] public string LChargeVat { get; set; } = "";
    /// <summary>SD-L-GOODS-NET PIC S9(13)V9(2) COMP-3.</summary>
    [F("lGoodsNet", "S9(13)V9(2)")] public decimal LGoodsNet { get; set; }
    /// <summary>SD-L-NET-AMT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lNetAmt", "S9(13)V9(2)")] public decimal LNetAmt { get; set; }
    /// <summary>SD-L-VAT-CODE PIC X(4).</summary>
    [F("lVatCode", "X(4)")] public string LVatCode { get; set; } = "";
    /// <summary>SD-L-VAT-RATE PIC S9(5)V9(4) COMP-3.</summary>
    [F("lVatRate", "S9(5)V9(4)")] public decimal LVatRate { get; set; }
    /// <summary>SD-L-VAT-AMT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lVatAmt", "S9(13)V9(2)")] public decimal LVatAmt { get; set; }
    /// <summary>SD-L-GROSS-TOT PIC S9(13)V9(2) COMP-3.</summary>
    [F("lGrossTot", "S9(13)V9(2)")] public decimal LGrossTot { get; set; }
    /// <summary>SD-L-COST-GBP PIC S9(13)V9(2) COMP-3.</summary>
    [F("lCostGbp", "S9(13)V9(2)")] public decimal LCostGbp { get; set; }
    /// <summary>SD-L-MARGIN-PCT PIC S9(5)V9(4) COMP-3.</summary>
    [F("lMarginPct", "S9(5)V9(4)")] public decimal LMarginPct { get; set; }
    /// <summary>SD-L-STATUS PIC X(8).</summary>
    [F("lStatus", "X(8)")] public string LStatus { get; set; } = "";
    /// <summary>SD-L-WHY PIC X(30).</summary>
    [F("lWhy", "X(30)")] public string LWhy { get; set; } = "";
}

/// <summary>SOVATCA body (cobol/copy/SOVATCA.cpy; OpenAPI SoVatResult).</summary>
public sealed partial class SovatArea : Commarea
{
    /// <summary>SV-DOC-TYPE PIC X(20).</summary>
    [F("docType", "X(20)")] public string DocType { get; set; } = "";
    /// <summary>SV-DOC-REF PIC X(40).</summary>
    [F("docRef", "X(40)")] public string DocRef { get; set; } = "";
    /// <summary>SV-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lineNum", "S9(9)", Binary = true)] public int LineNum { get; set; }
    /// <summary>SV-ITEM-CODE PIC X(40).</summary>
    [F("itemCode", "X(40)")] public string ItemCode { get; set; } = "";
    /// <summary>SV-LINE-TYPE PIC X(10).</summary>
    [F("lineType", "X(10)")] public string LineType { get; set; } = "";
    /// <summary>SV-SUPPLY-DATE PIC 9(8).</summary>
    [F("supplyDate", "9(8)", Date = true)] public int SupplyDate { get; set; }
    /// <summary>SV-DESPATCH-DATE PIC 9(8).</summary>
    [F("despatchDate", "9(8)", Date = true)] public int DespatchDate { get; set; }
    /// <summary>SV-NET-AMOUNT PIC S9(13)V9(2).</summary>
    [F("netAmount", "S9(13)V9(2)")] public decimal NetAmount { get; set; }
    /// <summary>SV-CURRENCY PIC X(3).</summary>
    [F("currency", "X(3)")] public string Currency { get; set; } = "";
    /// <summary>SV-NET-AMOUNT-GBP PIC S9(13)V9(2).</summary>
    [F("netAmountGbp", "S9(13)V9(2)")] public decimal NetAmountGbp { get; set; }
    /// <summary>SV-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SV-CUST-LOCATION PIC X(3).</summary>
    [F("custLocation", "X(3)")] public string CustLocation { get; set; } = "";
    /// <summary>SV-CUST-COUNTRY PIC X(2).</summary>
    [F("custCountry", "X(2)")] public string CustCountry { get; set; } = "";
    /// <summary>SV-CUST-VAT-REG-NO PIC X(15).</summary>
    [F("custVatRegNo", "X(15)")] public string CustVatRegNo { get; set; } = "";
    /// <summary>SV-CUST-VAT-CHECKED PIC X(8).</summary>
    [F("custVatChecked", "X(8)")] public string CustVatChecked { get; set; } = "";
    /// <summary>SV-CUST-IS-BUSINESS PIC X.</summary>
    [F("custIsBusiness", "X")] public string CustIsBusiness { get; set; } = "";
    /// <summary>SV-CUST-DRC-ELIG PIC X.</summary>
    [F("custDrcElig", "X")] public string CustDrcElig { get; set; } = "";
    /// <summary>SV-CUST-EORI PIC X(17).</summary>
    [F("custEori", "X(17)")] public string CustEori { get; set; } = "";
    /// <summary>SV-DELIVER-LOCATION PIC X(3).</summary>
    [F("deliverLocation", "X(3)")] public string DeliverLocation { get; set; } = "";
    /// <summary>SV-DELIVER-COUNTRY PIC X(2).</summary>
    [F("deliverCountry", "X(2)")] public string DeliverCountry { get; set; } = "";
    /// <summary>SV-PLACE-OF-SUPPLY PIC X(3).</summary>
    [F("placeOfSupply", "X(3)")] public string PlaceOfSupply { get; set; } = "";
    /// <summary>SV-ITEM-VAT-CODE PIC X(4).</summary>
    [F("itemVatCode", "X(4)")] public string ItemVatCode { get; set; } = "";
    /// <summary>SV-ITEM-DRC-GOODS PIC X.</summary>
    [F("itemDrcGoods", "X")] public string ItemDrcGoods { get; set; } = "";
    /// <summary>SV-ITEM-VAT-SOURCE PIC X(12).</summary>
    [F("itemVatSource", "X(12)")] public string ItemVatSource { get; set; } = "";
    /// <summary>SV-READ-MASTER PIC X.</summary>
    [F("readMaster", "X")] public string ReadMaster { get; set; } = "";
    /// <summary>SV-PROOF-TYPE PIC X(20).</summary>
    [F("proofType", "X(20)")] public string ProofType { get; set; } = "";
    /// <summary>SV-PROOF-REF PIC X(60).</summary>
    [F("proofRef", "X(60)")] public string ProofRef { get; set; } = "";
    /// <summary>SV-PROOF-OBTAINED PIC X.</summary>
    [F("proofObtained", "X")] public string ProofObtained { get; set; } = "";
    /// <summary>SV-PROOF-DATE PIC 9(8).</summary>
    [F("proofDate", "9(8)", Date = true)] public int ProofDate { get; set; }
    /// <summary>SV-PROOF-DEADLINE PIC 9(8).</summary>
    [F("proofDeadline", "9(8)")] public int ProofDeadline { get; set; }
    /// <summary>SV-EVIDENCE-MONTHS PIC S9(4).</summary>
    [F("evidenceMonths", "S9(4)")] public int EvidenceMonths { get; set; }
    /// <summary>SV-EXPORT-DECL-REF PIC X(40).</summary>
    [F("exportDeclRef", "X(40)")] public string ExportDeclRef { get; set; } = "";
    /// <summary>SV-INCOTERMS-RULE PIC X(3).</summary>
    [F("incotermsRule", "X(3)")] public string IncotermsRule { get; set; } = "";
    /// <summary>SV-OVERRIDE-CODE PIC X(4).</summary>
    [F("overrideCode", "X(4)")] public string OverrideCode { get; set; } = "";
    /// <summary>SV-OVERRIDE-REASON PIC X(200).</summary>
    [F("overrideReason", "X(200)")] public string OverrideReason { get; set; } = "";
    /// <summary>SV-OVERRIDE-APPROVL PIC X(20).</summary>
    [F("overrideApprovl", "X(20)")] public string OverrideApprovl { get; set; } = "";
    /// <summary>SV-OVERRIDE-USER PIC X(20).</summary>
    [F("overrideUser", "X(20)")] public string OverrideUser { get; set; } = "";
    /// <summary>SV-OVERRIDE-APPLIED PIC X.</summary>
    [F("overrideApplied", "X")] public string OverrideApplied { get; set; } = "";
    /// <summary>SV-VAT-CODE PIC X(4).</summary>
    [F("vatCode", "X(4)")] public string VatCode { get; set; } = "";
    /// <summary>SV-VAT-RATE-PCT PIC S9(5)V9(4).</summary>
    [F("vatRatePct", "S9(5)V9(4)")] public decimal VatRatePct { get; set; }
    /// <summary>SV-VAT-AMOUNT PIC S9(13)V9(2).</summary>
    [F("vatAmount", "S9(13)V9(2)")] public decimal VatAmount { get; set; }
    /// <summary>SV-LIABILITY PIC X(12).</summary>
    [F("liability", "X(12)")] public string Liability { get; set; } = "";
    /// <summary>SV-MECHANISM PIC X(12).</summary>
    [F("mechanism", "X(12)")] public string Mechanism { get; set; } = "";
    /// <summary>SV-SELF-ACCOUNTS PIC X.</summary>
    [F("selfAccounts", "X")] public string SelfAccounts { get; set; } = "";
    /// <summary>SV-TREATMENT-BASIS PIC X(20).</summary>
    [F("treatmentBasis", "X(20)")] public string TreatmentBasis { get; set; } = "";
    /// <summary>SV-INVOICE-WORDING PIC X(200).</summary>
    [F("invoiceWording", "X(200)")] public string InvoiceWording { get; set; } = "";
    /// <summary>SV-EVIDENCE-REQD PIC X(100).</summary>
    [F("evidenceReqd", "X(100)")] public string EvidenceReqd { get; set; } = "";
    /// <summary>SV-EVIDENCE-STATUS PIC X(10).</summary>
    [F("evidenceStatus", "X(10)")] public string EvidenceStatus { get; set; } = "";
    /// <summary>SV-BLOCK-INVOICE PIC X.</summary>
    [F("blockInvoice", "X")] public string BlockInvoice { get; set; } = "";
    /// <summary>SV-BLOCK-REASON PIC X(8).</summary>
    [F("blockReason", "X(8)")] public string BlockReason { get; set; } = "";
    /// <summary>SV-BLOCK-TEXT PIC X(200).</summary>
    [F("blockText", "X(200)")] public string BlockText { get; set; } = "";
    /// <summary>SV-DRC-DEMINIMIS PIC S9(11)V9(2).</summary>
    [F("drcDeminimis", "S9(11)V9(2)")] public decimal DrcDeminimis { get; set; }
    /// <summary>SV-DRC-APPLIES PIC X.</summary>
    [F("drcApplies", "X")] public string DrcApplies { get; set; } = "";
    /// <summary>SV-RULE-CNT PIC 9(2).</summary>
    [F("ruleCnt", "9(2)")] public int RuleCnt { get; set; }
    /// <summary>SV-RULE OCCURS 10.</summary>
    [T("rule", 10, "ruleCnt")] public SovatRule[] Rule { get; } = SoCodec.Table<SovatRule>(10);
}

/// <summary>One SV-RULE OCCURS 10 occurrence.</summary>
public sealed class SovatRule
{
    /// <summary>SV-R-SEQ PIC 9(2).</summary>
    [F("rSeq", "9(2)")] public int RSeq { get; set; }
    /// <summary>SV-R-TEXT PIC X(90).</summary>
    [F("rText", "X(90)")] public string RText { get; set; } = "";
}

/// <summary>SOMARGNCA body (cobol/copy/SOMARGNCA.cpy; OpenAPI SoMarginResult).</summary>
public sealed partial class SomargnArea : Commarea
{
    /// <summary>SM-DOC-TYPE PIC X(20).</summary>
    [F("docType", "X(20)")] public string DocType { get; set; } = "";
    /// <summary>SM-DOC-REF PIC X(40).</summary>
    [F("docRef", "X(40)")] public string DocRef { get; set; } = "";
    /// <summary>SM-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lineNum", "S9(9)", Binary = true)] public int LineNum { get; set; }
    /// <summary>SM-ITEM-CODE PIC X(40).</summary>
    [F("itemCode", "X(40)")] public string ItemCode { get; set; } = "";
    /// <summary>SM-REVISION PIC X(4).</summary>
    [F("revision", "X(4)")] public string Revision { get; set; } = "";
    /// <summary>SM-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SM-SITE-CODE PIC X(10).</summary>
    [F("siteCode", "X(10)")] public string SiteCode { get; set; } = "";
    /// <summary>SM-EFF-DATE PIC 9(8).</summary>
    [F("effDate", "9(8)", Date = true)] public int EffDate { get; set; }
    /// <summary>SM-QUANTITY PIC S9(13)V9(6).</summary>
    [F("quantity", "S9(13)V9(6)")] public decimal Quantity { get; set; }
    /// <summary>SM-CURRENCY PIC X(3).</summary>
    [F("currency", "X(3)")] public string Currency { get; set; } = "";
    /// <summary>SM-FX-PER-GBP PIC S9(9)V9(8).</summary>
    [F("fxPerGbp", "S9(9)V9(8)")] public decimal FxPerGbp { get; set; }
    /// <summary>SM-NET-AMOUNT PIC S9(13)V9(2).</summary>
    [F("netAmount", "S9(13)V9(2)")] public decimal NetAmount { get; set; }
    /// <summary>SM-NET-AMOUNT-GBP PIC S9(13)V9(2).</summary>
    [F("netAmountGbp", "S9(13)V9(2)")] public decimal NetAmountGbp { get; set; }
    /// <summary>SM-UNIT-PRICE PIC S9(11)V9(6).</summary>
    [F("unitPrice", "S9(11)V9(6)")] public decimal UnitPrice { get; set; }
    /// <summary>SM-PRICE-PER-QTY PIC S9(9)V9(6).</summary>
    [F("pricePerQty", "S9(9)V9(6)")] public decimal PricePerQty { get; set; }
    /// <summary>SM-PRICE-ACCEPTED PIC X.</summary>
    [F("priceAccepted", "X")] public string PriceAccepted { get; set; } = "";
    /// <summary>SM-COST-BASIS PIC X(12).</summary>
    [F("costBasis", "X(12)")] public string CostBasis { get; set; } = "";
    /// <summary>SM-UNIT-COST PIC S9(11)V9(6).</summary>
    [F("unitCost", "S9(11)V9(6)")] public decimal UnitCost { get; set; }
    /// <summary>SM-COST-AMOUNT PIC S9(13)V9(2).</summary>
    [F("costAmount", "S9(13)V9(2)")] public decimal CostAmount { get; set; }
    /// <summary>SM-COST-ADJUSTMENT PIC S9(13)V9(2).</summary>
    [F("costAdjustment", "S9(13)V9(2)")] public decimal CostAdjustment { get; set; }
    /// <summary>SM-COST-BASIS-USED PIC X(12).</summary>
    [F("costBasisUsed", "X(12)")] public string CostBasisUsed { get; set; } = "";
    /// <summary>SM-COST-SOURCE PIC X(20).</summary>
    [F("costSource", "X(20)")] public string CostSource { get; set; } = "";
    /// <summary>SM-COST-FOUND PIC X.</summary>
    [F("costFound", "X")] public string CostFound { get; set; } = "";
    /// <summary>SM-MIN-MARGIN-PCT PIC S9(5)V9(4).</summary>
    [F("minMarginPct", "S9(5)V9(4)")] public decimal MinMarginPct { get; set; }
    /// <summary>SM-MIN-SOURCE PIC X(20).</summary>
    [F("minSource", "X(20)")] public string MinSource { get; set; } = "";
    /// <summary>SM-TARGET-MARGIN PIC S9(5)V9(4).</summary>
    [F("targetMargin", "S9(5)V9(4)")] public decimal TargetMargin { get; set; }
    /// <summary>SM-RAISE-EXCEPTION PIC X.</summary>
    [F("raiseException", "X")] public string RaiseException { get; set; } = "";
    /// <summary>SM-MARGIN-VALUE PIC S9(13)V9(2).</summary>
    [F("marginValue", "S9(13)V9(2)")] public decimal MarginValue { get; set; }
    /// <summary>SM-MARGIN-PCT PIC S9(7)V9(4).</summary>
    [F("marginPct", "S9(7)V9(4)")] public decimal MarginPct { get; set; }
    /// <summary>SM-MARKUP-PCT PIC S9(7)V9(4).</summary>
    [F("markupPct", "S9(7)V9(4)")] public decimal MarkupPct { get; set; }
    /// <summary>SM-MARGIN-STATE PIC X(12).</summary>
    [F("marginState", "X(12)")] public string MarginState { get; set; } = "";
    /// <summary>SM-MARKUP-STATE PIC X(12).</summary>
    [F("markupState", "X(12)")] public string MarkupState { get; set; } = "";
    /// <summary>SM-BELOW-MINIMUM PIC X.</summary>
    [F("belowMinimum", "X")] public string BelowMinimum { get; set; } = "";
    /// <summary>SM-APPROVAL-REQD PIC X.</summary>
    [F("approvalReqd", "X")] public string ApprovalReqd { get; set; } = "";
    /// <summary>SM-EXCEPTION-ID PIC S9(9) COMP-5.</summary>
    [F("exceptionId", "S9(9)", Binary = true)] public int ExceptionId { get; set; }
    /// <summary>SM-REVIEW-REASON PIC X(100).</summary>
    [F("reviewReason", "X(100)")] public string ReviewReason { get; set; } = "";
    /// <summary>SM-PROPOSED-PRICE PIC S9(11)V9(6).</summary>
    [F("proposedPrice", "S9(11)V9(6)")] public decimal ProposedPrice { get; set; }
    /// <summary>SM-PROPOSED-NET PIC S9(13)V9(2).</summary>
    [F("proposedNet", "S9(13)V9(2)")] public decimal ProposedNet { get; set; }
    /// <summary>SM-PROPOSED-MARGIN PIC S9(7)V9(4).</summary>
    [F("proposedMargin", "S9(7)V9(4)")] public decimal ProposedMargin { get; set; }
    /// <summary>SM-PRICE-CHANGED PIC X.</summary>
    [F("priceChanged", "X")] public string PriceChanged { get; set; } = "";
    /// <summary>SM-AMENDMENT-REQD PIC X.</summary>
    [F("amendmentReqd", "X")] public string AmendmentReqd { get; set; } = "";
    /// <summary>SM-EXPLANATION PIC X(200).</summary>
    [F("explanation", "X(200)")] public string Explanation { get; set; } = "";
    /// <summary>SM-USED-CST-MARGIN PIC X.</summary>
    [F("usedCstMargin", "X")] public string UsedCstMargin { get; set; } = "";
}

/// <summary>SOAVAILCA body (cobol/copy/SOAVAILCA.cpy; OpenAPI SoAvailabilityResult).</summary>
public sealed partial class SoavailArea : Commarea
{
    /// <summary>SA-ITEM-CODE PIC X(40).</summary>
    [F("itemCode", "X(40)")] public string ItemCode { get; set; } = "";
    /// <summary>SA-REVISION PIC X(4).</summary>
    [F("revision", "X(4)")] public string Revision { get; set; } = "";
    /// <summary>SA-SITE PIC X(10).</summary>
    [F("site", "X(10)")] public string Site { get; set; } = "";
    /// <summary>SA-WAREHOUSE PIC X(20).</summary>
    [F("warehouse", "X(20)")] public string Warehouse { get; set; } = "";
    /// <summary>SA-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SA-ORDER-NUMBER PIC X(30).</summary>
    [F("orderNumber", "X(30)")] public string OrderNumber { get; set; } = "";
    /// <summary>SA-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lineNum", "S9(9)", Binary = true)] public int LineNum { get; set; }
    /// <summary>SA-SCHEDULE-NO PIC S9(9) COMP-5.</summary>
    [F("scheduleNo", "S9(9)", Binary = true)] public int ScheduleNo { get; set; }
    /// <summary>SA-UOM-CODE PIC X(10).</summary>
    [F("uomCode", "X(10)")] public string UomCode { get; set; } = "";
    /// <summary>SA-STOCK-UOM PIC X(10).</summary>
    [F("stockUom", "X(10)")] public string StockUom { get; set; } = "";
    /// <summary>SA-QTY-REQUIRED PIC S9(13)V9(6).</summary>
    [F("qtyRequired", "S9(13)V9(6)")] public decimal QtyRequired { get; set; }
    /// <summary>SA-REQUIRED-DATE PIC 9(8).</summary>
    [F("requiredDate", "9(8)", Date = true)] public int RequiredDate { get; set; }
    /// <summary>SA-AS-AT PIC 9(8).</summary>
    [F("asAt", "9(8)")] public int AsAt { get; set; }
    /// <summary>SA-HORIZON-DAYS PIC S9(5).</summary>
    [F("horizonDays", "S9(5)")] public int HorizonDays { get; set; }
    /// <summary>SA-WITH-RECEIPTS PIC X.</summary>
    [F("withReceipts", "X")] public string WithReceipts { get; set; } = "";
    /// <summary>SA-WITH-KIT PIC X.</summary>
    [F("withKit", "X")] public string WithKit { get; set; } = "";
    /// <summary>SA-QTY-PHYSICAL PIC S9(13)V9(6).</summary>
    [F("qtyPhysical", "S9(13)V9(6)")] public decimal QtyPhysical { get; set; }
    /// <summary>SA-QTY-ELIGIBLE PIC S9(13)V9(6).</summary>
    [F("qtyEligible", "S9(13)V9(6)")] public decimal QtyEligible { get; set; }
    /// <summary>SA-QTY-RESERVED PIC S9(13)V9(6).</summary>
    [F("qtyReserved", "S9(13)V9(6)")] public decimal QtyReserved { get; set; }
    /// <summary>SA-QTY-FREE PIC S9(13)V9(6).</summary>
    [F("qtyFree", "S9(13)V9(6)")] public decimal QtyFree { get; set; }
    /// <summary>SA-QTY-OWN-DEMAND PIC S9(13)V9(6).</summary>
    [F("qtyOwnDemand", "S9(13)V9(6)")] public decimal QtyOwnDemand { get; set; }
    /// <summary>SA-QTY-EXPECTED PIC S9(13)V9(6).</summary>
    [F("qtyExpected", "S9(13)V9(6)")] public decimal QtyExpected { get; set; }
    /// <summary>SA-QTY-PLANNED PIC S9(13)V9(6).</summary>
    [F("qtyPlanned", "S9(13)V9(6)")] public decimal QtyPlanned { get; set; }
    /// <summary>SA-QTY-KITABLE PIC S9(13)V9(6).</summary>
    [F("qtyKitable", "S9(13)V9(6)")] public decimal QtyKitable { get; set; }
    /// <summary>SA-QTY-QUARANTINE PIC S9(13)V9(6).</summary>
    [F("qtyQuarantine", "S9(13)V9(6)")] public decimal QtyQuarantine { get; set; }
    /// <summary>SA-QTY-EXPIRED PIC S9(13)V9(6).</summary>
    [F("qtyExpired", "S9(13)V9(6)")] public decimal QtyExpired { get; set; }
    /// <summary>SA-QTY-REJECTED PIC S9(13)V9(6).</summary>
    [F("qtyRejected", "S9(13)V9(6)")] public decimal QtyRejected { get; set; }
    /// <summary>SA-QTY-THIRDPARTY PIC S9(13)V9(6).</summary>
    [F("qtyThirdparty", "S9(13)V9(6)")] public decimal QtyThirdparty { get; set; }
    /// <summary>SA-EXPECTED-DATE PIC 9(8).</summary>
    [F("expectedDate", "9(8)", Date = true)] public int ExpectedDate { get; set; }
    /// <summary>SA-QTY-GUARANTEED PIC S9(13)V9(6).</summary>
    [F("qtyGuaranteed", "S9(13)V9(6)")] public decimal QtyGuaranteed { get; set; }
    /// <summary>SA-QTY-PROMISED PIC S9(13)V9(6).</summary>
    [F("qtyPromised", "S9(13)V9(6)")] public decimal QtyPromised { get; set; }
    /// <summary>SA-QTY-SHORTFALL PIC S9(13)V9(6).</summary>
    [F("qtyShortfall", "S9(13)V9(6)")] public decimal QtyShortfall { get; set; }
    /// <summary>SA-PROMISE-DATE PIC 9(8).</summary>
    [F("promiseDate", "9(8)", Date = true)] public int PromiseDate { get; set; }
    /// <summary>SA-PROMISE-BASIS PIC X(12).</summary>
    [F("promiseBasis", "X(12)")] public string PromiseBasis { get; set; } = "";
    /// <summary>SA-PROMISE-FULL PIC X.</summary>
    [F("promiseFull", "X")] public string PromiseFull { get; set; } = "";
    /// <summary>SA-GUARANTEED PIC X.</summary>
    [F("guaranteed", "X")] public string Guaranteed { get; set; } = "";
    /// <summary>SA-DATA-COMPLETE PIC X.</summary>
    [F("dataComplete", "X")] public string DataComplete { get; set; } = "";
    /// <summary>SA-PROMISE-TEXT PIC X(200).</summary>
    [F("promiseText", "X(200)")] public string PromiseText { get; set; } = "";
    /// <summary>SA-ORDER-LINE-CNT PIC 9(3).</summary>
    [F("orderLineCnt", "9(3)")] public int OrderLineCnt { get; set; }
    /// <summary>SA-ORDER-LINES OCCURS 20.</summary>
    [T("orderLines", 20, "orderLineCnt")] public SoavailOrderLines[] OrderLines { get; } = SoCodec.Table<SoavailOrderLines>(20);
    /// <summary>SA-LINE-CNT PIC 9(3).</summary>
    [F("lineCnt", "9(3)")] public int LineCnt { get; set; }
    /// <summary>SA-LINES OCCURS 40.</summary>
    [T("lines", 40, "lineCnt")] public SoavailLines[] Lines { get; } = SoCodec.Table<SoavailLines>(40);
}

/// <summary>One SA-ORDER-LINES OCCURS 20 occurrence.</summary>
public sealed class SoavailOrderLines
{
    /// <summary>SA-O-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("oLineNum", "S9(9)", Binary = true)] public int OLineNum { get; set; }
    /// <summary>SA-O-ITEM PIC X(40).</summary>
    [F("oItem", "X(40)")] public string OItem { get; set; } = "";
    /// <summary>SA-O-REVISION PIC X(4).</summary>
    [F("oRevision", "X(4)")] public string ORevision { get; set; } = "";
    /// <summary>SA-O-SITE PIC X(10).</summary>
    [F("oSite", "X(10)")] public string OSite { get; set; } = "";
    /// <summary>SA-O-REQUIRED PIC S9(13)V9(6).</summary>
    [F("oRequired", "S9(13)V9(6)")] public decimal ORequired { get; set; }
    /// <summary>SA-O-FREE PIC S9(13)V9(6).</summary>
    [F("oFree", "S9(13)V9(6)")] public decimal OFree { get; set; }
    /// <summary>SA-O-RESERVED PIC S9(13)V9(6).</summary>
    [F("oReserved", "S9(13)V9(6)")] public decimal OReserved { get; set; }
    /// <summary>SA-O-EXPECTED PIC S9(13)V9(6).</summary>
    [F("oExpected", "S9(13)V9(6)")] public decimal OExpected { get; set; }
    /// <summary>SA-O-KITABLE PIC S9(13)V9(6).</summary>
    [F("oKitable", "S9(13)V9(6)")] public decimal OKitable { get; set; }
    /// <summary>SA-O-SHORT PIC S9(13)V9(6).</summary>
    [F("oShort", "S9(13)V9(6)")] public decimal OShort { get; set; }
    /// <summary>SA-O-PROMISE PIC 9(8).</summary>
    [F("oPromise", "9(8)")] public int OPromise { get; set; }
    /// <summary>SA-O-GUARANTEED PIC X.</summary>
    [F("oGuaranteed", "X")] public string OGuaranteed { get; set; } = "";
    /// <summary>SA-O-STATUS PIC X(12).</summary>
    [F("oStatus", "X(12)")] public string OStatus { get; set; } = "";
}

/// <summary>One SA-LINES OCCURS 40 occurrence.</summary>
public sealed class SoavailLines
{
    /// <summary>SA-L-SEQ PIC S9(9) COMP-5.</summary>
    [F("lSeq", "S9(9)", Binary = true)] public int LSeq { get; set; }
    /// <summary>SA-L-KIND PIC X(12).</summary>
    [F("lKind", "X(12)")] public string LKind { get; set; } = "";
    /// <summary>SA-L-LABEL PIC X(30).</summary>
    [F("lLabel", "X(30)")] public string LLabel { get; set; } = "";
    /// <summary>SA-L-ITEM PIC X(40).</summary>
    [F("lItem", "X(40)")] public string LItem { get; set; } = "";
    /// <summary>SA-L-SITE PIC X(10).</summary>
    [F("lSite", "X(10)")] public string LSite { get; set; } = "";
    /// <summary>SA-L-WHSE PIC X(20).</summary>
    [F("lWhse", "X(20)")] public string LWhse { get; set; } = "";
    /// <summary>SA-L-QTY PIC S9(13)V9(6).</summary>
    [F("lQty", "S9(13)V9(6)")] public decimal LQty { get; set; }
    /// <summary>SA-L-DATE PIC 9(8).</summary>
    [F("lDate", "9(8)", Date = true)] public int LDate { get; set; }
    /// <summary>SA-L-DOC-TYPE PIC X(20).</summary>
    [F("lDocType", "X(20)")] public string LDocType { get; set; } = "";
    /// <summary>SA-L-DOC-REF PIC X(40).</summary>
    [F("lDocRef", "X(40)")] public string LDocRef { get; set; } = "";
    /// <summary>SA-L-DOC-LINE PIC S9(9) COMP-5.</summary>
    [F("lDocLine", "S9(9)", Binary = true)] public int LDocLine { get; set; }
    /// <summary>SA-L-GUARANTEED PIC X.</summary>
    [F("lGuaranteed", "X")] public string LGuaranteed { get; set; } = "";
    /// <summary>SA-L-TEXT PIC X(100).</summary>
    [F("lText", "X(100)")] public string LText { get; set; } = "";
}

/// <summary>SORESVCA body (cobol/copy/SORESVCA.cpy; OpenAPI SoReservationResult).</summary>
public sealed partial class SoresvArea : Commarea
{
    /// <summary>SV-ORDER-NUMBER PIC X(30).</summary>
    [F("orderNumber", "X(30)")] public string OrderNumber { get; set; } = "";
    /// <summary>SV-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lineNum", "S9(9)", Binary = true)] public int LineNum { get; set; }
    /// <summary>SV-SCHEDULE-NO PIC S9(9) COMP-5.</summary>
    [F("scheduleNo", "S9(9)", Binary = true)] public int ScheduleNo { get; set; }
    /// <summary>SV-ITEM-CODE PIC X(40).</summary>
    [F("itemCode", "X(40)")] public string ItemCode { get; set; } = "";
    /// <summary>SV-REVISION PIC X(4).</summary>
    [F("revision", "X(4)")] public string Revision { get; set; } = "";
    /// <summary>SV-SITE PIC X(10).</summary>
    [F("site", "X(10)")] public string Site { get; set; } = "";
    /// <summary>SV-WAREHOUSE PIC X(20).</summary>
    [F("warehouse", "X(20)")] public string Warehouse { get; set; } = "";
    /// <summary>SV-LOCATION PIC X(20).</summary>
    [F("location", "X(20)")] public string Location { get; set; } = "";
    /// <summary>SV-LOT-CODE PIC X(30).</summary>
    [F("lotCode", "X(30)")] public string LotCode { get; set; } = "";
    /// <summary>SV-SERIAL-NO PIC X(40).</summary>
    [F("serialNo", "X(40)")] public string SerialNo { get; set; } = "";
    /// <summary>SV-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SV-DEMAND-TYPE PIC X(12).</summary>
    [F("demandType", "X(12)")] public string DemandType { get; set; } = "";
    /// <summary>SV-DEMAND-REF PIC X(40).</summary>
    [F("demandRef", "X(40)")] public string DemandRef { get; set; } = "";
    /// <summary>SV-DEMAND-LINE PIC S9(9) COMP-5.</summary>
    [F("demandLine", "S9(9)", Binary = true)] public int DemandLine { get; set; }
    /// <summary>SV-FIRMNESS PIC X(10).</summary>
    [F("firmness", "X(10)")] public string Firmness { get; set; } = "";
    /// <summary>SV-PRIORITY PIC 9(2).</summary>
    [F("priority", "9(2)")] public int Priority { get; set; }
    /// <summary>SV-REQUIRED-DATE PIC 9(8).</summary>
    [F("requiredDate", "9(8)", Date = true)] public int RequiredDate { get; set; }
    /// <summary>SV-QTY-REQUESTED PIC S9(13)V9(6).</summary>
    [F("qtyRequested", "S9(13)V9(6)")] public decimal QtyRequested { get; set; }
    /// <summary>SV-QTY-RESERVED PIC S9(13)V9(6).</summary>
    [F("qtyReserved", "S9(13)V9(6)")] public decimal QtyReserved { get; set; }
    /// <summary>SV-QTY-SHORTFALL PIC S9(13)V9(6).</summary>
    [F("qtyShortfall", "S9(13)V9(6)")] public decimal QtyShortfall { get; set; }
    /// <summary>SV-QTY-CONSUMED PIC S9(13)V9(6).</summary>
    [F("qtyConsumed", "S9(13)V9(6)")] public decimal QtyConsumed { get; set; }
    /// <summary>SV-QTY-FREE-BEFORE PIC S9(13)V9(6).</summary>
    [F("qtyFreeBefore", "S9(13)V9(6)")] public decimal QtyFreeBefore { get; set; }
    /// <summary>SV-QTY-FREE-AFTER PIC S9(13)V9(6).</summary>
    [F("qtyFreeAfter", "S9(13)V9(6)")] public decimal QtyFreeAfter { get; set; }
    /// <summary>SV-ALLOW-PARTIAL PIC X.</summary>
    [F("allowPartial", "X")] public string AllowPartial { get; set; } = "";
    /// <summary>SV-RESERVATION-ID PIC S9(17).</summary>
    [F("reservationId", "S9(17)")] public long ReservationId { get; set; }
    /// <summary>SV-STATUS PIC X(10).</summary>
    [F("status", "X(10)")] public string Status { get; set; } = "";
    /// <summary>SV-INVALID-CNT PIC 9(3).</summary>
    [F("invalidCnt", "9(3)")] public int InvalidCnt { get; set; }
    /// <summary>SV-INVALID-REASON PIC X(200).</summary>
    [F("invalidReason", "X(200)")] public string InvalidReason { get; set; } = "";
    /// <summary>SV-QTY-QUARANTINE PIC S9(13)V9(6).</summary>
    [F("qtyQuarantine", "S9(13)V9(6)")] public decimal QtyQuarantine { get; set; }
    /// <summary>SV-QTY-EXPIRED PIC S9(13)V9(6).</summary>
    [F("qtyExpired", "S9(13)V9(6)")] public decimal QtyExpired { get; set; }
    /// <summary>SV-QTY-REJECTED PIC S9(13)V9(6).</summary>
    [F("qtyRejected", "S9(13)V9(6)")] public decimal QtyRejected { get; set; }
    /// <summary>SV-QTY-THIRDPARTY PIC S9(13)V9(6).</summary>
    [F("qtyThirdparty", "S9(13)V9(6)")] public decimal QtyThirdparty { get; set; }
    /// <summary>SV-QTY-UNSALEABLE PIC S9(13)V9(6).</summary>
    [F("qtyUnsaleable", "S9(13)V9(6)")] public decimal QtyUnsaleable { get; set; }
    /// <summary>SV-UNSALEABLE-TEXT PIC X(200).</summary>
    [F("unsaleableText", "X(200)")] public string UnsaleableText { get; set; } = "";
    /// <summary>SV-DESPATCH-DATE PIC 9(8).</summary>
    [F("despatchDate", "9(8)", Date = true)] public int DespatchDate { get; set; }
    /// <summary>SV-SHELF-DAYS-REQ PIC S9(5).</summary>
    [F("shelfDaysReq", "S9(5)")] public int ShelfDaysReq { get; set; }
    /// <summary>SV-SHELF-TEST-DATE PIC 9(8).</summary>
    [F("shelfTestDate", "9(8)", Date = true)] public int ShelfTestDate { get; set; }
    /// <summary>SV-LOT-EXPIRY PIC 9(8).</summary>
    [F("lotExpiry", "9(8)")] public int LotExpiry { get; set; }
    /// <summary>SV-DAYS-REMAINING PIC S9(7).</summary>
    [F("daysRemaining", "S9(7)")] public int DaysRemaining { get; set; }
    /// <summary>SV-LOT-USABLE PIC X.</summary>
    [F("lotUsable", "X")] public string LotUsable { get; set; } = "";
    /// <summary>SV-EXPIRY-CHECKED PIC X.</summary>
    [F("expiryChecked", "X")] public string ExpiryChecked { get; set; } = "";
    /// <summary>SV-ORDER-STATUS PIC X(14).</summary>
    [F("orderStatus", "X(14)")] public string OrderStatus { get; set; } = "";
    /// <summary>SV-LINE-STATUS PIC X(10).</summary>
    [F("lineStatus", "X(10)")] public string LineStatus { get; set; } = "";
    /// <summary>SV-ACCEPTED-QTY PIC S9(13)V9(6).</summary>
    [F("acceptedQty", "S9(13)V9(6)")] public decimal AcceptedQty { get; set; }
    /// <summary>SV-CANCELLED-QTY PIC S9(13)V9(6).</summary>
    [F("cancelledQty", "S9(13)V9(6)")] public decimal CancelledQty { get; set; }
    /// <summary>SV-DESPATCHED-QTY PIC S9(13)V9(6).</summary>
    [F("despatchedQty", "S9(13)V9(6)")] public decimal DespatchedQty { get; set; }
    /// <summary>SV-OUTSTANDING-QTY PIC S9(13)V9(6).</summary>
    [F("outstandingQty", "S9(13)V9(6)")] public decimal OutstandingQty { get; set; }
    /// <summary>SV-LINE-CNT PIC 9(3).</summary>
    [F("lineCnt", "9(3)")] public int LineCnt { get; set; }
    /// <summary>SV-LINES OCCURS 25.</summary>
    [T("lines", 25, "lineCnt")] public SoresvLines[] Lines { get; } = SoCodec.Table<SoresvLines>(25);
}

/// <summary>One SV-LINES OCCURS 25 occurrence.</summary>
public sealed class SoresvLines
{
    /// <summary>SV-L-RES-ID PIC S9(17).</summary>
    [F("lResId", "S9(17)")] public long LResId { get; set; }
    /// <summary>SV-L-ITEM PIC X(40).</summary>
    [F("lItem", "X(40)")] public string LItem { get; set; } = "";
    /// <summary>SV-L-SITE PIC X(10).</summary>
    [F("lSite", "X(10)")] public string LSite { get; set; } = "";
    /// <summary>SV-L-LOCATION PIC X(20).</summary>
    [F("lLocation", "X(20)")] public string LLocation { get; set; } = "";
    /// <summary>SV-L-LOT PIC X(30).</summary>
    [F("lLot", "X(30)")] public string LLot { get; set; } = "";
    /// <summary>SV-L-DEM-REF PIC X(40).</summary>
    [F("lDemRef", "X(40)")] public string LDemRef { get; set; } = "";
    /// <summary>SV-L-DEM-LINE PIC S9(9) COMP-5.</summary>
    [F("lDemLine", "S9(9)", Binary = true)] public int LDemLine { get; set; }
    /// <summary>SV-L-QTY PIC S9(13)V9(6).</summary>
    [F("lQty", "S9(13)V9(6)")] public decimal LQty { get; set; }
    /// <summary>SV-L-PICKED PIC S9(13)V9(6).</summary>
    [F("lPicked", "S9(13)V9(6)")] public decimal LPicked { get; set; }
    /// <summary>SV-L-CONSUMED PIC S9(13)V9(6).</summary>
    [F("lConsumed", "S9(13)V9(6)")] public decimal LConsumed { get; set; }
    /// <summary>SV-L-STATUS PIC X(10).</summary>
    [F("lStatus", "X(10)")] public string LStatus { get; set; } = "";
    /// <summary>SV-L-REQUIRED PIC 9(8).</summary>
    [F("lRequired", "9(8)")] public int LRequired { get; set; }
}

/// <summary>SOACCEPTCA body (cobol/copy/SOACCEPTCA.cpy; OpenAPI SoAcceptanceResult).</summary>
public sealed partial class SoacceptArea : Commarea
{
    /// <summary>SA-ORDER-NUMBER PIC X(30).</summary>
    [F("orderNumber", "X(30)")] public string OrderNumber { get; set; } = "";
    /// <summary>SA-SALES-ORDER-ID PIC S9(17).</summary>
    [F("salesOrderId", "S9(17)")] public long SalesOrderId { get; set; }
    /// <summary>SA-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>SA-INVOICE-CUST PIC X(20).</summary>
    [F("invoiceCust", "X(20)")] public string InvoiceCust { get; set; } = "";
    /// <summary>SA-ORDER-TYPE PIC X(12).</summary>
    [F("orderType", "X(12)")] public string OrderType { get; set; } = "";
    /// <summary>SA-ORDER-DATE PIC 9(8).</summary>
    [F("orderDate", "9(8)", Date = true)] public int OrderDate { get; set; }
    /// <summary>SA-CURRENCY PIC X(3).</summary>
    [F("currency", "X(3)")] public string Currency { get; set; } = "";
    /// <summary>SA-FX-RATE PIC S9(9)V9(8) COMP-3.</summary>
    [F("fxRate", "S9(9)V9(8)")] public decimal FxRate { get; set; }
    /// <summary>SA-TERMS-CODE PIC X(12).</summary>
    [F("termsCode", "X(12)")] public string TermsCode { get; set; } = "";
    /// <summary>SA-TERMS-SNAPSHOT PIC X(100).</summary>
    [F("termsSnapshot", "X(100)")] public string TermsSnapshot { get; set; } = "";
    /// <summary>SA-SALESPERSON PIC X(20).</summary>
    [F("salesperson", "X(20)")] public string Salesperson { get; set; } = "";
    /// <summary>SA-DELIVERY-REF PIC X(10).</summary>
    [F("deliveryRef", "X(10)")] public string DeliveryRef { get; set; } = "";
    /// <summary>SA-DELIVERY-SNAP PIC X(300).</summary>
    [F("deliverySnap", "X(300)")] public string DeliverySnap { get; set; } = "";
    /// <summary>SA-BILLTO-SNAP PIC X(300).</summary>
    [F("billtoSnap", "X(300)")] public string BilltoSnap { get; set; } = "";
    /// <summary>SA-INCOTERMS-RULE PIC X(3).</summary>
    [F("incotermsRule", "X(3)")] public string IncotermsRule { get; set; } = "";
    /// <summary>SA-INCOTERMS-PLACE PIC X(60).</summary>
    [F("incotermsPlace", "X(60)")] public string IncotermsPlace { get; set; } = "";
    /// <summary>SA-VAT-LOCATION PIC X(3).</summary>
    [F("vatLocation", "X(3)")] public string VatLocation { get; set; } = "";
    /// <summary>SA-INTERNATIONAL PIC X.</summary>
    [F("international", "X")] public string International { get; set; } = "";
    /// <summary>SA-TC-VERSION PIC X(20).</summary>
    [F("tcVersion", "X(20)")] public string TcVersion { get; set; } = "";
    /// <summary>SA-RETENTION-TITLE PIC X.</summary>
    [F("retentionTitle", "X")] public string RetentionTitle { get; set; } = "";
    /// <summary>SA-VAT-BASIS PIC X(20).</summary>
    [F("vatBasis", "X(20)")] public string VatBasis { get; set; } = "";
    /// <summary>SA-CUST-VAT-REG PIC X(15).</summary>
    [F("custVatReg", "X(15)")] public string CustVatReg { get; set; } = "";
    /// <summary>SA-ITEM-AUTHORITY PIC X(40).</summary>
    [F("itemAuthority", "X(40)")] public string ItemAuthority { get; set; } = "";
    /// <summary>SA-NET-AMOUNT PIC S9(11)V99 COMP-3.</summary>
    [F("netAmount", "S9(11)V99")] public decimal NetAmount { get; set; }
    /// <summary>SA-VAT-AMOUNT PIC S9(11)V99 COMP-3.</summary>
    [F("vatAmount", "S9(11)V99")] public decimal VatAmount { get; set; }
    /// <summary>SA-GROSS-AMOUNT PIC S9(11)V99 COMP-3.</summary>
    [F("grossAmount", "S9(11)V99")] public decimal GrossAmount { get; set; }
    /// <summary>SA-GROSS-GBP PIC S9(11)V99 COMP-3.</summary>
    [F("grossGbp", "S9(11)V99")] public decimal GrossGbp { get; set; }
    /// <summary>SA-LINE-COUNT PIC S9(9) COMP-5.</summary>
    [F("lineCount", "S9(9)", Binary = true)] public int LineCount { get; set; }
    /// <summary>SA-COMM-STATUS PIC X(12).</summary>
    [F("commStatus", "X(12)")] public string CommStatus { get; set; } = "";
    /// <summary>SA-PREV-COMM-STATUS PIC X(12).</summary>
    [F("prevCommStatus", "X(12)")] public string PrevCommStatus { get; set; } = "";
    /// <summary>SA-RELEASE-STATUS PIC X(14).</summary>
    [F("releaseStatus", "X(14)")] public string ReleaseStatus { get; set; } = "";
    /// <summary>SA-PREV-REL-STATUS PIC X(14).</summary>
    [F("prevRelStatus", "X(14)")] public string PrevRelStatus { get; set; } = "";
    /// <summary>SA-FULFIL-STATUS PIC X(14).</summary>
    [F("fulfilStatus", "X(14)")] public string FulfilStatus { get; set; } = "";
    /// <summary>SA-BILLING-STATUS PIC X(14).</summary>
    [F("billingStatus", "X(14)")] public string BillingStatus { get; set; } = "";
    /// <summary>SA-HOLD-REASON PIC X(200).</summary>
    [F("holdReason", "X(200)")] public string HoldReason { get; set; } = "";
    /// <summary>SA-CREDIT-DECISION PIC X(10).</summary>
    [F("creditDecision", "X(10)")] public string CreditDecision { get; set; } = "";
    /// <summary>SA-CREDIT-EXPOSURE PIC S9(11)V99 COMP-3.</summary>
    [F("creditExposure", "S9(11)V99")] public decimal CreditExposure { get; set; }
    /// <summary>SA-CREDIT-LIMIT PIC S9(11)V99 COMP-3.</summary>
    [F("creditLimit", "S9(11)V99")] public decimal CreditLimit { get; set; }
    /// <summary>SA-CREDIT-HEADROOM PIC S9(11)V99 COMP-3.</summary>
    [F("creditHeadroom", "S9(11)V99")] public decimal CreditHeadroom { get; set; }
    /// <summary>SA-CREDIT-EXCESS PIC S9(11)V99 COMP-3.</summary>
    [F("creditExcess", "S9(11)V99")] public decimal CreditExcess { get; set; }
    /// <summary>SA-OVERDUE-GBP PIC S9(11)V99 COMP-3.</summary>
    [F("overdueGbp", "S9(11)V99")] public decimal OverdueGbp { get; set; }
    /// <summary>SA-OVERDUE-DAYS PIC S9(5).</summary>
    [F("overdueDays", "S9(5)")] public int OverdueDays { get; set; }
    /// <summary>SA-OVERRIDE-ID PIC S9(9) COMP-5.</summary>
    [F("overrideId", "S9(9)", Binary = true)] public int OverrideId { get; set; }
    /// <summary>SA-OVERRIDE-EXPIRY PIC 9(8).</summary>
    [F("overrideExpiry", "9(8)")] public int OverrideExpiry { get; set; }
    /// <summary>SA-OVERRIDE-ACTIVE PIC X.</summary>
    [F("overrideActive", "X")] public string OverrideActive { get; set; } = "";
    /// <summary>SA-APPROVAL-ID PIC S9(9) COMP-5.</summary>
    [F("approvalId", "S9(9)", Binary = true)] public int ApprovalId { get; set; }
    /// <summary>SA-ACCOUNT-STATUS PIC X(10).</summary>
    [F("accountStatus", "X(10)")] public string AccountStatus { get; set; } = "";
    /// <summary>SA-PRINT-ONLY PIC X.</summary>
    [F("printOnly", "X")] public string PrintOnly { get; set; } = "";
    /// <summary>SA-ACK-PRINT-COUNT PIC S9(9) COMP-5.</summary>
    [F("ackPrintCount", "S9(9)", Binary = true)] public int AckPrintCount { get; set; }
    /// <summary>SA-DEPOSIT-REQ-GBP PIC S9(11)V99 COMP-3.</summary>
    [F("depositReqGbp", "S9(11)V99")] public decimal DepositReqGbp { get; set; }
    /// <summary>SA-DEPOSIT-RCVD-GBP PIC S9(11)V99 COMP-3.</summary>
    [F("depositRcvdGbp", "S9(11)V99")] public decimal DepositRcvdGbp { get; set; }
    /// <summary>SA-DEPOSIT-AMOUNT PIC S9(11)V99 COMP-3.</summary>
    [F("depositAmount", "S9(11)V99")] public decimal DepositAmount { get; set; }
    /// <summary>SA-DEPOSIT-REF PIC X(30).</summary>
    [F("depositRef", "X(30)")] public string DepositRef { get; set; } = "";
    /// <summary>SA-DEPOSIT-METHOD PIC X(10).</summary>
    [F("depositMethod", "X(10)")] public string DepositMethod { get; set; } = "";
    /// <summary>SA-DEPOSIT-VAT-CODE PIC X(4).</summary>
    [F("depositVatCode", "X(4)")] public string DepositVatCode { get; set; } = "";
    /// <summary>SA-DEPOSIT-CLEARED PIC X.</summary>
    [F("depositCleared", "X")] public string DepositCleared { get; set; } = "";
    /// <summary>SA-DEPOSIT-RESIDUAL PIC S9(11)V99 COMP-3.</summary>
    [F("depositResidual", "S9(11)V99")] public decimal DepositResidual { get; set; }
    /// <summary>SA-DEPOSIT-OUTSTAND PIC S9(11)V99 COMP-3.</summary>
    [F("depositOutstand", "S9(11)V99")] public decimal DepositOutstand { get; set; }
    /// <summary>SA-DEPOSIT-ID PIC S9(9) COMP-5.</summary>
    [F("depositId", "S9(9)", Binary = true)] public int DepositId { get; set; }
    /// <summary>SA-BLOCKER-CNT PIC 9(3).</summary>
    [F("blockerCnt", "9(3)")] public int BlockerCnt { get; set; }
    /// <summary>SA-HARD-CNT PIC 9(3).</summary>
    [F("hardCnt", "9(3)")] public int HardCnt { get; set; }
    /// <summary>SA-BLOCKERS OCCURS 30.</summary>
    [T("blockers", 30, "blockerCnt")] public SoacceptBlockers[] Blockers { get; } = SoCodec.Table<SoacceptBlockers>(30);
}

/// <summary>One SA-BLOCKERS OCCURS 30 occurrence.</summary>
public sealed class SoacceptBlockers
{
    /// <summary>SA-B-SEVERITY PIC X(8).</summary>
    [F("bSeverity", "X(8)")] public string BSeverity { get; set; } = "";
    /// <summary>SA-B-REASON PIC X(8).</summary>
    [F("bReason", "X(8)")] public string BReason { get; set; } = "";
    /// <summary>SA-B-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("bLineNum", "S9(9)", Binary = true)] public int BLineNum { get; set; }
    /// <summary>SA-B-FIELD PIC X(40).</summary>
    [F("bField", "X(40)")] public string BField { get; set; } = "";
    /// <summary>SA-B-TEXT PIC X(160).</summary>
    [F("bText", "X(160)")] public string BText { get; set; } = "";
}

/// <summary>DESPATCHCA body (cobol/copy/DESPATCHCA.cpy; OpenAPI SoDespatchResult).</summary>
public sealed partial class DespatchArea : Commarea
{
    /// <summary>DS-DESPATCH-REF PIC X(30).</summary>
    [F("despatchRef", "X(30)")] public string DespatchRef { get; set; } = "";
    /// <summary>DS-DESPATCH-ID PIC S9(17).</summary>
    [F("despatchId", "S9(17)")] public long DespatchId { get; set; }
    /// <summary>DS-ORDER-NUMBER PIC X(30).</summary>
    [F("orderNumber", "X(30)")] public string OrderNumber { get; set; } = "";
    /// <summary>DS-CUSTOMER PIC X(20).</summary>
    [F("customer", "X(20)")] public string Customer { get; set; } = "";
    /// <summary>DS-SITE PIC X(10).</summary>
    [F("site", "X(10)")] public string Site { get; set; } = "";
    /// <summary>DS-PICKLIST-REF PIC X(30).</summary>
    [F("picklistRef", "X(30)")] public string PicklistRef { get; set; } = "";
    /// <summary>DS-DESPATCH-DATE PIC 9(8).</summary>
    [F("despatchDate", "9(8)", Date = true)] public int DespatchDate { get; set; }
    /// <summary>DS-POSTING-DATE PIC 9(8).</summary>
    [F("postingDate", "9(8)", Date = true)] public int PostingDate { get; set; }
    /// <summary>DS-FISCAL-YEAR PIC S9(4).</summary>
    [F("fiscalYear", "S9(4)")] public int FiscalYear { get; set; }
    /// <summary>DS-PERIOD-NO PIC S9(4).</summary>
    [F("periodNo", "S9(4)")] public int PeriodNo { get; set; }
    /// <summary>DS-PERIOD-STATUS PIC X(10).</summary>
    [F("periodStatus", "X(10)")] public string PeriodStatus { get; set; } = "";
    /// <summary>DS-STATUS PIC X(12).</summary>
    [F("status", "X(12)")] public string Status { get; set; } = "";
    /// <summary>DS-INCOTERMS PIC X(3).</summary>
    [F("incoterms", "X(3)")] public string Incoterms { get; set; } = "";
    /// <summary>DS-INCOTERMS-PLACE PIC X(60).</summary>
    [F("incotermsPlace", "X(60)")] public string IncotermsPlace { get; set; } = "";
    /// <summary>DS-CARRIER-CODE PIC X(20).</summary>
    [F("carrierCode", "X(20)")] public string CarrierCode { get; set; } = "";
    /// <summary>DS-CARRIER-REF PIC X(60).</summary>
    [F("carrierRef", "X(60)")] public string CarrierRef { get; set; } = "";
    /// <summary>DS-PACK-COUNT PIC S9(9) COMP-5.</summary>
    [F("packCount", "S9(9)", Binary = true)] public int PackCount { get; set; }
    /// <summary>DS-GROSS-WEIGHT PIC S9(13)V9(6).</summary>
    [F("grossWeight", "S9(13)V9(6)")] public decimal GrossWeight { get; set; }
    /// <summary>DS-IS-EXPORT PIC X.</summary>
    [F("isExport", "X")] public string IsExport { get; set; } = "";
    /// <summary>DS-DEST-COUNTRY PIC X(2).</summary>
    [F("destCountry", "X(2)")] public string DestCountry { get; set; } = "";
    /// <summary>DS-CUSTOMER-EORI PIC X(17).</summary>
    [F("customerEori", "X(17)")] public string CustomerEori { get; set; } = "";
    /// <summary>DS-EXPORT-DECL-REF PIC X(40).</summary>
    [F("exportDeclRef", "X(40)")] public string ExportDeclRef { get; set; } = "";
    /// <summary>DS-ADDRESS-SNAP PIC X(300).</summary>
    [F("addressSnap", "X(300)")] public string AddressSnap { get; set; } = "";
    /// <summary>DS-CONTROL-POINT PIC X(12).</summary>
    [F("controlPoint", "X(12)")] public string ControlPoint { get; set; } = "";
    /// <summary>DS-CONTROL-DATE PIC 9(8).</summary>
    [F("controlDate", "9(8)", Date = true)] public int ControlDate { get; set; }
    /// <summary>DS-CONTROL-BASIS PIC X(100).</summary>
    [F("controlBasis", "X(100)")] public string ControlBasis { get; set; } = "";
    /// <summary>DS-IS-CUTOFF PIC X.</summary>
    [F("isCutoff", "X")] public string IsCutoff { get; set; } = "";
    /// <summary>DS-PERIOD-END-DATE PIC 9(8).</summary>
    [F("periodEndDate", "9(8)", Date = true)] public int PeriodEndDate { get; set; }
    /// <summary>DS-WORKDAYS-LEFT PIC S9(5).</summary>
    [F("workdaysLeft", "S9(5)")] public int WorkdaysLeft { get; set; }
    /// <summary>DS-CREDIT-DECISION PIC X(10).</summary>
    [F("creditDecision", "X(10)")] public string CreditDecision { get; set; } = "";
    /// <summary>DS-CREDIT-EXPOSURE PIC S9(11)V99.</summary>
    [F("creditExposure", "S9(11)V99")] public decimal CreditExposure { get; set; }
    /// <summary>DS-CREDIT-EXCESS PIC S9(11)V99.</summary>
    [F("creditExcess", "S9(11)V99")] public decimal CreditExcess { get; set; }
    /// <summary>DS-CREDIT-TEXT PIC X(200).</summary>
    [F("creditText", "X(200)")] public string CreditText { get; set; } = "";
    /// <summary>DS-CREDIT-OVERRIDE PIC S9(9) COMP-5.</summary>
    [F("creditOverride", "S9(9)", Binary = true)] public int CreditOverride { get; set; }
    /// <summary>DS-COGS-EVENT PIC X(30).</summary>
    [F("cogsEvent", "X(30)")] public string CogsEvent { get; set; } = "";
    /// <summary>DS-COGS-GBP PIC S9(11)V99.</summary>
    [F("cogsGbp", "S9(11)V99")] public decimal CogsGbp { get; set; }
    /// <summary>DS-JOURNAL-ID PIC S9(17).</summary>
    [F("journalId", "S9(17)")] public long JournalId { get; set; }
    /// <summary>DS-JOURNAL-NO PIC X(30).</summary>
    [F("journalNo", "X(30)")] public string JournalNo { get; set; } = "";
    /// <summary>DS-WAS-DUPLICATE PIC X.</summary>
    [F("wasDuplicate", "X")] public string WasDuplicate { get; set; } = "";
    /// <summary>DS-IDEM-OUTCOME PIC X(12).</summary>
    [F("idemOutcome", "X(12)")] public string IdemOutcome { get; set; } = "";
    /// <summary>DS-REVERSAL-OF PIC X(30).</summary>
    [F("reversalOf", "X(30)")] public string ReversalOf { get; set; } = "";
    /// <summary>DS-REVERSE-APPR PIC X(40).</summary>
    [F("reverseAppr", "X(40)")] public string ReverseAppr { get; set; } = "";
    /// <summary>DS-REVERSE-REASON PIC X(200).</summary>
    [F("reverseReason", "X(200)")] public string ReverseReason { get; set; } = "";
    /// <summary>DS-REVERSED-BY PIC X(30).</summary>
    [F("reversedBy", "X(30)")] public string ReversedBy { get; set; } = "";
    /// <summary>DS-LINE-CNT PIC 9(3).</summary>
    [F("lineCnt", "9(3)")] public int LineCnt { get; set; }
    /// <summary>DS-LINES OCCURS 30.</summary>
    [T("lines", 30, "lineCnt")] public DespatchLines[] Lines { get; } = SoCodec.Table<DespatchLines>(30);
}

/// <summary>One DS-LINES OCCURS 30 occurrence.</summary>
public sealed class DespatchLines
{
    /// <summary>DS-L-LINE-NUM PIC S9(9) COMP-5.</summary>
    [F("lLineNum", "S9(9)", Binary = true)] public int LLineNum { get; set; }
    /// <summary>DS-L-ORDER-LINE PIC S9(9) COMP-5.</summary>
    [F("lOrderLine", "S9(9)", Binary = true)] public int LOrderLine { get; set; }
    /// <summary>DS-L-SCHEDULE PIC S9(9) COMP-5.</summary>
    [F("lSchedule", "S9(9)", Binary = true)] public int LSchedule { get; set; }
    /// <summary>DS-L-SOL-ID PIC S9(17).</summary>
    [F("lSolId", "S9(17)")] public long LSolId { get; set; }
    /// <summary>DS-L-ITEM PIC X(40).</summary>
    [F("lItem", "X(40)")] public string LItem { get; set; } = "";
    /// <summary>DS-L-REVISION PIC X(4).</summary>
    [F("lRevision", "X(4)")] public string LRevision { get; set; } = "";
    /// <summary>DS-L-DESC PIC X(60).</summary>
    [F("lDesc", "X(60)")] public string LDesc { get; set; } = "";
    /// <summary>DS-L-PICKED PIC S9(13)V9(6).</summary>
    [F("lPicked", "S9(13)V9(6)")] public decimal LPicked { get; set; }
    /// <summary>DS-L-QTY PIC S9(13)V9(6).</summary>
    [F("lQty", "S9(13)V9(6)")] public decimal LQty { get; set; }
    /// <summary>DS-L-UOM PIC X(10).</summary>
    [F("lUom", "X(10)")] public string LUom { get; set; } = "";
    /// <summary>DS-L-FACTOR PIC S9(9)V9(9).</summary>
    [F("lFactor", "S9(9)V9(9)")] public decimal LFactor { get; set; }
    /// <summary>DS-L-STOCK-QTY PIC S9(13)V9(6).</summary>
    [F("lStockQty", "S9(13)V9(6)")] public decimal LStockQty { get; set; }
    /// <summary>DS-L-WHSE PIC X(20).</summary>
    [F("lWhse", "X(20)")] public string LWhse { get; set; } = "";
    /// <summary>DS-L-LOCATION PIC X(20).</summary>
    [F("lLocation", "X(20)")] public string LLocation { get; set; } = "";
    /// <summary>DS-L-LOT PIC X(30).</summary>
    [F("lLot", "X(30)")] public string LLot { get; set; } = "";
    /// <summary>DS-L-SERIAL PIC X(40).</summary>
    [F("lSerial", "X(40)")] public string LSerial { get; set; } = "";
    /// <summary>DS-L-UNIT-COST PIC S9(11)V9(6).</summary>
    [F("lUnitCost", "S9(11)V9(6)")] public decimal LUnitCost { get; set; }
    /// <summary>DS-L-COGS-GBP PIC S9(11)V99.</summary>
    [F("lCogsGbp", "S9(11)V99")] public decimal LCogsGbp { get; set; }
    /// <summary>DS-L-COST-METH PIC X(4).</summary>
    [F("lCostMeth", "X(4)")] public string LCostMeth { get; set; } = "";
    /// <summary>DS-L-ALLOWED PIC S9(13)V9(6).</summary>
    [F("lAllowed", "S9(13)V9(6)")] public decimal LAllowed { get; set; }
    /// <summary>DS-L-AVAILABLE PIC S9(13)V9(6).</summary>
    [F("lAvailable", "S9(13)V9(6)")] public decimal LAvailable { get; set; }
    /// <summary>DS-L-REMAINING PIC S9(13)V9(6).</summary>
    [F("lRemaining", "S9(13)V9(6)")] public decimal LRemaining { get; set; }
    /// <summary>DS-L-MOVE-ID PIC S9(17).</summary>
    [F("lMoveId", "S9(17)")] public long LMoveId { get; set; }
    /// <summary>DS-L-RES-ID PIC S9(17).</summary>
    [F("lResId", "S9(17)")] public long LResId { get; set; }
    /// <summary>DS-L-KIT-REF PIC X(30).</summary>
    [F("lKitRef", "X(30)")] public string LKitRef { get; set; } = "";
    /// <summary>DS-L-STATUS PIC X(12).</summary>
    [F("lStatus", "X(12)")] public string LStatus { get; set; } = "";
    /// <summary>DS-L-MESSAGE PIC X(100).</summary>
    [F("lMessage", "X(100)")] public string LMessage { get; set; } = "";
}
