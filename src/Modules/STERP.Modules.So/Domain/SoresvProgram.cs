using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SORESV (cobol/online/so/SORESV.cob, FR-SORESV-001..007): reserve stock for a sales order line (FR-STERP-SO-016). RESERVE
/// is implemented; RELEASE, CONSUME, REVALID and ENQUIRE have no production caller (D-SO-01, SME-015). SORESV opens no
/// transaction: the reservation write is atomic inside inv.usp_ReserveStock (IC-ITM-STKRESV).
/// </summary>
public sealed class SoresvProgram(
    ISqlRunner sql, IAvailabilityService availq, ILotSerialService lotser, IStockReservationService stkresv, IBusinessCalendar calendar)
{
    private const string OrderLineSql =
        "SELECT l.Status, ISNULL(l.ItemCode,''), " +
        "ISNULL(l.Revision,''), " +
        "CONVERT(varchar(30), l.AcceptedQty), " +
        "CONVERT(varchar(30), l.CancelledQty), " +
        "CONVERT(varchar(30), l.DespatchedQty), " +
        "o.ReleaseStatus, ISNULL(o.SiteCode,''), " +
        "o.CustomerCode " +
        "FROM so.SalesOrderLine l JOIN so.SalesOrder o " +
        "ON o.SalesOrderId = l.SalesOrderId " +
        "WHERE l.CompanyCode = ? AND l.OrderNumber = ? " +
        "AND l.LineNum = ?";

    private static readonly int[] Widths = Enumerable.Repeat(100, 12).ToArray();

    private const string DemandSales = "SALESORDER";

    private sealed class State
    {
        public bool Bad;
        public string Text = "";
    }

    public async Task RunAsync(SoresvArea sv, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sv);
        Validate(sv, s);
        if (s.Bad) return;
        switch (sv.Header.Function)
        {
            case "RESERVE":
                await ReserveAsync(sv, ct);
                break;
            case "RELEASE":
            case "CONSUME":
            case "REVALID":
            case "ENQUIRE":
                throw new NotSupportedException($"SORESV {sv.Header.Function} has no production caller (D-SO-01, SME-015).");
            default:
                sv.Header.ReturnCode = 8;
                sv.Header.ReasonCode = "SRV0008";
                sv.Header.Message = Cob.Str(200, "SORESV does not support the function ", Cob.Trim(sv.Header.Function));
                break;
        }
    }

    /// <summary>1100-INITIALISE: outputs cleared; the demand identity is fixed by SO-009.</summary>
    private static void Initialise(SoresvArea sv)
    {
        var h = sv.Header;
        h.Program = "SORESV";
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-009";
        sv.ItemCode = Cob.Upper(sv.ItemCode, 40);
        sv.QtyReserved = 0; sv.QtyShortfall = 0;
        sv.QtyConsumed = 0;
        sv.QtyFreeBefore = 0; sv.QtyFreeAfter = 0;
        sv.QtyQuarantine = 0; sv.QtyExpired = 0;
        sv.QtyRejected = 0; sv.QtyThirdparty = 0;
        sv.QtyUnsaleable = 0;
        sv.ReservationId = 0; sv.InvalidCnt = 0; sv.LineCnt = 0;
        sv.LotExpiry = 0; sv.DaysRemaining = 0;
        sv.ShelfTestDate = 0;
        sv.AcceptedQty = 0; sv.CancelledQty = 0;
        sv.DespatchedQty = 0; sv.OutstandingQty = 0;
        sv.Status = ""; sv.InvalidReason = "";
        sv.UnsaleableText = "";
        sv.OrderStatus = ""; sv.LineStatus = "";
        sv.LotUsable = "N";
        sv.ExpiryChecked = "N";
        sv.DemandType = DemandSales;
        if (Cob.Spaces(sv.DemandRef)) sv.DemandRef = Cob.X(sv.OrderNumber, 40);
        if (sv.DemandLine == 0) sv.DemandLine = sv.LineNum;
        if (Cob.Spaces(sv.Firmness)) sv.Firmness = "FIRM";
        if (sv.Priority == 0) sv.Priority = 5;
        if (sv.DespatchDate == 0) sv.DespatchDate = sv.RequiredDate == 0 ? h.BusinessDate : sv.RequiredDate;
        if (sv.RequiredDate == 0) sv.RequiredDate = sv.DespatchDate;
        if (sv.ShelfDaysReq < 0) sv.ShelfDaysReq = 0;
    }

    /// <summary>1200-VALIDATE.</summary>
    private static void Validate(SoresvArea sv, State s)
    {
        if (Cob.Spaces(sv.Header.Company) || Cob.Spaces(sv.ItemCode))
        {
            sv.Header.ReasonCode = "SRV0001";
            s.Text = Cob.Str(200, "A company code and an item code are required ", "to reserve sales demand.");
            Reject(sv, s);
            return;
        }
        if (sv.Header.Function == "ENQUIRE") return;
        if (Cob.Spaces(sv.DemandRef))
        {
            sv.Header.ReasonCode = "SRV0001";
            s.Text = Cob.Str(200, "A reservation must name the sales order it ", "is held for.");
            Reject(sv, s);
            return;
        }
        if (sv.Header.Function is "RESERVE" or "CONSUME" && sv.QtyRequested <= 0)
        {
            sv.Header.ReasonCode = "SRV0002";
            s.Text = "A quantity greater than zero is required.";
            Reject(sv, s);
        }
    }

    /// <summary>2000-RESERVE: order line, saleability, shelf life, then STKRESV, then the result sentence.</summary>
    private async Task ReserveAsync(SoresvArea sv, CancellationToken ct)
    {
        await ReadOrderLineAsync(sv, ct);
        if (sv.Header.Failed) return;
        await SaleabilityAsync(sv, ct);
        if (sv.Header.Failed) return;
        await ShelfLifeAsync(sv, ct);
        if (sv.Header.Failed) return;
        await CallStkresvAsync(sv, StkresvFunctions.Reserve, ct);
        if (sv.Header.Failed) return;
        ReserveResult(sv);
    }

    /// <summary>2100-READ-ORDER-LINE: CANCELLED/CLOSED refuse (SRV0005); an unknown line only warns (SRV0004).</summary>
    private async Task ReadOrderLineAsync(SoresvArea sv, CancellationToken ct)
    {
        if (Cob.Spaces(sv.OrderNumber)) return;
        var h = sv.Header;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(OrderLineSql,
            [
                SqlioParm.Text(h.Company), SqlioParm.Text(sv.OrderNumber), SqlioParm.Text(CobolNumeric.SqlioInteger(sv.LineNum).Trim()),
            ], Widths, ct);
        }
        catch (SqlioException ex)
        {
            h.ReturnCode = 12;
            h.ReasonCode = "SQL0001";
            h.Message = Cob.X(ex.DriverText, 200);
            h.MessageCount++;
            return;
        }
        if (!row.Found)
        {
            h.ReturnCode = 4;
            h.ReasonCode = "SRV0004";
            h.Message = Cob.Str(200, "Order line was not found; the reservation is ", "held against the quoted demand reference.");
            h.MessageCount++;
            return;
        }
        sv.LineStatus = Cob.X(row.Raw(0), 10);
        sv.OrderStatus = Cob.X(row.Raw(6), 14);
        sv.AcceptedQty = Cob.St(row.NumVal(3), 13, 6);
        sv.CancelledQty = Cob.St(row.NumVal(4), 13, 6);
        sv.DespatchedQty = Cob.St(row.NumVal(5), 13, 6);
        sv.OutstandingQty = Cob.St(sv.AcceptedQty - sv.CancelledQty - sv.DespatchedQty, 13, 6);
        if (sv.OutstandingQty < 0) sv.OutstandingQty = 0;
        if (Cob.Spaces(sv.Revision)) sv.Revision = Cob.X(row.Raw(2), 4);
        if (Cob.Spaces(sv.Site)) sv.Site = Cob.X(row.Raw(7), 10);
        if (Cob.Spaces(sv.Customer)) sv.Customer = Cob.X(row.Raw(8), 20);
        if (sv.LineStatus is "CANCELLED" or "CLOSED")
        {
            h.ReturnCode = 8;
            h.ReasonCode = "SRV0005";
            h.Message = Cob.Str(200, "Order line ", Cob.Trim(sv.OrderNumber), " is ", Cob.Trim(sv.LineStatus),
                " and has no demand left to reserve.");
            h.MessageCount++;
        }
    }

    /// <summary>2200-SALEABILITY: AVAILQ picture of unsaleable stock (a failed picture only warns, SRV0006).</summary>
    private async Task SaleabilityAsync(SoresvArea sv, CancellationToken ct)
    {
        var h = sv.Header;
        var av = new AvailqCommarea
        {
            ItemCode = sv.ItemCode,
            Revision = sv.Revision,
            Site = sv.Site,
            Warehouse = sv.Warehouse,
            LotCode = sv.LotCode,
            OwnerType = "OWNED",
            AsAt = CobolDate.FromYyyymmdd(sv.DespatchDate),
            DemandType = DemandSales,
            DemandRef = sv.DemandRef,
            DemandLine = sv.DemandLine,
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, av,
            new ItmCall(h.Company, h.Site, h.User, CobolDate.FromYyyymmdd(h.BusinessDate)), ct);
        if (r.ReturnCode > 4)
        {
            h.ReturnCode = 4;
            h.ReasonCode = "SRV0006";
            h.Message = Cob.Str(200, "The saleability picture could not be read; ", "the reservation is still bounded by free stock.");
            return;
        }
        sv.QtyQuarantine = Cob.St(av.QtyQuarantine, 13, 6);
        sv.QtyExpired = Cob.St(av.QtyExpired, 13, 6);
        sv.QtyRejected = Cob.St(av.QtyRejected, 13, 6);
        sv.QtyThirdparty = Cob.St(av.QtyThirdparty, 13, 6);
        sv.QtyUnsaleable = Cob.St(av.QtyQuarantine + av.QtyExpired + av.QtyRejected + av.QtyThirdparty, 13, 6);
        sv.QtyFreeBefore = Cob.St(av.QtyFree, 13, 6);
        if (sv.QtyUnsaleable > 0)
        {
            sv.UnsaleableText = Cob.Str(200, "Quarantined, expired, rejected or third party ", "owned stock of ",
                Cob.Ed(sv.QtyUnsaleable, "Z(9)9.9(6)"), " is held but is not saleable.");
        }
        if (!Cob.Spaces(sv.LotCode) && av.QtyFree <= 0 && sv.QtyUnsaleable > 0)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "SRV0006";
            h.Message = Cob.Str(200, "Lot ", Cob.Trim(sv.LotCode), " is quarantined, expired, rejected or not ", "owned, so it cannot be sold.");
            h.MessageCount++;
        }
    }

    /// <summary>2300-SHELF-LIFE: a named lot must keep the customer's remaining shelf life at the planned despatch date.</summary>
    private async Task ShelfLifeAsync(SoresvArea sv, CancellationToken ct)
    {
        if (Cob.Spaces(sv.LotCode)) return;
        var h = sv.Header;
        sv.ShelfTestDate = sv.DespatchDate;
        if (sv.ShelfDaysReq > 0)
        {
            var dc = await calendar.AddDaysAsync(sv.DespatchDate, sv.ShelfDaysReq, ct);
            if (dc.Header.ReturnCode < 8) sv.ShelfTestDate = dc.ResultDate;
        }
        var ls = new LotserCommarea
        {
            ItemCode = sv.ItemCode,
            Revision = sv.Revision,
            LotCode = sv.LotCode,
            SerialNo = sv.SerialNo,
            Site = sv.Site,
            Direction = "O",
            Quantity = sv.QtyRequested,
            AsAt = CobolDate.FromYyyymmdd(sv.ShelfTestDate),
            MinRemainDays = sv.ShelfDaysReq,
        };
        var r = await lotser.ExecuteAsync(LotserFunctions.ExpiryCheck, ls,
            new ItmCall(h.Company, h.Site, h.User, CobolDate.FromYyyymmdd(h.BusinessDate)), ct);
        sv.ExpiryChecked = "Y";
        sv.LotExpiry = CobolDate.ToYyyymmdd(ls.ExpiryDate);
        sv.DaysRemaining = (int)Cob.St(ls.DaysRemaining, 7, 0);
        sv.LotUsable = Cob.X(ls.Usable, 1);
        if (r.ReturnCode == 16)
        {
            sv.ExpiryChecked = "N";
            return;
        }
        if (ls.Usable != "Y")
        {
            h.ReturnCode = 8;
            h.ReasonCode = "SRV0007";
            h.Message = Cob.Str(200, "Lot ", Cob.Trim(sv.LotCode), " does not satisfy the remaining shelf life ",
                "required at the planned despatch date.");
            h.MessageCount++;
        }
    }

    /// <summary>4000-CALL-STKRESV: the only route to a reservation; RC >= 8 is passed on (RSV0003 as SRV0003).</summary>
    private async Task CallStkresvAsync(SoresvArea sv, string function, CancellationToken ct)
    {
        var h = sv.Header;
        var sr = new StkresvCommarea
        {
            ItemCode = sv.ItemCode,
            Revision = sv.Revision,
            Site = sv.Site,
            Warehouse = sv.Warehouse,
            Location = sv.Location,
            LotCode = sv.LotCode,
            SerialNo = sv.SerialNo,
            DemandType = DemandSales,
            DemandRef = sv.DemandRef,
            DemandLine = sv.DemandLine,
            Firmness = sv.Firmness,
            Priority = sv.Priority,
            RequiredDate = CobolDate.FromYyyymmdd(sv.RequiredDate),
            QtyRequested = sv.QtyRequested,
            AllowPartial = sv.AllowPartial,
        };
        var r = await stkresv.ExecuteAsync(function, sr,
            new ItmCall(h.Company, h.Site, h.User, CobolDate.FromYyyymmdd(h.BusinessDate), h.CorrelationId), ct);
        sv.QtyReserved = Cob.St(sr.QtyReserved, 13, 6);
        sv.QtyShortfall = Cob.St(sr.QtyShortfall, 13, 6);
        sv.QtyConsumed = Cob.St(sr.QtyConsumed, 13, 6);
        sv.QtyFreeBefore = Cob.St(sr.QtyFreeBefore, 13, 6);
        sv.QtyFreeAfter = Cob.St(sr.QtyFreeAfter, 13, 6);
        sv.ReservationId = sr.ReservationId;
        sv.InvalidCnt = sr.InvalidCnt;
        sv.InvalidReason = Cob.X(sr.InvalidReason, 200);
        sv.Status = Cob.X(sr.Status, 10);
        CopyLines(sv, sr);
        if (r.ReturnCode >= 8)
        {
            h.ReturnCode = r.ReturnCode;
            h.Message = Cob.X(r.Message, 200);
            h.ReasonCode = r.ReasonCode == "RSV0003" ? "SRV0003" : Cob.X(r.ReasonCode, 8);
            h.MessageCount++;
        }
    }

    /// <summary>4100-COPY-LINES.</summary>
    private static void CopyLines(SoresvArea sv, StkresvCommarea sr)
    {
        sv.LineCnt = sr.Lines.Count;
        for (var i = 0; i < sr.Lines.Count && i < sv.Lines.Length; i++)
        {
            var from = sr.Lines[i];
            var to = sv.Lines[i];
            to.LResId = from.ResId;
            to.LItem = Cob.X(from.Item, 40);
            to.LSite = Cob.X(from.Site, 10);
            to.LLocation = Cob.X(from.Location, 20);
            to.LLot = Cob.X(from.Lot, 30);
            to.LDemRef = Cob.X(from.DemRef, 40);
            to.LDemLine = from.DemLine;
            to.LQty = Cob.St(from.Qty, 13, 6);
            to.LPicked = Cob.St(from.Picked, 13, 6);
            to.LConsumed = Cob.St(from.Consumed, 13, 6);
            to.LStatus = Cob.X(from.Status, 10);
            to.LRequired = CobolDate.ToYyyymmdd(from.Required);
        }
    }

    /// <summary>2900-RESERVE-RESULT: a partial allocation is never silent (RC 04 SRV0003 with the shortfall sentence).</summary>
    private static void ReserveResult(SoresvArea sv)
    {
        var h = sv.Header;
        var text = Cob.Str(200, "Reserved ", Cob.Ed(sv.QtyReserved, "Z(9)9.9(6)"), " of ", Cob.Trim(sv.ItemCode), " for order ",
            Cob.Trim(sv.DemandRef));
        h.Message = text;
        if (sv.QtyShortfall > 0)
        {
            h.Message = Cob.Str(200, Cob.Trim(text), "; short by ", Cob.Ed(sv.QtyShortfall, "Z(9)9.9(6)"),
                " which remains outstanding demand.");
            if (h.ReturnCode < 4) h.ReturnCode = 4;
            h.ReasonCode = "SRV0003";
            h.MessageCount++;
        }
        if (sv.QtyReserved > 0) sv.Status = "ACTIVE";
        if (!Cob.Spaces(sv.UnsaleableText)) h.MessageCount++;
    }

    /// <summary>9000-REJECT: RC 08 with WS-TEXT as the message.</summary>
    private static void Reject(SoresvArea sv, State s)
    {
        sv.Header.ReturnCode = 8;
        sv.Header.Message = Cob.X(s.Text, 200);
        sv.Header.MessageCount++;
        s.Bad = true;
    }
}
