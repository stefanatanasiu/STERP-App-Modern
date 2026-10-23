using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-STKMOVE (FR-STERP-ITM-020): the only way physical stock changes - <c>CALL "STKMOVE" USING STKMOVECA</c>.
/// Runs on the caller's <see cref="Sterp.Platform.Kernel.Data.IUnitOfWork"/>: when the caller's connection is in autocommit
/// the service owns one transaction around every leg (BEGN .. COMT/ROLB); when the caller has a transaction open it only
/// sets <c>SAVE TRANSACTION STKMOVE</c>, rolls back to it on error while <c>XACT_STATE() = 1</c> and never commits.
/// </summary>
public interface IStockMovementService
{
    /// <summary>
    /// Executes one STKMOVE function (<see cref="StkmoveFunctions"/>; upper-cased by STKMOVE). The area is updated in place
    /// (outputs and normalised inputs exactly as the COMMAREA after the CALL); the result is the STCAHDR outcome:
    /// RC 00, 06, 08, 12 or 16 - never 04 (BR-STKMOVE-084); <see cref="LegacyResult.DocumentRef"/> = CA-REQ-REF "ITM-015".
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, StkmoveCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of STKMOVE (STKMOVE.cob:91-123).</summary>
public static class StkmoveFunctions
{
    public const string Receipt = "RECEIPT";
    public const string Issue = "ISSUE";
    public const string Return = "RETURN";
    public const string Adjust = "ADJUST";
    public const string Complete = "COMPLETE";
    public const string Opening = "OPENING";
    public const string Scrap = "SCRAP";
    public const string Transfer = "TRANSFER";
    public const string Confirm = "CONFIRM";
    public const string Hold = "HOLD";
    public const string Release = "RELEASE";
}

/// <summary>
/// STKMOVECA body (cobol/copy/STKMOVECA.cpy, prefix SM-; OpenAPI component ItmStkmoveCommarea). Text fields follow PIC X(n)
/// (Windows-1252, right truncation; outputs right-trimmed); dates are PIC 9(8) yyyymmdd with null = 00000000.
/// </summary>
public sealed class StkmoveCommarea
{
    /// <summary>SM-ITEM-CODE X(40) (upper-cased by STKMOVE).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>SM-REVISION X(4); blank = the item's current revision.</summary>
    public string Revision { get; set; } = "";
    /// <summary>SM-SITE X(10); blank = CA-SITE.</summary>
    public string Site { get; set; } = "";
    /// <summary>SM-WAREHOUSE X(20); blank = MAIN.</summary>
    public string Warehouse { get; set; } = "";
    /// <summary>SM-LOCATION X(20) (required).</summary>
    public string Location { get; set; } = "";
    /// <summary>SM-TO-SITE X(10); blank = SM-SITE.</summary>
    public string ToSite { get; set; } = "";
    /// <summary>SM-TO-WAREHOUSE X(20); blank = SM-WAREHOUSE.</summary>
    public string ToWarehouse { get; set; } = "";
    /// <summary>SM-TO-LOCATION X(20).</summary>
    public string ToLocation { get; set; } = "";
    /// <summary>SM-LOT-CODE X(30) (upper-cased).</summary>
    public string LotCode { get; set; } = "";
    /// <summary>SM-SERIAL-NO X(40) (upper-cased).</summary>
    public string SerialNo { get; set; } = "";
    /// <summary>SM-OWNER-TYPE X(10); blank = OWNED.</summary>
    public string OwnerType { get; set; } = "";
    /// <summary>SM-OWNER-CODE X(20).</summary>
    public string OwnerCode { get; set; } = "";
    /// <summary>SM-CONDITION X(12); blank = ACCEPTED (compared case-sensitively).</summary>
    public string Condition { get; set; } = "";
    /// <summary>SM-TO-CONDITION X(12).</summary>
    public string ToCondition { get; set; } = "";
    /// <summary>SM-QUANTITY S9(13)V9(6) in SM-UOM-CODE; negative only for ADJUST.</summary>
    public decimal Quantity { get; set; }
    /// <summary>SM-UOM-CODE X(10); blank = the item stock unit.</summary>
    public string UomCode { get; set; } = "";
    /// <summary>SM-STOCK-QTY S9(13)V9(6) (output: quantity posted in stock units, signed as SM-QUANTITY).</summary>
    public decimal StockQty { get; set; }
    /// <summary>SM-FACTOR S9(9)V9(9) (output: UOMCONV factor).</summary>
    public decimal Factor { get; set; }
    /// <summary>SM-UNIT-COST S9(11)V9(6) per stock unit (input; output = the posted unit cost).</summary>
    public decimal UnitCost { get; set; }
    /// <summary>SM-VALUE-GBP S9(11)V99 (input: document valuation evidence, 0 = none; output: posted value magnitude).</summary>
    public decimal ValueGbp { get; set; }
    /// <summary>SM-MOVEMENT-DATE 9(8); null = CA-BUSINESS-DATE, else the system date (ITMDATE TODAY).</summary>
    public DateOnly? MovementDate { get; set; }
    /// <summary>SM-POSTING-DATE 9(8); null = SM-MOVEMENT-DATE.</summary>
    public DateOnly? PostingDate { get; set; }
    /// <summary>SM-EXPIRY-DATE 9(8) (input for NEWLOT; output = the lot expiry after registration).</summary>
    public DateOnly? ExpiryDate { get; set; }
    /// <summary>SM-REASON-CODE X(20).</summary>
    public string ReasonCode { get; set; } = "";
    /// <summary>SM-REASON X(200).</summary>
    public string Reason { get; set; } = "";
    /// <summary>SM-APPROVAL-REF X(40).</summary>
    public string ApprovalRef { get; set; } = "";
    /// <summary>SM-SOURCE-MODULE X(6); blank = ITM.</summary>
    public string SourceModule { get; set; } = "";
    /// <summary>SM-SOURCE-DOC-TYPE X(20).</summary>
    public string SourceDocType { get; set; } = "";
    /// <summary>SM-SOURCE-DOC-REF X(40).</summary>
    public string SourceDocRef { get; set; } = "";
    /// <summary>SM-SOURCE-DOC-LINE S9(9) COMP-5; 0 = none.</summary>
    public int SourceDocLine { get; set; }
    /// <summary>SM-VIA-TRANSIT X ("Y" = staged transfer through the site transit location; case-sensitive).</summary>
    public string ViaTransit { get; set; } = "";
    /// <summary>SM-HOLD-TYPE X(12) (HOLD input; RELEASE output from the hold).</summary>
    public string HoldType { get; set; } = "";
    /// <summary>SM-HOLD-ID S9(9) COMP-5 (HOLD output; RELEASE input; cleared for every other function).</summary>
    public int HoldId { get; set; }
    /// <summary>SM-HOLD-OUTCOME X(12) (RELEASE: ACCEPT, REJECT, SCRAP; blank = ACCEPT).</summary>
    public string HoldOutcome { get; set; } = "";
    /// <summary>SM-MOVEMENT-ID S9(17) (output: leg 1 movement id; 0 when the procedure returned no row).</summary>
    public long MovementId { get; set; }
    /// <summary>SM-MOVEMENT-ID-2 S9(17) (output: leg 2 movement id).</summary>
    public long MovementId2 { get; set; }
    /// <summary>SM-QTY-BEFORE S9(13)V9(6) (output: leg 1 balance before).</summary>
    public decimal QtyBefore { get; set; }
    /// <summary>SM-QTY-AFTER S9(13)V9(6) (output: balance after the last leg).</summary>
    public decimal QtyAfter { get; set; }
    /// <summary>SM-MOVEMENT-TYPE X(14) (output: movement type of the last posted leg).</summary>
    public string MovementType { get; set; } = "";
    /// <summary>SM-DUPLICATE X ("Y" when the procedure answered a replay of the correlation id).</summary>
    public string Duplicate { get; set; } = "";
    /// <summary>SM-TRANSIT-LOC X(20) (output: transit location of a staged TRANSFER/CONFIRM).</summary>
    public string TransitLoc { get; set; } = "";
    /// <summary>SM-NARRATIVE X(100) (output: first 100 bytes of the message).</summary>
    public string Narrative { get; set; } = "";
}
