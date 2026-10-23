using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SOMARGN (cobol/online/so/SOMARGN.cob, FR-SOMARGN-001..008, 013, 014): margin and markup of a sales line against a named
/// comparable cost (FR-STERP-SO-014). LINE and EXPLAIN are implemented; CHECK and PROPOSE have no production caller and
/// are deferred (D-SO-01, SME-015). Reads only (cst.ItemCost).
/// </summary>
public sealed class SomargnProgram(ISqlRunner sql)
{
    private const string Program = "SOMARGN";

    private const string ItemCostSql =
        "SELECT TOP 1 ISNULL(c.StandardUnitCost,0), " +
        "ISNULL(c.AverageUnitCost,0), " +
        "ISNULL(c.LastPurchaseCost,0), c.CostMethod FROM " +
        "cst.ItemCost c WHERE c.CompanyCode = ? AND " +
        "c.ItemCode = ? AND (c.SiteCode = ? OR " +
        "c.SiteCode = '*') ORDER BY CASE WHEN c.SiteCode " +
        "= ? THEN 0 ELSE 1 END";

    private static readonly int[] Widths = Enumerable.Repeat(100, 4).ToArray();

    private sealed class State
    {
        public bool InputBad;
        public bool SqlBad;
        public decimal StdCost;
        public decimal AvgCost;
        public decimal LastCost;
    }

    public async Task RunAsync(SomargnArea sm, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sm);
        Validate(sm, s);
        if (s.InputBad) return;
        switch (sm.Header.Function)
        {
            case "LINE":
            case "EXPLAIN":
                await LineAsync(sm, s, ct);
                break;
            case "CHECK":
            case "PROPOSE":
                throw new NotSupportedException($"SOMARGN {sm.Header.Function} has no production caller (D-SO-01, SME-015).");
            default:
                sm.Header.ReasonCode = "MRG0008";
                sm.Header.Message = Cob.Str(200, "Function ", Cob.Trim(sm.Header.Function), " is not one of LINE, CHECK, PROPOSE or EXPLAIN");
                Reject(sm, s);
                break;
        }
        Explain(sm);
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(SomargnArea sm)
    {
        var h = sm.Header;
        h.Program = Program;
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-006";
        h.Function = Cob.Upper(h.Function, 8);
        sm.CostBasis = Cob.Upper(sm.CostBasis, 12);
        sm.ItemCode = Cob.Upper(sm.ItemCode, 40);
        if (sm.EffDate == 0) sm.EffDate = h.BusinessDate;
        if (Cob.Spaces(sm.Currency)) sm.Currency = "GBP";
        if (Cob.Spaces(sm.SiteCode)) sm.SiteCode = Cob.X(h.Site, 10);
        if (sm.PricePerQty <= 0) sm.PricePerQty = 1;
        if (sm.NetAmountGbp == 0) sm.NetAmountGbp = sm.NetAmount;
        sm.MarginValue = 0; sm.MarginPct = 0; sm.MarkupPct = 0;
        sm.ExceptionId = 0;
        sm.ProposedPrice = 0; sm.ProposedNet = 0;
        sm.ProposedMargin = 0;
        sm.MarginState = ""; sm.MarkupState = "";
        sm.ReviewReason = ""; sm.Explanation = "";
        sm.CostBasisUsed = ""; sm.CostSource = "";
        sm.MinSource = "";
        sm.BelowMinimum = "N"; sm.ApprovalReqd = "N";
        sm.CostFound = "N"; sm.PriceChanged = "N";
        sm.AmendmentReqd = "N"; sm.UsedCstMargin = "N";
    }

    /// <summary>0200-VALIDATE.</summary>
    private static void Validate(SomargnArea sm, State s)
    {
        if (Cob.Spaces(sm.Header.Company))
        {
            sm.Header.ReasonCode = "MRG0001";
            sm.Header.Message = "A company is required";
            Reject(sm, s);
            return;
        }
        if (sm.Header.Function == "PROPOSE")
        {
            if (sm.Quantity <= 0)
            {
                sm.Header.ReasonCode = "MRG0009";
                sm.Header.Message = "A positive quantity is needed to propose a";
                Reject(sm, s);
                return;
            }
            if (sm.TargetMargin >= 100)
            {
                sm.Header.ReasonCode = "MRG0006";
                sm.Header.Message = Cob.Str(200, "A target margin must be below 100 per ", "cent - there is no such selling price");
                Reject(sm, s);
            }
        }
    }

