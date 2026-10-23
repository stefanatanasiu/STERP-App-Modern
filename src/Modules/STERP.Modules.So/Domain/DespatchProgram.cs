using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// DESPATCH (cobol/online/so/DESPATCH.cob; FR-STERP-SO-019): CONFIRM - IDEMPOT BEGIN (autocommit), CREDCHK at DESPATCH (own
/// unit), PERIODCK, control point and cut-off review, NUMGEN SDES (own transaction), then one BEGN..COMT/ROLB holding the
/// line checks, so.Despatch/so.DespatchLine writes, STKMOVE ISSUE, STKRESV CONSUME, the demand updates and the COGS journal
/// (fin.JournalStage + fin.usp_PostJournal), then AUDITLOG and IDEMPOT END (autocommit). REVERSE has no production caller
/// (D-SO-01); ENQUIRE is reached only through the replay branch.
/// </summary>
public sealed class DespatchProgram(
    ISqlRunner sql,
    IUnitOfWork uow,
    IIdempotencyService idempot,
    ICreditCheckService credchk,
    IPeriodService periodck,
    IDocumentNumberService numgen,
    IBusinessCalendar calendar,
    IUnitConversionService uomconv,
    IAvailabilityService availq,
    IStockMovementService stkmove,
    IStockReservationService stkresv,
    IGlPostingGateway gl,
    IAuditWriter audit)
{
    private const string CogsEvent = "SO.DESPATCH.COGS";
    private const string SeriesDesp = "SDES";
    private const string SeriesAlt = "SDES";
    private const string Operation = "SO.DESPATCH.CONFIRM";

    private static readonly int[] W100 = Enumerable.Repeat(100, 14).ToArray();

    private const string ReadCustomerSql =
        "SELECT o.CustomerCode, ISNULL(o.SiteCode,''), " +
        "ISNULL(o.IncotermsRule,''), " +
        "ISNULL(o.DeliveryAddressSnapshot,''), " +
        "o.ReleaseStatus " +
        "FROM so.SalesOrder o WHERE o.CompanyCode = ? " +
        "AND o.OrderNumber = ?";

    private const string PeriodEndSql =
        "SELECT CONVERT(char(8), p.EndDate, 112), p.Status " +
        "FROM fin.Period p WHERE p.CompanyCode = ? AND " +
        "? BETWEEN CONVERT(char(8), p.StartDate, 112) AND " +
        "CONVERT(char(8), p.EndDate, 112)";

    private const string BinFromPickSql =
        "SELECT TOP (1) p.SiteCode, " +
        "ISNULL(NULLIF(e.WarehouseCode,''), " +
        "ISNULL(p.WarehouseCode,'')), " +
        "ISNULL(e.LocationCode,''), ISNULL(e.LotCode,''), " +
        "ISNULL(e.SerialNo,'') " +
        "FROM so.PickListLine e JOIN so.PickList p ON " +
        "p.PickListId = e.PickListId " +
        "WHERE p.CompanyCode = ? AND p.PickListRef = ? " +
        "AND e.ItemCode = ? AND e.PickedQty > 0 " +
        "AND e.Status IN ('PICKED','SHORT','CLOSED') " +
        "ORDER BY CASE WHEN e.SalesOrderLineId = ? THEN 0 " +
        "ELSE 1 END, e.LineNum";

    private const string BinFromStockSql =
        "SELECT TOP (1) b.SiteCode, b.WarehouseCode, " +
        "b.LocationCode, ISNULL(b.LotCode,''), " +
        "ISNULL(b.SerialNo,'') FROM inv.StockBalance b " +
        "JOIN inv.Location v ON v.CompanyCode = " +
        "b.CompanyCode AND v.SiteCode = b.SiteCode AND " +
        "v.WarehouseCode = b.WarehouseCode AND " +
        "v.LocationCode = b.LocationCode " +
        "WHERE b.CompanyCode = ? AND b.ItemCode = ? " +
        "AND (? = '' OR b.SiteCode = ?) " +
        "AND b.QualityStatus = 'ACCEPTED' " +
        "AND b.OwnerType = 'OWNED' AND v.IsAvailable = 1 " +
        "AND v.IsOwnedStock = 1 AND b.QtyOnHand > 0 " +
        "ORDER BY CASE WHEN b.QtyOnHand >= " +
        "CONVERT(decimal(19,6), ?) THEN 0 ELSE 1 END, " +
        "b.QtyOnHand DESC";

    private const string OrderLineSql =
        "SELECT CONVERT(varchar(24), l.SalesOrderLineId), " +
        "ISNULL(l.ItemCode,''), ISNULL(l.Revision,''), " +
        "l.Description, CONVERT(varchar(30), " +
        "l.AcceptedQty - l.CancelledQty - l.DespatchedQty)" +
        ", ISNULL(l.UomCode,''), " +
        "CONVERT(varchar(30), l.ConversionFactor), " +
        "l.Status, l.LineType, " +
        "ISNULL(l.LinkedKitRef,'') " +
        "FROM so.SalesOrderLine l WHERE l.CompanyCode = ? " +
        "AND l.OrderNumber = ? AND l.LineNum = ?";

    private const string UnitCostSql =
        "SELECT TOP (1) CONVERT(varchar(30), CASE WHEN " +
        "c.CostMethod = 'STD' THEN ISNULL(" +
        "c.StandardUnitCost, c.AverageUnitCost) ELSE " +
        "c.AverageUnitCost END), c.CostMethod " +
        "FROM cst.ItemCost c WHERE c.CompanyCode = ? AND " +
        "c.ItemCode = ? AND (c.SiteCode = ? OR " +
        "c.SiteCode = '*') ORDER BY CASE WHEN " +
        "c.SiteCode = ? THEN 0 ELSE 1 END";

    private const string InsertHeaderSql =
        "INSERT so.Despatch (CompanyCode, SiteCode, " +
        "DespatchRef, CustomerCode, OrderNumber, " +
        "DespatchDate, PostingDate, FiscalYear, PeriodNo, " +
        "DeliveryAddressSnapshot, IncotermsRule, " +
        "IncotermsNamedPlace, CarrierCode, " +
        "CarrierReference, IsExport, DestinationCountry, " +
        "CustomerEori, ExportDeclarationRef, Status, " +
        "CogsPostedGBP, IsCutoffReview, CorrelationId, " +
        "ConfirmedBy, ConfirmedAt, CreatedBy) VALUES " +
        "(?, ?, ?, ?, ?, ?, ?, NULLIF(?,0), NULLIF(?,0), " +
        "NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), " +
        "NULLIF(?,''), NULLIF(?,''), ?, NULLIF(?,''), " +
        "NULLIF(?,''), NULLIF(?,''), 'CONFIRMED', 0, ?, " +
        "?, ?, SYSDATETIME(), ?); SELECT CONVERT(" +
        "varchar(24), SCOPE_IDENTITY()), ''";

    private const string InsertLineSql =
        "INSERT so.DespatchLine (DespatchId, LineNum, " +
        "SalesOrderLineId, OrderNumber, OrderLineNum, " +
        "ScheduleNo, ItemCode, Revision, Description, " +
        "PickedQty, DespatchedQty, UomCode, " +
        "ConversionFactor, StockQty, WarehouseCode, " +
        "LocationCode, LotCode, SerialNo, UnitCostGBP, " +
        "CogsGBP, CostMethod, KitRef) VALUES " +
        "(?, ?, NULLIF(?,0), ?, ?, NULLIF(?,0), ?, " +
        "NULLIF(?,''), ?, ?, ?, NULLIF(?,''), ?, ?, " +
        "NULLIF(?,''), NULLIF(?,''), NULLIF(?,''), " +
        "NULLIF(?,''), ?, ?, NULLIF(?,''), NULLIF(?,''))";

    private const string RecordMovementSql =
        "UPDATE so.DespatchLine SET MovementId = " +
        "NULLIF(?,0), CogsGBP = ?, UnitCostGBP = ? " +
        "WHERE DespatchId = ? AND LineNum = ?";

    private const string UpdateDemandSql =
        "UPDATE so.SalesOrderLine SET DespatchedQty = " +
        "DespatchedQty + ? WHERE SalesOrderLineId = ?; " +
        "UPDATE so.DeliverySchedule SET DespatchedQty = " +
        "DespatchedQty + ?, Status = CASE WHEN " +
        "DespatchedQty + ? >= Quantity THEN 'COMPLETE' " +
        "ELSE 'PARTIAL' END WHERE SalesOrderLineId = ? " +
        "AND ScheduleNo = ?";

    private const string EventMapSql =
        "SELECT m.LegCode, m.AccountCode, m.DrCr, " +
        "ISNULL(m.DimCostCentre,''), " +
        "ISNULL(m.DimProject,'') " +
        "FROM fin.EventAccountMap m WHERE " +
        "m.CompanyCode = ? AND m.EventCode = ? AND " +
        "m.IsActive = 1 ORDER BY m.SortOrder, m.LegCode";

    private const string StageLegSql =
        "INSERT fin.JournalStage (CorrelationId, LineNum, " +
        "CompanyCode, AccountCode, DebitAmount, " +
        "CreditAmount, CurrencyCode, DimCostCentre, " +
        "DimProject, Narrative, SubledgerType, " +
        "SubledgerRef, SourceRef, CreatedBy) VALUES " +
        "(?, ?, ?, ?, ?, ?, 'GBP', NULLIF(?,''), " +
        "NULLIF(?,''), ?, 'SO', ?, ?, ?)";

    private const string StampHeaderSql =
        "UPDATE so.Despatch SET CogsPostedGBP = ?, " +
        "JournalId = NULLIF(?,0) WHERE DespatchId = ?";

    private const string EnquireSql =
        "SELECT CONVERT(varchar(24), d.DespatchId), " +
        "d.CustomerCode, ISNULL(d.OrderNumber,''), " +
        "CONVERT(char(8), d.DespatchDate, 112), " +
        "CONVERT(char(8), d.PostingDate, 112), d.Status, " +
        "CONVERT(varchar(24), d.CogsPostedGBP), " +
        "CONVERT(varchar(24), ISNULL(d.JournalId,0)), " +
        "CASE WHEN d.IsCutoffReview = 1 THEN 'Y' ELSE 'N' " +
        "END, ISNULL(d.IncotermsRule,''), " +
        "CASE WHEN d.IsExport = 1 THEN 'Y' ELSE 'N' END, " +
        "ISNULL(d.SiteCode,'') FROM so.Despatch d " +
        "WHERE d.CompanyCode = ? AND d.DespatchRef = ?";

    private const string EnquireLinesSql =
        "SELECT CONVERT(varchar(12), e.LineNum), " +
        "CONVERT(varchar(12), ISNULL(e.OrderLineNum,0)), " +
        "CONVERT(varchar(12), ISNULL(e.ScheduleNo,0)), " +
        "CONVERT(varchar(24), ISNULL(e.SalesOrderLineId," +
        "0)), e.ItemCode, ISNULL(e.Revision,''), " +
        "CONVERT(varchar(30), e.DespatchedQty), " +
        "ISNULL(e.UomCode,''), CONVERT(varchar(30), " +
        "e.StockQty), ISNULL(e.WarehouseCode,''), " +
        "ISNULL(e.LocationCode,''), ISNULL(e.LotCode,''), " +
        "ISNULL(e.SerialNo,''), CONVERT(varchar(24), " +
        "e.CogsGBP) FROM so.DespatchLine e WHERE " +
        "e.DespatchId = ? ORDER BY e.LineNum";

    private sealed record Leg(string Code, string Account, string DrCr, string CostCentre, string Project);

    private sealed class State
    {
        public bool Bad;
        public bool InTran;
        public bool IdemOpen;
        public KernelCaller IdemCaller = KernelCaller.Empty;
        public string Text = "";
        public string SubCorr = "";
        public int PeriodEnd;
        public CredchkArea? Credit;
        public readonly List<Leg> Legs = [];
    }

    public async Task RunAsync(DespatchArea ds, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(ds);
        Validate(ds, s);
        if (!s.Bad)
        {
            switch (ds.Header.Function)
            {
                case "CONFIRM":
                    await ConfirmAsync(ds, s, ct);
                    break;
                case "REVERSE":
                    throw new NotSupportedException("DESPATCH REVERSE has no production caller (D-SO-01, SME-015).");
                case "ENQUIRE":
                    await EnquireAsync(ds, s, ct);
                    break;
                default:
                    ds.Header.ReturnCode = 8;
                    ds.Header.ReasonCode = "DSP0008";
                    s.Text = Cob.Str(200, "DESPATCH does not support the function ", Cob.Trim(ds.Header.Function));
                    ds.Header.Message = s.Text;
                    break;
            }
        }
        if (s.InTran) await RollbackAsync(ds, s, ct);
        if (s.IdemOpen) await IdemEndAsync(ds, s, ct);
    }

    private static KernelCaller Caller(DespatchArea ds, bool site = true, bool correlation = true)
    {
        var h = ds.Header;
        return new KernelCaller(h.Company, site ? h.Site : "", h.User, h.BusinessDate, correlation ? h.CorrelationId : "");
    }

    private static ItmCall Itm(DespatchArea ds, string correlation = "")
    {
        var h = ds.Header;
        return new ItmCall(h.Company, h.Site, h.User, CobolDate.FromYyyymmdd(h.BusinessDate), correlation);
    }

    /// <summary>1100-INITIALISE.</summary>
    private static void Initialise(DespatchArea ds)
    {
        var h = ds.Header;
        h.Program = "DESPATCH";
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-013";
        ds.CogsEvent = CogsEvent;
        ds.WasDuplicate = "N";
        ds.IdemOutcome = "";
        if (h.Function == "CONFIRM")
        {
            ds.DespatchId = 0; ds.JournalId = 0;
            ds.CogsGbp = 0;
            ds.JournalNo = "";
            ds.IsCutoff = "N";
            ds.WorkdaysLeft = 0;
            ds.ControlBasis = "";
            ds.ControlPoint = "DESPATCH";
        }
        if (ds.DespatchDate == 0) ds.DespatchDate = h.BusinessDate;
        if (ds.PostingDate == 0) ds.PostingDate = ds.DespatchDate;
        if (Cob.Spaces(ds.Site)) ds.Site = Cob.X(h.Site, 10);
        if (ds.IsExport != "Y") ds.IsExport = "N";
    }

    /// <summary>1200-VALIDATE.</summary>
    private static void Validate(DespatchArea ds, State s)
    {
        var h = ds.Header;
        if (Cob.Spaces(h.Company))
        {
            h.ReasonCode = "DSP0001";
            s.Text = "A company code is required.";
            Reject(ds, s);
            return;
        }
        if (h.Function == "ENQUIRE")
        {
            if (Cob.Spaces(ds.DespatchRef))
            {
                h.ReasonCode = "DSP0001";
                s.Text = "A despatch reference is required.";
                Reject(ds, s);
            }
            return;
        }
        if (h.Function == "REVERSE")
        {
            if (Cob.Spaces(ds.ReversalOf) || Cob.Spaces(ds.ReverseAppr) || Cob.Spaces(ds.ReverseReason))
            {
                h.ReasonCode = "DSP0010";
                s.Text = Cob.Str(200, "A reversal needs the original despatch ", "reference, an approval reference and a ", "reason.");
                Reject(ds, s);
            }
            return;
        }
        if (Cob.Spaces(ds.OrderNumber))
        {
            h.ReasonCode = "DSP0001";
            s.Text = "An order number is required to despatch.";
            Reject(ds, s);
            return;
        }
        if (Cob.Spaces(h.CorrelationId))
        {
            h.ReasonCode = "DSP0001";
            s.Text = Cob.Str(200, "A correlation id is required so that a ", "repeated confirmation has no second effect.");
            Reject(ds, s);
            return;
        }
        if (ds.LineCnt < 1 || ds.LineCnt > 30)
        {
            h.ReasonCode = "DSP0013";
            s.Text = Cob.Str(200, "A despatch needs between one and thirty ", "lines.");
            Reject(ds, s);
        }
    }

    /// <summary>2000-CONFIRM: everything that can refuse is asked before a row is written; the writes are one unit.</summary>
    private async Task ConfirmAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        await IdempotentBeginAsync(ds, s, ct);
        if (h.Failed || ds.WasDuplicate == "Y") return;
        await CreditCheckAsync(ds, s, ct);
        if (h.ReturnCode >= 6) return;
        await PeriodCheckAsync(ds, ct);
        if (h.ReturnCode >= 6) return;
        ControlPoint(ds, s);
        await CutoffReviewAsync(ds, s, ct);
        await NextReferenceAsync(ds, ct);
        if (h.Failed) return;
        await BeginAsync(ds, s, ct);
        if (h.Failed) return;
        await CheckLinesAsync(ds, s, ct);
        if (h.Failed) return;
        await WriteHeaderAsync(ds, ct);
        if (h.Failed) return;
        await WriteLinesAsync(ds, s, ct);
        if (h.Failed) return;
        await PostCogsAsync(ds, s, ct);
        if (h.Failed) return;
        await CommitAsync(ds, s, ct);
        if (h.Failed) return;
        await AuditControlAsync(ds, ct);
        ds.Status = "CONFIRMED";
        ConfirmWords(ds, s);
    }

    /// <summary>2050-IDEMPOTENT-BEGIN: STARTED owns the instruction; COMPLETED answers RC 04 with the original despatch.</summary>
    private async Task IdempotentBeginAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        s.IdemCaller = Caller(ds);
        var id = await idempot.BeginAsync(s.IdemCaller, Operation, ct);
        ds.IdemOutcome = Cob.X(id.Outcome, 12);
        switch (id.Outcome)
        {
            case "STARTED":
                s.IdemOpen = true;
                break;
            case "COMPLETED":
                ds.WasDuplicate = "Y";
                ds.DespatchRef = Cob.X(id.ResultRef, 30);
                await EnquireAsync(ds, s, ct);
                h.ReturnCode = 4;
                h.ReasonCode = "DSP0012";
                h.Message = Cob.Str(200, "This despatch was already confirmed as ", Cob.Trim(ds.DespatchRef),
                    "; no stock has been consumed and no ", "further cost of sale has been posted.");
                h.MessageCount++;
                break;
            case "INPROGRESS":
                h.ReturnCode = 6;
                h.ReasonCode = "DSP0012";
                h.Message = Cob.Str(200, "Another confirmation of this instruction ", "is running; its outcome is not yet known.");
                h.MessageCount++;
                break;
            default:
                h.ReturnCode = 8;
                h.ReasonCode = "DSP0012";
                h.Message = Cob.X(id.Header.Message, 200);
                break;
        }
    }

    /// <summary>2100-CREDIT-CHECK: CREDCHK CHECK at DESPATCH with a zero order value (IC-AR-CREDCHK).</summary>
    private async Task CreditCheckAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        if (Cob.Spaces(ds.Customer))
        {
            await ReadCustomerAsync(ds, ct);
            if (h.Failed) return;
        }
        var ck = new CredchkArea();
        ck.Header.Prepare(Caller(ds), CreditCheckFunctions.Check);
        ck.Customer = ds.Customer;
        ck.CheckPoint = "DESPATCH";
        ck.DocumentRef = Cob.X(ds.OrderNumber, 40);
        ck.EvalDate = ds.DespatchDate;
        ck.Currency = "GBP";
        ck.FxRate = 1;
        ck.OrderValue = 0;
        ck.OrderValueGbp = 0;
        s.Credit = ck;
        var r = await credchk.CallAsync(ck, ct);
        ds.CreditDecision = Cob.X(ck.Decision, 10);
        ds.CreditExposure = Cob.St(ck.Exposure, 11, 2);
        ds.CreditExcess = Cob.St(ck.Excess, 11, 2);
        ds.CreditText = Cob.X(ck.DecisionText, 200);
        ds.CreditOverride = ck.OverrideId;
        if (r.ReturnCode >= 12)
        {
            h.ReturnCode = 12;
            h.ReasonCode = "DSP0006";
            h.Message = Cob.X(r.Message, 200);
            return;
        }
        if (ck.Blocked)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0006";
            h.Message = Cob.X(ck.DecisionText, 200);
            h.MessageCount++;
            return;
        }
        if (ck.Held)
        {
            h.ReturnCode = 6;
            h.ReasonCode = "DSP0006";
            h.Message = Cob.Str(200, "Despatch is held on credit: ", Cob.Trim(ck.DecisionText));
            h.MessageCount++;
        }
    }

    /// <summary>2110-READ-CUSTOMER: a blank customer is read from the order (RC 16 DSP0002 when missing).</summary>
    private async Task ReadCustomerAsync(DespatchArea ds, CancellationToken ct)
    {
        var h = ds.Header;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(ReadCustomerSql, [SqlioParm.Text(h.Company), SqlioParm.Text(ds.OrderNumber)], W100, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        if (!row.Found)
        {
            h.ReturnCode = 16;
            h.ReasonCode = "DSP0002";
            h.Message = Cob.Str(200, "Order ", Cob.Trim(ds.OrderNumber), " is not known in this company.");
            return;
        }
        ds.Customer = Cob.X(row.Raw(0), 20);
        if (Cob.Spaces(ds.Site)) ds.Site = Cob.X(row.Raw(1), 10);
        if (Cob.Spaces(ds.Incoterms)) ds.Incoterms = Cob.X(row.Raw(2), 3);
        if (Cob.Spaces(ds.AddressSnap)) ds.AddressSnap = Cob.X(row.Raw(3), 300);
    }

    /// <summary>2150-PERIOD-CHECK: PERIODCK POST on the posting date; RC 06 and above stop (DSP0005).</summary>
    private async Task PeriodCheckAsync(DespatchArea ds, CancellationToken ct)
    {
        var h = ds.Header;
        var pc = await periodck.CheckAsync(Caller(ds, site: false, correlation: false), ds.PostingDate, ds.DespatchDate, ds.DespatchDate, "POST",
            ct: ct);
        ds.FiscalYear = pc.FiscalYear;
        ds.PeriodNo = pc.PeriodNo;
        ds.PeriodStatus = Cob.X(pc.PeriodStatus, 10);
        if (pc.Header.ReturnCode >= 6)
        {
            h.ReturnCode = pc.Header.ReturnCode;
            h.ReasonCode = "DSP0005";
            h.Message = Cob.X(pc.Header.Message, 200);
            h.MessageCount++;
        }
    }

    /// <summary>2200-CONTROL-POINT: D-03 transfer of control by the Incoterms rule.</summary>
    private static void ControlPoint(DespatchArea ds, State s)
    {
        var h = ds.Header;
        ds.Incoterms = Cob.Upper(ds.Incoterms, 3);
        if (ds.IsExport != "Y")
        {
            ds.ControlPoint = "DESPATCH";
            ds.ControlDate = ds.DespatchDate;
            ds.ControlBasis = Cob.Str(100, "Domestic supply - control passes to the ", "customer at despatch confirmation (D-03).");
            return;
        }
        switch (ds.Incoterms)
        {
            case "EXW":
            case "FCA":
            case "FAS":
            case "FOB":
            case "CFR":
            case "CIF":
                ds.ControlPoint = "HANDOVER";
                ds.ControlDate = ds.DespatchDate;
                s.Text = Cob.Str(200, "Export on ", Cob.Trim(ds.Incoterms), " terms - control passes at handover to ", "the carrier (D-03).");
                break;
            case "CPT":
            case "CIP":
            case "DAP":
            case "DPU":
            case "DDP":
                ds.ControlPoint = "DELIVERY";
                ds.ControlDate = 0;
                s.Text = Cob.Str(200, "Export on ", Cob.Trim(ds.Incoterms), " delivered terms - control passes on ",
                    "delivery, not at despatch (D-03).");
                break;
            default:
                ds.ControlPoint = "DESPATCH";
                ds.ControlDate = ds.DespatchDate;
                s.Text = Cob.Str(200, "No Incoterms rule is recorded for this ", "export; control is taken to pass at ",
                    "despatch and needs review.");
                h.ReturnCode = 4;
                h.ReasonCode = "DSP0005";
                h.MessageCount++;
                break;
        }
        ds.ControlBasis = Cob.X(s.Text, 100);
    }

    /// <summary>2250-CUTOFF-REVIEW: delivered-terms goods leaving in the last two working days of the period.</summary>
    private async Task CutoffReviewAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        ds.IsCutoff = "N";
        if (ds.ControlPoint != "DELIVERY") return;
        await ReadPeriodEndAsync(ds, s, ct);
        if (s.PeriodEnd == 0) return;
        ds.PeriodEndDate = s.PeriodEnd;
        var add = await calendar.AddBusinessDaysAsync(s.PeriodEnd, -1, "ENG", ct);
        if (add.Header.ReturnCode >= 8) return;
        var cutoffFrom = add.ResultDate;
        var between = await calendar.DaysBetweenAsync(ds.DespatchDate, s.PeriodEnd, ct);
        if (between.Header.ReturnCode < 8) ds.WorkdaysLeft = (int)Cob.St(between.ResultNum, 5, 0);
        if (ds.DespatchDate >= cutoffFrom && ds.DespatchDate <= s.PeriodEnd)
        {
            ds.IsCutoff = "Y";
            s.Text = Cob.Str(200, Cob.Trim(ds.ControlBasis), "  Despatched inside the last two working ", "days of the period - flagged for cut off ",
                "review.");
            ds.ControlBasis = Cob.X(s.Text, 100);
        }
    }

    /// <summary>2260-READ-PERIOD-END (a failure or no period leaves it zero).</summary>
    private async Task ReadPeriodEndAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        s.PeriodEnd = 0;
        try
        {
            var row = await sql.SingleAsync(PeriodEndSql, [SqlioParm.Text(ds.Header.Company), SqlioParm.Text(Cob.D8(ds.DespatchDate))], W100, ct);
            if (!row.Found) return;
            s.PeriodEnd = Digits8(row.Raw(0));
        }
        catch (SqlioException)
        {
            s.PeriodEnd = 0;
        }
    }

    /// <summary>2300-NEXT-REFERENCE: NUMGEN NEXT SDES (retried once on the same series after a refusal).</summary>
    private async Task NextReferenceAsync(DespatchArea ds, CancellationToken ct)
    {
        var h = ds.Header;
        var n = await numgen.NextAsync(Caller(ds), SeriesDesp, ct);
        if (n.Header.ReturnCode >= 8) n = await numgen.NextAsync(Caller(ds), SeriesAlt, ct);
        if (n.Header.ReturnCode >= 8)
        {
            h.ReturnCode = n.Header.ReturnCode;
            h.ReasonCode = "DSP0001";
            h.Message = Cob.X(n.Header.Message, 200);
            return;
        }
        ds.DespatchRef = Cob.X(n.DocumentNumber, 30);
    }

    /// <summary>2400-CHECK-LINES: the two ceilings (remaining demand, physical availability) before any write.</summary>
    private async Task CheckLinesAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        for (var i = 1; i <= ds.LineCnt; i++)
        {
            if (!ds.Header.Failed) await OneLineCheckAsync(ds, s, i, ct);
        }
    }

    /// <summary>2420-ONE-LINE-CHECK.</summary>
    private async Task OneLineCheckAsync(DespatchArea ds, State s, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        l.LMessage = "";
        l.LStatus = "CHECKED";
        if (l.LQty <= 0)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0003";
            s.Text = Cob.Str(200, "A despatch quantity must be greater than ", "zero.");
            h.Message = s.Text;
            l.LMessage = Cob.X(s.Text, 100);
            return;
        }
        await ReadOrderLineAsync(ds, s, i, ct);
        if (h.Failed) return;
        await DefaultBinAsync(ds, i, ct);
        if (h.Failed) return;
        if (l.LQty > l.LAllowed)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0004";
            s.Text = Cob.Str(200, "Line ", Cob.Trim(l.LItem), " may despatch at most ", Cob.Ed(l.LAllowed, "Z(9)9.9(6)"),
                " - accepted less cancelled less already ", "despatched.");
            h.Message = s.Text;
            l.LMessage = Cob.X(s.Text, 100);
            l.LStatus = "REFUSED";
            h.MessageCount++;
            return;
        }
        await ConvertQtyAsync(ds, i, ct);
        if (h.Failed) return;
        await CheckAvailableAsync(ds, s, i, ct);
        if (h.Failed) return;
        await ReadUnitCostAsync(ds, s, i, ct);
    }

    /// <summary>2425-DEFAULT-BIN (CR2607): the confirmed pick line's bin, else the owned accepted balance that can supply.</summary>
    private async Task DefaultBinAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var l = ds.Lines[i - 1];
        if (!Cob.Spaces(l.LWhse) && !Cob.Spaces(l.LLocation)) return;
        string site = "", whse = "", locn = "", lot = "", serial = "";
        if (!Cob.Spaces(ds.PicklistRef))
        {
            try
            {
                var row = await sql.SingleAsync(BinFromPickSql,
                [
                    SqlioParm.Text(ds.Header.Company), SqlioParm.Text(ds.PicklistRef), SqlioParm.Text(l.LItem),
                    SqlioParm.Text(CobolNumeric.SqlioInteger(l.LSolId).Trim()),
                ], W100, ct);
                if (row.Found)
                {
                    site = Cob.X(row.Raw(0), 10);
                    whse = Cob.X(row.Raw(1), 20);
                    locn = Cob.X(row.Raw(2), 20);
                    lot = Cob.X(row.Raw(3), 30);
                    serial = Cob.X(row.Raw(4), 40);
                }
            }
            catch (SqlioException ex)
            {
                SqlFailed(ds, ex);
                return;
            }
        }
        if (Cob.Spaces(whse) || Cob.Spaces(locn))
        {
            site = ""; whse = ""; locn = ""; lot = ""; serial = "";
            try
            {
                var row = await sql.SingleAsync(BinFromStockSql,
                [
                    SqlioParm.Text(ds.Header.Company), SqlioParm.Text(l.LItem), SqlioParm.Text(ds.Site), SqlioParm.Text(ds.Site),
                    SqlioParm.Number(l.LQty),
                ], W100, ct);
                if (row.Found)
                {
                    site = Cob.X(row.Raw(0), 10);
                    whse = Cob.X(row.Raw(1), 20);
                    locn = Cob.X(row.Raw(2), 20);
                    lot = Cob.X(row.Raw(3), 30);
                    serial = Cob.X(row.Raw(4), 40);
                }
            }
            catch (SqlioException ex)
            {
                SqlFailed(ds, ex);
                return;
            }
        }
        if (Cob.Spaces(ds.Site)) ds.Site = site;
        if (Cob.Spaces(l.LWhse)) l.LWhse = whse;
        if (Cob.Spaces(l.LLocation)) l.LLocation = locn;
        if (Cob.Spaces(l.LLot)) l.LLot = lot;
        if (Cob.Spaces(l.LSerial)) l.LSerial = serial;
    }

    /// <summary>2430-READ-ORDER-LINE: remaining demand, description, unit and kit; a line not OPEN refuses (DSP0003).</summary>
    private async Task ReadOrderLineAsync(DespatchArea ds, State s, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(OrderLineSql,
            [
                SqlioParm.Text(h.Company), SqlioParm.Text(ds.OrderNumber), SqlioParm.Text(CobolNumeric.SqlioInteger(l.LOrderLine).Trim()),
            ], W100, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        if (!row.Found)
        {
            h.ReturnCode = 16;
            h.ReasonCode = "DSP0002";
            s.Text = Cob.Str(200, "Order line ", Cob.Ed(l.LOrderLine, "-(9)9"), " of ", Cob.Trim(ds.OrderNumber), " is not known.");
            h.Message = s.Text;
            return;
        }
        l.LSolId = (long)Cob.St(row.NumVal(0), 17, 0);
        if (Cob.Spaces(l.LItem)) l.LItem = Cob.X(row.Raw(1), 40);
        if (Cob.Spaces(l.LRevision)) l.LRevision = Cob.X(row.Raw(2), 4);
        l.LDesc = Cob.X(row.Raw(3), 60);
        l.LAllowed = Cob.St(row.NumVal(4), 13, 6);
        if (l.LAllowed < 0) l.LAllowed = 0;
        if (Cob.Spaces(l.LUom)) l.LUom = Cob.X(row.Raw(5), 10);
        l.LKitRef = Cob.X(row.Raw(9), 30);
        var status = Cob.X(row.Raw(7), 100);
        if (status != "OPEN")
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0003";
            s.Text = Cob.Str(200, "Order line is ", Cob.Trim(status), " and has nothing outstanding to despatch.");
            h.Message = s.Text;
            l.LMessage = Cob.X(s.Text, 100);
        }
    }

    /// <summary>2440-CONVERT-QTY: UOMCONV TOSTOCK (a missing conversion refuses, DSP0003).</summary>
    private async Task ConvertQtyAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        var uc = new UomconvCommarea
        {
            ItemCode = l.LItem,
            FromUom = l.LUom,
            UsageType = "SALES",
            EffDate = CobolDate.FromYyyymmdd(ds.DespatchDate),
            EnteredQty = l.LQty,
        };
        var r = await uomconv.ExecuteAsync(UomconvFunctions.ToStock, uc, Itm(ds), ct);
        if (r.ReturnCode >= 8)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0003";
            h.Message = Cob.X(r.Message, 200);
            l.LMessage = Cob.X(r.Message, 100);
            return;
        }
        l.LStockQty = Cob.St(uc.StockQty, 13, 6);
        l.LFactor = Cob.St(uc.Factor, 9, 9);
    }

    /// <summary>2450-CHECK-AVAILABLE: free plus own-demand stock must cover the stock quantity (DSP0004).</summary>
    private async Task CheckAvailableAsync(DespatchArea ds, State s, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        var av = new AvailqCommarea
        {
            ItemCode = l.LItem,
            Revision = l.LRevision,
            Site = ds.Site,
            Warehouse = l.LWhse,
            LotCode = l.LLot,
            SerialNo = l.LSerial,
            OwnerType = "OWNED",
            AsAt = CobolDate.FromYyyymmdd(ds.DespatchDate),
            DemandType = "SALESORDER",
            DemandRef = Cob.X(ds.OrderNumber, 40),
            DemandLine = l.LOrderLine,
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, av, Itm(ds), ct);
        if (r.ReturnCode >= 8)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0004";
            h.Message = Cob.X(r.Message, 200);
            return;
        }
        var qty = Cob.St(av.QtyFree + av.QtyOwnDemand, 13, 6);
        l.LAvailable = qty;
        if (l.LStockQty > qty)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0004";
            s.Text = Cob.Str(200, "Only ", Cob.Ed(qty, "Z(9)9.9(6)"), " of ", Cob.Trim(l.LItem), " is physically available to this order.");
            h.Message = s.Text;
            l.LMessage = Cob.X(s.Text, 100);
            l.LStatus = "REFUSED";
            h.MessageCount++;
        }
    }

    /// <summary>2460-READ-UNIT-COST: a missing comparable cost refuses (DSP0007); COGS = cost x stock quantity ROUNDED.</summary>
    private async Task ReadUnitCostAsync(DespatchArea ds, State s, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(UnitCostSql,
                [SqlioParm.Text(h.Company), SqlioParm.Text(l.LItem), SqlioParm.Text(ds.Site), SqlioParm.Text(ds.Site)], W100, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        if (!row.Found)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0007";
            s.Text = Cob.Str(200, "No comparable cost is held for ", Cob.Trim(l.LItem), "; a missing cost is not treated as zero.");
            h.Message = s.Text;
            l.LMessage = Cob.X(s.Text, 100);
            h.MessageCount++;
            return;
        }
        var cost = Cob.St(row.NumVal(0), 11, 6);
        l.LUnitCost = cost;
        l.LCostMeth = Cob.X(row.Raw(1), 4);
        l.LCogsGbp = Cob.Rd(cost * l.LStockQty, 11, 2);
    }

    /// <summary>2500-WRITE-HEADER: so.Despatch CONFIRMED (200 of the 300 snapshot characters; pack count and weight dropped).</summary>
    private async Task WriteHeaderAsync(DespatchArea ds, CancellationToken ct)
    {
        var h = ds.Header;
        try
        {
            var row = await sql.SingleAsync(InsertHeaderSql,
            [
                SqlioParm.Text(h.Company), SqlioParm.Text(ds.Site), SqlioParm.Text(ds.DespatchRef), SqlioParm.Text(ds.Customer),
                SqlioParm.Text(ds.OrderNumber), SqlioParm.Text(Cob.D8(ds.DespatchDate)), SqlioParm.Text(Cob.D8(ds.PostingDate)),
                SqlioParm.Integer(ds.FiscalYear), SqlioParm.Integer(ds.PeriodNo), SqlioParm.Text(Cob.X(ds.AddressSnap, 200)),
                SqlioParm.Text(ds.Incoterms), SqlioParm.Text(ds.IncotermsPlace), SqlioParm.Text(ds.CarrierCode), SqlioParm.Text(ds.CarrierRef),
                SqlioParm.Text(ds.IsExport == "Y" ? "1" : "0"), SqlioParm.Text(ds.DestCountry), SqlioParm.Text(ds.CustomerEori),
                SqlioParm.Text(ds.ExportDeclRef), SqlioParm.Text(ds.IsCutoff == "Y" ? "1" : "0"), SqlioParm.Text(h.CorrelationId),
                SqlioParm.Text(h.User), SqlioParm.Text(h.User),
            ], W100, ct);
            ds.DespatchId = (long)Cob.St(row.NumVal(0), 17, 0);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
        }
    }

    /// <summary>2550-WRITE-LINES: per line insert, STKMOVE ISSUE, STKRESV CONSUME, demand update, COGS total.</summary>
    private async Task WriteLinesAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        ds.CogsGbp = 0;
        for (var i = 1; i <= ds.LineCnt; i++)
        {
            if (h.Failed) continue;
            var l = ds.Lines[i - 1];
            l.LLineNum = i;
            await InsertLineAsync(ds, i, ct);
            if (!h.Failed) await MoveStockAsync(ds, s, i, ct);
            if (!h.Failed) await ConsumeReservationAsync(ds, i, ct);
            if (!h.Failed) await UpdateDemandAsync(ds, i, ct);
            if (!h.Failed)
            {
                ds.CogsGbp = Cob.St(ds.CogsGbp + l.LCogsGbp, 11, 2);
                l.LStatus = "CONFIRMED";
            }
        }
    }

    /// <summary>2560-INSERT-LINE (6 of the 9 factor decimals reach the row through SQLIO-NUM-EDIT).</summary>
    private async Task InsertLineAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var l = ds.Lines[i - 1];
        try
        {
            await sql.ExecAsync(InsertLineSql,
            [
                SqlioParm.Integer(ds.DespatchId), SqlioParm.Integer(l.LLineNum), SqlioParm.Integer(l.LSolId), SqlioParm.Text(ds.OrderNumber),
                SqlioParm.Integer(l.LOrderLine), SqlioParm.Integer(l.LSchedule), SqlioParm.Text(l.LItem), SqlioParm.Text(l.LRevision),
                SqlioParm.Text(l.LDesc), SqlioParm.Number(l.LPicked), SqlioParm.Number(l.LQty), SqlioParm.Text(l.LUom),
                SqlioParm.Number(l.LFactor), SqlioParm.Number(l.LStockQty), SqlioParm.Text(l.LWhse), SqlioParm.Text(l.LLocation),
                SqlioParm.Text(l.LLot), SqlioParm.Text(l.LSerial), SqlioParm.Number(l.LUnitCost), SqlioParm.Number(l.LCogsGbp),
                SqlioParm.Text(l.LCostMeth), SqlioParm.Text(l.LKitRef),
            ], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
        }
    }

    /// <summary>2570-MOVE-STOCK: STKMOVE ISSUE under the derived correlation id &lt;id&gt;-Mnnn (IC-ITM-STKMOVE, no own unit).</summary>
    private async Task MoveStockAsync(DespatchArea ds, State s, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        SubCorr(ds, s, "-M" + Cob.Digits(i, 3));
        var sm = new StkmoveCommarea
        {
            ItemCode = l.LItem,
            Revision = l.LRevision,
            Site = ds.Site,
            Warehouse = l.LWhse,
            Location = l.LLocation,
            LotCode = l.LLot,
            SerialNo = l.LSerial,
            OwnerType = "OWNED",
            Condition = "ACCEPTED",
            Quantity = l.LQty,
            UomCode = l.LUom,
            UnitCost = l.LUnitCost,
            ValueGbp = l.LCogsGbp,
            MovementDate = CobolDate.FromYyyymmdd(ds.DespatchDate),
            PostingDate = CobolDate.FromYyyymmdd(ds.PostingDate),
            ReasonCode = "SALEDESP",
            SourceModule = "SO",
            SourceDocType = "DESPATCH",
            SourceDocRef = Cob.X(ds.DespatchRef, 40),
            SourceDocLine = l.LLineNum,
            Reason = Cob.Str(200, "Despatch ", Cob.Trim(ds.DespatchRef), " against sales order ", Cob.Trim(ds.OrderNumber)),
        };
        var r = await stkmove.ExecuteAsync(StkmoveFunctions.Issue, sm, Itm(ds, s.SubCorr), ct);
        if (r.ReturnCode >= 8)
        {
            h.ReturnCode = r.ReturnCode;
            h.ReasonCode = "DSP0004";
            h.Message = Cob.X(r.Message, 200);
            l.LMessage = Cob.X(r.Message, 100);
            h.MessageCount++;
            return;
        }
        l.LMoveId = sm.MovementId;
        l.LCogsGbp = Cob.St(sm.ValueGbp, 11, 2);
        l.LUnitCost = Cob.St(sm.UnitCost, 11, 6);
        if (sm.StockQty != 0) l.LStockQty = Cob.St(sm.StockQty, 13, 6);
        await RecordMovementAsync(ds, i, ct);
    }

    /// <summary>2575-RECORD-MOVEMENT.</summary>
    private async Task RecordMovementAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var l = ds.Lines[i - 1];
        try
        {
            await sql.ExecAsync(RecordMovementSql,
            [
                SqlioParm.Integer(l.LMoveId), SqlioParm.Number(l.LCogsGbp), SqlioParm.Number(l.LUnitCost), SqlioParm.Integer(ds.DespatchId),
                SqlioParm.Integer(l.LLineNum),
            ], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
        }
    }

    /// <summary>2580-CONSUME-RESV: STKRESV CONSUME; no reservation (RSV0005) is legitimate.</summary>
    private async Task ConsumeReservationAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var h = ds.Header;
        var l = ds.Lines[i - 1];
        var sr = new StkresvCommarea
        {
            ItemCode = l.LItem,
            Revision = l.LRevision,
            Site = ds.Site,
            LotCode = l.LLot,
            SerialNo = l.LSerial,
            DemandType = "SALESORDER",
            DemandRef = Cob.X(ds.OrderNumber, 40),
            DemandLine = l.LOrderLine,
            QtyRequested = l.LStockQty,
        };
        var r = await stkresv.ExecuteAsync(StkresvFunctions.Consume, sr, Itm(ds), ct);
        l.LResId = sr.ReservationId;
        if (r.ReturnCode >= 8 && r.ReasonCode != "RSV0005")
        {
            h.ReturnCode = r.ReturnCode;
            h.ReasonCode = "DSP0004";
            h.Message = Cob.X(r.Message, 200);
            l.LMessage = Cob.X(r.Message, 100);
            h.MessageCount++;
        }
    }

    /// <summary>2590-UPDATE-DEMAND: the order line and the schedule move together (no conversion, no update lock, SME-2238).</summary>
    private async Task UpdateDemandAsync(DespatchArea ds, int i, CancellationToken ct)
    {
        var l = ds.Lines[i - 1];
        try
        {
            await sql.ExecAsync(UpdateDemandSql,
            [
                SqlioParm.Number(l.LQty), SqlioParm.Integer(l.LSolId), SqlioParm.Number(l.LQty), SqlioParm.Number(l.LQty),
                SqlioParm.Integer(l.LSolId), SqlioParm.Integer(l.LSchedule),
            ], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        l.LRemaining = Cob.St(l.LAllowed - l.LQty, 13, 6);
    }

    /// <summary>2600-POST-COGS: zero COGS is RC 04 DSP0007 without a journal; otherwise the mapped legs are staged and posted once.</summary>
    private async Task PostCogsAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        if (ds.CogsGbp == 0)
        {
            h.ReturnCode = 4;
            h.ReasonCode = "DSP0007";
            s.Text = Cob.Str(200, "The despatch carries no cost value, so no ", "cost of sale has been posted.");
            h.Message = s.Text;
            return;
        }
        await ReadEventMapAsync(ds, s, ct);
        if (h.Failed) return;
        SubCorr(ds, s, "-COGS");
        var clear = await gl.ClearStageAsync(s.SubCorr, ct);
        if (clear.Failed)
        {
            SqlFailed(ds, clear.Error!);
            return;
        }
        var narr = Cob.Str(200, "Cost of sale on despatch ", Cob.Trim(ds.DespatchRef), " - control passes ", Cob.Trim(ds.ControlPoint));
        var lineNo = 0;
        foreach (var leg in s.Legs)
        {
            if (h.Failed) break;
            lineNo++;
            var money = Cob.Ed(ds.CogsGbp, "-(11)9.99");
            var dr = leg.DrCr == "DR";
            var staged = await gl.StageStatementAsync(StageLegSql,
            [
                SqlioParm.Text(s.SubCorr), SqlioParm.Integer(lineNo), SqlioParm.Text(h.Company), SqlioParm.Text(leg.Account),
                SqlioParm.Text(dr ? money : "0"), SqlioParm.Text(dr ? "0" : money), SqlioParm.Text(leg.CostCentre), SqlioParm.Text(leg.Project),
                SqlioParm.Text(narr), SqlioParm.Text(ds.Customer), SqlioParm.Text(ds.DespatchRef), SqlioParm.Text(h.User),
            ], ct);
            if (staged.Failed) SqlFailed(ds, staged.Error!);
        }
        if (h.Failed) return;
        var post = await gl.PostAsync(new GlPostRequest
        {
            CompanyCode = h.Company,
            CorrelationId = s.SubCorr,
            UserCode = h.User,
            Description = Cob.Str(200, "Cost of sale, despatch ", Cob.Trim(ds.DespatchRef)),
            PostingDate = ds.PostingDate,
            JournalType = "SO",
            DocumentDate = ds.DespatchDate,
            CurrencyCode = "GBP",
            SourceModule = "SO",
            SourceRef = ds.DespatchRef,
            EventCode = CogsEvent,
            SeriesCode = "JNL",
            ArgumentCount = 12,
        }, W100[..5], ct);
        if (post.Failed)
        {
            h.ReturnCode = 12;
            h.ReasonCode = "DSP0007";
            h.Message = Cob.X(post.ErrorText, 200);
            h.MessageCount++;
            return;
        }
        if (post.NoRow)
        {
            h.ReturnCode = 12;
            h.ReasonCode = "DSP0007";
            h.Message = "The cost of sale posting returned no result.";
            return;
        }
        ds.JournalId = (long)Cob.St(post.Row!.NumVal(0), 17, 0);
        ds.JournalNo = Cob.X(post.Row.Raw(1), 30);
        try
        {
            await sql.ExecAsync(StampHeaderSql,
                [SqlioParm.Text(Cob.Ed(ds.CogsGbp, "-(11)9.99")), SqlioParm.Integer(ds.JournalId), SqlioParm.Integer(ds.DespatchId)], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
        }
    }

    /// <summary>2610-READ-EVENT-MAP: fewer than two active legs blocks the posting (RC 08 DSP0007).</summary>
    private async Task ReadEventMapAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        s.Legs.Clear();
        try
        {
            var rows = await sql.FetchAllAsync(EventMapSql, [SqlioParm.Text(h.Company), SqlioParm.Text(CogsEvent)], W100, ct);
            foreach (var r in rows)
            {
                if (s.Legs.Count >= 12) continue;
                s.Legs.Add(new Leg(Cob.X(r.Raw(0), 12), Cob.X(r.Raw(1), 12), Cob.X(r.Raw(2), 2), Cob.X(r.Raw(3), 20), Cob.X(r.Raw(4), 20)));
            }
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        if (s.Legs.Count < 2)
        {
            h.ReturnCode = 8;
            h.ReasonCode = "DSP0007";
            s.Text = Cob.Str(200, "No approved account mapping exists for ", CogsEvent, "; the cost of sale is blocked rather than ",
                "posted to a suspense account.");
            h.Message = s.Text;
            h.MessageCount++;
        }
    }

    /// <summary>2700-AUDIT-CONTROL: the control point and its basis in the attributable history.</summary>
    private async Task AuditControlAsync(DespatchArea ds, CancellationToken ct)
    {
        await audit.WriteAsync(Caller(ds), new AuditlogArea
        {
            EntityType = "so.Despatch",
            EntityKey = Cob.X(ds.DespatchRef, 100),
            Action = "CONFIRM",
            FieldName = "ControlPassesAt",
            NewValue = ds.ControlPoint,
            Reason = Cob.X(ds.ControlBasis, 200),
            Source = "DESPATCH",
            Sensitive = "N",
        }, ct);
    }

    /// <summary>2900-CONFIRM-WORDS.</summary>
    private static void ConfirmWords(DespatchArea ds, State s)
    {
        var h = ds.Header;
        s.Text = Cob.Str(200, "Despatch ", Cob.Trim(ds.DespatchRef), " confirmed; cost of sale GBP ", Cob.Ed(ds.CogsGbp, "-(11)9.99"),
            " posted once as ", Cob.Trim(ds.JournalNo));
        h.Message = s.Text;
        if (ds.IsCutoff == "Y")
        {
            h.ReturnCode = 4;
            h.ReasonCode = "DSP0005";
            h.Message = Cob.Str(200, Cob.Trim(h.Message), "; flagged for period cut off review.");
            h.MessageCount++;
        }
    }

    /// <summary>4000-ENQUIRE / 4100-READ-LINES: the despatch as recorded.</summary>
    private async Task EnquireAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        var h = ds.Header;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(EnquireSql, [SqlioParm.Text(h.Company), SqlioParm.Text(ds.DespatchRef)], W100, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        if (!row.Found)
        {
            h.ReturnCode = 16;
            h.ReasonCode = "DSP0009";
            s.Text = Cob.Str(200, "Despatch ", Cob.Trim(ds.DespatchRef), " is not known in this company.");
            h.Message = s.Text;
            return;
        }
        ds.DespatchId = (long)Cob.St(row.NumVal(0), 17, 0);
        ds.Customer = Cob.X(row.Raw(1), 20);
        ds.OrderNumber = Cob.X(row.Raw(2), 30);
        ds.DespatchDate = Digits8(row.Raw(3));
        ds.PostingDate = Digits8(row.Raw(4));
        ds.Status = Cob.X(row.Raw(5), 12);
        ds.CogsGbp = Cob.St(row.NumVal(6), 11, 2);
        ds.JournalId = (long)Cob.St(row.NumVal(7), 17, 0);
        ds.IsCutoff = Cob.X(row.Raw(8), 1);
        ds.Incoterms = Cob.X(row.Raw(9), 3);
        ds.IsExport = Cob.X(row.Raw(10), 1);
        ds.Site = Cob.X(row.Raw(11), 10);
        IReadOnlyList<SqlioRow> lines;
        try
        {
            lines = await sql.FetchAllAsync(EnquireLinesSql, [SqlioParm.Integer(ds.DespatchId)], W100, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ds, ex);
            return;
        }
        ds.LineCnt = 0;
        foreach (var r in lines)
        {
            if (ds.LineCnt >= 30) continue;
            ds.LineCnt++;
            var l = ds.Lines[ds.LineCnt - 1];
            l.LLineNum = (int)Cob.St(r.NumVal(0), 9, 0);
            l.LOrderLine = (int)Cob.St(r.NumVal(1), 9, 0);
            l.LSchedule = (int)Cob.St(r.NumVal(2), 9, 0);
            l.LSolId = (long)Cob.St(r.NumVal(3), 17, 0);
            l.LItem = Cob.X(r.Raw(4), 40);
            l.LRevision = Cob.X(r.Raw(5), 4);
            l.LQty = Cob.St(r.NumVal(6), 13, 6);
            l.LUom = Cob.X(r.Raw(7), 10);
            l.LStockQty = Cob.St(r.NumVal(8), 13, 6);
            l.LWhse = Cob.X(r.Raw(9), 20);
            l.LLocation = Cob.X(r.Raw(10), 20);
            l.LLot = Cob.X(r.Raw(11), 30);
            l.LSerial = Cob.X(r.Raw(12), 40);
            l.LCogsGbp = Cob.St(r.NumVal(13), 11, 2);
            l.LStatus = "CONFIRMED";
        }
    }

    /// <summary>9700-SUB-CORR: the caller's id (cut only when the suffix would not fit 36) plus the suffix.</summary>
    private static void SubCorr(DespatchArea ds, State s, string suffix)
    {
        var baseId = CobolText.Fit(ds.Header.CorrelationId, 36).TrimEnd(' ');
        var suf = CobolText.Fit(suffix, 8).TrimEnd(' ');
        var baseLen = baseId.Length;
        if (baseLen + suf.Length > 36) baseLen = 36 - suf.Length;
        s.SubCorr = Cob.X(baseId[..baseLen] + suf, 36);
    }

    /// <summary>9400-BEGIN.</summary>
    private async Task BeginAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        try
        {
            await uow.BeginAsync(ct);
            s.InTran = true;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            Severe(ds, ex.Message);
        }
    }

    /// <summary>9450-COMMIT.</summary>
    private async Task CommitAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        s.InTran = false;
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            Severe(ds, ex.Message);
        }
    }

    /// <summary>9500-ROLLBACK: nothing partly shipped; the despatch id, journal and COGS are cleared.</summary>
    private async Task RollbackAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        await uow.RollbackAsync(ct);
        s.InTran = false;
        ds.DespatchId = 0;
        ds.JournalId = 0;
        ds.CogsGbp = 0;
        ds.JournalNo = "";
        ds.Status = "ROLLEDBACK";
    }

    /// <summary>9600-IDEM-END: COMPLETED with the despatch reference when RC is below 08, else FAILED.</summary>
    private async Task IdemEndAsync(DespatchArea ds, State s, CancellationToken ct)
    {
        s.IdemOpen = false;
        var h = ds.Header;
        var ok = h.ReturnCode < 8 && !Cob.Spaces(ds.DespatchRef);
        var caller = s.IdemCaller with { CorrelationId = h.CorrelationId };
        await idempot.EndAsync(caller, ok ? "COMPLETED" : "FAILED", ok ? ds.DespatchRef : "", Cob.X(h.Message, 200), ct);
        h.Function = "CONFIRM";
    }

    private static int Digits8(string raw)
    {
        var t = CobolText.Fit(raw, 8);
        var v = 0;
        foreach (var ch in t) v = v * 10 + (ch is >= '0' and <= '9' ? ch - '0' : 0);
        return v;
    }

    private static void Reject(DespatchArea ds, State s)
    {
        ds.Header.ReturnCode = 8;
        ds.Header.Message = Cob.X(s.Text, 200);
        ds.Header.MessageCount++;
        s.Bad = true;
    }

    /// <summary>9300-SQL-FAILED.</summary>
    private static void SqlFailed(DespatchArea ds, SqlioException ex) => Severe(ds, ex.DriverText);

    private static void Severe(DespatchArea ds, string text)
    {
        ds.Header.ReturnCode = 12;
        ds.Header.ReasonCode = "SQL0001";
        ds.Header.Message = Cob.X(text, 200);
        ds.Header.MessageCount++;
    }
}
