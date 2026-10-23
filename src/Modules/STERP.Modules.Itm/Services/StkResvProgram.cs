using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// STKRESV (cobol/online/itm/STKRESV.cob; FR-STERP-ITM-021): RESERVE, RELEASE, CONSUME, REVALID through one
/// <c>EXEC inv.usp_ReserveStock</c> with 18 positional parameters, and ENQUIRE. No transaction control: the procedure opens
/// its own (nested) transaction on the caller's connection and rolls back the whole open transaction on error.
/// </summary>
internal sealed class StkResvProgram(Sqlio sql)
{
    private const string Prog = "STKRESV";

    private bool _error;
    private string _action = "";

    public async Task RunAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        Initialise(ca, sr);
        Validate(ca, sr);
        if (_error) return;
        switch (ca.Function)
        {
            case "RESERVE": await ReserveAsync(ca, sr, ct); break;
            case "RELEASE": await ReleaseAsync(ca, sr, ct); break;
            case "CONSUME": await ConsumeAsync(ca, sr, ct); break;
            case "REVALID": await RevalidateAsync(ca, sr, ct); break;
            case "ENQUIRE": await EnquireAsync(ca, sr, ct); break;
            default:
                ca.ReasonCode = "RSV0008";
                ca.SetMessage("STKRESV does not support function " + Cob.Trim(ca.Function));
                Reject(ca);
                break;
        }
    }

    /// <summary>0100-INITIALISE: SR-QTY-CONSUMED is not reset (FR-STERP-ITM-021; section 7.5).</summary>
    private void Initialise(CaHeader ca, StkresvCommarea sr)
    {
        _error = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-014";
        sr.QtyReserved = sr.QtyShortfall = sr.QtyFreeBefore = sr.QtyFreeAfter = 0;
        sr.ReservationId = 0;
        sr.InvalidCnt = 0;
        sr.Lines.Clear();
        sr.Status = "";
        sr.InvalidReason = "";
        sr.ItemCode = Cob.Upper(sr.ItemCode, 40);
        sr.Revision = Cob.X(sr.Revision, 4);
        sr.Site = Cob.X(sr.Site, 10);
        sr.Warehouse = Cob.X(sr.Warehouse, 20);
        sr.Location = Cob.X(sr.Location, 20);
        sr.LotCode = Cob.X(sr.LotCode, 30);
        sr.SerialNo = Cob.X(sr.SerialNo, 40);
        sr.DemandType = Cob.X(sr.DemandType, 12);
        sr.DemandRef = Cob.X(sr.DemandRef, 40);
        sr.Firmness = Cob.X(sr.Firmness, 10);
        sr.Priority = Math.Abs(sr.Priority) % 100;
        sr.QtyRequested = Cob.Q(sr.QtyRequested);
        sr.QtyConsumed = Cob.Q(sr.QtyConsumed);
        sr.AllowPartial = Cob.X(sr.AllowPartial, 1);
        if (Cob.Spaces(sr.DemandType)) sr.DemandType = "OTHER";
        if (Cob.Spaces(sr.Firmness)) sr.Firmness = "FIRM";
        if (sr.Priority == 0) sr.Priority = 5;
    }

    private void Validate(CaHeader ca, StkresvCommarea sr)
    {
        if (Cob.Spaces(ca.Company) || Cob.Spaces(sr.ItemCode))
        {
            ca.ReasonCode = "RSV0001";
            ca.SetMessage("A company and an item are required");
            Reject(ca);
            return;
        }
        if (ca.Function == "ENQUIRE") return;
        if (Cob.Spaces(sr.DemandRef))
        {
            ca.ReasonCode = "RSV0004";
            ca.SetMessage("A reservation must name the demand it is held for");
            Reject(ca);
            return;
        }
        if ((ca.Function == "RESERVE" || ca.Function == "CONSUME") && sr.QtyRequested <= 0)
        {
            ca.ReasonCode = "RSV0002";
            ca.SetMessage("A quantity greater than zero is required");
            Reject(ca);
        }
    }

    /// <summary>0500-CALL-PROC: the shared call on inv.usp_ReserveStock (WS-ACTION selects the action).</summary>
    private async Task CallProcAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        var required = Cob.Date8(sr.RequiredDate);
        var r = await sql.SingleAsync(
            "EXEC inv.usp_ReserveStock @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17, @p18",
            [
                ca.Company, sr.ItemCode, _action, sr.DemandType, sr.DemandRef, ca.User,
                Cob.Spaces(sr.Site) ? null : sr.Site,
                sr.DemandLine == 0 ? null : Cob.SqlInt(sr.DemandLine),
                sr.Revision,
                Cob.Spaces(sr.Warehouse) ? null : sr.Warehouse,
                Cob.Spaces(sr.Location) ? null : sr.Location,
                sr.LotCode, sr.SerialNo, Cob.SqlNum(sr.QtyRequested),
                sr.AllowPartial == "Y" ? "1" : "0",
                sr.Firmness,
                required == 0 ? null : Cob.D8(required),
                Cob.SqlInt(sr.Priority),
            ], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        var row = r.Row;
        sr.ReservationId = (long)CobolNumeric.Store(Col.Num(row, 1), 17, 0);
        sr.QtyReserved = Cob.Q(Col.Num(row, 2));
        sr.QtyShortfall = Cob.Q(Col.Num(row, 3));
        sr.QtyFreeBefore = Cob.Q(Col.Num(row, 4));
        sr.QtyFreeAfter = Cob.Q(Col.Num(row, 5));
        sr.InvalidCnt = (int)(Math.Abs(decimal.Truncate(Col.Num(row, 9))) % 1000m);
    }

    private async Task ReserveAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        _action = "RESERVE";
        await CallProcAsync(ca, sr, ct);
        if (ca.Failed) return;
        sr.Status = "ACTIVE";
        var text = Cob.Str(200, "Reserved ", Cob.QtyEd(sr.QtyReserved), " of ", Cob.Trim(sr.ItemCode), " for ", Cob.Trim(sr.DemandRef));
        ca.SetMessage(text);
        if (sr.QtyShortfall > 0)
        {
            ca.SetMessage(Cob.Trim(text) + "; short by " + Cob.QtyEd(sr.QtyShortfall));
            ca.ReturnCode = 4;
            ca.ReasonCode = "RSV0003";
            ca.AddMessage();
        }
    }

    private async Task ReleaseAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        _action = "RELEASE";
        await CallProcAsync(ca, sr, ct);
        if (ca.Failed) return;
        sr.Status = "RELEASED";
        ca.SetMessage("Reservation of " + Cob.Trim(sr.ItemCode) + " for " + Cob.Trim(sr.DemandRef) + " released");
    }

    /// <summary>3000-CONSUME: the reservation is revalidated first.</summary>
    private async Task ConsumeAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        _action = "REVALID";
        await CallProcAsync(ca, sr, ct);
        if (ca.Failed) return;
        if (sr.InvalidCnt > 0)
        {
            ca.ReasonCode = "RSV0006";
            ca.SetMessage("The stock reserved for " + Cob.Trim(sr.DemandRef) + " is no longer eligible; reallocate before issuing");
            sr.Status = "INVALID";
            ca.ReturnCode = 6;
            _error = true;
            ca.AddMessage();
            return;
        }
        _action = "CONSUME";
        await CallProcAsync(ca, sr, ct);
        if (ca.Failed) return;
        sr.QtyConsumed = sr.QtyRequested;
        sr.Status = "CONSUMED";
        ca.SetMessage("Consumed " + Cob.QtyEd(sr.QtyRequested) + " of " + Cob.Trim(sr.ItemCode) + " against " + Cob.Trim(sr.DemandRef));
    }

    private async Task RevalidateAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        _action = "REVALID";
        await CallProcAsync(ca, sr, ct);
        if (ca.Failed) return;
        if (sr.InvalidCnt == 0)
        {
            ca.SetMessage("All reservations for " + Cob.Trim(sr.ItemCode) + " remain valid");
            return;
        }
        ca.SetMessage(Cob.IntEd(sr.InvalidCnt) + " reservation(s) were invalidated and an exception was raised for the demand owner");
        ca.ReturnCode = 4;
        ca.ReasonCode = "RSV0006";
        ca.AddMessage();
    }

    /// <summary>5000-ENQUIRE: at most 25 lines; the outstanding quantity of the listed ACTIVE lines.</summary>
    private async Task EnquireAsync(CaHeader ca, StkresvCommarea sr, CancellationToken ct)
    {
        var r = await sql.QueryAsync(
            "SELECT ReservationId, ItemCode, SiteCode, ISNULL(LocationCode,''), ISNULL(LotCode,''), DemandType, DemandRef, " +
            "ISNULL(DemandLine,0), QtyReserved, QtyPicked, QtyConsumed, Status, Firmness, ISNULL(CONVERT(char(8), RequiredDate, 112),'') " +
            "FROM inv.StockReservation WHERE CompanyCode = @p1 AND (@p2 = '' OR ItemCode = @p3) AND (@p4 = '' OR DemandRef = @p5) AND " +
            "(@p6 = '' OR SiteCode = @p7) ORDER BY Priority, ReservationId",
            [ca.Company, sr.ItemCode, sr.ItemCode, sr.DemandRef, sr.DemandRef, sr.Site, sr.Site], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        decimal outstanding = 0;
        foreach (var row in r.Rows!)
        {
            if (sr.Lines.Count >= 25) continue;
            var required = Col.Text(row, 13);
            sr.Lines.Add(new StkresvLine
            {
                ResId = (long)CobolNumeric.Store(Col.Num(row, 0), 17, 0),
                Item = Col.Text(row, 1, 40),
                Site = Col.Text(row, 2, 10),
                Location = Col.Text(row, 3, 20),
                Lot = Col.Text(row, 4, 30),
                DemType = Col.Text(row, 5, 12),
                DemRef = Col.Text(row, 6, 40),
                DemLine = (int)CobolNumeric.Store(Col.Num(row, 7), 9, 0),
                Qty = Cob.Q(Col.Num(row, 8)),
                Picked = Cob.Q(Col.Num(row, 9)),
                Consumed = Cob.Q(Col.Num(row, 10)),
                Status = Col.Text(row, 11, 10),
                Firmness = Col.Text(row, 12, 10),
                Required = Cob.Spaces(required) ? null : Cob.Date((int)(Math.Abs(decimal.Truncate(Col.NumVal(required))) % 100_000_000m)),
            });
            if (Col.Text(row, 11) == "ACTIVE") outstanding = Cob.Q(outstanding + Col.Num(row, 8) - Col.Num(row, 10));
        }
        sr.QtyReserved = outstanding;
        if (sr.Lines.Count == 0)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "RSV0005";
            ca.SetMessage("No reservations match that enquiry");
            return;
        }
        ca.SetMessage(Cob.IntEd(sr.Lines.Count) + " reservation(s)");
    }

    private void Reject(CaHeader ca)
    {
        _error = true;
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    /// <summary>9300-SQL-FAILED: a procedure refusal reaching SQLIO as "STERP-E-..." is RSV0003 RC 8, anything else SQL0001 RC 12.</summary>
    private void SqlFailed(CaHeader ca, SqlioOutcome r)
    {
        _error = true;
        ca.SetMessage(r.Message200(Prog));
        if (ca.Message.StartsWith("STERP-E-", StringComparison.Ordinal))
        {
            ca.ReasonCode = "RSV0003";
            ca.ReturnCode = 8;
        }
        else
        {
            ca.ReasonCode = "SQL0001";
            ca.ReturnCode = 12;
        }
        ca.AddMessage();
    }
}
