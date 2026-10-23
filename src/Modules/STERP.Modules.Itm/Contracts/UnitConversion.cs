using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-UOMCONV (FR-STERP-ITM-023): unit of measure conversion - <c>CALL "UOMCONV" USING UOMCONVCA</c>. Read-only
/// (<c>inv.Item</c>, <c>inv.ItemUom</c>, <c>inv.Uom</c>) on the caller's connection; RC 00, 08, 12, 16 only.
/// </summary>
public interface IUnitConversionService
{
    /// <summary>
    /// Executes TOSTOCK or FACTOR (<see cref="UomconvFunctions"/>; compared exactly). The test-harness-only functions
    /// FROMSTK, CONVERT, CHECKQTY, UNITCOST and ITEMUOM are deferred (D-ITM-04) and throw <see cref="NotSupportedException"/>;
    /// any other code is RC 08 UOM0008. The area is updated in place.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, UomconvCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of UOMCONV in production scope (UOMCONV.cob:70-79).</summary>
public static class UomconvFunctions
{
    public const string ToStock = "TOSTOCK";
    public const string Factor = "FACTOR";
}

/// <summary>UOMCONVCA body (cobol/copy/UOMCONVCA.cpy, prefix UC-; OpenAPI component ItmUomconvCommarea).</summary>
public sealed class UomconvCommarea
{
    /// <summary>UC-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>UC-FROM-UOM X(10).</summary>
    public string FromUom { get; set; } = "";
    /// <summary>UC-TO-UOM X(10).</summary>
    public string ToUom { get; set; } = "";
    /// <summary>UC-USAGE-TYPE X(10): STOCK PURCHASE SALES ISSUE PRICE PACK; blank = PURCHASE.</summary>
    public string UsageType { get; set; } = "";
    /// <summary>UC-EFF-DATE 9(8); null = CA-BUSINESS-DATE, else 20260401.</summary>
    public DateOnly? EffDate { get; set; }
    /// <summary>UC-ENTERED-QTY S9(13)V9(6).</summary>
    public decimal EnteredQty { get; set; }
    /// <summary>UC-STOCK-QTY S9(13)V9(6) (TOSTOCK output).</summary>
    public decimal StockQty { get; set; }
    /// <summary>UC-RESULT-QTY S9(13)V9(6) (output).</summary>
    public decimal ResultQty { get; set; }
    /// <summary>UC-FACTOR S9(9)V9(9) (output: stock units per UC-FROM-UOM).</summary>
    public decimal Factor { get; set; }
    /// <summary>UC-INVERSE-FACTOR S9(9)V9(9) (output).</summary>
    public decimal InverseFactor { get; set; }
    /// <summary>UC-STOCK-UOM X(10) (output).</summary>
    public string StockUom { get; set; } = "";
    /// <summary>UC-FROM-DIM X(10) (output).</summary>
    public string FromDim { get; set; } = "";
    /// <summary>UC-TO-DIM X(10) (output).</summary>
    public string ToDim { get; set; } = "";
    /// <summary>UC-DECIMALS 9(2) (output).</summary>
    public int Decimals { get; set; }
    /// <summary>UC-QUANTUM S9(13)V9(6) (output).</summary>
    public decimal Quantum { get; set; }
    /// <summary>UC-INDIVISIBLE X (output).</summary>
    public string Indivisible { get; set; } = "";
    /// <summary>UC-TRACEABILITY X(8) (output).</summary>
    public string Traceability { get; set; } = "";
    /// <summary>UC-PACK-DESC X(40) (output).</summary>
    public string PackDesc { get; set; } = "";
    /// <summary>UC-EFF-FROM 9(8) (output).</summary>
    public DateOnly? EffFrom { get; set; }
    /// <summary>UC-EFF-TO 9(8) (output).</summary>
    public DateOnly? EffTo { get; set; }
    /// <summary>UC-SOURCE X(10): ITEMUOM, UOM or IDENTITY (output).</summary>
    public string Source { get; set; } = "";
    /// <summary>UC-FOREIGN-PRICE S9(11)V9(6) (UNITCOST, deferred).</summary>
    public decimal ForeignPrice { get; set; }
    /// <summary>UC-PRICE-PER-QTY S9(9)V9(6) (UNITCOST, deferred).</summary>
    public decimal PricePerQty { get; set; }
    /// <summary>UC-PRICE-UOM X(10) (UNITCOST, deferred).</summary>
    public string PriceUom { get; set; } = "";
    /// <summary>UC-FX-PER-GBP S9(9)V9(8) (UNITCOST, deferred).</summary>
    public decimal FxPerGbp { get; set; }
    /// <summary>UC-UNIT-COST-GBP S9(11)V9(6) (UNITCOST, deferred).</summary>
    public decimal UnitCostGbp { get; set; }
    /// <summary>UC-ROUNDED-QTY S9(13)V9(6) (TOSTOCK quantum refusal: rounded-up suggestion).</summary>
    public decimal RoundedQty { get; set; }
    /// <summary>UC-ADJUSTED X (output).</summary>
    public string Adjusted { get; set; } = "";
    /// <summary>UC-LINES OCCURS 20 (ITEMUOM, deferred).</summary>
    public List<UomconvLine> Lines { get; set; } = [];
}

/// <summary>One UC-LINES entry.</summary>
public sealed class UomconvLine
{
    public string Uom { get; set; } = "";
    public string Usage { get; set; } = "";
    public decimal Factor { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Pack { get; set; } = "";
}
