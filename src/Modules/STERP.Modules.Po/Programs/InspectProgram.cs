using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// INSPECT (cobol/online/po/INSPECT.cob) RECORD: validates an inspection of a quarantined receipt line and disposes it -
/// STKMOVE RELEASE of the good quantity (without the quality-hold id, F07-02 reproduced), STKMOVE SCRAP + POPOST of the
/// scrap value, then the receipt-line UPDATE (its quality status is still the initial ACCEPTED when written). No BEGN:
/// each callee and statement commits on its own. ENQUIRE has no production caller (spec 12.2) and is not delivered.
/// </summary>
internal sealed class InspectProgram(ISqlRunner runner, IStockMovementService stkmove, PopostProgram popost)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
        public string Corr = "";
    }

    public async Task RunAsync(CaHeader ca, InspectArea ins, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, ins, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "RECORD":
                await ValidateAsync(ca, ins, w, ct);
                if (!ca.Failed) await DisposeAsync(ca, ins, w, ct);
                break;
            case "ENQUIRE":
                throw new NotSupportedException("INSPECT ENQUIRE has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "INS0006";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by INSPECT");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, InspectArea ins, Work w)
    {
        ca.Program = "INSPECT";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ins.RemainingQty = 0; ins.ToGoodQty = 0;
        ins.ToScrapQty = 0; ins.HeldForRetQty = 0;
        ins.ScrapValueGbp = 0;
        ins.MovementId = 0; ins.MovementId2 = 0;
        ins.JournalNumber = "";
        ins.QualityStatus = "ACCEPTED";
        ins.Outcome = "OK";
        if (ins.InspectDate == 0) ins.InspectDate = ca.BusinessDate;
        if (Cob.Spaces(ins.Inspector)) ins.Inspector = ca.User;
        if (Cob.Spaces(ins.Disposition)) ins.Disposition = "HOLD";
        w.Corr = Cob.Str(64, "INS-", Cob.Trim(ca.Company), "-", Cob.Trim(ins.ReceiptRef), "-", Cob.Ed(ins.ReceiptLine, "-(8)9"));
        if (!Cob.Spaces(ca.CorrelationId)) w.Corr = Cob.X(ca.CorrelationId, 64);
    }

    /// <summary>2000-VALIDATE: add-back, quarantine ceiling, reject reason, concession authority, certificate.</summary>
    private async Task ValidateAsync(CaHeader ca, InspectArea ins, Work w, CancellationToken ct)
    {
        if (ins.QuarantineQty <= 0) await ReadLineAsync(ca, ins, w, ct);
        if (w.Fatal) return;
        if (ins.InspectedQty <= 0) ins.InspectedQty = Cob.T(ins.AcceptedQty + ins.RejectedQty + ins.ReworkQty, 13, 6);
        var sum = Cob.T(ins.AcceptedQty + ins.RejectedQty + ins.ReworkQty, 13, 6);
        if (sum != ins.InspectedQty)
        {
            ca.ReasonCode = "INS0002";
            ca.Message = Cob.Msg("Accepted, rejected and reworked quantities must add back to the inspected quantity of ",
                Cob.Qty(ins.InspectedQty));
            Reject(ca, ins, w);
            return;
        }
        if (ins.InspectedQty > ins.QuarantineQty)
        {
            ca.ReasonCode = "INS0003";
            ca.Message = Cob.Msg("Only ", Cob.Qty(ins.QuarantineQty), " is held in quarantine on this receipt line; an inspection " +
                "cannot sign off stock that is not there");
            Reject(ca, ins, w);
            return;
        }
        if (ins.RejectedQty > 0 && Cob.Spaces(ins.RejectReason))
        {
            ca.ReasonCode = "INS0004";
            ca.Message = Cob.Msg("A rejection must state its reason; the supplier performance measure and any claim both depend on it");
            Reject(ca, ins, w);
            return;
        }
        if (ins.Disposition == "CONCESSION" && Cob.Spaces(ins.ConcessionBy))
        {
            ca.ReasonCode = "INS0005";
            ca.Message = Cob.Msg("Accepting goods outside specification is a named decision; record who authorised the concession " +
                "and its reference");
            Reject(ca, ins, w);
            return;
        }
        if (ins.CertRequired == "Y" && Cob.Spaces(ins.CertRef))
        {
            ca.ReasonCode = "INS0009";
            ca.Message = Cob.Msg("This item needs a certificate of conformity; record its reference before the goods leave quarantine");
            Reject(ca, ins, w);
        }
    }

    /// <summary>1500-READ-LINE: the quarantined quantity of the receipt line.</summary>
    private async Task ReadLineAsync(CaHeader ca, InspectArea ins, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(l.QuarantineQty AS varchar(30)) FROM po.ReceiptLine l JOIN po.Receipt r ON r.ReceiptId = l.ReceiptId WHERE " +
            "r.CompanyCode = ? AND r.ReceiptRef = ? AND l.LineNum = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(ins.ReceiptRef), PoSql.Int(ins.ReceiptLine)], [60], ct);
        if (r.Code != 0 || Cob.Spaces(r.Row.Raw(0)))
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "INS0001";
            ca.Message = Cob.Msg("Receipt line not found, or it is not awaiting inspection");
            w.Fatal = true;
            return;
        }
        ins.QuarantineQty = Cob.T(r.Row.NumVal(0), 13, 6);
    }

    /// <summary>3000-DISPOSE.</summary>
    private async Task DisposeAsync(CaHeader ca, InspectArea ins, Work w, CancellationToken ct)
    {
        ins.ToGoodQty = Cob.T(ins.AcceptedQty + ins.ReworkQty, 13, 6);
        if (ins.Disposition == "CONCESSION") ins.ToGoodQty = Cob.T(ins.ToGoodQty + ins.RejectedQty, 13, 6);
        if (ins.Disposition == "SCRAP") ins.ToScrapQty = ins.RejectedQty;
        if (ins.Disposition is "RETURN" or "HOLD") ins.HeldForRetQty = ins.RejectedQty;
        ins.RemainingQty = Cob.T(ins.QuarantineQty - ins.ToGoodQty - ins.ToScrapQty, 13, 6);
        if (ins.ToGoodQty > 0) await ReleaseAsync(ca, ins, w, ct);
        if (ins.ToScrapQty > 0 && !ca.Failed) await ScrapAsync(ca, ins, w, ct);
        if (!ca.Failed) await UpdateLineAsync(ca, ins, ct);
        if (ins.RejectedQty > 0)
        {
            ca.ReturnCode = 4;
            ca.Message = Cob.Msg(Cob.Qty(ins.RejectedQty), " rejected with disposition ", Cob.Trim(ins.Disposition),
                "; the supplier quality measure has been updated");
        }
        if (ins.RemainingQty > 0) ins.QualityStatus = "QUARANTINE";
        else if (ins.ToScrapQty > 0 && ins.ToGoodQty == 0) ins.QualityStatus = "REJECTED";
    }

    /// <summary>3200-RELEASE: STKMOVE RELEASE QUARANTINE -> GOOD (no hold id is passed, F07-02).</summary>
    private async Task ReleaseAsync(CaHeader ca, InspectArea ins, Work w, CancellationToken ct)
    {
        var concession = ins.Disposition == "CONCESSION";
        var sm = new StkmoveCommarea
        {
            ItemCode = ins.ItemCode,
            Site = ca.Site,
            Warehouse = ins.Warehouse,
            Location = ins.Location,
            ToLocation = Cob.Spaces(ins.GoodLocation) ? "" : ins.GoodLocation,
            LotCode = ins.LotCode,
            SerialNo = ins.SerialNo,
            Condition = "QUARANTINE",
            ToCondition = "GOOD",
            Quantity = ins.ToGoodQty,
            UomCode = ins.UomCode,
            UnitCost = ins.UnitCost,
            MovementDate = CobolDate.FromYyyymmdd(ins.InspectDate),
            HoldType = "QUALITY",
            HoldOutcome = "ACCEPTED",
            ReasonCode = "INSPACCEPT",
            Reason = concession
                ? Cob.Str(200, "Accepted on concession ", Cob.Trim(ins.ConcessionRef), " authorised by ", Cob.Trim(ins.ConcessionBy))
                : Cob.Str(200, "Inspection passed on receipt ", Cob.Trim(ins.ReceiptRef)),
            ApprovalRef = concession ? ins.ConcessionRef : "",
            SourceModule = "PO",
            SourceDocType = "INSPECTION",
            SourceDocRef = Cob.X(ins.ReceiptRef, 40),
            SourceDocLine = ins.ReceiptLine,
        };
        var r = await stkmove.ExecuteAsync(StkmoveFunctions.Release, sm,
            new ItmCall(ca.Company, ca.Site, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), Cob.X(w.Corr, 36)), ct);
        ins.MovementId = sm.MovementId;
        if (r.ReturnCode > 4)
        {
            ca.ReturnCode = r.ReturnCode;
            ca.ReasonCode = "INS0007";
            ca.Message = Cob.X(r.Message, 200);
        }
    }

    /// <summary>3400-SCRAP: STKMOVE SCRAP, then the scrap journal through POPOST when the value is not zero.</summary>
    private async Task ScrapAsync(CaHeader ca, InspectArea ins, Work w, CancellationToken ct)
    {
        var sm = new StkmoveCommarea
        {
            ItemCode = ins.ItemCode,
            Site = ca.Site,
            Warehouse = ins.Warehouse,
            Location = ins.Location,
            LotCode = ins.LotCode,
            SerialNo = ins.SerialNo,
            Condition = "QUARANTINE",
            Quantity = ins.ToScrapQty,
            UomCode = ins.UomCode,
            UnitCost = ins.UnitCost,
            MovementDate = CobolDate.FromYyyymmdd(ins.InspectDate),
            ReasonCode = ins.RejectReason,
            Reason = ins.RejectNarr,
            SourceModule = "PO",
            SourceDocType = "INSPECTION",
            SourceDocRef = Cob.X(ins.ReceiptRef, 40),
            SourceDocLine = ins.ReceiptLine,
        };
        var r = await stkmove.ExecuteAsync(StkmoveFunctions.Scrap, sm,
            new ItmCall(ca.Company, ca.Site, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), Cob.Str(36, Cob.Trim(w.Corr), "-S")), ct);
        ins.MovementId2 = sm.MovementId;
        if (r.ReturnCode > 4)
        {
            ca.ReturnCode = r.ReturnCode;
            ca.ReasonCode = "INS0007";
            ca.Message = Cob.X(r.Message, 200);
            return;
        }
        ins.ScrapValueGbp = Cob.R(ins.ToScrapQty * ins.UnitCost, 13, 2);
        if (ins.ScrapValueGbp == 0) return;
        var pp = new PopostArea
        {
            Func = "POST",
            Company = ca.Company,
            Site = ca.Site,
            User = ca.User,
            BusinessDate = ca.BusinessDate,
            Correlation = Cob.Str(64, Cob.Trim(w.Corr), "-SCRAP"),
            PostingDate = ins.InspectDate,
            DocumentDate = ins.InspectDate,
            Currency = "GBP",
            FxRate = 1,
            EventCode = "PO.INSPSCRAP",
            SourceRef = Cob.X(ins.ReceiptRef, 40),
            SubledgerType = "PO",
            SubledgerRef = ins.Supplier,
            EvidenceRef = ins.CertRef,
            Description = Cob.Str(200, "Inspection scrap on receipt ", Cob.Trim(ins.ReceiptRef), " - ", Cob.Trim(ins.RejectReason)),
        };
        pp.Lines.Add(new PopostLine
        {
            Account = "5110", Dr = ins.ScrapValueGbp, Cr = 0, CostCtr = "PURCH", Narrative = "Goods scrapped at inspection",
        });
        pp.Lines.Add(new PopostLine { Account = "1100", Dr = 0, Cr = ins.ScrapValueGbp, Narrative = "Stock removed at inspection" });
        await popost.RunAsync(pp, ct);
        ins.JournalNumber = Cob.X(pp.JournalNumber, 30);
        if (pp.Rc > 4)
        {
            ca.ReturnCode = pp.Rc;
            ca.ReasonCode = "INS0008";
            ca.Message = Cob.X(pp.Message, 200);
        }
    }

    /// <summary>3600-UPDATE-LINE: the receipt line (fires po.tr_ReceiptLineAccepted); result not inspected.</summary>
    private async Task UpdateLineAsync(CaHeader ca, InspectArea ins, CancellationToken ct)
    {
        var remaining = PoSql.Num(ins.RemainingQty);
        await _sql.Exec(
            "UPDATE l SET l.AcceptedQty = l.AcceptedQty + ?, l.RejectedQty = l.RejectedQty + ?, l.QuarantineQty = ?, " +
            "l.QualityStatus = ?, l.InspectionRequired = CASE WHEN ? > 0 THEN 1 ELSE 0 END FROM po.ReceiptLine l JOIN po.Receipt r " +
            "ON r.ReceiptId = l.ReceiptId WHERE r.CompanyCode = ? AND r.ReceiptRef = ? AND l.LineNum = ?",
            [PoSql.Num(ins.ToGoodQty), PoSql.Num(ins.RejectedQty), remaining, PoSql.Tx(ins.QualityStatus), remaining,
             PoSql.Tx(ca.Company), PoSql.Tx(ins.ReceiptRef), PoSql.Int(ins.ReceiptLine)], ct);
    }

    private static void Reject(CaHeader ca, InspectArea ins, Work w)
    {
        ca.ReturnCode = 8;
        w.Fatal = true;
        ins.Outcome = "REJECTED";
    }
}
