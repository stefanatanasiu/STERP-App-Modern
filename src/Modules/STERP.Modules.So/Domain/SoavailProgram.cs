using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SOAVAIL (cobol/online/so/SOAVAIL.cob, FR-SOAVAIL-001..011): availability of an item by labelled source (FR-STERP-SO-015).
/// ENQUIRE is implemented; PROMISE and ORDER have no production caller (D-SO-01, SME-015). Reads only: AVAILQ (IC-ITM-AVAILQ)
/// and the purchase schedule / execution kit / kit definition statements.
/// </summary>
public sealed class SoavailProgram(ISqlRunner sql, IAvailabilityService availq)
{
    private const string ReceiptSql =
        "SELECT p.OrderNumber, " +
        "CONVERT(char(8), COALESCE(s.ExpectedArrivalDate, " +
        "s.CurrentConfirmedDate, s.RequestedDate), 112), " +
        "CONVERT(varchar(30), s.Quantity - s.ReceivedQty), " +
        "CONVERT(varchar(12), s.LineNum), " +
        "ISNULL(s.DestinationSiteCode, p.SiteCode), " +
        "p.SupplierCode " +
        "FROM po.PurchaseSchedule s " +
        "JOIN po.PurchaseOrderLine l ON " +
        "l.PurchaseOrderLineId = s.PurchaseOrderLineId " +
        "JOIN po.PurchaseOrder p ON " +
        "p.PurchaseOrderId = l.PurchaseOrderId " +
        "WHERE s.CompanyCode = ? AND l.ItemCode = ? " +
        "AND s.Status IN ('OPEN','PARTIAL') " +
        "AND l.Status = 'OPEN' " +
        "AND p.Status IN ('APPROVED','ISSUED', " +
        "'ACKNOWLEDGED','AMENDED') " +
        "AND s.Quantity > s.ReceivedQty " +
        "AND (? = '' OR ISNULL(s.DestinationSiteCode, " +
        "p.SiteCode) = ?) " +
        "ORDER BY 2, p.OrderNumber";

    private const string PlannedSql =
        "SELECT k.KitRef, CONVERT(char(8), " +
        "COALESCE(k.PlannedCompletionDate, k.NeedDate), " +
        "112), CONVERT(varchar(30), k.PlannedGoodQty - " +
        "k.CompletedGoodQty), k.SiteCode " +
        "FROM kit.ExecutionKit k " +
        "WHERE k.CompanyCode = ? AND k.ParentItemCode = ? " +
        "AND k.Status IN ('RELEASED','PARTISSUED', " +
        "'ISSUED','PARTCOMPLETE','OPCOMPLETE') " +
        "AND k.PlannedGoodQty > k.CompletedGoodQty " +
        "AND (? = '' OR k.SiteCode = ?) " +
        "ORDER BY 2, k.KitRef";

    private const string KitSql =
        "SELECT CONVERT(varchar(30), ISNULL(MIN(" +
        "FLOOR(ISNULL(f.Freeq,0) / l.Quantity)), 0)), " +
        "CONVERT(varchar(12), COUNT(*)) " +
        "FROM kit.KitDefinition d " +
        "JOIN kit.KitDefinitionLine l ON " +
        "l.KitDefId = d.KitDefId " +
        "OUTER APPLY (SELECT SUM(b.QtyOnHand - " +
        "b.QtyReserved) AS Freeq FROM inv.StockBalance b " +
        "WHERE b.CompanyCode = d.CompanyCode " +
        "AND b.ItemCode = l.ComponentItemCode " +
        "AND b.QualityStatus = 'ACCEPTED' " +
        "AND b.OwnerType = 'OWNED' " +
        "AND (? = '' OR b.SiteCode = ?)) f " +
        "WHERE d.CompanyCode = ? AND d.KitItemCode = ? " +
        "AND d.Status = 'RELEASED' AND l.IsOptional = 0 " +
        "AND l.Quantity > 0";

    private static readonly int[] Widths = Enumerable.Repeat(100, 12).ToArray();

    private sealed class State
    {
        public bool Bad;
        public int Lx;
        public string Text = "";
    }