    /// <summary>1000-LINE: the measurement.</summary>
    private async Task LineAsync(SomargnArea sm, State s, CancellationToken ct)
    {
        await CostBasisAsync(sm, s, ct);
        if (s.InputBad) return;
        if (sm.CostFound != "Y")
        {
            NoCost(sm);
            return;
        }
        Calculate(sm);
    }

    /// <summary>3000-COST-BASIS: a supplied cost as given, else cst.ItemCost (site row before '*').</summary>
    private async Task CostBasisAsync(SomargnArea sm, State s, CancellationToken ct)
    {
        if (sm.CostAmount != 0 || sm.UnitCost != 0)
        {
            SuppliedCost(sm);
            return;
        }
        if (Cob.Spaces(sm.ItemCode)) return;
        await ReadItemCostAsync(sm, s, ct);
        if (s.SqlBad) return;
        PickBasis(sm, s);
    }

    /// <summary>3100-SUPPLIED-COST.</summary>
    private static void SuppliedCost(SomargnArea sm)
    {
        if (sm.CostAmount == 0 && sm.Quantity > 0) sm.CostAmount = Cob.Rd(sm.UnitCost * sm.Quantity, 13, 2);
        if (sm.UnitCost == 0 && sm.Quantity > 0) sm.UnitCost = Cob.Rd(sm.CostAmount / sm.Quantity, 11, 6);
        sm.CostBasisUsed = Cob.Spaces(sm.CostBasis) || sm.CostBasis == "SUPPLIED" ? "SUPPLIED" : Cob.X(sm.CostBasis, 12);
        sm.CostSource = "COMMAREA";
        sm.CostFound = "Y";
    }

    /// <summary>3200-READ-ITEM-COST.</summary>
    private async Task ReadItemCostAsync(SomargnArea sm, State s, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(ItemCostSql,
            [
                SqlioParm.Text(sm.Header.Company), SqlioParm.Text(sm.ItemCode), SqlioParm.Text(sm.SiteCode), SqlioParm.Text(sm.SiteCode),
            ], Widths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(sm, s, ex);
            return;
        }
        if (!row.Found) return;
        s.StdCost = Cob.St(row.NumVal(0), 11, 6);
        s.AvgCost = Cob.St(row.NumVal(1), 11, 6);
        s.LastCost = Cob.St(row.NumVal(2), 11, 6);
    }

    /// <summary>3300-PICK-BASIS: the requested basis when held, else standard, average, last buy.</summary>
    private static void PickBasis(SomargnArea sm, State s)
    {
        decimal unit;
        if (sm.CostBasis == "STANDARD" && s.StdCost != 0) { unit = s.StdCost; sm.CostBasisUsed = "STANDARD"; }
        else if (sm.CostBasis == "AVERAGE" && s.AvgCost != 0) { unit = s.AvgCost; sm.CostBasisUsed = "AVERAGE"; }
        else if (sm.CostBasis == "LASTBUY" && s.LastCost != 0) { unit = s.LastCost; sm.CostBasisUsed = "LASTBUY"; }
        else if (s.StdCost != 0) { unit = s.StdCost; sm.CostBasisUsed = "STANDARD"; }
        else if (s.AvgCost != 0) { unit = s.AvgCost; sm.CostBasisUsed = "AVERAGE"; }
        else if (s.LastCost != 0) { unit = s.LastCost; sm.CostBasisUsed = "LASTBUY"; }
        else unit = 0;
        if (unit == 0) return;
        sm.UnitCost = unit;
        sm.CostAmount = sm.Quantity > 0 ? Cob.Rd(unit * sm.Quantity, 13, 2) : Cob.St(unit, 13, 2);
        sm.CostSource = "CST.ITEMCOST";
        sm.CostFound = "Y";
    }

