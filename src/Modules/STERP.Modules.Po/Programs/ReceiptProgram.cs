using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// RECEIPT (cobol/online/po/RECEIPT.cob): goods receipt - VALIDATE (lines, traceability, location, TOLCHK QTY, values; no
/// database work), CONFIRM (one unit: NUMGEN PORCP, duplicate delivery note, header, STKMOVE RECEIPT + line per line, GRNI
/// journal through POPOST, po.usp_ReceiptDeliveryState CONFIRMED) and REVERSE (STKMOVE ISSUE, reversal journal, delivery
/// state REVERSED). 8300/8400: the program owns the transaction when the connection was in autocommit (BEGN .. COMT/ROLB),
/// else a savepoint rolled back on failure. TRANSIT has no production caller (spec 12.2) and is not delivered.
/// SQLIO null flags are bound per statement (BC-PO-02, keep-stateless).
/// </summary>
internal sealed class ReceiptProgram(
    ISqlRunner runner, IDocumentNumberService numbers, IStockMovementService stkmove, TolchkProgram tolchk, PopostProgram popost)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
        public bool DbReady;
        public bool Held;
        public bool OwnCorr;
        public string Corr = "";
        public string CorrShort = "";
        public string PostCorr = "";
        public int MoveDate;
        public bool UowActive;
        public bool UowOwn;
        public int OkLines;
    }

    public async Task RunAsync(CaHeader ca, ReceiptArea rc, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, rc, w);
        if (w.Fatal) return;
        if (ca.Function is "CONFIRM" or "TRANSIT" or "REVERSE")
        {
            await BeginUowAsync(ca, rc, w, ct);
            if (ca.Failed) return;
        }
        switch (ca.Function)
        {
            case "VALIDATE":
                await ValidateAsync(ca, rc, w, ct);
                break;
            case "CONFIRM":
                await ValidateAsync(ca, rc, w, ct);
                if (!ca.Failed) await ConfirmAsync(ca, rc, w, ct);
                break;
            case "TRANSIT":
                throw new NotSupportedException("RECEIPT TRANSIT has no production caller and is not delivered (STERP-PO 12.2).");
            case "REVERSE":
                await ReverseAsync(ca, rc, w, ct);
                break;
            default:
                ca.ReasonCode = "RCP0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by RECEIPT");
                ca.ReturnCode = 8;
                break;
        }
        if (w.UowActive) await EndUowAsync(ca, rc, w, ct);
    }

    /// <summary>1000-INIT.</summary>
    private static void Init(CaHeader ca, ReceiptArea rc, Work w)
    {
        ca.Program = "RECEIPT";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        rc.ReceiptId = 0; rc.TotalGbp = 0; rc.GrniGbp = 0;
        rc.PpvGbp = 0; rc.LinesAccepted = 0; rc.LinesHeld = 0;
        rc.LinesQuarant = 0;
        rc.JournalNumber = "";
        rc.WasDuplicate = "N";
        rc.IsTransit = "N";
        rc.Status = "DRAFT";
        if (Cob.Spaces(rc.Ownership)) rc.Ownership = "RECEIPT";
        if (Cob.Spaces(rc.DupPolicy)) rc.DupPolicy = "BLOCK";
        if (Cob.Spaces(rc.Currency)) rc.Currency = "GBP";
        if (rc.FxRate == 0) rc.FxRate = 1;
        if (rc.PhysicalDate == 0) rc.PhysicalDate = ca.BusinessDate;
        if (rc.PostingDate == 0) rc.PostingDate = rc.PhysicalDate;
        w.MoveDate = rc.PhysicalDate;
        if (Cob.Spaces(rc.ReceivedBy)) rc.ReceivedBy = ca.User;
        w.OwnCorr = Cob.Spaces(ca.CorrelationId);
        Correlation(ca, rc, w);
        if (rc.PhysicalDate > ca.BusinessDate)
        {
            ca.ReasonCode = "RCP0013";
            ca.Message = Cob.Msg("The goods cannot be received before they arrive; the physical date is in the future");
            Reject(ca, rc, w);
            return;
        }
        if (rc.Ownership == "INCOTERMS" && Cob.Spaces(rc.ShipEvidence))
        {
            ca.ReasonCode = "RCP0009";
            ca.Message = Cob.Msg("Ownership passed at the supplier's door, so the shipment evidence (bill of lading or air waybill) " +
                "must be recorded before the goods can be taken into stock in transit");
            Reject(ca, rc, w);
            return;
        }
        if (rc.Ownership == "INCOTERMS") rc.IsTransit = "Y";
        if (ca.Function is "CONFIRM" or "REVERSE" or "TRANSIT") w.DbReady = true;
    }

    /// <summary>1050-CORRELATION: the receipt's own id, the line stem (30 characters) and the GRNI journal key.</summary>
    private static void Correlation(CaHeader ca, ReceiptArea rc, Work w)
    {
        if (w.OwnCorr)
        {
            w.Corr = Cob.Str(64, "RCP-", Cob.Trim(ca.Company), "-", Cob.Trim(rc.ReceiptRef));
            ca.CorrelationId = Cob.X(w.Corr, 36);
        }
        else
        {
            w.Corr = Cob.X(ca.CorrelationId, 64);
        }
        w.CorrShort = Cob.X(CobolText.Fit(w.Corr, 64)[..30], 30);
        w.PostCorr = Cob.Str(64, Cob.Trim(w.CorrShort), "-GRNI");
    }

    /// <summary>1100-NUMBER: NUMGEN PORCP when the caller presented no reference.</summary>
    private async Task NumberAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        var n = await numbers.NextAsync(new KernelCaller(ca.Company, ca.Site, ca.User, rc.PhysicalDate, ca.CorrelationId), "PORCP", ct);
        if (n.Header.ReturnCode > 4)
        {
            ca.ReasonCode = "RCP0012";
            ca.Message = Cob.X(n.Header.Message, 200);
            Reject(ca, rc, w);
            return;
        }
        rc.ReceiptRef = Cob.X(n.DocumentNumber, 30);
        if (w.OwnCorr) Correlation(ca, rc, w);
    }

    /// <summary>1200-DUPLICATE: the same supplier delivery note on another live receipt (driver errors ignored).</summary>
    private async Task DuplicateAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(rc.DeliveryNote)) return;
        var r = await _sql.Sngl(
            "SELECT TOP 1 ReceiptRef FROM po.Receipt WHERE CompanyCode = ? AND SupplierCode = ? AND DeliveryNoteRef = ? AND Status " +
            "<> 'REVERSED' AND ReceiptRef <> ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(rc.Supplier), PoSql.Tx(rc.DeliveryNote), PoSql.Tx(rc.ReceiptRef)], [60], ct);
        if (r.Ok && !Cob.Spaces(r.Row.Raw(0)))
        {
            rc.WasDuplicate = "Y";
            ca.ReasonCode = "RCP0007";
            ca.Message = Cob.Msg("Delivery note ", Cob.Trim(rc.DeliveryNote), " was already received from this supplier on ", r.Row.Text(0));
            if (rc.DupPolicy == "BLOCK") Reject(ca, rc, w);
            else ca.ReturnCode = 4;
        }
    }

    // ------------------------------------------------------------------ VALIDATE

    /// <summary>2000-VALIDATE.</summary>
    private async Task ValidateAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        if (rc.LineCnt <= 0)
        {
            ca.ReasonCode = "RCP0001";
            ca.Message = Cob.Msg("A receipt must have at least one line");
            Reject(ca, rc, w);
            return;
        }
        if (rc.LineCnt > 20) rc.LineCnt = 20;
        if (w.DbReady && Cob.Spaces(rc.ReceiptRef))
        {
            await NumberAsync(ca, rc, w, ct);
            if (ca.Failed) return;
        }
        if (w.DbReady)
        {
            await DuplicateAsync(ca, rc, w, ct);
            if (ca.Failed) return;
        }
        w.OkLines = 0;
        rc.TotalGbp = 0;
        rc.PpvGbp = 0;
        for (var i = 0; i < rc.LineCnt; i++) OneLine(ca, rc, rc.Lines[i], w);
        rc.LinesAccepted = (int)CaJson.Digits(w.OkLines, 4);
        if (ca.ReturnCode >= 8) return;
        if (w.Held)
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "RCP0006";
            ca.Message = Cob.Msg("One or more lines are above the approved over delivery tolerance; the order must be amended and " +
                "approved before the delivery can be confirmed");
        }
        if (w.OkLines == 0 && !w.Held)
        {
            ca.ReasonCode = "RCP0003";
            ca.Message = Cob.Msg("No line carries a receivable quantity");
            Reject(ca, rc, w);
        }
    }

    /// <summary>2100-ONE-LINE.</summary>
    private void OneLine(CaHeader ca, ReceiptArea rc, ReceiptLine l, Work w)
    {
        l.LAccepted = 0; l.LQuarant = 0;
        l.LOverQty = 0; l.LOutstand = 0;
        l.LValueGbp = 0; l.LPpvGbp = 0;
        l.LMovement = 0;
        l.LOutcome = "OK";
        l.LReason = "";
        if (l.LFactor == 0) l.LFactor = 1;
        if (Cob.Spaces(l.LTrace)) l.LTrace = "NONE";
        if (Cob.Spaces(l.LStocked)) l.LStocked = "Y";
        if (l.LReceived <= 0)
        {
            LineReject(ca, l, "RCP0003", Cob.Msg("Item ", Cob.Trim(l.LItem), " must carry a positive receivable quantity"));
            return;
        }
        l.LStockQty = Cob.R(l.LReceived * l.LFactor, 13, 6);
        if (l.LTrace == "LOT" && Cob.Spaces(l.LLot))
        {
            LineReject(ca, l, "RCP0004", Cob.Msg("Item ", Cob.Trim(l.LItem), " is lot traceable; a lot number must be recorded at " +
                "goods inward because it cannot be reconstructed afterwards"));
            return;
        }
        if (l.LTrace == "SERIAL" && Cob.Spaces(l.LSerial))
        {
            LineReject(ca, l, "RCP0004", Cob.Msg("Item ", Cob.Trim(l.LItem), " is serial traceable; a serial number must be recorded at goods inward"));
            return;
        }
        if (l.LTrace == "SERIAL" && l.LStockQty != 1)
        {
            LineReject(ca, l, "RCP0004", Cob.Msg("A serial traceable receipt line carries exactly one unit; split the delivery into one " +
                "line for each serial number"));
            return;
        }
        if (l.LStocked == "Y" && Cob.Spaces(l.LLocation) && rc.Ownership != "DROPSHIP" && rc.Ownership != "INCOTERMS")
        {
            LineReject(ca, l, "RCP0005", Cob.Msg("A stocked receipt must say where the goods were put away; the location is not optional"));
            return;
        }
        Tolerance(ca, rc, l);
        if (l.LOutcome == "HOLD")
        {
            rc.LinesHeld = (int)CaJson.Digits(rc.LinesHeld + 1, 4);
            w.Held = true;
            return;
        }
        if (l.LInspect == "Y")
        {
            l.LQuarant = l.LReceived;
            l.LAccepted = 0;
            rc.LinesQuarant = (int)CaJson.Digits(rc.LinesQuarant + 1, 4);
        }
        else
        {
            l.LAccepted = l.LReceived;
        }
        var lineGbp = Cob.R(l.LReceived * l.LPrice / rc.FxRate, 13, 2);
        l.LValueGbp = lineGbp;
        rc.TotalGbp = Cob.T(rc.TotalGbp + lineGbp, 13, 2);
        if (l.LStdCost > 0)
        {
            var stdGbp = Cob.R(l.LStockQty * l.LStdCost, 13, 2);
            l.LPpvGbp = Cob.T(lineGbp - stdGbp, 13, 2);
            rc.PpvGbp = Cob.T(rc.PpvGbp + l.LPpvGbp, 13, 2);
        }
        w.OkLines++;
    }

    private static void LineReject(CaHeader ca, ReceiptLine l, string reason, string message)
    {
        l.LOutcome = "REJECT";
        l.LReason = reason;
        ca.ReasonCode = reason;
        ca.ReturnCode = 8;
        ca.Message = message;
    }

    /// <summary>2200-TOLERANCE: TOLCHK QTY with the caller-passed over-delivery percentage, action BLOCK.</summary>
    private void Tolerance(CaHeader ca, ReceiptArea rc, ReceiptLine l)
    {
        if (l.LOrdered <= 0)
        {
            l.LOutstand = 0;
            return;
        }
        var tk = CaHeader.Call("QTY", ca.Company, ca.Site, ca.User, ca.BusinessDate, "");
        var tc = new TolchkArea
        {
            OrderNumber = rc.OrderNumber,
            LineNum = l.LPoLine,
            Supplier = rc.Supplier,
            ItemCode = l.LItem,
            RuleSource = "PASSED",
            OrderedQty = l.LOrdered,
            CancelledQty = l.LCancelled,
            CountedQty = l.LCounted,
            ProposedQty = l.LReceived,
            OverPct = l.LOverPct,
            ActionOver = "BLOCK",
        };
        tolchk.Run(tk, tc);
        l.LOutstand = tc.OutstandingQty;
        l.LOverQty = tc.OverQty;
        if (tk.ReturnCode >= 6)
        {
            l.LOutcome = "HOLD";
            l.LReason = Cob.X(tk.ReasonCode, 8);
        }
        else if (tk.ReturnCode == 4)
        {
            l.LOutcome = "WARN";
            l.LReason = Cob.X(tk.ReasonCode, 8);
        }
    }

    // ------------------------------------------------------------------ CONFIRM

    /// <summary>3000-CONFIRM.</summary>
    private async Task ConfirmAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        await WriteHeadAsync(ca, rc, w, ct);
        if (ca.Failed) return;
        for (var i = 0; i < rc.LineCnt && !ca.Failed; i++)
        {
            var l = rc.Lines[i];
            if (l.LOutcome != "REJECT" && l.LOutcome != "HOLD")
            {
                await MoveStockAsync(ca, rc, l, w, ct);
                if (!ca.Failed) await WriteLineAsync(ca, rc, l, w, ct);
            }
        }
        if (ca.Failed) return;
        rc.GrniGbp = rc.TotalGbp;
        await PostGrniAsync(ca, rc, w, ct);
        if (!ca.Failed)
        {
            await StampAsync(ca, rc, w, ct);
            if (!ca.Failed) rc.Status = "CONFIRMED";
        }
    }

    /// <summary>3100-WRITE-HEAD: optional references 5..14 bound as NULL when blank (8100-NULL-BLANKS).</summary>
    private async Task WriteHeadAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        static Sterp.Platform.Kernel.Data.SqlioParm N(string v) => SqlioParm.NullIfSpaces(v);
        var r = await _sql.Sngl(
            "INSERT po.Receipt (CompanyCode, SiteCode, ReceiptRef, SupplierCode, PurchaseOrderNumber, DeliveryNoteRef, PhysicalDate, " +
            "PostingDate, WarehouseCode, OwnershipBasis, IsStockInTransit, ShipmentEvidenceRef, ImportEntryRef, SubcontractJobRef, " +
            "Status, CorrelationId, ReceivedBy, CreatedBy) OUTPUT INSERTED.ReceiptId VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,'DRAFT',?,?,?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(ca.Site), PoSql.Tx(rc.ReceiptRef), PoSql.Tx(rc.Supplier), N(rc.OrderNumber),
             N(rc.DeliveryNote), N(Cob.D8(rc.PhysicalDate)), N(Cob.D8(rc.PostingDate)), N(rc.Warehouse), N(rc.Ownership),
             N(rc.IsTransit == "Y" ? "1" : "0"), N(rc.ShipEvidence), N(rc.ImportEntry), N(rc.SubcontractJob), PoSql.Tx(w.Corr),
             PoSql.Tx(rc.ReceivedBy), PoSql.Tx(ca.User)],
            [30], ct);
        if (!r.Ok)
        {
            ca.ReasonCode = "RCP0010";
            ca.Message = Cob.X(r.Err, 200);
            Reject(ca, rc, w);
            return;
        }
        rc.ReceiptId = (long)CaJson.Digits((long)Cob.NumVal(r.Row.Raw(0)), 17);
    }

    /// <summary>3200-MOVE-STOCK: STKMOVE RECEIPT (QUARANTINE when inspection is needed); drop-ship and non-stock lines move nothing.</summary>
    private async Task MoveStockAsync(CaHeader ca, ReceiptArea rc, ReceiptLine l, Work w, CancellationToken ct)
    {
        if (rc.Ownership == "DROPSHIP" || l.LStocked != "Y") return;
        var corr = Cob.Str(36, Cob.Trim(w.CorrShort), "-L", Cob.Ed(l.LNum, "-(8)9"));
        var valueGbp = Cob.T(l.LValueGbp - l.LPpvGbp, 11, 2);
        var sm = new StkmoveCommarea
        {
            ItemCode = l.LItem,
            Revision = l.LRevision,
            Site = ca.Site,
            Warehouse = rc.Warehouse,
            Location = l.LLocation,
            LotCode = l.LLot,
            SerialNo = l.LSerial,
            OwnerType = rc.RetentionTitle == "Y" ? "ROT" : "OWNED",
            OwnerCode = rc.RetentionTitle == "Y" ? Cob.X(rc.Supplier, 20) : "",
            Condition = l.LInspect == "Y" ? "QUARANTINE" : "ACCEPTED",
            Quantity = l.LReceived,
            UomCode = l.LUom,
            ValueGbp = valueGbp,
            UnitCost = l.LStockQty > 0 ? Cob.R(valueGbp / l.LStockQty, 11, 6) : 0,
            MovementDate = CobolDate.FromYyyymmdd(w.MoveDate),
            PostingDate = CobolDate.FromYyyymmdd(rc.PostingDate),
            ExpiryDate = CobolDate.FromYyyymmdd(l.LExpiry),
            ReasonCode = "PORECEIPT",
            Reason = Cob.Str(200, "Goods received on ", Cob.Trim(rc.ReceiptRef), " against order ", Cob.Trim(rc.OrderNumber)),
            SourceModule = "PO",
            SourceDocType = "GOODSRECEIPT",
            SourceDocRef = Cob.X(rc.ReceiptRef, 40),
            SourceDocLine = l.LNum,
            ViaTransit = rc.IsTransit == "Y" ? "Y" : "",
        };
        var r = await stkmove.ExecuteAsync(StkmoveFunctions.Receipt, sm,
            new ItmCall(ca.Company, ca.Site, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), corr), ct);
        l.LMovement = sm.MovementId;
        if (sm.StockQty != 0)
        {
            l.LStockQty = sm.StockQty;
            l.LFactor = sm.Factor;
        }
        if (r.ReturnCode > 4)
        {
            l.LOutcome = "REJECT";
            l.LReason = "RCP0010";
            ca.ReturnCode = r.ReturnCode;
            ca.ReasonCode = "RCP0010";
            ca.Message = Cob.X(r.Message, 200);
            rc.TotalGbp = Cob.T(rc.TotalGbp - l.LValueGbp, 13, 2);
            l.LValueGbp = 0;
        }
    }

    /// <summary>3300-WRITE-LINE.</summary>
    private async Task WriteLineAsync(CaHeader ca, ReceiptArea rc, ReceiptLine l, Work w, CancellationToken ct)
    {
        var inspect = l.LInspect == "Y";
        var r = await _sql.Exec(
            "INSERT po.ReceiptLine (ReceiptId, LineNum, PurchaseOrderNumber, PurchaseOrderLine, ScheduleNo, ItemCode, Revision, " +
            "Description, ReceivedQty, UomCode, ConversionFactor, StockQty, AcceptedQty, QuarantineQty, OverDeliveryQty, LocationCode, " +
            "SupplierLotRef, LotCode, SerialNo, UnitPrice, ProvisionalValueGBP, StandardCostGBP, PriceVarianceGBP, QualityStatus, " +
            "InspectionRequired, MovementId) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            [PoSql.Int(rc.ReceiptId), PoSql.Int(l.LNum), PoSql.Tx(rc.OrderNumber), PoSql.Int(l.LPoLine), PoSql.Int(l.LSchedule),
             PoSql.Tx(l.LItem), PoSql.Tx(l.LRevision), PoSql.Tx(l.LDesc), PoSql.Num(l.LReceived), PoSql.Tx(l.LUom), PoSql.Num(l.LFactor),
             PoSql.Num(l.LStockQty), PoSql.Num(l.LAccepted), PoSql.Num(l.LQuarant), PoSql.Num(l.LOverQty), PoSql.Tx(l.LLocation),
             PoSql.Tx(l.LSuppLot), PoSql.Tx(l.LLot), PoSql.Tx(l.LSerial), PoSql.Num(l.LPrice), PoSql.Num(l.LValueGbp),
             PoSql.Num(l.LStdCost), PoSql.Num(l.LPpvGbp), PoSql.Tx(inspect ? "QUARANTINE" : "ACCEPTED"), PoSql.Tx(inspect ? "1" : "0"),
             l.LMovement == 0 ? PoSql.Null : PoSql.Int(l.LMovement)],
            ct);
        if (r.Code != 0)
        {
            ca.ReasonCode = "RCP0011";
            ca.Message = Cob.X(r.Err, 200);
            Reject(ca, rc, w);
        }
    }

    /// <summary>3500-POST-GRNI: stock (and PPV) against GRNI through POPOST.</summary>
    private async Task PostGrniAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        if (rc.TotalGbp == 0) return;
        var pp = new PopostArea
        {
            Func = "POST",
            Company = ca.Company,
            Site = ca.Site,
            User = ca.User,
            BusinessDate = ca.BusinessDate,
            Correlation = w.PostCorr,
            PostingDate = rc.PostingDate,
            DocumentDate = rc.PhysicalDate,
            Currency = "GBP",
            FxRate = 1,
            EventCode = "PO.RECEIPT",
            SourceRef = Cob.X(rc.ReceiptRef, 40),
            SubledgerType = "PO",
            SubledgerRef = rc.Supplier,
            EvidenceRef = Cob.X(rc.DeliveryNote, 120),
            Description = Cob.Str(200, "Goods received ", Cob.Trim(rc.ReceiptRef), " from ", Cob.Trim(rc.Supplier), " on order ",
                Cob.Trim(rc.OrderNumber)),
        };
        var stockGbp = Cob.T(rc.TotalGbp - rc.PpvGbp, 13, 2);
        pp.Lines.Add(new PopostLine { Account = "1100", Dr = stockGbp, Cr = 0, Narrative = "Goods received into stock" });
        if (rc.PpvGbp != 0)
        {
            pp.Lines.Add(new PopostLine
            {
                Account = "5100", Dr = rc.PpvGbp, Cr = 0, CostCtr = "PURCH", Narrative = "Purchase price variance on receipt",
            });
        }
        pp.Lines.Add(new PopostLine { Account = "2010", Dr = 0, Cr = rc.TotalGbp, Narrative = "Goods received not invoiced" });
        await popost.RunAsync(pp, ct);
        rc.JournalNumber = Cob.X(pp.JournalNumber, 30);
        if (pp.WasDuplicate == "Y") rc.WasDuplicate = "Y";
        if (pp.Rc > 4)
        {
            ca.ReturnCode = pp.Rc;
            ca.ReasonCode = "RCP0011";
            ca.Message = Cob.X(pp.Message, 200);
        }
    }

    /// <summary>3600-STAMP: po.usp_ReceiptDeliveryState CONFIRMED (order line link, quantities, delivery status, GRNI, journal).</summary>
    private async Task StampAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "EXEC po.usp_ReceiptDeliveryState ?, ?, ?, 'CONFIRMED', ?, ?, ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(ca.Site), PoSql.Tx(rc.ReceiptRef), PoSql.Num(rc.GrniGbp), PoSql.Tx(w.PostCorr),
             PoSql.Tx(ca.User)], ct);
        CheckWrite(ca, rc, w, r);
    }

    /// <summary>8200-CHECK-WRITE: any SQLCODE other than 0 is RCP0010.</summary>
    private static void CheckWrite(CaHeader ca, ReceiptArea rc, Work w, Sq r)
    {
        if (r.Code != 0)
        {
            ca.ReasonCode = "RCP0010";
            ca.Message = Cob.X(r.Err, 200);
            Reject(ca, rc, w);
        }
    }

    // ------------------------------------------------------------------ REVERSE

    /// <summary>5000-REVERSE: a true reversal of a CONFIRMED receipt.</summary>
    private async Task ReverseAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(rc.ReverseReason))
        {
            ca.ReasonCode = "RCP0012";
            ca.Message = Cob.Msg("A reversal must say why; an unexplained reversal of a goods receipt is not auditable");
            Reject(ca, rc, w);
            return;
        }
        var r = await _sql.Sngl(
            "SELECT CAST(ReceiptId AS varchar(30)), CAST(GrniValueGBP AS varchar(30)) FROM po.Receipt WHERE CompanyCode = ? AND " +
            "ReceiptRef = ? AND SiteCode = ? AND Status = 'CONFIRMED'",
            [PoSql.Tx(ca.Company), PoSql.Tx(rc.ReverseRef), PoSql.Tx(ca.Site)], [30, 30], ct);
        if (r.Code != 0 || Cob.Spaces(r.Row.Raw(0)))
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "RCP0012";
            ca.Message = Cob.Msg("Receipt ", Cob.Trim(rc.ReverseRef), " is not a confirmed receipt of this company");
            return;
        }
        rc.GrniGbp = Cob.T(r.Row.NumVal(1), 13, 2);
        await ReverseStockAsync(ca, rc, ct);
        await ReversePostAsync(ca, rc, ct);
        if (!ca.Failed)
        {
            var s = await _sql.Exec(
                "EXEC po.usp_ReceiptDeliveryState ?, ?, ?, 'REVERSED', 0, NULL, ?",
                [PoSql.Tx(ca.Company), PoSql.Tx(ca.Site), PoSql.Tx(rc.ReverseRef), PoSql.Tx(ca.User)], ct);
            CheckWrite(ca, rc, w, s);
            if (!ca.Failed) rc.Status = "REVERSED";
        }
    }

    /// <summary>5200-REV-STOCK: STKMOVE ISSUE per stocked line (no correlation key).</summary>
    private async Task ReverseStockAsync(CaHeader ca, ReceiptArea rc, CancellationToken ct)
    {
        for (var i = 0; i < rc.LineCnt; i++)
        {
            var l = rc.Lines[i];
            if (!(l.LStockQty > 0 && l.LStocked == "Y" && rc.Ownership != "DROPSHIP")) continue;
            var valueGbp = Cob.T(l.LValueGbp - l.LPpvGbp, 11, 2);
            var sm = new StkmoveCommarea
            {
                ItemCode = l.LItem,
                Site = ca.Site,
                Warehouse = rc.Warehouse,
                Location = l.LLocation,
                LotCode = l.LLot,
                SerialNo = l.LSerial,
                Quantity = l.LReceived,
                Revision = l.LRevision,
                Condition = l.LQuarant > 0 ? "QUARANTINE" : "",
                UomCode = l.LUom,
                ValueGbp = valueGbp,
                UnitCost = Cob.R(valueGbp / l.LStockQty, 11, 6),
                MovementDate = CobolDate.FromYyyymmdd(ca.BusinessDate),
                ReasonCode = "PORCPTREV",
                Reason = rc.ReverseReason,
                SourceModule = "PO",
                SourceDocType = "RECEIPTREVERSAL",
                SourceDocRef = Cob.X(rc.ReverseRef, 40),
                SourceDocLine = l.LNum,
            };
            var r = await stkmove.ExecuteAsync(StkmoveFunctions.Issue, sm,
                new ItmCall(ca.Company, ca.Site, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), ""), ct);
            l.LMovement = sm.MovementId;
            if (r.ReturnCode > 4)
            {
                ca.ReturnCode = r.ReturnCode;
                ca.ReasonCode = "RCP0010";
                ca.Message = Cob.X(r.Message, 200);
            }
        }
    }

    /// <summary>5400-REV-POST: GRNI back against stock; journal key RCPREV-&lt;company&gt;-&lt;ref&gt;.</summary>
    private async Task ReversePostAsync(CaHeader ca, ReceiptArea rc, CancellationToken ct)
    {
        if (rc.GrniGbp == 0 || ca.Failed) return;
        var pp = new PopostArea
        {
            Func = "POST",
            Company = ca.Company,
            Site = ca.Site,
            User = ca.User,
            BusinessDate = ca.BusinessDate,
            Correlation = Cob.Str(64, "RCPREV-", Cob.Trim(ca.Company), "-", Cob.Trim(rc.ReverseRef)),
            PostingDate = ca.BusinessDate,
            DocumentDate = ca.BusinessDate,
            Currency = "GBP",
            FxRate = 1,
            EventCode = "PO.RECEIPT",
            SourceRef = Cob.X(rc.ReverseRef, 40),
            SubledgerType = "PO",
            SubledgerRef = rc.Supplier,
            Description = Cob.Str(200, "Reversal of goods receipt ", Cob.Trim(rc.ReverseRef), " - ", Cob.Trim(rc.ReverseReason)),
        };
        pp.Lines.Add(new PopostLine { Account = "2010", Dr = rc.GrniGbp, Cr = 0, Narrative = "GRNI reversed with the receipt" });
        pp.Lines.Add(new PopostLine { Account = "1100", Dr = 0, Cr = rc.GrniGbp, Narrative = "Stock removed with the receipt" });
        await popost.RunAsync(pp, ct);
        rc.JournalNumber = Cob.X(pp.JournalNumber, 30);
        if (pp.Rc > 4)
        {
            ca.ReturnCode = pp.Rc;
            ca.ReasonCode = "RCP0011";
            ca.Message = Cob.X(pp.Message, 200);
        }
    }

    // ------------------------------------------------------------------ unit of work

    /// <summary>8300-BEGIN-UOW: own the transaction when the connection is in autocommit, then a savepoint.</summary>
    private async Task BeginUowAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        var uow = runner.UnitOfWork;
        if (uow.TransactionState == 1)
        {
            try
            {
                await uow.BeginAsync(ct);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                ca.Message = Cob.X(runner.DriverText(ex), 200);
                ca.ReasonCode = "RCP0010";
                Reject(ca, rc, w);
                return;
            }
            w.UowOwn = true;
        }
        w.UowActive = true;
        var r = await _sql.Exec("IF @@TRANCOUNT = 0 BEGIN TRANSACTION; SAVE TRANSACTION RECEIPT", [], ct);
        if (r.Error)
        {
            ca.Message = Cob.X(r.Err, 200);
            ca.ReasonCode = "RCP0010";
            Reject(ca, rc, w);
            await EndUowAsync(ca, rc, w, ct);
        }
    }

    /// <summary>8400-END-UOW: commit unless RC 8-16; inside a caller's transaction only the savepoint is rolled back.</summary>
    private async Task EndUowAsync(CaHeader ca, ReceiptArea rc, Work w, CancellationToken ct)
    {
        var uow = runner.UnitOfWork;
        if (w.UowOwn)
        {
            if (ca.Failed)
            {
                try
                {
                    await uow.RollbackAsync(ct);
                }
                catch (Microsoft.Data.SqlClient.SqlException)
                {
                    // ROLB failures were not inspected once the receipt had failed (RECEIPT.cob 8400)
                }
            }
            else
            {
                try
                {
                    await uow.CommitAsync(ct);
                }
                catch (Microsoft.Data.SqlClient.SqlException ex)
                {
                    ca.Message = Cob.X(runner.DriverText(ex), 200);
                    ca.ReasonCode = "RCP0010";
                    Reject(ca, rc, w);
                }
            }
        }
        else if (ca.Failed)
        {
            await _sql.Exec("IF XACT_STATE() = 1 ROLLBACK TRANSACTION RECEIPT", [], ct);
        }
        w.UowActive = false;
    }

    /// <summary>9000-REJECT.</summary>
    private static void Reject(CaHeader ca, ReceiptArea rc, Work w)
    {
        ca.ReturnCode = 8;
        w.Fatal = true;
        rc.Status = "REJECTED";
    }
}
