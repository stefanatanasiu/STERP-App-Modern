using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Cst.Engines;

/// <summary>
/// STDCOST (cobol/online/cst/STDCOST.cob) - the standard cost set lifecycle: REVIEW, APPROVE, ACTIVATE (status change then
/// the read-only IMPACT), IMPACT and COMPARE (decision D-CST-04: the other function codes have no production route). Every
/// statement runs on the caller's unit of work in autocommit (STDCOST issues no BEGN/COMT); no lock, no rowversion.
/// </summary>
internal sealed class StdCostEngine(ISqlRunner sql)
{
    private const int Write = 1;

    public async Task LinkAsync(StdCostArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        var db = new CstSqlio(sql, ca);
        var flags = Initialise(a);
        var inputBad = Validate(a);
        if (inputBad) return;
        var st = new State(db, flags);
        switch (ca.Function)
        {
            case "REVIEW":
                await ChangeStatusAsync(a, st, "DRAFT", "REVIEW", ct);
                break;
            case "APPROVE":
                await ChangeStatusAsync(a, st, "REVIEW", "APPROVED", ct);
                break;
            case "ACTIVATE":
                await ChangeStatusAsync(a, st, "APPROVED", "ACTIVE", ct);
                if (ca.ReturnCode > 4) return;
                await ImpactAsync(a, st, ct);
                break;
            case "IMPACT":
                await ImpactAsync(a, st, ct);
                break;
            case "COMPARE":
                await CompareAsync(a, st, ct);
                break;
            case "GETSTD" or "RECEIPT" or "INVOICE" or "RETURN":
                throw new NotSupportedException($"STDCOST {ca.Function} has no production route (decision D-CST-04).");
            default:
                // 9100-BAD-FUNCTION
                ca.Reject("CST0409", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by STDCOST"));
                break;
        }
    }

    private sealed class State(CstSqlio db, int flags)
    {
        public CstSqlio Db { get; } = db;
        public bool DoWrite { get; } = (flags & Write) != 0;
        public bool InputBad { get; set; }
        public string SetStatus { get; set; } = "";
        public string SetPurpose { get; set; } = "";
        public string Preparer { get; set; } = "";
    }

    /// <summary>0100-INITIALISE.</summary>
    private static int Initialise(StdCostArea a)
    {
        a.Ca.Reset("STDCOST", "CST-007");
        a.RevalQty = 0;
        a.RevalGbp = 0;
        a.ItemCount = 0;
        a.LineCnt = 0;
        a.Adverse = "N";
        a.MoreLines = "N";
        a.Warning = "";
        a.Status = "";
        a.Purpose = "";
        a.Completeness = "COMPLETE";
        a.ItemCode = Pic.Upper(a.ItemCode);
        a.CostSet = Pic.Upper(a.CostSet);
        if (a.EffDate == 0) a.EffDate = a.Ca.BusinessDate;
        if (a.LotSize == 0) a.LotSize = 1;
        return a.Commit is "Y" or "T" ? Write : 0;
    }

    /// <summary>0200-VALIDATE.</summary>
    private static bool Validate(StdCostArea a)
    {
        if (Pic.Blank(a.Ca.Company))
        {
            a.Ca.Reject("CST0401", "A company is required");
            return true;
        }
        if (a.LotSize <= 0)
        {
            a.Ca.Reject("CST0408", "The costing lot size must be positive");
            return true;
        }
        if (a.Ca.Function is "RECEIPT" or "INVOICE" or "RETURN" && a.Quantity <= 0)
        {
            a.Ca.Reject("CST0407", "Quantity must be greater than zero");
            return true;
        }
        return false;
    }

    /// <summary>2300-CHANGE-STATUS.</summary>
    private static async Task ChangeStatusAsync(StdCostArea a, State st, string required, string newStatus, CancellationToken ct)
    {
        var ca = a.Ca;
        await ReadSetAsync(a, st, ct);
        if (st.Db.SqlFailed || st.InputBad) return;
        if (!st.Db.RowFound)
        {
            ca.ReasonCode = "CST0402";
            ca.Message = Pic.Str(200, "Cost set ", Pic.T(a.CostSet), " was not found");
            ca.ReturnCode = 16;
            return;
        }
        if (!Pic.Eq(st.SetStatus, Pic.X(required, 10)))
        {
            ca.Reject("CST0403", Pic.Str(200, "Cost set is ", Pic.T(st.SetStatus), " - it must be ", Pic.T(required), " for this action"));
            st.InputBad = true;
            return;
        }
        if (newStatus == "ACTIVE" && !Pic.Eq(st.SetPurpose, "STANDARD"))
        {
            ca.Reject("CST0404", "A simulation or quote set cannot be activated");
            st.InputBad = true;
            return;
        }
        if (newStatus == "APPROVED" && Pic.T(st.Preparer) == Pic.T(ca.User))
        {
            ca.ReasonCode = "CST0405";
            ca.Message = "The preparer of a cost set may not approve it";
            ca.ReturnCode = 6;
            ca.CountMessage();
            return;
        }
        a.Status = newStatus;
        if (!st.DoWrite) return;
        string statement;
        SqlioParm[] parms;
        switch (newStatus)
        {
            case "APPROVED":
                statement = "UPDATE cst.CostSet SET Status = ?, ApprovedBy = ?, ApprovedAt = SYSDATETIME(), ModifiedBy = ?, " +
                            "ModifiedAt = SYSDATETIME() WHERE CompanyCode = ? AND CostSetCode = ?";
                parms = [SqlioParm.Text(newStatus), SqlioParm.Text(ca.User), SqlioParm.Text(ca.User), SqlioParm.Text(ca.Company), SqlioParm.Text(a.CostSet)];
                break;
            case "ACTIVE":
                statement = "UPDATE cst.CostSet SET Status = ?, ActivatedAt = SYSDATETIME(), ModifiedBy = ?, ModifiedAt = SYSDATETIME() " +
                            "WHERE CompanyCode = ? AND CostSetCode = ? AND ? IS NOT NULL";
                parms = [SqlioParm.Text(newStatus), SqlioParm.Text(ca.User), SqlioParm.Text(ca.Company), SqlioParm.Text(a.CostSet), SqlioParm.Text(ca.User)];
                break;
            default:
                statement = "UPDATE cst.CostSet SET Status = ?, ReviewedBy = ?, ModifiedBy = ?, ModifiedAt = SYSDATETIME() " +
                            "WHERE CompanyCode = ? AND CostSetCode = ?";
                parms = [SqlioParm.Text(newStatus), SqlioParm.Text(ca.User), SqlioParm.Text(ca.User), SqlioParm.Text(ca.Company), SqlioParm.Text(a.CostSet)];
                break;
        }
        await st.Db.ExecuteAsync(statement, parms, ct);
        if (!st.Db.SqlFailed) a.ApprovedBy = ca.User;
    }

    /// <summary>2400-READ-SET.</summary>
    private static async Task ReadSetAsync(StdCostArea a, State st, CancellationToken ct)
    {
        await st.Db.SingleAsync(
            "SELECT Status, Purpose, ISNULL(PreparedBy,''), ISNULL(ComparedToSetCode,''), ISNULL(ApprovedBy,'') FROM cst.CostSet " +
            "WHERE CompanyCode = ? AND CostSetCode = ?", 5,
            [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.CostSet)], ct);
        if (!st.Db.RowFound) return;
        var row = st.Db.Row;
        st.SetStatus = Pic.X(row.Raw(0), 10);
        a.Status = st.SetStatus;
        st.SetPurpose = Pic.X(row.Raw(1), 12);
        st.Preparer = Pic.X(row.Raw(2), 20);
        a.Purpose = st.SetPurpose;
        a.PreparedBy = st.Preparer;
        a.ApprovedBy = Pic.X(row.Raw(4), 20);
        if (Pic.Blank(a.CompareSet)) a.CompareSet = Pic.X(row.Raw(3), 20);
    }

