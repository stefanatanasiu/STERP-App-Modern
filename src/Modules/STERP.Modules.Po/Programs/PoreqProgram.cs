using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POREQ (cobol/online/po/POREQ.cob): purchase requisition lifecycle - CREATE, ADDLINE, SUBMIT (BUDGCHK CHECK), ENQUIRE.
/// No BEGN: every statement autocommits on the caller's engine connection; NUMGEN draws the number first.
/// APPROVE, CANCEL and CONVERT have no production caller (spec 12.2, SME-015) and are not delivered.
/// </summary>
internal sealed class PoreqProgram(ISqlRunner runner, IDocumentNumberService numbers, BudgchkProgram budgchk)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
    }

    public async Task RunAsync(CaHeader ca, PoreqArea rq, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, rq, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "CREATE":
                await CreateAsync(ca, rq, w, ct);
                break;
            case "ADDLINE":
                await AddLineAsync(ca, rq, w, ct);
                break;
            case "SUBMIT":
                await SubmitAsync(ca, rq, w, ct);
                break;
            case "ENQUIRE":
                await ReadHeaderAsync(ca, rq, w, ct);
                break;
            case "APPROVE" or "CANCEL" or "CONVERT":
                throw new NotSupportedException($"POREQ {ca.Function} has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "REQ0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by POREQ");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, PoreqArea rq, Work w)
    {
        ca.Program = "POREQ";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        rq.ReqId = 0; rq.LineId = 0; rq.LineValueGbp = 0;
        rq.TotalGbp = 0; rq.OpenGbp = 0; rq.LineCnt = 0;
        rq.BudgetUsedPct = 0; rq.BudgetAvailGbp = 0;
        rq.Status = ""; rq.BudgetOutcome = ""; rq.Outcome = "";
        if (rq.ReqDate == 0) rq.ReqDate = ca.BusinessDate;
        if (Cob.Spaces(rq.DemandType)) rq.DemandType = "MANUAL";
        if (Cob.Spaces(rq.Requester)) rq.Requester = Cob.X(ca.User, 20);
        if (Cob.Spaces(ca.Company) || Cob.Spaces(ca.Site) || Cob.Spaces(rq.Requester))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0001";
            ca.Message = Cob.Msg("A company, a site and a named requester are needed - a requisition must have an owner");
            w.Fatal = true;
            return;
        }
        if (rq.DemandType is not ("MANUAL" or "SHORTAGE" or "REPLEN" or "MRP" or "SALESORD" or "KIT"))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0009";
            ca.Message = Cob.Msg("Demand type ", Cob.Trim(rq.DemandType), " is not recognised");
            w.Fatal = true;
        }
    }

    private async Task CreateAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        if (rq.NeededBy != 0 && rq.NeededBy < rq.ReqDate)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0006";
            ca.Message = Cob.Msg("The needed-by date is before the requisition date");
            return;
        }
        if (Cob.Spaces(rq.ReqNumber))
        {
            await NumberAsync(ca, rq, w, ct);
            if (w.Fatal) return;
        }
        var r = await _sql.Sngl(
            "INSERT po.Requisition (CompanyCode, SiteCode, RequisitionNumber, RequesterUser, Purpose, RequisitionDate, " +
            "NeededByDate, DimCostCentre, DimProject, JobRef, SourceDemandType, SourceDemandRef, BudgetCode, Status, CreatedBy) " +
            "OUTPUT CAST(INSERTED.RequisitionId AS varchar(20)) VALUES (?, ?, ?, ?, NULLIF(?, ''), TRY_CONVERT(date, ?), " +
            "TRY_CONVERT(date, NULLIF(?, '00000000')), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), ?, NULLIF(?, ''), " +
            "NULLIF(?, ''), 'DRAFT', ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(ca.Site), PoSql.Tx(rq.ReqNumber), PoSql.Tx(rq.Requester), PoSql.Tx(rq.Purpose),
             PoSql.D8(rq.ReqDate), PoSql.D8(rq.NeededBy), PoSql.Tx(rq.CostCentre), PoSql.Tx(rq.Project), PoSql.Tx(rq.JobRef),
             PoSql.Tx(rq.DemandType), PoSql.Tx(rq.DemandRef), PoSql.Tx(rq.BudgetCode), PoSql.Tx(ca.User)],
            [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        rq.ReqId = (long)Cob.T(r.Row.NumVal(0), 17, 0);
        rq.Status = "DRAFT";
        rq.Outcome = "DRAFT";
        ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " raised by ", Cob.Trim(rq.Requester), " for ",
            Cob.Trim(rq.DemandType), " demand ", Cob.Trim(rq.DemandRef));
    }

    private async Task AddLineAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        await ReadHeaderAsync(ca, rq, w, ct);
        if (w.Fatal) return;
        if (rq.Status != "DRAFT" && rq.Status != "REJECTED")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0003";
            ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " is ", Cob.Trim(rq.Status), " and can no longer be changed");
            return;
        }
        if (Cob.Spaces(rq.Description) || rq.Quantity <= 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0004";
            ca.Message = Cob.Msg("A requisition line needs a description and a quantity greater than zero");
            return;
        }
        if (Cob.Spaces(rq.ItemCode) && Cob.Spaces(rq.AccountCode))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0005";
            ca.Message = Cob.Msg("A free text line needs a nominal account code so that the spend can be coded and budget checked");
            return;
        }
        rq.LineValueGbp = Cob.R(rq.Quantity * rq.EstUnitPrice, 13, 2);
        if (rq.LineNum == 0) await NextLineAsync(ca, rq, w, ct);
        var p = new List<Sterp.Platform.Kernel.Data.SqlioParm> { PoSql.Int(rq.ReqId), PoSql.Int(rq.LineNum) };
        for (var i = 0; i < 2; i++)
        {
            p.Add(PoSql.Tx(rq.ItemCode));
            p.Add(PoSql.Tx(rq.Description));
            p.Add(PoSql.Num(rq.Quantity));
            p.Add(PoSql.Tx(rq.UomCode));
            p.Add(PoSql.Num(rq.EstUnitPrice));
            p.Add(PoSql.Num(rq.LineValueGbp));
            p.Add(PoSql.Tx(rq.SuggestSupp));
            p.Add(PoSql.D8(rq.LineNeededBy == 0 ? rq.NeededBy : rq.LineNeededBy));
            p.Add(PoSql.Tx(rq.AccountCode));
        }
        var r = await _sql.Sngl(
            "MERGE po.RequisitionLine AS t USING (SELECT ? AS RequisitionId, ? AS LineNum) AS s ON t.RequisitionId = " +
            "s.RequisitionId AND t.LineNum = s.LineNum WHEN MATCHED THEN UPDATE SET ItemCode = NULLIF(?, ''), Description = ?, " +
            "Quantity = ?, UomCode = NULLIF(?, ''), EstimatedUnitPrice = ?, EstimatedValueGBP = ?, SuggestedSupplierCode = " +
            "NULLIF(?, ''), NeededByDate = TRY_CONVERT(date, NULLIF(?, '00000000')), AccountCode = NULLIF(?, '') WHEN NOT " +
            "MATCHED THEN INSERT (RequisitionId, LineNum, ItemCode, Description, Quantity, UomCode, EstimatedUnitPrice, " +
            "EstimatedValueGBP, SuggestedSupplierCode, NeededByDate, AccountCode, Status) VALUES (s.RequisitionId, s.LineNum, " +
            "NULLIF(?, ''), ?, ?, NULLIF(?, ''), ?, ?, NULLIF(?, ''), TRY_CONVERT(date, NULLIF(?, '00000000')), NULLIF(?, " +
            "''), 'OPEN') OUTPUT CAST(INSERTED.RequisitionLineId AS varchar(20));",
            [.. p], [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (!r.NotFound) rq.LineId = (long)Cob.T(r.Row.NumVal(0), 17, 0);
        await RetotalAsync(ca, rq, w, ct);
        ca.Message = Cob.Msg("Line ", Cob.Trim(rq.Description), " valued at ", Cob.Amt(rq.LineValueGbp), " added to requisition ",
            Cob.Trim(rq.ReqNumber));
    }

    private async Task NextLineAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(MAX(LineNum), 0) + 1 AS varchar(24)) FROM po.RequisitionLine WHERE RequisitionId = ?",
            [PoSql.Int(rq.ReqId)], [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        rq.LineNum = (int)r.Row.NumVal(0);
    }

    private async Task RetotalAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE r SET TotalValueGBP = (SELECT ISNULL(SUM(l.EstimatedValueGBP), 0) FROM po.RequisitionLine l WHERE " +
            "l.RequisitionId = r.RequisitionId AND l.Status <> 'CANCELLED'), ModifiedBy = ?, ModifiedAt = SYSDATETIME() FROM " +
            "po.Requisition r WHERE r.RequisitionId = ?",
            [PoSql.Tx(ca.User), PoSql.Int(rq.ReqId)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        await ReadHeaderAsync(ca, rq, w, ct);
    }

    private async Task SubmitAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        await ReadHeaderAsync(ca, rq, w, ct);
        if (w.Fatal) return;
        if (rq.Status != "DRAFT" && rq.Status != "REJECTED")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0003";
            ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " is ", Cob.Trim(rq.Status), " and cannot be submitted again");
            return;
        }
        if (rq.LineCnt == 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "REQ0004";
            ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " has no lines to submit");
            return;
        }
        await BudgetAsync(ca, rq, ct);
        if (ca.ReturnCode >= 8) return;
        if (rq.BudgetOutcome == "BLOCK" && Cob.Spaces(rq.OverrideBy))
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "REQ0007";
            rq.Outcome = "SUBMITTED";
            return;
        }
        await SetStatusAsync(ca, rq, w, ct);
    }

    /// <summary>4100-BUDGET: BUDGCHK CHECK of the requisition total (RC 16 is only a warning on a requisition).</summary>
    private async Task BudgetAsync(CaHeader ca, PoreqArea rq, CancellationToken ct)
    {
        if (Cob.Spaces(rq.BudgetCode) && Cob.Spaces(rq.CostCentre))
        {
            rq.BudgetOutcome = "WITHIN";
            return;
        }
        var bg = CaHeader.Call("CHECK", ca.Company, ca.Site, ca.User, rq.ReqDate, ca.CorrelationId);
        var bc = new BudgchkArea
        {
            BudgetCode = rq.BudgetCode,
            AccountCode = Cob.X(rq.AccountCode, 12),
            CostCentre = rq.CostCentre,
            Project = rq.Project,
            DocType = "REQUISITION",
            DocRef = Cob.X(rq.ReqNumber, 40),
            Requester = rq.Requester,
            ItemCode = rq.ItemCode,
            MovementDate = rq.ReqDate,
            NetGbp = rq.TotalGbp,
            OverrideBy = rq.OverrideBy,
            OverrideReason = rq.OverrideReason,
        };
        await budgchk.RunAsync(bg, bc, ct);
        rq.BudgetOutcome = bc.Outcome;
        rq.BudgetUsedPct = bc.UsedAfterPct;
        rq.BudgetAvailGbp = bc.AvailableGbp;
        if (bg.ReturnCode >= 4)
        {
            ca.ReturnCode = bg.ReturnCode;
            ca.ReasonCode = bg.ReasonCode;
            ca.Message = bg.Message;
        }
        if (bg.ReturnCode == 16) ca.ReturnCode = 4;
    }

    private async Task SetStatusAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE po.Requisition SET Status = 'SUBMITTED', BudgetUsedPct = ?, BudgetOverrideBy = NULLIF(?, ''), " +
            "BudgetOverrideReason = NULLIF(?, ''), ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE RequisitionId = ?",
            [PoSql.Num(rq.BudgetUsedPct), PoSql.Tx(rq.OverrideBy), PoSql.Tx(rq.OverrideReason), PoSql.Tx(ca.User), PoSql.Int(rq.ReqId)],
            ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        rq.Status = "SUBMITTED";
        rq.Outcome = "SUBMITTED";
        if (ca.ReturnCode == 0)
        {
            ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " for ", Cob.Amt(rq.TotalGbp), " submitted for approval");
        }
    }

    /// <summary>8000-READ-HDR.</summary>
    private async Task ReadHeaderAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(r.RequisitionId AS varchar(20)), r.Status, CAST(r.TotalValueGBP AS varchar(24)), r.RequesterUser, " +
            "ISNULL(r.DimCostCentre, ''), ISNULL(r.BudgetCode, ''), CAST((SELECT COUNT(*) FROM po.RequisitionLine l WHERE " +
            "l.RequisitionId = r.RequisitionId AND l.Status <> 'CANCELLED') AS varchar(12)), CAST((SELECT " +
            "ISNULL(SUM(l2.EstimatedValueGBP * CASE WHEN l2.Quantity > 0 THEN (l2.Quantity - l2.ConvertedQty) / l2.Quantity " +
            "ELSE 0 END), 0) FROM po.RequisitionLine l2 WHERE l2.RequisitionId = r.RequisitionId AND l2.Status IN ('OPEN', " +
            "'PARTCONV')) AS varchar(24)) FROM po.Requisition r WHERE r.CompanyCode = ? AND r.RequisitionNumber = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(rq.ReqNumber)], [20, 12, 24, 20, 20, 20, 12, 24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "REQ0002";
            ca.Message = Cob.Msg("Requisition ", Cob.Trim(rq.ReqNumber), " is not on file for company ", Cob.Trim(ca.Company));
            w.Fatal = true;
            return;
        }
        var row = r.Row;
        rq.ReqId = (long)Cob.T(row.NumVal(0), 17, 0);
        rq.Status = row.Text(1);
        rq.TotalGbp = Cob.T(row.NumVal(2), 13, 2);
        if (Cob.Spaces(rq.Requester) || ca.Function == "ENQUIRE") rq.Requester = row.Text(3);
        if (Cob.Spaces(rq.CostCentre)) rq.CostCentre = row.Text(4);
        if (Cob.Spaces(rq.BudgetCode)) rq.BudgetCode = row.Text(5);
        rq.LineCnt = (int)Cob.T(row.NumVal(6), 4, 0, signed: false);
        rq.OpenGbp = Cob.T(row.NumVal(7), 13, 2);
    }

    private static void SqlFail(CaHeader ca, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        w.Fatal = true;
    }

    /// <summary>9100-NUMBER: NUMGEN NEXT on series POREQ.</summary>
    private async Task NumberAsync(CaHeader ca, PoreqArea rq, Work w, CancellationToken ct)
    {
        var n = await numbers.NextAsync(new KernelCaller(ca.Company, ca.Site, ca.User, rq.ReqDate, ca.CorrelationId), "POREQ", ct);
        if (n.Header.ReturnCode > 4)
        {
            ca.ReturnCode = 12;
            ca.ReasonCode = "REQ0010";
            ca.Message = Cob.X(n.Header.Message, 200);
            w.Fatal = true;
            return;
        }
        rq.ReqNumber = Cob.X(n.DocumentNumber, 30);
    }
}