    public async Task RunAsync(SoavailArea sa, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sa);
        Validate(sa, s);
        if (s.Bad) return;
        switch (sa.Header.Function)
        {
            case "ENQUIRE":
                await EnquireAsync(sa, s, ct);
                break;
            case "PROMISE":
            case "ORDER":
                throw new NotSupportedException($"SOAVAIL {sa.Header.Function} has no production caller (D-SO-01, SME-015).");
            default:
                sa.Header.ReturnCode = 8;
                sa.Header.ReasonCode = "SAV0002";
                s.Text = Cob.Str(200, "SOAVAIL does not support the function ", Cob.Trim(sa.Header.Function));
                sa.Header.Message = s.Text;
                break;
        }
    }

    /// <summary>1100-INITIALISE.</summary>
    private static void Initialise(SoavailArea sa)
    {
        var h = sa.Header;
        h.Program = "SOAVAIL";
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-010";
        sa.ItemCode = Cob.Upper(sa.ItemCode, 40);
        sa.QtyPhysical = 0; sa.QtyEligible = 0;
        sa.QtyReserved = 0; sa.QtyFree = 0;
        sa.QtyOwnDemand = 0; sa.QtyExpected = 0;
        sa.QtyPlanned = 0; sa.QtyKitable = 0;
        sa.QtyQuarantine = 0; sa.QtyExpired = 0;
        sa.QtyRejected = 0; sa.QtyThirdparty = 0;
        sa.QtyGuaranteed = 0; sa.QtyPromised = 0;
        sa.QtyShortfall = 0;
        sa.ExpectedDate = 0; sa.PromiseDate = 0;
        sa.LineCnt = 0; sa.OrderLineCnt = 0;
        sa.PromiseText = ""; sa.StockUom = "";
        sa.PromiseBasis = "NONE";
        sa.PromiseFull = "N";
        sa.Guaranteed = "N";
        sa.DataComplete = "Y";
        if (sa.AsAt == 0) sa.AsAt = h.BusinessDate;
        if (sa.RequiredDate == 0) sa.RequiredDate = sa.AsAt;
        if (sa.HorizonDays == 0) sa.HorizonDays = 90;
        if (sa.WithReceipts != "N") sa.WithReceipts = "Y";
        if (sa.WithKit != "N") sa.WithKit = "Y";
    }

    /// <summary>1200-VALIDATE.</summary>
    private static void Validate(SoavailArea sa, State s)
    {
        if (Cob.Spaces(sa.Header.Company))
        {
            sa.Header.ReasonCode = "SAV0001";
            s.Text = "A company code is required.";
            Reject(sa, s);
            return;
        }
        if (sa.Header.Function == "ORDER")
        {
            if (Cob.Spaces(sa.OrderNumber))
            {
                sa.Header.ReasonCode = "SAV0001";
                s.Text = Cob.Str(200, "An order number is required to report ", "availability for every line.");
                Reject(sa, s);
            }
            return;
        }
        if (Cob.Spaces(sa.ItemCode))
        {
            sa.Header.ReasonCode = "SAV0001";
            s.Text = "An item code is required.";
            Reject(sa, s);
            return;
        }
        if (sa.Header.Function == "PROMISE" && sa.QtyRequired <= 0)
        {
            sa.Header.ReasonCode = "SAV0006";
            s.Text = Cob.Str(200, "A quantity greater than zero is needed ", "before a delivery can be promised.");
            Reject(sa, s);
        }
    }

    /// <summary>2000-ENQUIRE: the full labelled picture for one item at one site.</summary>
    private async Task EnquireAsync(SoavailArea sa, State s, CancellationToken ct)
    {
        await CallAvailqAsync(sa, ct);
        if (sa.Header.Failed) return;
        StockLines(sa, s);
        if (sa.WithReceipts == "Y")
        {
            await ReceiptLinesAsync(sa, s, ct);
            await PlannedLinesAsync(sa, s, ct);
        }
        if (sa.WithKit == "Y") await KitLineAsync(sa, s, ct);
        Summarise(sa, s);
    }

    /// <summary>2100-CALL-AVAILQ: AVAILQ owns the quantity definitions (IC-ITM-AVAILQ AVAIL).</summary>
    private async Task CallAvailqAsync(SoavailArea sa, CancellationToken ct)
    {
        var h = sa.Header;
        var av = new AvailqCommarea
        {
            ItemCode = sa.ItemCode,
            Revision = sa.Revision,
            Site = sa.Site,
            Warehouse = sa.Warehouse,
            OwnerType = "OWNED",
            AsAt = CobolDate.FromYyyymmdd(sa.AsAt),
        };
        if (!Cob.Spaces(sa.OrderNumber))
        {
            av.DemandType = "SALESORDER";
            av.DemandRef = Cob.X(sa.OrderNumber, 40);
            av.DemandLine = sa.LineNum;
        }
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, av,
            new ItmCall(h.Company, h.Site, h.User, CobolDate.FromYyyymmdd(h.BusinessDate), h.CorrelationId), ct);
        if (r.ReturnCode > 4)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "SAV0003";
            h.Message = Cob.X(r.Message, 200);
            return;
        }
        sa.StockUom = Cob.X(av.StockUom, 10);
        sa.QtyPhysical = Cob.St(av.QtyPhysical, 13, 6);
        sa.QtyEligible = Cob.St(av.QtyEligible, 13, 6);
        sa.QtyReserved = Cob.St(av.QtyReserved, 13, 6);
        sa.QtyFree = Cob.St(av.QtyFree, 13, 6);
        sa.QtyOwnDemand = Cob.St(av.QtyOwnDemand, 13, 6);
        sa.QtyQuarantine = Cob.St(av.QtyQuarantine, 13, 6);
        sa.QtyExpired = Cob.St(av.QtyExpired, 13, 6);
        sa.QtyRejected = Cob.St(av.QtyRejected, 13, 6);
        sa.QtyThirdparty = Cob.St(av.QtyThirdparty, 13, 6);
        if (av.DataComplete != "Y") sa.DataComplete = "N";
    }

    /// <summary>2200-STOCK-LINES: line 1 FREESTOCK (the only guaranteed one), line 2 RESERVED when stock is held.</summary>
    private static void StockLines(SoavailArea sa, State s)
    {
        var l = NextLine(sa, s);
        l.LKind = "FREESTOCK";
        l.LLabel = "Physical free stock";
        l.LItem = Cob.X(sa.ItemCode, 40);
        l.LSite = Cob.X(sa.Site, 10);
        l.LWhse = Cob.X(sa.Warehouse, 20);
        l.LQty = sa.QtyFree;
        l.LDate = sa.AsAt;
        l.LDocType = "STOCKBALANCE";
        l.LDocRef = Cob.X(sa.Site, 40);
        l.LGuaranteed = "Y";
        l.LText = Cob.Str(100, "Owned, eligible and unreserved - this quantity ", "may be allocated now.");
        if (sa.QtyReserved > 0)
        {
            l = NextLine(sa, s);
            l.LKind = "RESERVED";
            l.LLabel = "Already reserved";
            l.LItem = Cob.X(sa.ItemCode, 40);
            l.LSite = Cob.X(sa.Site, 10);
            l.LQty = sa.QtyReserved;
            l.LDate = sa.AsAt;
            l.LDocType = "RESERVATION";
            l.LGuaranteed = "N";
            l.LText = Cob.Str(100, "Held for other demand; it is not available ", "to this order.");
        }
    }

    /// <summary>2300-RECEIPT-LINES: open purchase schedules (never a physical allocation).</summary>
    private async Task ReceiptLinesAsync(SoavailArea sa, State s, CancellationToken ct)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(ReceiptSql,
                [SqlioParm.Text(sa.Header.Company), SqlioParm.Text(sa.ItemCode), SqlioParm.Text(sa.Site), SqlioParm.Text(sa.Site)], Widths, ct);
        }
        catch (SqlioException)
        {
            sa.DataComplete = "N";
            return;
        }
        foreach (var r in rows)
        {
            if (sa.LineCnt >= 40)
            {
                sa.DataComplete = "N";
                continue;
            }
            var qty = Cob.St(r.NumVal(2), 13, 6);
            if (qty <= 0) continue;
            var l = NextLine(sa, s);
            var date = DateDigits(r.Raw(1));
            l.LKind = "EXPECTED";
            l.LLabel = "Expected purchase receipt";
            l.LItem = Cob.X(sa.ItemCode, 40);
            l.LSite = Cob.X(r.Raw(4), 10);
            l.LQty = qty;
            l.LDate = date;
            l.LDocType = "PURCHASEORDER";
            l.LDocRef = Cob.X(r.Raw(0), 40);
            l.LDocLine = (int)CobolNumeric.NumVal(r.Raw(3));
            l.LGuaranteed = "N";
            l.LText = Cob.Str(100, "Not a physical allocation - supplier ", Cob.Trim(r.Raw(5)), " has yet to deliver this quantity.");
            sa.QtyExpected = Cob.St(sa.QtyExpected + qty, 13, 6);
            if (sa.ExpectedDate == 0 || date < sa.ExpectedDate) sa.ExpectedDate = date;
        }
    }

    /// <summary>2400-PLANNED-LINES: released execution kits that will finish this item.</summary>
    private async Task PlannedLinesAsync(SoavailArea sa, State s, CancellationToken ct)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(PlannedSql,
                [SqlioParm.Text(sa.Header.Company), SqlioParm.Text(sa.ItemCode), SqlioParm.Text(sa.Site), SqlioParm.Text(sa.Site)], Widths, ct);
        }
        catch (SqlioException)
        {
            sa.DataComplete = "N";
            return;
        }
        foreach (var r in rows)
        {
            if (sa.LineCnt >= 40)
            {
                sa.DataComplete = "N";
                continue;
            }
            var qty = Cob.St(r.NumVal(2), 13, 6);
            if (qty <= 0) continue;
            var l = NextLine(sa, s);
            l.LKind = "PLANNED";
            l.LLabel = "Planned assembly output";
            l.LItem = Cob.X(sa.ItemCode, 40);
            l.LSite = Cob.X(r.Raw(3), 10);
            l.LQty = qty;
            l.LDate = DateDigits(r.Raw(1));
            l.LDocType = "EXECUTIONKIT";
            l.LDocRef = Cob.X(r.Raw(0), 40);
            l.LGuaranteed = "N";
            l.LText = Cob.Str(100, "Not a physical allocation - the assembly has ", "still to be completed and booked in.");
            sa.QtyPlanned = Cob.St(sa.QtyPlanned + qty, 13, 6);
        }
    }

    /// <summary>2500-KIT-LINE: trial kit feasibility from free component stock (never from the page: SA-WITH-KIT N).</summary>
    private async Task KitLineAsync(SoavailArea sa, State s, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(KitSql,
                [SqlioParm.Text(sa.Site), SqlioParm.Text(sa.Site), SqlioParm.Text(sa.Header.Company), SqlioParm.Text(sa.ItemCode)], Widths, ct);
        }
        catch (SqlioException)
        {
            sa.DataComplete = "N";
            return;
        }
        if (!row.Found) return;
        var rows = (int)Cob.St(row.NumVal(1), 4, 0);
        if (rows == 0) return;
        var qty = Cob.St(row.NumVal(0), 13, 6);
        sa.QtyKitable = qty;
        if (qty <= 0) return;
        var l = NextLine(sa, s);
        l.LKind = "KITBUILD";
        l.LLabel = "Feasible kit or assembly";
        l.LItem = Cob.X(sa.ItemCode, 40);
        l.LSite = Cob.X(sa.Site, 10);
        l.LQty = qty;
        l.LDate = sa.AsAt;
        l.LDocType = "KITDEFINITION";
        l.LDocRef = Cob.X(sa.ItemCode, 40);
        l.LGuaranteed = "N";
        l.LText = Cob.Str(100, "Trial result only - the components are free but ", "no kit has been built or reserved.");
    }

    /// <summary>2900-SUMMARISE: only free stock is guaranteed; RC 04 SAV0008 when the answer is incomplete.</summary>
    private static void Summarise(SoavailArea sa, State s)
    {
        sa.QtyGuaranteed = sa.QtyFree;
        sa.Guaranteed = sa.QtyFree > 0 ? "Y" : "N";
        s.Text = Cob.Str(200, "Free stock ", Cob.Ed(sa.QtyFree, "Z(9)9.9(6)"), " is available now; expected receipts and kit ",
            "builds are shown separately and are not ", "guaranteed allocations.");
        sa.PromiseText = s.Text;
        sa.Header.Message = s.Text;
        if (sa.DataComplete != "Y")
        {
            sa.Header.ReturnCode = 4;
            sa.Header.ReasonCode = "SAV0008";
            sa.Header.MessageCount++;
        }
    }

    /// <summary>8000-NEXT-LINE: at 40 lines the answer is incomplete and line 40 is reused.</summary>
    private static SoavailLines NextLine(SoavailArea sa, State s)
    {
        if (sa.LineCnt >= 40)
        {
            sa.DataComplete = "N";
            s.Lx = 40;
            return sa.Lines[39];
        }
        sa.LineCnt++;
        s.Lx = sa.LineCnt;
        var l = sa.Lines[s.Lx - 1];
        l.LSeq = sa.LineCnt;
        l.LKind = ""; l.LLabel = ""; l.LItem = ""; l.LSite = ""; l.LWhse = "";
        l.LQty = 0; l.LDate = 0;
        l.LDocType = ""; l.LDocRef = ""; l.LDocLine = 0;
        l.LGuaranteed = "N";
        l.LText = "";
        return l;
    }

    /// <summary>MOVE WS-COL(n)(1:8) TO WS-DATE-N (PIC 9(8)): the digits of the column, spaces as zero.</summary>
    private static int DateDigits(string raw)
    {
        var t = CobolText.Fit(raw, 8);
        var v = 0;
        foreach (var ch in t) v = v * 10 + (ch is >= '0' and <= '9' ? ch - '0' : 0);
        return v;
    }

    /// <summary>9000-REJECT: RC 08 with WS-TEXT as the message.</summary>
    private static void Reject(SoavailArea sa, State s)
    {
        sa.Header.ReturnCode = 8;
        sa.Header.Message = Cob.X(s.Text, 200);
        sa.Header.MessageCount++;
        s.Bad = true;
    }
}
