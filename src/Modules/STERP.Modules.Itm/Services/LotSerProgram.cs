using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// LOTSER (cobol/online/itm/LOTSER.cob; FR-STERP-ITM-024): CHKMOVE, NEWLOT, NEWSER, SETSTAT, EXPCHK, TRACE. No transaction
/// control: the MERGE/UPDATE writes join the caller's unit of work (or autocommit).
/// </summary>
internal sealed class LotSerProgram(Sqlio sql, ItmDateProgram itmDate)
{
    private const string Prog = "LOTSER";

    private bool _error;
    private bool _itemRead;
    private bool _lotRead;
    private decimal _stockQty;
    private string _serSite = "";
    private string _serLoc = "";
    private readonly ItdBlock _itd = new();

    // date fields held as PIC 9(8)
    private int _asAt;
    private int _expiry;
    private int _manufactured;
    private int _received;

    /// <summary>LS-EXPIRY-DATE as moved in by STKMOVE when it is not a calendar date (see StkMoveProgram.RawExpiryDate).</summary>
    internal int? RawExpiryDate { get; set; }

    /// <summary>LS-EXPIRY-DATE after the call as PIC 9(8).</summary>
    internal int ExpiryOut => _expiry;

    public async Task RunAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        Initialise(ca, ls);
        Validate(ca, ls);
        if (!_error)
        {
            switch (ca.Function)
            {
                case "CHKMOVE": await CheckMovementAsync(ca, ls, ct); break;
                case "NEWLOT": await NewLotAsync(ca, ls, ct); break;
                case "NEWSER": await NewSerialAsync(ca, ls, ct); break;
                case "SETSTAT": await SetSerialStatusAsync(ca, ls, ct); break;
                case "EXPCHK": await ExpiryCheckAsync(ca, ls, ct); break;
                case "TRACE": await TraceAsync(ca, ls, ct); break;
                default:
                    ca.ReasonCode = "LOT0011";
                    ca.SetMessage("LOTSER does not support function " + Cob.Trim(ca.Function));
                    Reject(ca, ls);
                    break;
            }
        }
        ls.AsAt = Cob.Date(_asAt);
        ls.ExpiryDate = Cob.Date(_expiry);
        ls.Manufactured = Cob.Date(_manufactured);
        ls.ReceivedDate = Cob.Date(_received);
    }

    private void Initialise(CaHeader ca, LotserCommarea ls)
    {
        _error = false;
        _itemRead = false;
        _lotRead = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-011";
        ls.Usable = "N";
        ls.Created = "N";
        ls.Trace.Clear();
        ls.DaysRemaining = 0;
        ls.Traceability = "";
        ls.ItemCode = Cob.Upper(ls.ItemCode, 40);
        ls.LotCode = Cob.Upper(ls.LotCode, 30);
        ls.SerialNo = Cob.Upper(ls.SerialNo, 40);
        ls.Revision = Cob.X(ls.Revision, 4);
        ls.Site = Cob.X(ls.Site, 10);
        ls.Warehouse = Cob.X(ls.Warehouse, 20);
        ls.Location = Cob.X(ls.Location, 20);
        ls.Quantity = Cob.Q(ls.Quantity);
        ls.SupplierCode = Cob.X(ls.SupplierCode, 20);
        ls.SupplierLot = Cob.X(ls.SupplierLot, 40);
        ls.CertificateRef = Cob.X(ls.CertificateRef, 60);
        ls.OriginCountry = Cob.X(ls.OriginCountry, 2);
        ls.Condition = Cob.X(ls.Condition, 12);
        ls.SerialStatus = Cob.X(ls.SerialStatus, 12);
        _expiry = RawExpiryDate ?? Cob.Date8(ls.ExpiryDate);
        _manufactured = Cob.Date8(ls.Manufactured);
        _received = Cob.Date8(ls.ReceivedDate);
        _asAt = Cob.Date8(ls.AsAt);
        if (_asAt == 0) _asAt = ca.BusinessDate;
        if (_asAt == 0) _asAt = 20261005;
        ls.Direction = Cob.X(ls.Direction, 1);
        if (ls.Direction != "O") ls.Direction = "I";
    }

    private void Validate(CaHeader ca, LotserCommarea ls)
    {
        if (Cob.Spaces(ca.Company) || Cob.Spaces(ls.ItemCode))
        {
            ca.ReasonCode = "LOT0006";
            ca.SetMessage("A company and an item are required");
            Reject(ca, ls);
        }
    }

    /// <summary>0300-READ-ITEM: traceability, shelf life and minimum remaining shelf life override the inputs.</summary>
    private async Task ReadItemAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        if (_itemRead) return;
        var r = await sql.SingleAsync(
            "SELECT TraceabilityType, ISNULL(ShelfLifeDays,0), ISNULL(MinRemainingShelfLifeDays,0), QuantumQty FROM inv.Item " +
            "WHERE CompanyCode = @p1 AND ItemCode = @p2",
            [ca.Company, ls.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "LOT0006";
            ca.SetMessage("Item " + Cob.Trim(ls.ItemCode) + " is not held in company " + Cob.Trim(ca.Company));
            ca.ReturnCode = 16;
            _error = true;
            ca.AddMessage();
            return;
        }
        var row = r.Row;
        ls.Traceability = Cob.X(Col.Text(row, 0), 8);
        ls.ShelfLifeDays = (int)CobolNumeric.Store(Col.Num(row, 1), 5, 0);
        ls.MinRemainDays = (int)CobolNumeric.Store(Col.Num(row, 2), 5, 0);
        _itemRead = true;
    }

    /// <summary>0400-READ-LOT with 0450-DAYS-REMAINING.</summary>
    private async Task ReadLotAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        _expiry = 0;
        _manufactured = 0;
        _received = 0;
        ls.Condition = "";
        ls.SupplierLot = "";
        _lotRead = false;
        if (Cob.Spaces(ls.LotCode)) return;
        var r = await sql.SingleAsync(
            "SELECT ISNULL(CONVERT(char(8), ExpiryDate, 112),''), QualityStatus, ISNULL(CONVERT(char(8), ManufacturedDate, 112),''), " +
            "ISNULL(CONVERT(char(8), ReceivedDate, 112),''), ISNULL(SupplierLotRef,''), ISNULL(CertificateRef,''), ISNULL(SupplierCode,''), " +
            "ISNULL(OriginCountry,'') FROM inv.Lot WHERE CompanyCode = @p1 AND ItemCode = @p2 AND LotCode = @p3",
            [ca.Company, ls.ItemCode, ls.LotCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        _lotRead = true;
        var row = r.Row;
        if (!Cob.Spaces(Col.Text(row, 0))) _expiry = (int)Col.Num(row, 0);
        ls.Condition = Cob.X(Col.Text(row, 1), 12);
        if (!Cob.Spaces(Col.Text(row, 2))) _manufactured = (int)Col.Num(row, 2);
        if (!Cob.Spaces(Col.Text(row, 3))) _received = (int)Col.Num(row, 3);
        ls.SupplierLot = Cob.X(Col.Text(row, 4), 40);
        ls.CertificateRef = Cob.X(Col.Text(row, 5), 60);
        ls.SupplierCode = Cob.X(Col.Text(row, 6), 20);
        ls.OriginCountry = Cob.X(Col.Text(row, 7), 2);
        if (_expiry > 0)
        {
            _itd.Func = "DAYSBTWN";
            _itd.Date1 = _asAt;
            _itd.Date2 = _expiry;
            await itmDate.CallAsync(_itd, ct);
            ls.DaysRemaining = _itd.Rc > 4 ? 0 : (int)CobolNumeric.Store(_itd.NumR, 7, 0);
        }
    }

    // ------------------------------------------------------------------ 1000 CHKMOVE

    private async Task CheckMovementAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        await ReadItemAsync(ca, ls, ct);
        if (ca.Failed) return;
        switch (ls.Traceability)
        {
            case "LOT": await CheckLotAsync(ca, ls, ct); break;
            case "SERIAL": await CheckSerialAsync(ca, ls, ct); break;
            default: CheckNone(ca, ls); break;
        }
        if (ca.Failed) return;
        ls.Usable = "Y";
        if (Cob.Spaces(ca.Message))
        {
            ca.SetMessage("Traceability accepted for " + Cob.Trim(ls.ItemCode));
        }
    }

    private async Task CheckLotAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        if (Cob.Spaces(ls.LotCode))
        {
            ca.ReasonCode = "LOT0001";
            ca.SetMessage("Item " + Cob.Trim(ls.ItemCode) + " is lot controlled, so every movement must name its lot");
            Reject(ca, ls);
            return;
        }
        await ReadLotAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (!_lotRead)
        {
            if (ls.Direction == "O")
            {
                ca.ReasonCode = "LOT0006";
                ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " is not recorded for item " + Cob.Trim(ls.ItemCode));
                Reject(ca, ls);
            }
            return;
        }
        ShelfLife(ca, ls);
        if (ca.Failed) return;
        if (ls.Direction == "O" && ls.Condition != "ACCEPTED")
        {
            ca.ReasonCode = "LOT0010";
            ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " is in condition " + Cob.Trim(ls.Condition) + " and may not be issued without a release");
            ca.ReturnCode = 6;
            _error = true;
            ca.AddMessage();
        }
    }

    /// <summary>1150-SHELF-LIFE.</summary>
    private void ShelfLife(CaHeader ca, LotserCommarea ls)
    {
        if (ls.ShelfLifeDays <= 0) return;
        if (_expiry == 0)
        {
            ca.ReasonCode = "LOT0007";
            ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " has no expiry date and item " + Cob.Trim(ls.ItemCode) + " is expiry controlled");
            Reject(ca, ls);
            return;
        }
        if (ls.Direction != "O") return;
        if (_expiry < _asAt)
        {
            ca.ReasonCode = "LOT0008";
            var d = Cob.D8(_expiry);
            ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " expired on " + d[6..8] + "/" + d[4..6] + "/" + d[..4] + " and is not eligible stock");
            Reject(ca, ls);
            return;
        }
        if (ls.MinRemainDays > 0 && ls.DaysRemaining < ls.MinRemainDays)
        {
            ca.ReasonCode = "LOT0009";
            ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " has only " + Cob.IntEd(ls.DaysRemaining) + " day(s) of shelf life left");
            ca.ReturnCode = 6;
            _error = true;
            ca.AddMessage();
        }
    }

    private async Task CheckSerialAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        if (Cob.Spaces(ls.SerialNo))
        {
            ca.ReasonCode = "LOT0002";
            ca.SetMessage("Item " + Cob.Trim(ls.ItemCode) + " is serial controlled, so every movement must name its serial number");
            Reject(ca, ls);
            return;
        }
        if (ls.Quantity != 1 && ls.Quantity != 0)
        {
            ca.ReasonCode = "LOT0003";
            ca.SetMessage("A serialised unit moves one at a time; " + Cob.QtyEd(ls.Quantity) + " was entered");
            Reject(ca, ls);
            return;
        }
        await SerialInStockAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (ls.Direction == "I" && _stockQty > 0)
        {
            ca.ReasonCode = "LOT0004";
            ca.SetMessage("Serial " + Cob.Trim(ls.SerialNo) + " of item " + Cob.Trim(ls.ItemCode) + " is already in stock at " +
                Cob.Trim(_serSite) + " " + Cob.Trim(_serLoc));
            Reject(ca, ls);
            return;
        }
        if (ls.Direction == "O" && _stockQty <= 0)
        {
            ca.ReasonCode = "LOT0005";
            ca.SetMessage("Serial " + Cob.Trim(ls.SerialNo) + " of item " + Cob.Trim(ls.ItemCode) + " is not in stock, so it cannot be issued");
            Reject(ca, ls);
            return;
        }
        if (!Cob.Spaces(ls.LotCode))
        {
            await ReadLotAsync(ca, ls, ct);
            ShelfLife(ca, ls);
        }
    }

    /// <summary>1250-SERIAL-IN-STOCK.</summary>
    private async Task SerialInStockAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        _stockQty = 0;
        _serSite = "";
        _serLoc = "";
        var r = await sql.SingleAsync(
            "SELECT TOP 1 SUM(QtyOnHand) OVER (), SiteCode, LocationCode FROM inv.StockBalance WHERE CompanyCode = @p1 AND ItemCode = @p2 " +
            "AND SerialNo = @p3 AND QtyOnHand > 0",
            [ca.Company, ls.ItemCode, ls.SerialNo], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        _stockQty = Cob.Q(Col.Num(r.Row, 0));
        _serSite = Cob.X(Col.Text(r.Row, 1), 10);
        _serLoc = Cob.X(Col.Text(r.Row, 2), 20);
    }

    private static void CheckNone(CaHeader ca, LotserCommarea ls)
    {
        if (!Cob.Spaces(ls.LotCode) || !Cob.Spaces(ls.SerialNo))
        {
            ca.ReasonCode = "LOT0012";
            ca.SetMessage("Item " + Cob.Trim(ls.ItemCode) + " is not traceability controlled; the lot or serial supplied will be recorded but not enforced");
            ca.ReturnCode = 4;
            ca.AddMessage();
        }
    }

    // ------------------------------------------------------------------ 2000 NEWLOT

    private async Task NewLotAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        await ReadItemAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (Cob.Spaces(ls.LotCode))
        {
            ca.ReasonCode = "LOT0001";
            ca.SetMessage("A lot reference is required");
            Reject(ca, ls);
            return;
        }
        if (_received == 0) _received = _asAt;
        if (_expiry == 0 && ls.ShelfLifeDays > 0)
        {
            _itd.Func = "ADDDAYS";
            _itd.Date1 = _manufactured > 0 ? _manufactured : _received;
            _itd.Number = ls.ShelfLifeDays;
            await itmDate.CallAsync(_itd, ct);
            if (_itd.Rc < 8) _expiry = _itd.DateR;
        }
        if (Cob.Spaces(ls.Condition)) ls.Condition = "ACCEPTED";
        var exp = _expiry == 0 ? "19000101" : Cob.D8(_expiry);
        var r = await sql.ExecAsync(
            "MERGE inv.Lot AS t USING (SELECT @p1 AS C, @p2 AS I, @p3 AS L) AS s ON t.CompanyCode = s.C AND t.ItemCode = s.I AND " +
            "t.LotCode = s.L WHEN MATCHED THEN UPDATE SET ExpiryDate = NULLIF(CONVERT(date, @p4, 112),'19000101'), QualityStatus = @p5, " +
            "SupplierLotRef = @p6, SupplierCode = @p7, CertificateRef = @p8, ModifiedBy = @p9, ModifiedAt = SYSDATETIME() WHEN NOT MATCHED " +
            "THEN INSERT (CompanyCode, ItemCode, LotCode, Revision, SupplierLotRef, SupplierCode, ReceivedDate, ExpiryDate, QualityStatus, " +
            "CertificateRef, OriginCountry, CreatedBy) VALUES (s.C, s.I, s.L, NULLIF(@p10,''), @p11, @p12, CONVERT(date, @p13, 112), " +
            "NULLIF(CONVERT(date, @p14, 112),'19000101'), @p15, @p16, NULLIF(@p17,''), @p18);",
            [ca.Company, ls.ItemCode, ls.LotCode, exp, ls.Condition, ls.SupplierLot, ls.SupplierCode, ls.CertificateRef, ca.User,
             ls.Revision, ls.SupplierLot, ls.SupplierCode, Cob.D8(_received), exp, ls.Condition, ls.CertificateRef, ls.OriginCountry, ca.User], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        ls.Created = "Y";
        ls.Usable = "Y";
        ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " recorded for " + Cob.Trim(ls.ItemCode));
    }

    // ------------------------------------------------------------------ 3000 NEWSER

    private async Task NewSerialAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        await ReadItemAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (Cob.Spaces(ls.SerialNo))
        {
            ca.ReasonCode = "LOT0002";
            ca.SetMessage("A serial number is required");
            Reject(ca, ls);
            return;
        }
        ls.Direction = "I";
        await SerialInStockAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (_stockQty > 0)
        {
            ca.ReasonCode = "LOT0004";
            ca.SetMessage("Serial " + Cob.Trim(ls.SerialNo) + " already represents a unit in stock");
            Reject(ca, ls);
            return;
        }
        if (Cob.Spaces(ls.SerialStatus)) ls.SerialStatus = "INSTOCK";
        var r = await sql.ExecAsync(
            "MERGE inv.Serial AS t USING (SELECT @p1 AS C, @p2 AS I, @p3 AS S) AS s ON t.CompanyCode = s.C AND t.ItemCode = s.I AND " +
            "t.SerialNo = s.S WHEN MATCHED THEN UPDATE SET Status = @p4, CurrentSiteCode = @p5, CurrentLocationCode = @p6, " +
            "LotCode = NULLIF(@p7,''), ModifiedBy = @p8, ModifiedAt = SYSDATETIME() WHEN NOT MATCHED THEN INSERT (CompanyCode, ItemCode, " +
            "SerialNo, LotCode, Revision, Status, CurrentSiteCode, CurrentLocationCode, QualityStatus, CreatedBy) VALUES (s.C, s.I, s.S, " +
            "NULLIF(@p9,''), NULLIF(@p10,''), @p11, @p12, @p13, 'ACCEPTED', @p14);",
            [ca.Company, ls.ItemCode, ls.SerialNo, ls.SerialStatus, ls.Site, ls.Location, ls.LotCode, ca.User, ls.LotCode, ls.Revision,
             ls.SerialStatus, ls.Site, ls.Location, ca.User], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        ls.Created = "Y";
        ls.Usable = "Y";
        ca.SetMessage("Serial " + Cob.Trim(ls.SerialNo) + " registered for " + Cob.Trim(ls.ItemCode));
    }

    // ------------------------------------------------------------------ 4000 SETSTAT

    private async Task SetSerialStatusAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        if (Cob.Spaces(ls.SerialNo))
        {
            ca.ReasonCode = "LOT0002";
            ca.SetMessage("A serial number is required");
            Reject(ca, ls);
            return;
        }
        if (Cob.Spaces(ls.SerialStatus)) ls.SerialStatus = "INSTOCK";
        var r = await sql.ExecAsync(
            "UPDATE inv.Serial SET Status = @p1, CurrentSiteCode = NULLIF(@p2,''), CurrentLocationCode = NULLIF(@p3,''), ModifiedBy = @p4, " +
            "ModifiedAt = SYSDATETIME() WHERE CompanyCode = @p5 AND ItemCode = @p6 AND SerialNo = @p7",
            [ls.SerialStatus, ls.Site, ls.Location, ca.User, ca.Company, ls.ItemCode, ls.SerialNo], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        ls.Usable = "Y";
        ca.SetMessage("Serial " + Cob.Trim(ls.SerialNo) + " is now " + Cob.Trim(ls.SerialStatus));
    }

    // ------------------------------------------------------------------ 5000 EXPCHK

    private async Task ExpiryCheckAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        await ReadItemAsync(ca, ls, ct);
        if (ca.Failed) return;
        await ReadLotAsync(ca, ls, ct);
        if (ca.Failed) return;
        if (!_lotRead)
        {
            ca.ReasonCode = "LOT0006";
            ca.SetMessage("Lot " + Cob.Trim(ls.LotCode) + " is not recorded");
            ca.ReturnCode = 16;
            ca.AddMessage();
            return;
        }
        ls.Direction = "O";
        ShelfLife(ca, ls);
        if (!ca.Failed)
        {
            ls.Usable = "Y";
            ca.SetMessage(_expiry == 0
                ? "Lot " + Cob.Trim(ls.LotCode) + " has no expiry policy"
                : "Lot " + Cob.Trim(ls.LotCode) + " has " + Cob.IntEd(ls.DaysRemaining) + " day(s) of shelf life remaining");
        }
    }

    // ------------------------------------------------------------------ 6000 TRACE

    private async Task TraceAsync(CaHeader ca, LotserCommarea ls, CancellationToken ct)
    {
        var r = await sql.QueryAsync(
            "SELECT MovementId, MovementType, CONVERT(char(8), MovementDate, 112), Quantity, SiteCode, LocationCode, " +
            "ISNULL(SourceDocType,''), ISNULL(SourceDocRef,''), ISNULL(CreatedBy,'') FROM inv.StockMovement WHERE CompanyCode = @p1 AND " +
            "ItemCode = @p2 AND ((@p3 <> '' AND LotCode = @p4) OR (@p5 <> '' AND SerialNo = @p6)) ORDER BY MovementId",
            [ca.Company, ls.ItemCode, ls.LotCode, ls.LotCode, ls.SerialNo, ls.SerialNo], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        ls.Trace.Clear();
        foreach (var row in r.Rows!)
        {
            if (ls.Trace.Count >= 25) continue;
            ls.Trace.Add(new LotserTraceLine
            {
                MoveId = (long)CobolNumeric.Store(Col.Num(row, 0), 17, 0),
                Type = Cob.X(Col.Text(row, 1), 14),
                Date = Cob.Date((int)Col.Num(row, 2)),
                Qty = Cob.Q(Col.Num(row, 3)),
                Site = Cob.X(Col.Text(row, 4), 10),
                Location = Cob.X(Col.Text(row, 5), 20),
                DocType = Cob.X(Col.Text(row, 6), 20),
                DocRef = Cob.X(Col.Text(row, 7), 40),
                User = Cob.X(Col.Text(row, 8), 20),
            });
        }
        if (ls.Trace.Count == 0)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "LOT0006";
            ca.SetMessage("No movements are recorded for that identity");
        }
        else
        {
            ca.SetMessage(Cob.IntEd(ls.Trace.Count) + " traced movement(s)");
        }
    }

    private void Reject(CaHeader ca, LotserCommarea ls)
    {
        _error = true;
        ls.Usable = "N";
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    private void SqlFailed(CaHeader ca, SqlioOutcome r)
    {
        _error = true;
        ca.SetMessage(r.Message200(Prog));
        ca.ReasonCode = "SQL0001";
        ca.ReturnCode = 12;
        ca.AddMessage();
    }
}
