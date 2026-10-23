using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-STKRESV (FR-STERP-ITM-021): stock reservation primitives - <c>CALL "STKRESV" USING STKRESVCA</c>. No transaction
/// control in the service; <c>inv.usp_ReserveStock</c> always opens its own (nested) transaction on the caller's connection
/// and on error rolls back the whole open transaction (as legacy).
/// </summary>
public interface IStockReservationService
{
    /// <summary>
    /// Executes RESERVE, RELEASE, CONSUME, REVALID or ENQUIRE (<see cref="StkresvFunctions"/>; compared exactly, never
    /// upper-cased; other values RC 08 RSV0008). The area is updated in place; SR-QTY-CONSUMED is not reset and is echoed
    /// unless CONSUME sets it.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, StkresvCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of STKRESV (STKRESV.cob:52-58).</summary>
public static class StkresvFunctions
{
    public const string Reserve = "RESERVE";
    public const string Release = "RELEASE";
    public const string Consume = "CONSUME";
    public const string Revalidate = "REVALID";
    public const string Enquire = "ENQUIRE";
}

/// <summary>STKRESVCA body (cobol/copy/STKRESVCA.cpy, prefix SR-; OpenAPI component ItmStkresvCommarea).</summary>
public sealed class StkresvCommarea
{
    /// <summary>SR-ITEM-CODE X(40) (upper-cased).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>SR-REVISION X(4).</summary>
    public string Revision { get; set; } = "";
    /// <summary>SR-SITE X(10); blank = NULL to the procedure.</summary>
    public string Site { get; set; } = "";
    /// <summary>SR-WAREHOUSE X(20); blank = NULL.</summary>
    public string Warehouse { get; set; } = "";
    /// <summary>SR-LOCATION X(20); blank = NULL.</summary>
    public string Location { get; set; } = "";
    /// <summary>SR-LOT-CODE X(30).</summary>
    public string LotCode { get; set; } = "";
    /// <summary>SR-SERIAL-NO X(40).</summary>
    public string SerialNo { get; set; } = "";
    /// <summary>SR-DEMAND-TYPE X(12); blank = OTHER.</summary>
    public string DemandType { get; set; } = "";
    /// <summary>SR-DEMAND-REF X(40) (required except ENQUIRE).</summary>
    public string DemandRef { get; set; } = "";
    /// <summary>SR-DEMAND-LINE S9(9) COMP-5; 0 = NULL.</summary>
    public int DemandLine { get; set; }
    /// <summary>SR-FIRMNESS X(10); blank = FIRM.</summary>
    public string Firmness { get; set; } = "";
    /// <summary>SR-PRIORITY 9(2); 0 = 5.</summary>
    public int Priority { get; set; }
    /// <summary>SR-REQUIRED-DATE 9(8); null = NULL.</summary>
    public DateOnly? RequiredDate { get; set; }
    /// <summary>SR-QTY-REQUESTED S9(13)V9(6).</summary>
    public decimal QtyRequested { get; set; }
    /// <summary>SR-QTY-RESERVED S9(13)V9(6) (output).</summary>
    public decimal QtyReserved { get; set; }
    /// <summary>SR-QTY-SHORTFALL S9(13)V9(6) (output).</summary>
    public decimal QtyShortfall { get; set; }
    /// <summary>SR-QTY-FREE-BEFORE S9(13)V9(6) (output).</summary>
    public decimal QtyFreeBefore { get; set; }
    /// <summary>SR-QTY-FREE-AFTER S9(13)V9(6) (output).</summary>
    public decimal QtyFreeAfter { get; set; }
    /// <summary>SR-QTY-CONSUMED S9(13)V9(6) (not reset: echoed unless CONSUME sets it).</summary>
    public decimal QtyConsumed { get; set; }
    /// <summary>SR-ALLOW-PARTIAL X ("Y", upper-case only, allows a partial reservation).</summary>
    public string AllowPartial { get; set; } = "";
    /// <summary>SR-RESERVATION-ID S9(17) (output).</summary>
    public long ReservationId { get; set; }
    /// <summary>SR-INVALID-CNT 9(3) (output).</summary>
    public int InvalidCnt { get; set; }
    /// <summary>SR-STATUS X(10) (output).</summary>
    public string Status { get; set; } = "";
    /// <summary>SR-INVALID-REASON X(200) (output).</summary>
    public string InvalidReason { get; set; } = "";
    /// <summary>SR-LINES OCCURS 25 (ENQUIRE output; SR-LINE-CNT = Count).</summary>
    public List<StkresvLine> Lines { get; set; } = [];
}

/// <summary>One SR-LINES entry.</summary>
public sealed class StkresvLine
{
    /// <summary>SR-L-RES-ID S9(17).</summary>
    public long ResId { get; set; }
    /// <summary>SR-L-ITEM X(40).</summary>
    public string Item { get; set; } = "";
    /// <summary>SR-L-SITE X(10).</summary>
    public string Site { get; set; } = "";
    /// <summary>SR-L-LOCATION X(20).</summary>
    public string Location { get; set; } = "";
    /// <summary>SR-L-LOT X(30).</summary>
    public string Lot { get; set; } = "";
    /// <summary>SR-L-DEM-TYPE X(12).</summary>
    public string DemType { get; set; } = "";
    /// <summary>SR-L-DEM-REF X(40).</summary>
    public string DemRef { get; set; } = "";
    /// <summary>SR-L-DEM-LINE S9(9) COMP-5.</summary>
    public int DemLine { get; set; }
    /// <summary>SR-L-QTY S9(13)V9(6).</summary>
    public decimal Qty { get; set; }
    /// <summary>SR-L-PICKED S9(13)V9(6).</summary>
    public decimal Picked { get; set; }
    /// <summary>SR-L-CONSUMED S9(13)V9(6).</summary>
    public decimal Consumed { get; set; }
    /// <summary>SR-L-STATUS X(10).</summary>
    public string Status { get; set; } = "";
    /// <summary>SR-L-FIRMNESS X(10).</summary>
    public string Firmness { get; set; } = "";
    /// <summary>SR-L-REQUIRED 9(8).</summary>
    public DateOnly? Required { get; set; }
}