    /// <summary>3000-IMPACT: what activation would do to owned inventory (changes nothing).</summary>
    private static async Task ImpactAsync(StdCostArea a, State st, CancellationToken ct)
    {
        var ca = a.Ca;
        await ReadSetAsync(a, st, ct);
        if (st.Db.SqlFailed || st.InputBad) return;
        if (!st.Db.RowFound)
        {
            ca.ReasonCode = "CST0402";
            ca.Message = Pic.Str(200, "Cost set ", Pic.T(a.CostSet), " was not found");
            ca.ReturnCode = 16;
            return;
        }
        await st.Db.SingleAsync(
            "SELECT COUNT(*), ISNULL(SUM(ic.QtyOnHand),0), ISNULL(SUM(ic.QtyOnHand * (sc.TotalUnitCost - ISNULL(ic.StandardUnitCost, " +
            "ic.AverageUnitCost))),0) FROM cst.StandardCost sc JOIN cst.ItemCost ic ON ic.CompanyCode = sc.CompanyCode AND " +
            "ic.ItemCode = sc.ItemCode WHERE sc.CompanyCode = ? AND sc.CostSetCode = ? AND ic.QtyOnHand <> 0", 3,
            [SqlioParm.Text(ca.Company), SqlioParm.Text(a.CostSet)], ct);
        if (st.Db.RowFound)
        {
            var row = st.Db.Row;
            a.ItemCount = Pic.I(row.NumVal(0), 9);
            a.RevalQty = Pic.N(row.NumVal(1), 13, 6);
            a.RevalGbp = Pic.N(row.NumVal(2), 13, 2);
        }
        if (a.RevalGbp < 0) a.Adverse = "Y";
        if (ca.Function == "IMPACT") a.Warning = "No stock has been revalued - this is a preview";
    }