    /// <summary>3500-CALCULATE: margin on net and markup on cost, ROUNDED to 4 dp; a zero denominator is UNDEFINED.</summary>
    private static void Calculate(SomargnArea sm)
    {
        var h = sm.Header;
        var costTotal = Cob.St(sm.CostAmount + sm.CostAdjustment, 13, 2);
        sm.CostAmount = costTotal;
        var net = sm.NetAmount;
        sm.MarginValue = Cob.St(net - costTotal, 13, 2);
        if (net == 0)
        {
            sm.MarginPct = 0;
            sm.MarginState = "UNDEFINED";
            sm.ReviewReason = Cob.Str(100, "Net selling amount is zero so the margin ", "percentage is not defined - review the line");
            if (h.ReturnCode < 4)
            {
                h.ReturnCode = 4;
                h.ReasonCode = "MRG0003";
                h.Message = sm.ReviewReason;
                h.MessageCount++;
            }
        }
        else
        {
            sm.MarginPct = Cob.Rd((net - costTotal) / net * 100, 7, 4);
            sm.MarginState = "OK";
        }
        if (costTotal == 0)
        {
            sm.MarkupPct = 0;
            sm.MarkupState = "UNDEFINED";
            if (Cob.Spaces(sm.ReviewReason))
            {
                sm.ReviewReason = Cob.Str(100, "Comparable cost is zero so the markup ", "percentage is not defined - review");
            }
            if (h.ReturnCode < 4)
            {
                h.ReturnCode = 4;
                h.ReasonCode = "MRG0004";
                h.Message = sm.ReviewReason;
                h.MessageCount++;
            }
        }
        else
        {
            sm.MarkupPct = Cob.Rd((net - costTotal) / costTotal * 100, 7, 4);
            sm.MarkupState = "OK";
        }
    }

    /// <summary>3900-NO-COST: a missing cost is not zero - RC 06 MRG0002, approval required.</summary>
    private static void NoCost(SomargnArea sm)
    {
        sm.MarginState = "INCOMPLETE";
        sm.MarkupState = "INCOMPLETE";
        sm.MarginPct = 0; sm.MarkupPct = 0; sm.MarginValue = 0;
        sm.ApprovalReqd = "Y";
        sm.ReviewReason = Cob.Str(100, "No comparable cost is held for item ", Cob.Trim(sm.ItemCode), " - the margin cannot be measured");
        sm.Header.ReasonCode = "MRG0002";
        sm.Header.Message = sm.ReviewReason;
        sm.Header.ReturnCode = 6;
        sm.Header.MessageCount++;
    }

    /// <summary>6000-EXPLAIN (STRING with FUNCTION TRIM of the target).</summary>
    private static void Explain(SomargnArea sm)
    {
        if (sm.Header.ReasonCode == "MRG0008") return;
        var e = Cob.Str(200, "Net ", Cob.Trim(sm.Currency), " ", Cob.Ed(sm.NetAmount, "-(11)9.99"), " less cost ");
        e = Cob.Overlay(e, 200, Cob.Trim(e), Cob.Ed(sm.CostAmount, "-(11)9.99"), " on the ", Cob.Trim(sm.CostBasisUsed), " basis");
        e = sm.MarginState == "OK"
            ? Cob.Overlay(e, 200, Cob.Trim(e), " gives margin ", Cob.Ed(sm.MarginPct, "-(6)9.9(4)"), "%")
            : Cob.Overlay(e, 200, Cob.Trim(e), " gives margin ", Cob.Trim(sm.MarginState));
        e = sm.MarkupState == "OK"
            ? Cob.Overlay(e, 200, Cob.Trim(e), " and markup ", Cob.Ed(sm.MarkupPct, "-(6)9.9(4)"), "%")
            : Cob.Overlay(e, 200, Cob.Trim(e), " and markup ", Cob.Trim(sm.MarkupState));
        if (sm.ApprovalReqd == "Y") e = Cob.Overlay(e, 200, Cob.Trim(e), " - approval is required");
        sm.Explanation = e;
        if (Cob.Spaces(sm.Header.Message)) sm.Header.Message = Cob.X(sm.Explanation, 200);
    }

    private static void Reject(SomargnArea sm, State s)
    {
        sm.Header.ReturnCode = 8;
        s.InputBad = true;
        sm.Header.MessageCount++;
    }

    /// <summary>9300-SQL-FAILED.</summary>
    private static void SqlFailed(SomargnArea sm, State s, SqlioException ex)
    {
        s.InputBad = true;
        s.SqlBad = true;
        sm.Header.Message = Header.SqlMessage(ex, Program);
        sm.Header.ReasonCode = "SQL0001";
        sm.Header.ReturnCode = 12;
        sm.Header.MessageCount++;
    }
}
