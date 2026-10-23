using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-LOTSER (FR-STERP-ITM-024): lot, serial and shelf-life control - <c>CALL "LOTSER" USING LOTSERCA</c>. No
/// transaction control: the NEWLOT/NEWSER/SETSTAT writes join the caller's unit of work (or autocommit).
/// </summary>
public interface ILotSerialService
{
    /// <summary>
    /// Executes CHKMOVE, NEWLOT, NEWSER, SETSTAT, EXPCHK or TRACE (<see cref="LotserFunctions"/>; other codes RC 08 LOT0011).
    /// EXPCHK and TRACE are the public functions (SO SORESV, LOTTRACE, online trace); the other four serve STKMOVE.
    /// The area is updated in place.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, LotserCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of LOTSER (LOTSER.cob:62-70).</summary>
public static class LotserFunctions
{
    public const string CheckMove = "CHKMOVE";
    public const string NewLot = "NEWLOT";
    public const string NewSerial = "NEWSER";
    public const string SetStatus = "SETSTAT";
    public const string ExpiryCheck = "EXPCHK";
    public const string Trace = "TRACE";
}

/// <summary>LOTSERCA body (cobol/copy/LOTSERCA.cpy, prefix LS-; OpenAPI component ItmLotserCommarea).</summary>
public sealed class LotserCommarea
{
    /// <summary>LS-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>LS-REVISION X(4).</summary>
    public string Revision { get; set; } = "";
    /// <summary>LS-LOT-CODE X(30).</summary>
    public string LotCode { get; set; } = "";
    /// <summary>LS-SERIAL-NO X(40).</summary>
    public string SerialNo { get; set; } = "";
    /// <summary>LS-SITE X(10).</summary>
    public string Site { get; set; } = "";
    /// <summary>LS-WAREHOUSE X(20).</summary>
    public string Warehouse { get; set; } = "";
    /// <summary>LS-LOCATION X(20).</summary>
    public string Location { get; set; } = "";
    /// <summary>LS-DIRECTION X: "I" increase or "O" decrease (anything but "O" becomes "I").</summary>
    public string Direction { get; set; } = "";
    /// <summary>LS-QUANTITY S9(13)V9(6).</summary>
    public decimal Quantity { get; set; }
    /// <summary>LS-AS-AT 9(8); null = CA-BUSINESS-DATE, else 20261005.</summary>
    public DateOnly? AsAt { get; set; }
    /// <summary>LS-TRACEABILITY X(8) (overridden from the item).</summary>
    public string Traceability { get; set; } = "";
    /// <summary>LS-SHELF-LIFE-DAYS S9(5) (overridden from the item).</summary>
    public int ShelfLifeDays { get; set; }
    /// <summary>LS-MIN-REMAIN-DAYS S9(5) (overridden from the item).</summary>
    public int MinRemainDays { get; set; }
    /// <summary>LS-EXPIRY-DATE 9(8).</summary>
    public DateOnly? ExpiryDate { get; set; }
    /// <summary>LS-MANUFACTURED 9(8).</summary>
    public DateOnly? Manufactured { get; set; }
    /// <summary>LS-RECEIVED-DATE 9(8).</summary>
    public DateOnly? ReceivedDate { get; set; }
    /// <summary>LS-DAYS-REMAINING S9(7) (output).</summary>
    public int DaysRemaining { get; set; }
    /// <summary>LS-SUPPLIER-CODE X(20).</summary>
    public string SupplierCode { get; set; } = "";
    /// <summary>LS-SUPPLIER-LOT X(40).</summary>
    public string SupplierLot { get; set; } = "";
    /// <summary>LS-CERTIFICATE-REF X(60).</summary>
    public string CertificateRef { get; set; } = "";
    /// <summary>LS-ORIGIN-COUNTRY X(2).</summary>
    public string OriginCountry { get; set; } = "";
    /// <summary>LS-CONDITION X(12).</summary>
    public string Condition { get; set; } = "";
    /// <summary>LS-SERIAL-STATUS X(12).</summary>
    public string SerialStatus { get; set; } = "";
    /// <summary>LS-USABLE X ("Y"/"N", output).</summary>
    public string Usable { get; set; } = "";
    /// <summary>LS-CREATED X (output).</summary>
    public string Created { get; set; } = "";
    /// <summary>LS-TRACE OCCURS 25 (TRACE output; LS-TRACE-CNT = Count).</summary>
    public List<LotserTraceLine> Trace { get; set; } = [];
}

/// <summary>One LS-TRACE entry.</summary>
public sealed class LotserTraceLine
{
    /// <summary>LS-T-MOVE-ID S9(17).</summary>
    public long MoveId { get; set; }
    /// <summary>LS-T-TYPE X(14).</summary>
    public string Type { get; set; } = "";
    /// <summary>LS-T-DATE 9(8).</summary>
    public DateOnly? Date { get; set; }
    /// <summary>LS-T-QTY S9(13)V9(6).</summary>
    public decimal Qty { get; set; }
    /// <summary>LS-T-SITE X(10).</summary>
    public string Site { get; set; } = "";
    /// <summary>LS-T-LOCATION X(20).</summary>
    public string Location { get; set; } = "";
    /// <summary>LS-T-DOC-TYPE X(20).</summary>
    public string DocType { get; set; } = "";
    /// <summary>LS-T-DOC-REF X(40).</summary>
    public string DocRef { get; set; } = "";
    /// <summary>LS-T-USER X(20).</summary>
    public string User { get; set; } = "";
}
