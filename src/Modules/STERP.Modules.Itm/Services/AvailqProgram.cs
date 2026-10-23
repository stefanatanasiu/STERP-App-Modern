using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// AVAILQ (cobol/online/itm/AVAILQ.cob; FR-STERP-ITM-022): AVAIL, ENQUIRE, BYLOC and LOTS share one computation; AVAIL
/// returns no lines. Read-only, no transaction control.
/// </summary>
internal sealed class AvailqProgram(Sqlio sql, ItmDateProgram itmDate)
{
    private const string Prog = "AVAILQ";

    private bool _error;
    private bool _overflow;
    private int _asAt;
    private readonly ItdBlock _itd = new();

    public async Task RunAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        try
        {
            await InitialiseAsync(ca, av, ct);
            await ValidateAsync(ca, av, ct);
            if (_error) return;
            switch (ca.Function)
            {
                case "AVAIL" or "ENQUIRE" or "BYLOC" or "LOTS":
                    await AvailabilityAsync(ca, av, ct);
                    break;
                default:
                    ca.ReasonCode = "AVL0002";
                    ca.SetMessage("AVAILQ does not support function " + Cob.Trim(ca.Function));
                    Reject(ca);
                    break;
            }
        }
        finally
        {
            av.AsAt = Cob.Date(_asAt);
        }
    }

    private async Task InitialiseAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        _error = false;
        _overflow = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-013";
        av.QtyPhysical = av.QtyEligible = av.QtyReserved = 0;
        av.QtyFree = av.QtyExpected = av.QtyPlanned = 0;
        av.QtyQuarantine = av.QtyInspection = 0;
        av.QtyRejected = av.QtyExpired = av.QtyIntransit = 0;
        av.QtyThirdparty = av.QtyUnavailLoc = 0;
        av.QtyOwnDemand = av.QtyForDemand = 0;
        av.QtySafety = av.QtyPolicy = 0;
        av.ExpectedDate = null;
        av.Lines.Clear();
        av.DataComplete = "Y";
        av.Restricted = "N";
        av.StockUom = "";
        av.ItemCode = Cob.Upper(av.ItemCode, 40);
        av.Revision = Cob.X(av.Revision, 4);
        av.Site = Cob.X(av.Site, 10);
        av.Warehouse = Cob.X(av.Warehouse, 20);
        av.Location = Cob.X(av.Location, 20);
        av.LotCode = Cob.X(av.LotCode, 30);
        av.SerialNo = Cob.X(av.SerialNo, 40);
        av.OwnerType = Cob.X(av.OwnerType, 10);
        av.OwnerCode = Cob.X(av.OwnerCode, 20);
        av.Condition = Cob.X(av.Condition, 12);
        av.DemandType = Cob.X(av.DemandType, 12);
        av.DemandRef = Cob.X(av.DemandRef, 40);
        if (Cob.Spaces(av.OwnerType)) av.OwnerType = "OWNED";
        _asAt = Cob.Date8(av.AsAt);
        if (_asAt == 0) _asAt = ca.BusinessDate;
        if (_asAt == 0)
        {
            _itd.Func = "TODAY";
            await itmDate.CallAsync(_itd, ct);
            _asAt = _itd.DateR;
        }
    }

    private async Task ValidateAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        if (Cob.Spaces(ca.Company) || Cob.Spaces(av.ItemCode))
        {
            ca.ReasonCode = "AVL0001";
            ca.SetMessage("A company and an item are required for an availability enquiry");
            Reject(ca);
            return;
        }
        _itd.Func = "VALIDATE";
        _itd.Date1 = _asAt;
        await itmDate.CallAsync(_itd, ct);
        if (_itd.Rc > 4)
        {
            ca.ReasonCode = "AVL0005";
            ca.SetMessage("The as-at date is not a valid date");
            Reject(ca);
        }
    }

    private async Task AvailabilityAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        await ReadBucketsAsync(ca, av, ct);
        if (ca.Failed) return;
        await ReadReservationsAsync(ca, av, ct);
        if (ca.Failed) return;
        await ReadPlanningAsync(ca, av, ct);
        await ReadExpectedAsync(ca, av, ct);
        await ReadPlannedAsync(ca, av, ct);
        Compute(ca, av);
        if (_overflow)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "AVL0003";
            ca.AddMessage();
        }
    }

    /// <summary>1100-READ-BUCKETS with 1200-CLASSIFY-BUCKET per row.</summary>
    private async Task ReadBucketsAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        var r = await sql.QueryAsync(
            "SELECT SiteCode, WarehouseCode, LocationCode, LocationType, Revision, LotCode, SerialNo, OwnerType, OwnerCode, QualityStatus, " +
            "QtyOnHand, QtyInTransit, ISNULL(CONVERT(char(8), ExpiryDate, 112),''), IsAvailable, IsOwnedStock, StockUomCode, LifecycleStatus, " +
            "ISNULL(MinRemainingShelfLifeDays,0) FROM inv.vw_StockAvailability WHERE CompanyCode = @p1 AND ItemCode = @p2 AND " +
            "(@p3 = '' OR SiteCode = @p4) AND (@p5 = '' OR WarehouseCode = @p6) AND (@p7 = '' OR LocationCode = @p8) AND " +
            "(@p9 = '' OR Revision = @p10) AND (@p11 = '' OR LotCode = @p12) AND (@p13 = '' OR SerialNo = @p14) AND " +
            "(@p15 = '' OR QualityStatus = @p16) ORDER BY SiteCode, WarehouseCode, LocationCode, LotCode, SerialNo",
            [ca.Company, av.ItemCode, av.Site, av.Site, av.Warehouse, av.Warehouse, av.Location, av.Location, av.Revision, av.Revision,
             av.LotCode, av.LotCode, av.SerialNo, av.SerialNo, av.Condition, av.Condition], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        foreach (var row in r.Rows!) await ClassifyBucketAsync(ca, av, row, ct);
    }

    private async Task ClassifyBucketAsync(CaHeader ca, AvailqCommarea av, object?[] row, CancellationToken ct)
    {
        var bucketQty = Cob.Q(Col.Num(row, 10));
        var ownerType = Cob.X(Col.Text(row, 7), 10);
        var condition = Cob.X(Col.Text(row, 9), 12);
        var locType = Cob.X(Col.Text(row, 3), 12);
        var locAvail = Col.Text(row, 13) is { Length: > 0 } a ? a[..1] : "";
        var locOwned = Col.Text(row, 14) is { Length: > 0 } o ? o[..1] : "";
        var lifecycle = Cob.X(Col.Text(row, 16), 10);
        av.StockUom = Cob.X(Col.Text(row, 15), 10);
        var minShelf = (int)CobolNumeric.Store(Col.Num(row, 17), 5, 0);
        var expiry = Cob.Spaces(Col.Text(row, 12)) ? 0 : (int)Col.Num(row, 12);

        // 1300-ELIGIBILITY: the first applicable reason wins
        var eligible = "N";
        string why;
        if (ownerType != "OWNED") why = "Third party owned";
        else if (locOwned != "1") why = "Location holds external stock";
        else if (locType == "TRANSIT") why = "In transit";
        else if (condition == "QUARANTINE") why = "Quarantined";
        else if (condition == "INSPECTION") why = "Awaiting inspection";
        else if (condition == "REJECTED") why = "Rejected";
        else if (condition == "EXPIRED") why = "Expired";
        else if (expiry > 0 && expiry < _asAt) why = "Expired";
        else if (expiry > 0 && minShelf > 0 && await InsideShelfLifeAsync(expiry, minShelf, ct)) why = "Inside minimum shelf life";
        else if (locAvail != "1") why = "Location not available";
        else if (lifecycle is "BLOCKED" or "RETIRED") why = "Item is blocked or retired";
        else
        {
            why = "";
            eligible = "Y";
        }

        // 1400-ACCUMULATE
        if (ownerType != "OWNED")
        {
            av.QtyThirdparty = Cob.Q(av.QtyThirdparty + bucketQty);
        }
        else
        {
            av.QtyPhysical = Cob.Q(av.QtyPhysical + bucketQty);
            if (eligible == "Y")
            {
                av.QtyEligible = Cob.Q(av.QtyEligible + bucketQty);
            }
            else
            {
                switch (why)
                {
                    case "In transit": av.QtyIntransit = Cob.Q(av.QtyIntransit + bucketQty); break;
                    case "Quarantined": av.QtyQuarantine = Cob.Q(av.QtyQuarantine + bucketQty); break;
                    case "Awaiting inspection": av.QtyInspection = Cob.Q(av.QtyInspection + bucketQty); break;
                    case "Rejected": av.QtyRejected = Cob.Q(av.QtyRejected + bucketQty); break;
                    case "Expired" or "Inside minimum shelf life": av.QtyExpired = Cob.Q(av.QtyExpired + bucketQty); break;
                    default: av.QtyUnavailLoc = Cob.Q(av.QtyUnavailLoc + bucketQty); break;
                }
            }
        }

        // 1450-ADD-LINE (never for AVAIL)
        if (ca.Function == "AVAIL") return;
        if (av.Lines.Count >= 40)
        {
            _overflow = true;
            return;
        }
        av.Lines.Add(new AvailqLine
        {
            Site = Cob.X(Col.Text(row, 0), 10),
            Warehouse = Cob.X(Col.Text(row, 1), 20),
            Location = Cob.X(Col.Text(row, 2), 20),
            LocType = locType,
            Revision = Cob.X(Col.Text(row, 4), 4),
            Lot = Cob.X(Col.Text(row, 5), 30),
            Serial = Cob.X(Col.Text(row, 6), 40),
            OwnerType = ownerType,
            Owner = Cob.X(Col.Text(row, 8), 20),
            Condition = condition,
            Qty = bucketQty,
            Reserved = 0,
            Free = eligible == "Y" ? bucketQty : 0,
            Expiry = Cob.Date(expiry),
            Eligible = eligible,
            Why = Cob.X(why, 30),
        });
    }

    /// <summary>ITMDATE ADDDAYS as-at + minimum shelf life; the expiry is inside it when earlier than that limit.</summary>
    private async Task<bool> InsideShelfLifeAsync(int expiry, int minShelf, CancellationToken ct)
    {
        _itd.Func = "ADDDAYS";
        _itd.Date1 = _asAt;
        _itd.Number = minShelf;
        await itmDate.CallAsync(_itd, ct);
        return expiry < _itd.DateR;
    }

    /// <summary>1500-READ-RESERVATIONS.</summary>
    private async Task ReadReservationsAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SUM(QtyReserved - QtyConsumed),0), ISNULL(SUM(CASE WHEN DemandType = @p1 AND DemandRef = @p2 THEN QtyReserved - " +
            "QtyConsumed ELSE 0 END),0) FROM inv.StockReservation WHERE CompanyCode = @p3 AND ItemCode = @p4 AND Status = 'ACTIVE' AND " +
            "(@p5 = '' OR SiteCode = @p6) AND (@p7 = '' OR LocationCode IS NULL OR LocationCode = @p8)",
            [av.DemandType, av.DemandRef, ca.Company, av.ItemCode, av.Site, av.Site, av.Location, av.Location], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        av.QtyReserved = Cob.Q(Col.Num(r.Row, 0));
        av.QtyOwnDemand = Cob.Q(Col.Num(r.Row, 1));
    }

    /// <summary>1600-READ-PLANNING: safety stock of the site (failures ignored).</summary>
    private async Task ReadPlanningAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        if (Cob.Spaces(av.Site)) return;
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SafetyStockQty,0) FROM inv.ItemSite WHERE CompanyCode = @p1 AND SiteCode = @p2 AND ItemCode = @p3",
            [ca.Company, av.Site, av.ItemCode], ct);
        if (r.SqlCode == 0) av.QtySafety = Cob.Q(Col.Num(r.Row, 0));
    }

    /// <summary>1700-READ-EXPECTED: open purchase order lines; a failure marks the data incomplete.</summary>
    private async Task ReadExpectedAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SUM((l.OrderedQty - l.ReceivedQty - l.CancelledQty) * ISNULL(NULLIF(l.ConversionFactor,0),1)),0), " +
            "ISNULL(CONVERT(char(8), MIN(x.Eta), 112),'') FROM po.PurchaseOrderLine l JOIN po.PurchaseOrder h ON h.PurchaseOrderId = " +
            "l.PurchaseOrderId OUTER APPLY (SELECT MIN(s.ExpectedArrivalDate) AS Eta FROM po.PurchaseSchedule s WHERE s.PurchaseOrderLineId" +
            " = l.PurchaseOrderLineId) x WHERE l.CompanyCode = @p1 AND l.ItemCode = @p2 AND h.Status IN ('ISSUED','ACKNOWLEDGED','AMENDED') " +
            "AND l.OrderedQty > l.ReceivedQty + l.CancelledQty AND (@p3 = '' OR ISNULL(l.DestinationSiteCode, h.SiteCode) = @p4)",
            [ca.Company, av.ItemCode, av.Site, av.Site], ct);
        if (r.IsError)
        {
            av.DataComplete = "N";
            av.QtyExpected = 0;
            return;
        }
        if (r.SqlCode == 0)
        {
            av.QtyExpected = Cob.Q(Col.Num(r.Row, 0));
            if (!Cob.Spaces(Col.Text(r.Row, 1))) av.ExpectedDate = Cob.Date((int)Col.Num(r.Row, 1));
        }
    }

    /// <summary>1800-READ-PLANNED: planned kit output; a failure marks the data incomplete.</summary>
    private async Task ReadPlannedAsync(CaHeader ca, AvailqCommarea av, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SUM(PlannedGoodQty - CompletedGoodQty),0) FROM kit.ExecutionKit WHERE CompanyCode = @p1 AND ParentItemCode = @p2 " +
            "AND Status IN ('RELEASED','ISSUED','PARTISSUED','PARTCOMPLETE') AND (@p3 = '' OR SiteCode = @p4)",
            [ca.Company, av.ItemCode, av.Site, av.Site], ct);
        if (r.IsError)
        {
            av.DataComplete = "N";
            av.QtyPlanned = 0;
            return;
        }
        if (r.SqlCode == 0) av.QtyPlanned = Cob.Q(Col.Num(r.Row, 0));
    }

    /// <summary>1900-COMPUTE: free, for-demand, policy and the summary message.</summary>
    private static void Compute(CaHeader ca, AvailqCommarea av)
    {
        av.QtyFree = Cob.Q(av.QtyEligible - av.QtyReserved);
        if (av.QtyFree < 0) av.QtyFree = 0;
        av.QtyForDemand = Cob.Q(av.QtyFree + av.QtyOwnDemand);
        av.QtyPolicy = Cob.Q(av.QtyFree - av.QtySafety);
        if (av.QtyPolicy < 0) av.QtyPolicy = 0;
        var text = Cob.Str(200, "Free ", Cob.QtyEd(av.QtyFree), " ", Cob.Trim(av.StockUom), " of eligible ");
        var message = Cob.Str(200, Cob.Trim(text), Cob.QtyEd(av.QtyEligible), " (physical ");
        ca.SetMessage(Cob.Str(200, Cob.Trim(message), Cob.QtyEd(av.QtyPhysical), ")"));
        if (av.QtyPhysical == 0 && av.QtyThirdparty == 0)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "AVL0001";
            ca.SetMessage("No stock of " + Cob.Trim(av.ItemCode) + " is recorded in that scope");
        }
        if (av.DataComplete != "Y")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "AVL0004";
            ca.SetMessage("Expected and planned supply are not available and are shown as not reported");
        }
    }

    private void Reject(CaHeader ca)
    {
        _error = true;
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