    /// <summary>5000-COMPARE / 5100-COMPARE-LINE: the target set's standards against the comparison set, 20-line window.</summary>
    private static async Task CompareAsync(StdCostArea a, State st, CancellationToken ct)
    {
        var ca = a.Ca;
        if (Pic.Blank(a.CompareSet)) await ReadSetAsync(a, st, ct);
        if (Pic.Blank(a.CompareSet))
        {
            ca.Reject("CST0410", "No comparison cost set was named");
            st.InputBad = true;
            return;
        }
        var rows = await st.Db.FetchAllAsync(
            "SELECT n.ItemCode, n.Revision, ISNULL(o.TotalUnitCost,0), n.TotalUnitCost, ISNULL(ic.QtyOnHand,0) FROM cst.StandardCost n " +
            "LEFT JOIN cst.StandardCost o ON o.CompanyCode = n.CompanyCode AND o.ItemCode = n.ItemCode AND o.CostSetCode = ? " +
            "LEFT JOIN cst.ItemCost ic ON ic.CompanyCode = n.CompanyCode AND ic.ItemCode = n.ItemCode WHERE n.CompanyCode = ? AND " +
            "n.CostSetCode = ? ORDER BY n.ItemCode", 5,
            [SqlioParm.Text(a.CompareSet), SqlioParm.Text(ca.Company), SqlioParm.Text(a.CostSet)], ct);
        if (rows is null) return;
        var count = 0;
        foreach (var row in rows)
        {
            count++;
            if (count > a.FromLine && a.LineCnt < 20)
            {
                CompareLine(a, row);
            }
            else if (a.LineCnt >= 20)
            {
                a.MoreLines = "Y";
            }
        }
        a.ItemCount = Pic.I(count, 9);
    }

    private static void CompareLine(StdCostArea a, SqlioRow row)
    {
        a.LineCnt++;
        var l = a.Lines[a.LineCnt - 1];
        l.Item = Pic.X(row.Raw(0), 40);
        l.Rev = Pic.X(row.Raw(1), 4);
        l.OldCost = Pic.N(row.NumVal(2), 11, 6);
        l.NewCost = Pic.N(row.NumVal(3), 11, 6);
        l.QtyHeld = Pic.N(row.NumVal(4), 13, 6);
        l.Diff = ((Cd)l.NewCost - l.OldCost).Store(11, 6);
        if (l.OldCost != 0)
        {
            l.Pct = ((Cd)l.Diff * 100 / l.OldCost).Store(5, 4, rounded: true, previous: l.Pct);
            l.Note = "";
        }
        else
        {
            l.Pct = 0;
            l.Note = "New item in this set";
        }
        l.Reval = ((Cd)l.QtyHeld * l.Diff).Store(13, 2, rounded: true);
        a.RevalGbp = ((Cd)a.RevalGbp + l.Reval).Store(13, 2);
    }
}
