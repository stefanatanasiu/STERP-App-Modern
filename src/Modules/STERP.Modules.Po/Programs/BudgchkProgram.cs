using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// BUDGCHK (cobol/online/po/BUDGCHK.cob): commitment plus actual against the approved annual budget, warn 90 % / block
/// 100 % (D-08), related-request aggregation (BUD0007), commitment movements (po.Commitment, insert-only). Runs on the
/// caller's engine connection; every statement autocommits unless the caller has begun a unit.
/// Carried WS-NARRATIVE is not reproduced (BC-PO-01, keep-stateless): every movement builds its own narrative.
/// </summary>
internal sealed class BudgchkProgram(ISqlRunner runner)
{
    private const decimal DefWarnPct = 90m;
    private const decimal DefBlockPct = 100m;
    private const int SplitWindow = 30;

    private readonly PoSql _sql = new(runner);

    public async Task RunAsync(CaHeader ca, BudgchkArea bc, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, bc, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "CHECK":
                await CheckAsync(ca, bc, w, ct);
                break;
            case "COMMIT":
                if (!w.NoBudget) await CheckAsync(ca, bc, w, ct);
                if (!w.Fatal && (ca.ReturnCode < 6 || w.NoBudget))
                {
                    w.Stage = "COMMITTED";
                    await MovementAsync(ca, bc, w, ct);
                    Unbudgeted(ca, bc, w);
                }
                break;
            case "RELEASE":
                w.Stage = "RELEASED";
                await MovementAsync(ca, bc, w, ct);
                Unbudgeted(ca, bc, w);
                break;
            case "OVERRIDE":
                await OverrideAsync(ca, bc, w, ct);
                break;
            default:
                ca.ReasonCode = "BUD0006";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by BUDGCHK");
                ca.ReturnCode = 8;
                break;
        }
    }

    private sealed class Work
    {
        public bool Fatal;
        public bool NoBudget;
        public string Narrative = "";
        public string Stage = "";
    }

    private static void Init(CaHeader ca, BudgchkArea bc, Work w)
    {
        ca.Program = "BUDGCHK";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        bc.BudgetGbp = 0; bc.ActualGbp = 0; bc.CommittedGbp = 0;
        bc.AvailableGbp = 0; bc.UsedPct = 0; bc.UsedAfterPct = 0;
        bc.RelatedCnt = 0; bc.RelatedGbp = 0; bc.CommitmentId = 0;
        bc.Outcome = "WITHIN";
        if (bc.WarnPct <= 0) bc.WarnPct = DefWarnPct;
        if (bc.BlockPct <= 0) bc.BlockPct = DefBlockPct;
        if (bc.MovementDate == 0) bc.MovementDate = ca.BusinessDate;
        if (bc.FiscalYear == 0) bc.FiscalYear = (int)Cob.NumVal(Cob.D8(bc.MovementDate)[..4]);
        bc.ProposedGbp = Cob.T(bc.NetGbp + bc.NonrecVatGbp + bc.ChargeGbp, 13, 2);
        if (Cob.Spaces(ca.Company) || (Cob.Spaces(bc.BudgetCode) && Cob.Spaces(bc.CostCentre)))
        {
            if (!Cob.Spaces(ca.Company) && ca.Function is "COMMIT" or "RELEASE")
            {
                w.NoBudget = true;
            }
            else
            {
                ca.ReturnCode = 8;
                ca.ReasonCode = "BUD0001";
                bc.Outcome = "BLOCK";
                ca.Message = Cob.Msg("A company code and either a budget code or a cost centre are needed for a budget check");
                w.Fatal = true;
            }
        }
    }

    private async Task CheckAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        await ReadBudgetAsync(ca, bc, w, ct);
        if (w.Fatal) return;
        if (bc.BudgetGbp == 0)
        {
            w.NoBudget = true;
            ca.ReturnCode = 16;
            ca.ReasonCode = "BUD0002";
            bc.Outcome = "BLOCK";
            ca.Message = Cob.Msg("No approved budget for ", Cob.Trim(bc.BudgetCode), " in ", Cob.Z(bc.FiscalYear, 4),
                " - the purchase cannot be committed against it");
            return;
        }
        await ReadActualAsync(ca, bc, w, ct);
        await ReadCommittedAsync(ca, bc, w, ct);
        await ReadRelatedAsync(ca, bc, w, ct);
        if (w.Fatal) return;
        Assess(ca, bc);
    }

    private async Task ReadBudgetAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(SUM(l.Amount), 0) AS varchar(24)), CAST(ISNULL(MAX(l.Version), 0) AS varchar(12)) FROM " +
            "fin.BudgetLine l WHERE l.CompanyCode = ? AND l.FiscalYear = ? AND (? = '' OR l.BudgetCode = ?) AND " +
            "(? = '' OR l.AccountCode = ?) AND (? = '' OR ISNULL(l.DimCostCentre, '') = ?) AND l.Version = (SELECT " +
            "MAX(b.Version) FROM fin.Budget b WHERE b.CompanyCode = l.CompanyCode AND b.BudgetCode = l.BudgetCode AND " +
            "b.FiscalYear = l.FiscalYear AND b.Status = 'APPROVED')",
            [PoSql.Tx(ca.Company), PoSql.Tx(Cob.Z(bc.FiscalYear, 4)), PoSql.Tx(bc.BudgetCode), PoSql.Tx(bc.BudgetCode),
             PoSql.Tx(bc.AccountCode), PoSql.Tx(bc.AccountCode), PoSql.Tx(bc.CostCentre), PoSql.Tx(bc.CostCentre)],
            [24, 12], ct);
        if (r.Error) { SqlFail(ca, bc, w, r); return; }
        if (r.NotFound) { bc.BudgetGbp = 0; return; }
        bc.BudgetGbp = Cob.T(r.Row.NumVal(0), 13, 2);
    }

    private async Task ReadActualAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(SUM(jl.DebitAmountGBP - jl.CreditAmountGBP), 0) AS varchar(24)) FROM fin.JournalLine jl " +
            "JOIN fin.Journal j ON j.JournalId = jl.JournalId WHERE jl.CompanyCode = ? AND j.FiscalYear = ? AND " +
            "j.Status = 'POSTED' AND (? = '' OR jl.AccountCode = ?) AND (? = '' OR ISNULL(jl.DimCostCentre, '') = ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(Cob.Z(bc.FiscalYear, 4)), PoSql.Tx(bc.AccountCode), PoSql.Tx(bc.AccountCode),
             PoSql.Tx(bc.CostCentre), PoSql.Tx(bc.CostCentre)],
            [24], ct);
        if (r.Error) { SqlFail(ca, bc, w, r); return; }
        if (!r.NotFound) bc.ActualGbp = Cob.T(r.Row.NumVal(0), 13, 2);
    }

    private async Task ReadCommittedAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(SUM(CASE c.Stage WHEN 'COMMITTED' THEN (c.AmountGBP + c.NonRecoverableVatGBP + c.ChargeGBP) " +
            "WHEN 'RELEASED' THEN -(c.AmountGBP + c.NonRecoverableVatGBP + c.ChargeGBP) WHEN 'CANCELLED' THEN " +
            "-(c.AmountGBP + c.NonRecoverableVatGBP + c.ChargeGBP) ELSE 0 END), 0) AS varchar(24)) FROM po.Commitment c " +
            "WHERE c.CompanyCode = ? AND ISNULL(c.FiscalYear, 0) IN (0, ?) AND (? = '' OR ISNULL(c.BudgetCode, '') = ?) AND " +
            "(? = '' OR ISNULL(c.DimCostCentre, '') = ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(Cob.Z(bc.FiscalYear, 4)), PoSql.Tx(bc.BudgetCode), PoSql.Tx(bc.BudgetCode),
             PoSql.Tx(bc.CostCentre), PoSql.Tx(bc.CostCentre)],
            [24], ct);
        if (r.Error) { SqlFail(ca, bc, w, r); return; }
        if (!r.NotFound) bc.CommittedGbp = Cob.T(r.Row.NumVal(0), 13, 2);
    }

    private async Task ReadRelatedAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(bc.Requester)) return;
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(SUM(r.TotalValueGBP), 0) AS varchar(24)), CAST(COUNT(*) AS varchar(12)) FROM " +
            "po.Requisition r WHERE r.CompanyCode = ? AND r.RequesterUser = ? AND (? = '' OR ISNULL(r.DimCostCentre, '') = ?) " +
            "AND r.Status IN ('SUBMITTED', 'APPROVED', 'PARTCONV') AND r.RequisitionNumber <> ? AND r.RequisitionDate >= " +
            "DATEADD(day, -1 * TRY_CONVERT(int, ?), TRY_CONVERT(date, ?)) AND r.RequisitionDate <= TRY_CONVERT(date, ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(bc.Requester), PoSql.Tx(bc.CostCentre), PoSql.Tx(bc.CostCentre),
             PoSql.Tx(bc.DocRef), PoSql.Int(SplitWindow), PoSql.D8(bc.MovementDate), PoSql.D8(bc.MovementDate)],
            [24, 12], ct);
        if (r.Error) { SqlFail(ca, bc, w, r); return; }
        if (!r.NotFound)
        {
            bc.RelatedGbp = Cob.T(r.Row.NumVal(0), 13, 2);
            bc.RelatedCnt = (int)Cob.T(r.Row.NumVal(1), 4, 0, signed: false);
        }
    }

    /// <summary>3000-ASSESS: percentages after this purchase.</summary>
    private static void Assess(CaHeader ca, BudgchkArea bc)
    {
        var totalUsed = Cob.T(bc.ActualGbp + bc.CommittedGbp, 13, 2);
        var afterUsed = Cob.T(totalUsed + bc.ProposedGbp, 13, 2);
        var withRelated = Cob.T(afterUsed + bc.RelatedGbp, 13, 2);
        bc.AvailableGbp = Cob.T(bc.BudgetGbp - totalUsed, 13, 2);
        bc.UsedPct = Cob.R(totalUsed / bc.BudgetGbp * 100m, 7, 4);
        bc.UsedAfterPct = Cob.R(afterUsed / bc.BudgetGbp * 100m, 7, 4);
        var relatedPct = Cob.R(withRelated / bc.BudgetGbp * 100m, 7, 4);
        var pctEd = Cob.Ed(bc.UsedAfterPct, "-(5)9.99");
        if (bc.UsedAfterPct >= bc.BlockPct)
        {
            bc.Outcome = "BLOCK";
            ca.ReturnCode = 6;
            ca.ReasonCode = "BUD0004";
            ca.Message = Cob.Msg("This purchase would take budget ", Cob.Trim(bc.BudgetCode), " to ", pctEd,
                "% - the financial controller must approve it before it can proceed");
        }
        else if (relatedPct >= bc.BlockPct && bc.RelatedCnt > 0)
        {
            bc.Outcome = "BLOCK";
            ca.ReturnCode = 6;
            ca.ReasonCode = "BUD0007";
            ca.Message = Cob.Msg("With ", Cob.Ed(bc.RelatedCnt, "Z(3)9"),
                " related open request(s) from the same requester this takes the budget to ", Cob.Ed(relatedPct, "-(5)9.99"),
                "% and needs approval");
        }
        else if (bc.UsedAfterPct >= bc.WarnPct)
        {
            bc.Outcome = "WARN";
            ca.ReturnCode = 4;
            ca.ReasonCode = "BUD0003";
            ca.Message = Cob.Msg("Budget ", Cob.Trim(bc.BudgetCode), " reaches ", pctEd, "% after this purchase; ",
                Cob.Amt(bc.AvailableGbp), " remains uncommitted");
        }
        else
        {
            bc.Outcome = "WITHIN";
            ca.ReturnCode = 0;
        }
    }

    private async Task OverrideAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(bc.OverrideBy) || Cob.Spaces(bc.OverrideReason))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "BUD0005";
            bc.Outcome = "BLOCK";
            ca.Message = Cob.Msg("A budget override needs both an approver and a reason");
            return;
        }
        await CheckAsync(ca, bc, w, ct);
        if (w.Fatal) return;
        w.Stage = "COMMITTED";
        w.Narrative = Cob.Str(200, "Budget override by ", Cob.Trim(bc.OverrideBy), ": ", Cob.Trim(bc.OverrideReason));
        await MovementAsync(ca, bc, w, ct);
        if (w.Fatal) return;
        bc.Outcome = "OVERRIDE";
        ca.ReturnCode = 4;
        ca.ReasonCode = "BUD0004";
        ca.Message = Cob.Msg("Budget override authorised by ", Cob.Trim(bc.OverrideBy), " - the commitment has been recorded");
    }

    /// <summary>5000-MOVEMENT: one po.Commitment row per stage movement.</summary>
    private async Task MovementAsync(CaHeader ca, BudgchkArea bc, Work w, CancellationToken ct)
    {
        if (bc.ProposedGbp == 0) bc.ProposedGbp = Cob.T(bc.NetGbp + bc.NonrecVatGbp + bc.ChargeGbp, 13, 2);
        if (Cob.Spaces(w.Narrative))
        {
            w.Narrative = Cob.Str(200, Cob.Trim(w.Stage), " movement for ", Cob.Trim(bc.DocType), " ", Cob.Trim(bc.DocRef));
        }
        var r = await _sql.Sngl(
            "INSERT po.Commitment (CompanyCode, OrderNumber, LineNum, ScheduleNo, BudgetCode, AccountCode, DimCostCentre, " +
            "DimProject, FiscalYear, Stage, MovementDate, Quantity, AmountGBP, NonRecoverableVatGBP, ChargeGBP, SourceDocType, " +
            "SourceDocRef, Narrative, CreatedBy) OUTPUT CAST(INSERTED.CommitmentId AS varchar(24)) VALUES (?, ?, ?, " +
            "NULLIF(?, 0), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), NULLIF(?, ''), ?, ?, TRY_CONVERT(date, ?), ?, ?, ?, ?, " +
            "NULLIF(?, ''), NULLIF(?, ''), ?, ?)",
            [PoSql.Tx(ca.Company), PoSql.Tx(bc.DocRef), PoSql.Int(bc.DocLine), PoSql.Int(bc.ScheduleNo), PoSql.Tx(bc.BudgetCode),
             PoSql.Tx(bc.AccountCode), PoSql.Tx(bc.CostCentre), PoSql.Tx(bc.Project), PoSql.Tx(Cob.Z(bc.FiscalYear, 4)),
             PoSql.Tx(w.Stage), PoSql.D8(bc.MovementDate), PoSql.Num(bc.Quantity), PoSql.Num(bc.NetGbp),
             PoSql.Num(bc.NonrecVatGbp), PoSql.Num(bc.ChargeGbp), PoSql.Tx(bc.DocType), PoSql.Tx(bc.DocRef),
             PoSql.Tx(w.Narrative), PoSql.Tx(ca.User)],
            [24], ct);
        if (r.Error) { SqlFail(ca, bc, w, r); return; }
        bc.CommitmentId = (long)Cob.T(r.Row.NumVal(0), 17, 0);
    }

    /// <summary>5100-UNBUDGETED: an encumbrance without an approved budget is recorded and reported (RC 04 BUD0008).</summary>
    private static void Unbudgeted(CaHeader ca, BudgchkArea bc, Work w)
    {
        if (w.Fatal || !w.NoBudget) return;
        ca.ReturnCode = 4;
        ca.ReasonCode = "BUD0008";
        bc.Outcome = "NOBUDGET";
        ca.Message = Cob.Msg(Cob.Trim(w.Stage), " commitment of ", Cob.Amt(bc.ProposedGbp), " recorded for ", Cob.Trim(bc.DocType),
            " ", Cob.Trim(bc.DocRef), "; no approved budget covers it in ", Cob.Z(bc.FiscalYear, 4));
    }

    private static void SqlFail(CaHeader ca, BudgchkArea bc, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        bc.Outcome = "BLOCK";
        w.Fatal = true;
    }
}
