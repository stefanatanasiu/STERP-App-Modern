using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-AVAILQ (FR-STERP-ITM-022): stock enquiry and availability - <c>CALL "AVAILQ" USING AVAILQCA</c>. Read-only on the
/// caller's connection; no transaction control.
/// </summary>
public interface IAvailabilityService
{
    /// <summary>
    /// Executes AVAIL, ENQUIRE, BYLOC or LOTS (<see cref="AvailqFunctions"/>; one computation, AVAIL returns no lines; any
    /// other code RC 08 AVL0002). The area is updated in place.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, AvailqCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of AVAILQ (AVAILQ.cob:76-82).</summary>
public static class AvailqFunctions
{
    public const string Avail = "AVAIL";
    public const string Enquire = "ENQUIRE";
    public const string ByLocation = "BYLOC";
    public const string Lots = "LOTS";
}

/// <summary>AVAILQCA body (cobol/copy/AVAILQCA.cpy, prefix AV-; OpenAPI component ItmAvailqCommarea).</summary>
public sealed class AvailqCommarea
{
    /// <summary>AV-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>AV-REVISION X(4); blank = all.</summary>
    public string Revision { get; set; } = "";
    /// <summary>AV-SITE X(10); blank = all.</summary>
    public string Site { get; set; } = "";
    /// <summary>AV-WAREHOUSE X(20); blank = all.</summary>
    public string Warehouse { get; set; } = "";
    /// <summary>AV-LOCATION X(20); blank = all.</summary>
    public string Location { get; set; } = "";
    /// <summary>AV-LOT-CODE X(30); blank = all.</summary>
    public string LotCode { get; set; } = "";
    /// <summary>AV-SERIAL-NO X(40); blank = all.</summary>
    public string SerialNo { get; set; } = "";
    /// <summary>AV-OWNER-TYPE X(10); blank = OWNED (not used as a filter).</summary>
    public string OwnerType { get; set; } = "";
    /// <summary>AV-OWNER-CODE X(20) (not used as a filter).</summary>
    public string OwnerCode { get; set; } = "";
    /// <summary>AV-CONDITION X(12); blank = all quality conditions.</summary>
    public string Condition { get; set; } = "";
    /// <summary>AV-AS-AT 9(8); null = CA-BUSINESS-DATE, else the system date.</summary>
    public DateOnly? AsAt { get; set; }
    /// <summary>AV-DEMAND-TYPE X(12).</summary>
    public string DemandType { get; set; } = "";
    /// <summary>AV-DEMAND-REF X(40) (own-demand reservations are reported separately).</summary>
    public string DemandRef { get; set; } = "";
    /// <summary>AV-DEMAND-LINE S9(9) COMP-5.</summary>
    public int DemandLine { get; set; }
    /// <summary>AV-STOCK-UOM X(10) (output).</summary>
    public string StockUom { get; set; } = "";
    /// <summary>AV-QTY-PHYSICAL S9(13)V9(6) (output).</summary>
    public decimal QtyPhysical { get; set; }
    /// <summary>AV-QTY-ELIGIBLE (output).</summary>
    public decimal QtyEligible { get; set; }
    /// <summary>AV-QTY-RESERVED (output).</summary>
    public decimal QtyReserved { get; set; }
    /// <summary>AV-QTY-FREE (output).</summary>
    public decimal QtyFree { get; set; }
    /// <summary>AV-QTY-EXPECTED (output).</summary>
    public decimal QtyExpected { get; set; }
    /// <summary>AV-QTY-PLANNED (output).</summary>
    public decimal QtyPlanned { get; set; }
    /// <summary>AV-QTY-QUARANTINE (output).</summary>
    public decimal QtyQuarantine { get; set; }
    /// <summary>AV-QTY-INSPECTION (output).</summary>
    public decimal QtyInspection { get; set; }
    /// <summary>AV-QTY-REJECTED (output).</summary>
    public decimal QtyRejected { get; set; }
    /// <summary>AV-QTY-EXPIRED (output).</summary>
    public decimal QtyExpired { get; set; }
    /// <summary>AV-QTY-INTRANSIT (output).</summary>
    public decimal QtyIntransit { get; set; }
    /// <summary>AV-QTY-THIRDPARTY (output).</summary>
    public decimal QtyThirdparty { get; set; }
    /// <summary>AV-QTY-UNAVAIL-LOC (output).</summary>
    public decimal QtyUnavailLoc { get; set; }
    /// <summary>AV-QTY-OWN-DEMAND (output).</summary>
    public decimal QtyOwnDemand { get; set; }
    /// <summary>AV-QTY-FOR-DEMAND (output).</summary>
    public decimal QtyForDemand { get; set; }
    /// <summary>AV-QTY-SAFETY (output).</summary>
    public decimal QtySafety { get; set; }
    /// <summary>AV-QTY-POLICY (output).</summary>
    public decimal QtyPolicy { get; set; }
    /// <summary>AV-EXPECTED-DATE 9(8) (output).</summary>
    public DateOnly? ExpectedDate { get; set; }
    /// <summary>AV-DATA-COMPLETE X ("Y"/"N", output).</summary>
    public string DataComplete { get; set; } = "";
    /// <summary>AV-RESTRICTED X (output).</summary>
    public string Restricted { get; set; } = "";
    /// <summary>AV-LINES OCCURS 40 (ENQUIRE/BYLOC/LOTS output; AV-LINE-CNT = Count).</summary>
    public List<AvailqLine> Lines { get; set; } = [];
}

/// <summary>One AV-LINES entry.</summary>
public sealed class AvailqLine
{
    /// <summary>AV-L-SITE X(10).</summary>
    public string Site { get; set; } = "";
    /// <summary>AV-L-WAREHOUSE X(20).</summary>
    public string Warehouse { get; set; } = "";
    /// <summary>AV-L-LOCATION X(20).</summary>
    public string Location { get; set; } = "";
    /// <summary>AV-L-LOC-TYPE X(12).</summary>
    public string LocType { get; set; } = "";
    /// <summary>AV-L-REVISION X(4).</summary>
    public string Revision { get; set; } = "";
    /// <summary>AV-L-LOT X(30).</summary>
    public string Lot { get; set; } = "";
    /// <summary>AV-L-SERIAL X(40).</summary>
    public string Serial { get; set; } = "";
    /// <summary>AV-L-OWNER-TYPE X(10).</summary>
    public string OwnerType { get; set; } = "";
    /// <summary>AV-L-OWNER X(20).</summary>
    public string Owner { get; set; } = "";
    /// <summary>AV-L-CONDITION X(12).</summary>
    public string Condition { get; set; } = "";
    /// <summary>AV-L-QTY S9(13)V9(6).</summary>
    public decimal Qty { get; set; }
    /// <summary>AV-L-RESERVED S9(13)V9(6).</summary>
    public decimal Reserved { get; set; }
    /// <summary>AV-L-FREE S9(13)V9(6).</summary>
    public decimal Free { get; set; }
    /// <summary>AV-L-EXPIRY 9(8).</summary>
    public DateOnly? Expiry { get; set; }
    /// <summary>AV-L-ELIGIBLE X ("Y"/"N").</summary>
    public string Eligible { get; set; } = "";
    /// <summary>AV-L-WHY X(30) (first applicable reason).</summary>
    public string Why { get; set; } = "";
}
