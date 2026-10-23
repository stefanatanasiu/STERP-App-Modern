using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POORDER (cobol/online/po/POORDER.cob): purchase order capture and issue - CREATE (PAYTERMC APPLY, NUMGEN POORD),
/// ADDLINE (item governance, POPRICE PRICE, line value, delete+insert, re-total), ISSUE (POSUPCHK CHECK, PAYTERMC APPLY,
/// POAPPRV CHECK, BUDGCHK COMMIT per OPEN line, mark ISSUED, blanket balance), ENQUIRE and AMEND (POAMEND on the same
/// COMMAREA). No BEGN anywhere: every statement and callee autocommits. SCHEDULE, ACKNOWL, CALLOFF and COPY have no
/// production caller (spec 12.2) and are not delivered.
/// </summary>
internal sealed class PoorderProgram(
    ISqlRunner runner, IDocumentNumberService numbers, PopriceProgram poprice, PaytermcProgram paytermc,
    PosupchkProgram posupchk, PoapprvProgram poapprv, BudgchkProgram budgchk, PoamendProgram poamend)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
    }

    public async Task RunAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, or, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "CREATE":
                await CreateAsync(ca, or, w, ct);
                break;
            case "ADDLINE":
                await AddLineAsync(ca, or, w, ct);
                break;
            case "ISSUE":
                await IssueAsync(ca, or, w, ct);
                break;
            case "ENQUIRE":
                await ReadHeaderAsync(ca, or, w, ct);
                break;
            case "AMEND":
                await poamend.RunAsync(ca, or, ct);
                break;
            case "SCHEDULE" or "ACKNOWL" or "CALLOFF" or "COPY":
                throw new NotSupportedException($"POORDER {ca.Function} has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "ORD0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by POORDER");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, PoorderArea or, Work w)
    {
        ca.Program = "POORDER";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        or.OrderId = 0; or.LineId = 0; or.ScheduleId = 0;
        or.LineNetGbp = 0; or.LineVatGbp = 0;
        or.LineNonrecVat = 0; or.NetAmount = 0;
        or.VatAmount = 0; or.GrossAmount = 0;
        or.CommitmentGbp = 0; or.OutstandingQty = 0;
        or.ScheduledQty = 0; or.LineCnt = 0; or.ScheduleCnt = 0;
        or.BlanketQtyLeft = 0; or.BlanketValLeft = 0;
        or.PriceSource = ""; or.Status = ""; or.Outcome = "";
        or.NeedsReappr = "N"; or.MaterialChange = "N";
        if (or.OrderDate == 0) or.OrderDate = ca.BusinessDate;
        if (Cob.Spaces(or.OrderType)) or.OrderType = "STANDARD";
        if (Cob.Spaces(or.Currency)) or.Currency = "GBP";
        if (or.FxRate <= 0) or.FxRate = 1;
        if (Cob.Spaces(or.LineType)) or.LineType = "STOCK";
        if (or.PricePer <= 0) or.PricePer = 1;
        if (or.ConvFactor <= 0) or.ConvFactor = 1;
        if (Cob.Spaces(or.RecoverableVat)) or.RecoverableVat = "Y";
        if (Cob.Spaces(ca.Company) || Cob.Spaces(ca.Site))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0001";
            ca.Message = Cob.Msg("A company code and a site code are needed for purchase order maintenance");
            w.Fatal = true;
        }
    }

    // ------------------------------------------------------------------ CREATE

    private async Task CreateAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(or.Supplier))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0001";
            ca.Message = Cob.Msg("A purchase order must name a supplier");
            return;
        }
        if (or.OrderType is not ("STANDARD" or "BLANKET" or "CALLOFF" or "SUBCONTRACT" or "DROPSHIP" or "SERVICE"))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0008";
            ca.Message = Cob.Msg("Order type ", Cob.Trim(or.OrderType), " is not recognised");
            return;
        }
        if (or.OrderType == "CALLOFF" && Cob.Spaces(or.BlanketNumber))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0017";
            ca.Message = Cob.Msg("A call-off order must name the blanket order it is called off against");
            return;
        }
        await TermsAsync(ca, or, ct);
        if (ca.ReturnCode >= 8) return;
        if (Cob.Spaces(or.OrderNumber))
        {
            await NumberAsync(ca, or, w, ct);
            if (w.Fatal) return;
        }
        if (Cob.Spaces(or.Buyer)) or.Buyer = ca.User;
        var r = await _sql.Sngl(
            "INSERT po.PurchaseOrder (CompanyCode, SiteCode, OrderNumber, OrderType, BlanketOrderNumber, SupplierCode, BuyerUser, " +
            "OrderDate, CurrencyCode, FxRate, TermsCode, TermsSnapshot, IncotermsRule, IncotermsNamedPlace, OwnershipPassesAt, " +
            "DeliveryAddressSnapshot, TermsAndConditionsVersion, SupplierReference, BudgetCode, LinkedSalesOrderNumber, Status, " +
            "CorrelationId, CreatedBy) OUTPUT CAST(INSERTED.PurchaseOrderId AS varchar(20)) VALUES (?, ?, ?, ?, NULLIF(?, ''), ?, " +
            "NULLIF(?, ''), TRY_CONVERT(date, ?), ?, ?, NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), " +
            "NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), 'DRAFT', NULLIF(?, ''), ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(ca.Site), PoSql.Tx(or.OrderNumber), PoSql.Tx(or.OrderType), PoSql.Tx(or.BlanketNumber),
             PoSql.Tx(or.Supplier), PoSql.Tx(or.Buyer), PoSql.D8(or.OrderDate), PoSql.Tx(or.Currency), PoSql.Num(or.FxRate),
             PoSql.Tx(or.TermsCode), PoSql.Tx(or.TermsSnapshot), PoSql.Tx(or.Incoterms), PoSql.Tx(or.NamedPlace),
             PoSql.Tx(or.OwnershipAt), PoSql.Tx(or.DeliveryAddr), PoSql.Tx(or.TcVersion), PoSql.Tx(or.SupplierRef),
             PoSql.Tx(or.BudgetCode), PoSql.Tx(or.LinkedSo), PoSql.Tx(ca.CorrelationId), PoSql.Tx(ca.User)],
            [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.OrderId = (long)Cob.T(r.Row.NumVal(0), 17, 0);
        or.Status = "DRAFT";
        or.Outcome = "CAPTURED";
        if (ca.ReturnCode == 0)
        {
            ca.Message = Cob.Msg("Draft order ", Cob.Trim(or.OrderNumber), " opened for supplier ", Cob.Trim(or.Supplier), " on ",
                Cob.Trim(or.TermsSnapshot));
        }
    }

    /// <summary>2100-TERMS: PAYTERMC APPLY when a terms code is present; RC 6 becomes ORD0014.</summary>
    private async Task TermsAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        if (Cob.Spaces(or.TermsCode)) return;
        var tr = CaHeader.Call("APPLY", ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId);
        var pt = new PaytermcArea
        {
            Supplier = or.Supplier,
            OrderNumber = or.OrderNumber,
            TermsCode = or.TermsCode,
            OrderDate = or.OrderDate,
        };
        await paytermc.RunAsync(tr, pt, ct);
        if (!Cob.Spaces(pt.TermsSnapshot)) or.TermsSnapshot = pt.TermsSnapshot;
        if (tr.ReturnCode >= 4)
        {
            ca.ReturnCode = tr.ReturnCode;
            ca.ReasonCode = tr.ReasonCode;
            ca.Message = tr.Message;
        }
        if (tr.ReturnCode == 6) ca.ReasonCode = "ORD0014";
    }

    // ------------------------------------------------------------------ ADDLINE

    private async Task AddLineAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        await ReadHeaderAsync(ca, or, w, ct);
        if (w.Fatal) return;
        if (or.Status is "ISSUED" or "CLOSED" or "CANCELLED" or "ACKNOWLEDGED")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0003";
            ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " is ", Cob.Trim(or.Status), " - use AMEND rather than ADDLINE");
            return;
        }
        if (Cob.Spaces(or.Description) || or.OrderedQty <= 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0004";
            ca.Message = Cob.Msg("An order line needs a description and a quantity greater than zero");
            return;
        }
        var stock = or.LineType == "STOCK";
        if (stock && Cob.Spaces(or.ItemCode))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0018";
            ca.Message = Cob.Msg("A stock line must name an existing item; free text cannot create a stocked item");
            return;
        }
        if (stock)
        {
            await ItemGovernedAsync(ca, or, w, ct);
            if (ca.ReturnCode >= 8) return;
        }
        if (Cob.Spaces(or.ItemCode) && Cob.Spaces(or.AccountCode))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0005";
            ca.Message = Cob.Msg("A non stock or service line needs a nominal account code");
            return;
        }
        if (stock && Cob.Spaces(or.DestSite)) or.DestSite = ca.Site;
        if (or.AutoPrice == "Y") await PriceAsync(ca, or, ct);
        Value(or);
        if (or.LineNum == 0) await NextLineAsync(ca, or, w, ct);
        await WriteLineAsync(ca, or, w, ct);
        if (w.Fatal) return;
        await RetotalAsync(ca, or, w, ct);
        if (ca.ReturnCode == 0)
        {
            ca.Message = Cob.Msg("Line ", Cob.Trim(or.Description), " valued at ", Cob.Amt(or.LineNetGbp), " added to order ",
                Cob.Trim(or.OrderNumber));
        }
    }

    /// <summary>3050-ITEM-GOVERNED: the item must exist and be purchasable (PO-003).</summary>
    private async Task ItemGovernedAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CASE WHEN i.IsPurchasable = 1 THEN 'Y' ELSE 'N' END FROM inv.Item i WHERE i.CompanyCode = ? AND i.ItemCode = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(or.ItemCode)], [1], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0018";
            ca.Message = Cob.Msg("Item ", Cob.Trim(or.ItemCode), " is not on the item master; a stocked item cannot be created from free text");
            return;
        }
        if (r.Row.Raw(0) != "Y")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0018";
            ca.Message = Cob.Msg("Item ", Cob.Trim(or.ItemCode), " is not flagged as purchasable on the item master");
        }
    }

    /// <summary>3100-PRICE: POPRICE PRICE at OR-ORDER-DATE; a failure is a warning and the typed price stays.</summary>
    private async Task PriceAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        var pc = CaHeader.Call("PRICE", ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId);
        var pr = new PopriceArea
        {
            Supplier = or.Supplier,
            ItemCode = or.ItemCode,
            UomCode = or.UomCode,
            Quantity = or.OrderedQty,
            PricingDate = or.OrderDate,
            Currency = or.Currency,
        };
        await poprice.RunAsync(pc, pr, ct);
        if (pc.ReturnCode >= 8)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = pc.ReasonCode;
            ca.Message = pc.Message;
            return;
        }
        or.UnitPrice = pr.UnitPrice;
        or.PricePer = pr.PricePer;
        or.DiscountPct = pr.DiscountPct;
        or.PriceSource = Cob.X(pr.PriceBasis, 20);
        if (or.PricePer <= 0) or.PricePer = 1;
    }

    /// <summary>3200-VALUE: six-decimal effective price, the line rounded once to two decimals.</summary>
    private static void Value(PoorderArea or)
    {
        var effective = Cob.R(or.UnitPrice / or.PricePer * (1 - or.DiscountPct / 100m), 11, 6);
        or.LineNetGbp = Cob.R(effective * or.OrderedQty * or.FxRate + or.ChargeAmt, 13, 2);
        or.LineVatGbp = Cob.R(or.LineNetGbp * (or.VatRatePct / 100m), 13, 2);
        or.LineNonrecVat = or.RecoverableVat == "Y" ? 0 : or.LineVatGbp;
    }

    private async Task NextLineAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(MAX(LineNum), 0) + 1 AS varchar(24)) FROM po.PurchaseOrderLine WHERE PurchaseOrderId = ?",
            [PoSql.Int(or.OrderId)], [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.LineNum = (int)r.Row.NumVal(0);
    }

    /// <summary>3400-WRITE-LINE: one batch - delete the line's schedules and the line, insert the line OPEN.</summary>
    private async Task WriteLineAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var id = PoSql.Int(or.OrderId);
        var line = PoSql.Int(or.LineNum);
        var r = await _sql.Sngl(
            "DELETE po.PurchaseSchedule WHERE PurchaseOrderLineId IN (SELECT PurchaseOrderLineId FROM po.PurchaseOrderLine WHERE " +
            "PurchaseOrderId = ? AND LineNum = ?); DELETE po.PurchaseOrderLine WHERE PurchaseOrderId = ? AND LineNum = ?; INSERT " +
            "po.PurchaseOrderLine (PurchaseOrderId, CompanyCode, OrderNumber, LineNum, ItemCode, Revision, Description, LineType, " +
            "OrderedQty, UomCode, ConversionFactor, UnitPrice, PricePerQty, PriceSource, DiscountPct, ChargeAmount, NetAmount, " +
            "VatCode, VatRatePct, ExpectedVatAmount, IsRecoverableVat, NonRecoverableVatGBP, AccountCode, DimCostCentre, " +
            "DimProject, JobRef, DestinationSiteCode, DestinationLocationCode, RequisitionNumber, LinkedSalesOrderNumber, " +
            "LinkedSalesOrderLine, Status) OUTPUT CAST(INSERTED.PurchaseOrderLineId AS varchar(20)) VALUES (?, ?, ?, ?, NULLIF(?, " +
            "''), NULLIF(?, ''), ?, ?, ?, NULLIF(?, ''), ?, ?, ?, NULLIF(?, ''), ?, ?, ?, NULLIF(?, ''), ?, ?, ?, ?, NULLIF(?, ''), " +
            "NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, 0), " +
            "'OPEN')",
            [id, line, id, line, id, PoSql.Tx(ca.Company), PoSql.Tx(or.OrderNumber), line, PoSql.Tx(or.ItemCode), PoSql.Tx(or.Revision),
             PoSql.Tx(or.Description), PoSql.Tx(or.LineType), PoSql.Num(or.OrderedQty), PoSql.Tx(or.UomCode), PoSql.Num(or.ConvFactor),
             PoSql.Num(or.UnitPrice), PoSql.Num(or.PricePer), PoSql.Tx(or.PriceSource), PoSql.Num(or.DiscountPct),
             PoSql.Num(or.ChargeAmt), PoSql.Num(or.LineNetGbp), PoSql.Tx(or.VatCode), PoSql.Num(or.VatRatePct),
             PoSql.Num(or.LineVatGbp), PoSql.Tx(or.RecoverableVat == "Y" ? "1" : "0"), PoSql.Num(or.LineNonrecVat),
             PoSql.Tx(or.AccountCode), PoSql.Tx(or.CostCentre), PoSql.Tx(or.Project), PoSql.Tx(or.JobRef), PoSql.Tx(or.DestSite),
             PoSql.Tx(or.DestLocation), PoSql.Tx(or.ReqNumber), PoSql.Tx(or.LinkedSo), PoSql.Int(or.LinkedSoLine)],
            [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (!r.NotFound) or.LineId = (long)Cob.T(r.Row.NumVal(0), 17, 0);
    }

    /// <summary>3500-RETOTAL: header totals from the live lines, then 8000-READ-HDR.</summary>
    private async Task RetotalAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE o SET NetAmount = t.Net, VatAmount = t.Vat, GrossAmount = t.Net + t.Vat, CommitmentGBP = t.Net + t.NonRec, " +
            "ModifiedBy = ?, ModifiedAt = SYSDATETIME() FROM po.PurchaseOrder o CROSS APPLY (SELECT ISNULL(SUM(l.NetAmount), 0) AS " +
            "Net, ISNULL(SUM(l.ExpectedVatAmount), 0) AS Vat, ISNULL(SUM(l.NonRecoverableVatGBP), 0) AS NonRec FROM " +
            "po.PurchaseOrderLine l WHERE l.PurchaseOrderId = o.PurchaseOrderId AND l.Status <> 'CANCELLED') t WHERE " +
            "o.PurchaseOrderId = ?",
            [PoSql.Tx(ca.User), PoSql.Int(or.OrderId)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        await ReadHeaderAsync(ca, or, w, ct);
    }

    // ------------------------------------------------------------------ ISSUE

    private async Task IssueAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        await ReadHeaderAsync(ca, or, w, ct);
        if (w.Fatal) return;
        if (or.LineCnt == 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0009";
            ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " has no lines and cannot be issued");
            return;
        }
        if (or.Status is "ISSUED" or "ACKNOWLEDGED")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "ORD0003";
            or.Outcome = "ISSUED";
            ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " has already been issued to the supplier");
            return;
        }
        await DiligenceAsync(ca, or, ct);
        if (ca.ReturnCode >= 6) { or.Outcome = "HELD"; return; }
        await TermsAsync(ca, or, ct);
        if (ca.ReturnCode >= 6) { or.Outcome = "HELD"; return; }
        await ApprovalAsync(ca, or, ct);
        if (ca.ReturnCode >= 6) { or.Outcome = "HELD"; return; }
        await CommitLinesAsync(ca, or, w, ct);
        if (w.Fatal) return;
        await MarkIssuedAsync(ca, or, w, ct);
        if (or.OrderType == "BLANKET") await BlanketBalanceAsync(ca, or, w, ct);
    }

    /// <summary>5100-DILIGENCE: POSUPCHK CHECK on the gross value at the order date.</summary>
    private async Task DiligenceAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        var dl = CaHeader.Call("CHECK", ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId);
        var dd = new PosupchkArea
        {
            Supplier = or.Supplier,
            OrderNumber = or.OrderNumber,
            OrderValueGbp = or.GrossAmount,
            CheckDate = or.OrderDate,
        };
        await posupchk.RunAsync(dl, dd, ct);
        if (dl.ReturnCode >= 6 || dd.CanOrder != "Y")
        {
            ca.ReturnCode = dl.ReturnCode == 8 ? 8 : 6;
            ca.ReasonCode = "ORD0013";
            ca.Message = dl.Message;
            return;
        }
        if (dl.ReturnCode == 4)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "ORD0013";
            ca.Message = dl.Message;
        }
    }

    /// <summary>5200-APPROVAL: POAPPRV CHECK; any non-zero answer is passed through with ORD0012.</summary>
    private async Task ApprovalAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        var ap = CaHeader.Call("CHECK", ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId);
        var av = new PoapprvArea { OrderNumber = or.OrderNumber };
        await poapprv.RunAsync(ap, av, ct);
        or.ContentHash = av.ContentHash;
        if (av.WasInvalidated == "Y") or.NeedsReappr = "Y";
        if (ap.ReturnCode != 0)
        {
            ca.ReturnCode = ap.ReturnCode;
            ca.ReasonCode = "ORD0012";
            ca.Message = ap.Message;
        }
    }

    /// <summary>5300-COMMIT-LINES: one BUDGCHK COMMIT per OPEN line; a hold marks the order fatal but the loop continues.</summary>
    private async Task CommitLinesAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Fetch(
            "SELECT CAST(l.PurchaseOrderLineId AS varchar(20)), CAST(l.NetAmount AS varchar(24)), CAST(l.NonRecoverableVatGBP AS " +
            "varchar(24)), ISNULL(l.AccountCode, ''), ISNULL(l.DimCostCentre, ''), ISNULL(l.DimProject, ''), CAST(l.OrderedQty AS " +
            "varchar(24)), CAST(l.LineNum AS varchar(12)) FROM po.PurchaseOrderLine l WHERE l.PurchaseOrderId = ? AND l.Status = " +
            "'OPEN' ORDER BY l.LineNum",
            [PoSql.Int(or.OrderId)], [20, 24, 24, 12, 20, 20, 24, 12], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.CommitmentGbp = 0;
        foreach (var row in r.Rows)
        {
            var bg = CaHeader.Call("COMMIT", ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId);
            var bc = new BudgchkArea
            {
                BudgetCode = or.BudgetCode,
                AccountCode = row.Text(3),
                CostCentre = row.Text(4),
                Project = row.Text(5),
                DocType = "PURCHASEORDER",
                DocRef = Cob.X(or.OrderNumber, 40),
                DocLine = (int)row.NumVal(7),
                Requester = or.Buyer,
                MovementDate = or.OrderDate,
                Quantity = Cob.T(row.NumVal(6), 13, 6),
                NetGbp = Cob.T(row.NumVal(1), 13, 2),
                NonrecVatGbp = Cob.T(row.NumVal(2), 13, 2),
            };
            await budgchk.RunAsync(bg, bc, ct);
            or.CommitmentGbp = Cob.T(or.CommitmentGbp + bc.ProposedGbp, 13, 2);
            if (bg.ReturnCode >= 6)
            {
                ca.ReturnCode = bg.ReturnCode;
                ca.ReasonCode = bg.ReasonCode;
                ca.Message = bg.Message;
                or.Outcome = "HELD";
                w.Fatal = true;
            }
        }
    }

    /// <summary>5400-MARK-ISSUED: guarded by status APPROVED/AMENDED; the row count is not checked.</summary>
    private async Task MarkIssuedAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE po.PurchaseOrder SET Status = 'ISSUED', IssuedAt = SYSDATETIME(), CommitmentGBP = ?, ModifiedBy = ?, " +
            "ModifiedAt = SYSDATETIME() WHERE PurchaseOrderId = ? AND Status IN ('APPROVED', 'AMENDED')",
            [PoSql.Num(or.CommitmentGbp), PoSql.Tx(ca.User), PoSql.Int(or.OrderId)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.Status = "ISSUED";
        or.Outcome = "ISSUED";
        ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " issued to ", Cob.Trim(or.Supplier), " committing ", Cob.Amt(or.CommitmentGbp));
    }

    /// <summary>5500-BLANKET-BAL: insert-only MERGE of the call-off balances.</summary>
    private async Task BlanketBalanceAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "MERGE po.BlanketBalance AS t USING (SELECT l.CompanyCode, l.OrderNumber, l.LineNum, l.ItemCode, l.Description, " +
            "l.OrderedQty, l.NetAmount, o.CurrencyCode FROM po.PurchaseOrderLine l JOIN po.PurchaseOrder o ON o.PurchaseOrderId = " +
            "l.PurchaseOrderId WHERE o.PurchaseOrderId = ? AND l.Status = 'OPEN') AS s ON t.CompanyCode = s.CompanyCode AND " +
            "t.BlanketOrderNumber = s.OrderNumber AND t.LineNum = s.LineNum WHEN NOT MATCHED THEN INSERT (CompanyCode, " +
            "BlanketOrderNumber, LineNum, ItemCode, Description, AgreedQty, AgreedValue, CurrencyCode, Status, ModifiedBy, " +
            "ModifiedAt) VALUES (s.CompanyCode, s.OrderNumber, s.LineNum, s.ItemCode, s.Description, s.OrderedQty, s.NetAmount, " +
            "s.CurrencyCode, 'OPEN', ?, SYSDATETIME());",
            [PoSql.Int(or.OrderId), PoSql.Tx(ca.User)], ct);
        if (r.Error) SqlFail(ca, w, r);
    }

    // ------------------------------------------------------------------ common

    /// <summary>8000-READ-HDR (order date and FX rate are not read back).</summary>
    private async Task ReadHeaderAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(o.PurchaseOrderId AS varchar(20)), o.Status, o.SupplierCode, o.CurrencyCode, ISNULL(o.TermsCode, ''), " +
            "CAST(o.NetAmount AS varchar(24)), CAST(o.VatAmount AS varchar(24)), CAST(o.GrossAmount AS varchar(24)), CAST((SELECT " +
            "COUNT(*) FROM po.PurchaseOrderLine l WHERE l.PurchaseOrderId = o.PurchaseOrderId AND l.Status <> 'CANCELLED') AS " +
            "varchar(12)), ISNULL(o.BudgetCode, ''), o.OrderType FROM po.PurchaseOrder o WHERE o.CompanyCode = ? AND " +
            "o.OrderNumber = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(or.OrderNumber)], [20, 14, 20, 3, 12, 24, 24, 24, 12, 20, 12], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "ORD0002";
            ca.Message = Cob.Msg("Purchase order ", Cob.Trim(or.OrderNumber), " is not on file for company ", Cob.Trim(ca.Company));
            w.Fatal = true;
            return;
        }
        var h = r.Row;
        or.OrderId = (long)Cob.T(h.NumVal(0), 17, 0);
        or.Status = h.Text(1);
        or.Supplier = h.Text(2);
        or.Currency = h.Text(3);
        if (Cob.Spaces(or.TermsCode)) or.TermsCode = h.Text(4);
        if (Cob.Spaces(or.BudgetCode)) or.BudgetCode = h.Text(9);
        or.OrderType = h.Text(10);
        or.NetAmount = Cob.T(h.NumVal(5), 13, 2);
        or.VatAmount = Cob.T(h.NumVal(6), 13, 2);
        or.GrossAmount = Cob.T(h.NumVal(7), 13, 2);
        or.LineCnt = (int)Cob.T(h.NumVal(8), 4, 0, signed: false);
    }

    private static void SqlFail(CaHeader ca, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        w.Fatal = true;
    }

    /// <summary>9100-NUMBER: NUMGEN NEXT on series POORD.</summary>
    private async Task NumberAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var n = await numbers.NextAsync(new KernelCaller(ca.Company, ca.Site, ca.User, or.OrderDate, ca.CorrelationId), "POORD", ct);
        if (n.Header.ReturnCode > 4)
        {
            ca.ReturnCode = 12;
            ca.ReasonCode = "ORD0010";
            ca.Message = Cob.X(n.Header.Message, 200);
            w.Fatal = true;
            return;
        }
        or.OrderNumber = Cob.X(n.DocumentNumber, 30);
    }
}
