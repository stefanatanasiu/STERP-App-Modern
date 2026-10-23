using Sterp.Modules.Cst.Data;
using Sterp.Modules.Cst.Domain;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Cst.Engines;

/// <summary>
/// JOBCOST (cobol/online/cst/JOBCOST.cob) - job cost enquiry: ENQUIRE (4000-4400) and FORECAST (5000). The write functions
/// (CREATE, BUDGET, APPRBUDG, POSTCOST, REVERSE, COMPLETE, CLOSE, REOPEN) have no production route (D-CST-04, SME-805).
/// Read-only on the caller's unit of work.
/// </summary>
internal sealed class JobCostEngine(ISqlRunner sql)
{
    public async Task LinkAsync(JobCostArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        var db = new CstSqlio(sql, ca);
        Initialise(a);
        if (Validate(a)) return;
        var exists = await ReadJobAsync(a, db, ct);
        if (db.SqlFailed) return;
        switch (ca.Function)
        {
            case "ENQUIRE":
                await EnquireAsync(a, db, exists, ct);
                break;
            case "FORECAST":
                await ForecastAsync(a, db, exists, ct);
                break;
            case "CREATE" or "BUDGET" or "APPRBUDG" or "POSTCOST" or "REVERSE" or "COMPLETE" or "CLOSE" or "REOPEN":
                throw new NotSupportedException($"JOBCOST {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0811", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by JOBCOST"));
                break;
        }
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(JobCostArea a)
    {
        a.Ca.Reset("JOBCOST", "CST-014");
        a.ActualGbp = 0;
        a.AccruedGbp = 0;
        a.CommittedGbp = 0;
        a.EacGbp = 0;
        a.FcstVarGbp = 0;
        a.RevenueGbp = 0;
        a.WipGbp = 0;
        a.SubjobCount = 0;
        a.TxnCount = 0;
        a.LineCnt = 0;
        a.OriginalBudget = 0;
        a.ApprovedBudget = 0;
        a.FcstAdverse = "N";
        a.MoreLines = "N";
        a.Warning = "";
        a.JobRef = Pic.Upper(a.JobRef);
        a.ParentJob = Pic.Upper(a.ParentJob);
        a.ElementCode = Pic.Upper(a.ElementCode);
        if (Pic.Blank(a.Currency)) a.Currency = "GBP";
        if (a.RecognitionDate == 0) a.RecognitionDate = a.Ca.BusinessDate;
        if (Pic.Blank(a.CostNature)) a.CostNature = "ACTUAL";
    }

    /// <summary>0200-VALIDATE.</summary>
    private static bool Validate(JobCostArea a)
    {
        if (Pic.Blank(a.Ca.Company) || Pic.Blank(a.JobRef))
        {
            a.Ca.Reject("CST0801", "A company and a job reference are required");
            return true;
        }
        return false;
    }

    /// <summary>0300-READ-JOB.</summary>
    private static async Task<bool> ReadJobAsync(JobCostArea a, CstSqlio db, CancellationToken ct)
    {
        await db.SingleAsync(
            "SELECT JobLevel, OperationalStatus, FinancialStatus, ISNULL(ParentJobRef,''), Description FROM cst.Job WHERE CompanyCode = ? " +
            "AND JobRef = ?", 5, [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef)], ct);
        if (!db.RowFound) return false;
        var row = db.Row;
        a.JobLevel = Pic.UI(row.NumVal(0), 1);
        a.OperStatus = Pic.X(row.Raw(1), 14);
        a.FinStatus = Pic.X(row.Raw(2), 14);
        if (Pic.Blank(a.ParentJob)) a.ParentJob = Pic.X(row.Raw(3), 20);
        if (Pic.Blank(a.Description)) a.Description = Pic.X(row.Raw(4), 150);
        return true;
    }

    /// <summary>4000-ENQUIRE.</summary>
    private static async Task EnquireAsync(JobCostArea a, CstSqlio db, bool exists, CancellationToken ct)
    {
        if (!exists)
        {
            JobNotFound(a);
            return;
        }
        await TotalsAsync(a, db, ct);
        if (db.SqlFailed) return;
        await BudgetTotalsAsync(a, db, ct);
        if (db.SqlFailed) return;
        await ByElementAsync(a, db, ct);
        await SubjobCountAsync(a, db, ct);
    }

    /// <summary>9200-JOB-NOT-FOUND.</summary>
    private static void JobNotFound(JobCostArea a)
    {
        a.Ca.ReasonCode = "CST0802";
        a.Ca.Message = Pic.Str(200, "Job ", Pic.T(a.JobRef), " was not found in this company");
        a.Ca.ReturnCode = 16;
    }

    /// <summary>4400-SUBJOB-COUNT.</summary>
    private static async Task SubjobCountAsync(JobCostArea a, CstSqlio db, CancellationToken ct)
    {
        await db.SingleAsync("SELECT COUNT(*) FROM cst.Job WHERE CompanyCode = ? AND ParentJobRef = ?", 1,
            [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef)], ct);
        if (db.RowFound) a.SubjobCount = Pic.I(db.Row.NumVal(0), 4);
    }

    /// <summary>4100-TOTALS: approved transactions of the job and, with JC-INCLUDE-SUBJOBS Y, of its direct sub-jobs.</summary>
    private static async Task TotalsAsync(JobCostArea a, CstSqlio db, CancellationToken ct)
    {
        await db.SingleAsync(
            "SELECT SUM(CASE WHEN CostNature = 'ACTUAL' AND ElementCode <> 'REVENUE' THEN AmountGBP ELSE 0 END), SUM(CASE WHEN " +
            "CostNature = 'ACCRUED' THEN AmountGBP ELSE 0 END), SUM(CASE WHEN CostNature = 'COMMITTED' THEN AmountGBP ELSE 0 END), " +
            "SUM(CASE WHEN CostNature = 'ESTIMATE' THEN AmountGBP ELSE 0 END), SUM(CASE WHEN ElementCode = 'REVENUE' THEN -AmountGBP " +
            "ELSE 0 END), COUNT(*) FROM cst.JobCostTransaction WHERE CompanyCode = ? AND IsApproved = 1 AND (JobRef = ? OR (? = 'Y' " +
            "AND JobRef IN (SELECT JobRef FROM cst.Job WHERE CompanyCode = ? AND ParentJobRef = ?)))", 6,
            [
                SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef), SqlioParm.Text(a.IncludeSubjobs == "Y" ? "Y" : "N"),
                SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef),
            ], ct);
        if (!db.RowFound) return;
        var row = db.Row;
        a.ActualGbp = Pic.N(row.NumVal(0), 13, 2);
        a.AccruedGbp = Pic.N(row.NumVal(1), 13, 2);
        a.CommittedGbp = Pic.N(row.NumVal(2), 13, 2);
        if (a.EtcGbp == 0) a.EtcGbp = Pic.N(row.NumVal(3), 13, 2);
        a.RevenueGbp = Pic.N(row.NumVal(4), 13, 2);
        a.TxnCount = Pic.I(row.NumVal(5), 9);
    }

    /// <summary>4200-BUDGET-TOTALS: original and approved budget of the job itself.</summary>
    private static async Task BudgetTotalsAsync(JobCostArea a, CstSqlio db, CancellationToken ct)
    {
        await db.SingleAsync(
            "SELECT ISNULL(MAX(CASE WHEN IsOriginal = 1 THEN TotalBudgetGBP END),0), ISNULL(MAX(CASE WHEN Status = 'APPROVED' THEN " +
            "TotalBudgetGBP END),0) FROM cst.JobBudget WHERE CompanyCode = ? AND JobRef = ?", 2,
            [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef)], ct);
        if (db.RowFound)
        {
            a.OriginalBudget = Pic.N(db.Row.NumVal(0), 13, 2);
            a.ApprovedBudget = Pic.N(db.Row.NumVal(1), 13, 2);
        }
        if (a.ApprovedBudget == 0) a.ApprovedBudget = a.OriginalBudget;
        a.BudgetTotal = a.ApprovedBudget;
    }

    /// <summary>4300-BY-ELEMENT: the job's own approved transactions by element code, at most 20 lines.</summary>
    private static async Task ByElementAsync(JobCostArea a, CstSqlio db, CancellationToken ct)
    {
        var rows = await db.FetchAllAsync(
            "SELECT t.ElementCode, SUM(CASE WHEN t.CostNature = 'ACTUAL' THEN t.AmountGBP ELSE 0 END), SUM(CASE WHEN t.CostNature = " +
            "'COMMITTED' THEN t.AmountGBP ELSE 0 END), SUM(CASE WHEN t.CostNature = 'ESTIMATE' THEN t.AmountGBP ELSE 0 END) FROM " +
            "cst.JobCostTransaction t WHERE t.CompanyCode = ? AND t.JobRef = ? AND t.IsApproved = 1 GROUP BY t.ElementCode ORDER BY " +
            "t.ElementCode", 4, [SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef)], ct);
        if (rows is null) return;
        foreach (var row in rows)
        {
            if (a.LineCnt >= 20)
            {
                a.MoreLines = "Y";
                break;
            }
            a.LineCnt++;
            var l = a.Lines[a.LineCnt - 1];
            l.Element = Pic.X(row.Raw(0), 10);
            l.Key = Pic.X(row.Raw(0), 20);
            l.Actual = Pic.N(row.NumVal(1), 13, 2);
            l.Committed = Pic.N(row.NumVal(2), 13, 2);
            l.Etc = Pic.N(row.NumVal(3), 13, 2);
            l.Budget = 0;
            l.Eac = ((Cd)l.Actual + l.Committed + l.Etc).Store(13, 2);
            l.Variance = ((Cd)l.Eac - l.Budget).Store(13, 2);
            l.Adverse = l.Variance > 0 ? "Y" : "N";
        }
    }

    /// <summary>5000-FORECAST: estimate at completion over the ENQUIRE totals.</summary>
    private static async Task ForecastAsync(JobCostArea a, CstSqlio db, bool exists, CancellationToken ct)
    {
        await EnquireAsync(a, db, exists, ct);
        if (db.SqlFailed) return;
        if (a.Ca.ReturnCode > 4) return;
        a.EacGbp = ((Cd)a.ActualGbp + a.AccruedGbp + a.CommittedGbp + a.EtcGbp).Store(13, 2);
        a.FcstVarGbp = ((Cd)a.EacGbp - a.ApprovedBudget).Store(13, 2);
        if (a.FcstVarGbp > 0)
        {
            a.FcstAdverse = "Y";
            a.Warning = "A positive forecast variance is adverse";
        }
        if (a.ApprovedBudget == 0)
        {
            a.Warning = "No approved budget - the variance is the whole";
            a.Ca.ReturnCode = 4;
        }
    }
}

/// <summary>
/// WIPCALC (cobol/online/cst/WIPCALC.cob) - the work in progress roll-forward: CALC (1000-1300) and POST (2000: UPDATE else
/// INSERT of cst.WipBalance, each statement autocommitted). ENQUIRE, RESIDUAL and WRITEOFF have no production route.
/// </summary>
internal sealed class WipCalcEngine(ISqlRunner sql)
{
    public async Task LinkAsync(WipCalcArea a, CancellationToken ct)
    {
        var ca = a.Ca;
        var db = new CstSqlio(sql, ca);
        var write = Initialise(a);
        if (Validate(a)) return;
        switch (ca.Function)
        {
            case "CALC":
                await CalculateAsync(a, db, ct);
                break;
            case "POST":
                await PostAsync(a, db, write, ct);
                break;
            case "ENQUIRE" or "RESIDUAL" or "WRITEOFF":
                throw new NotSupportedException($"WIPCALC {ca.Function} has no production route (decision D-CST-04).");
            default:
                ca.Reject("CST0908", Pic.Str(200, "Function ", Pic.T(ca.Function), " is not supported by WIPCALC"));
                break;
        }
    }

    /// <summary>0100-INITIALISE; returns WS-WRITE.</summary>
    private static bool Initialise(WipCalcArea a)
    {
        a.Ca.Reset("WIPCALC", "CST-018");
        a.OpeningGbp = 0;
        a.MatIssuesGbp = 0;
        a.MatReturnsGbp = 0;
        a.LabourGbp = 0;
        a.MachineGbp = 0;
        a.OverheadGbp = 0;
        a.SubconGbp = 0;
        a.AdjustmentGbp = 0;
        a.TransfersGbp = 0;
        a.ScrapGbp = 0;
        a.ClosingGbp = 0;
        a.AdditionsGbp = 0;
        a.DeductionsGbp = 0;
        a.ResidualGbp = 0;
        a.TxnCount = 0;
        a.WipId = 0;
        if (a.Ca.Function != "WRITEOFF") a.WriteoffGbp = 0;
        a.HasResidual = "N";
        a.Balanced = "Y";
        a.Warning = "";
        a.PeriodStatus = "";
        a.JobRef = Pic.Upper(a.JobRef);
        return a.Commit is "Y" or "T";
    }

    /// <summary>0200-VALIDATE.</summary>
    private static bool Validate(WipCalcArea a)
    {
        if (Pic.Blank(a.Ca.Company))
        {
            a.Ca.Reject("CST0901", "A company is required");
            return true;
        }
        if (Pic.Blank(a.JobRef) && Pic.Blank(a.KitRef))
        {
            a.Ca.Reject("CST0902", "A job or a kit reference is required");
            return true;
        }
        if (a.FiscalYear == 0 || a.PeriodNo == 0)
        {
            a.Ca.Reject("CST0903", "A fiscal year and period are required");
            return true;
        }
        return false;
    }

    /// <summary>1000-CALCULATE.</summary>
    private static async Task CalculateAsync(WipCalcArea a, CstSqlio db, CancellationToken ct)
    {
        await OpeningBalanceAsync(a, db, ct);
        if (db.SqlFailed) return;
        await PeriodMovementsAsync(a, db, ct);
        if (db.SqlFailed) return;
        a.AdditionsGbp = ((Cd)a.MatIssuesGbp + a.LabourGbp + a.MachineGbp + a.OverheadGbp + a.SubconGbp + a.AdjustmentGbp).Store(13, 2);
        a.DeductionsGbp = ((Cd)a.MatReturnsGbp + a.TransfersGbp + a.ScrapGbp + a.WriteoffGbp).Store(13, 2);
        a.ClosingGbp = ((Cd)a.OpeningGbp + a.AdditionsGbp - a.DeductionsGbp).Store(13, 2);
        a.ResidualGbp = a.ClosingGbp;
        if (a.ResidualGbp != 0) a.HasResidual = "Y";
    }

    /// <summary>1100-OPENING-BALANCE: closing of the latest balance strictly before the period.</summary>
    private static async Task OpeningBalanceAsync(WipCalcArea a, CstSqlio db, CancellationToken ct)
    {
        await db.SingleAsync(
            "SELECT TOP 1 ClosingValueGBP FROM cst.WipBalance WHERE CompanyCode = ? AND ISNULL(JobRef,'') = ? AND ISNULL(KitRef,'') = ? " +
            "AND (FiscalYear < ? OR (FiscalYear = ? AND PeriodNo < ?)) ORDER BY FiscalYear DESC, PeriodNo DESC", 1,
            [
                SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef), SqlioParm.Text(a.KitRef), SqlioParm.Digits(a.FiscalYear, 4),
                SqlioParm.Digits(a.FiscalYear, 4), SqlioParm.Digits(a.PeriodNo, 2),
            ], ct);
        if (db.RowFound) a.OpeningGbp = Pic.N(db.Row.NumVal(0), 13, 2);
    }

    /// <summary>1200-PERIOD-MOVEMENTS / 1300-BUCKET.</summary>
    private static async Task PeriodMovementsAsync(WipCalcArea a, CstSqlio db, CancellationToken ct)
    {
        var rows = await db.FetchAllAsync(
            "SELECT e.ElementType, t.ElementCode, SUM(t.AmountGBP), COUNT(*) FROM cst.JobCostTransaction t JOIN cst.CostElement e ON " +
            "e.ElementCode = t.ElementCode WHERE t.CompanyCode = ? AND ISNULL(t.JobRef,'') = ? AND ISNULL(t.KitRef,'') = ? AND " +
            "t.FiscalYear = ? AND t.PeriodNo = ? AND t.IsApproved = 1 AND t.CostNature IN ('ACTUAL','ACCRUED') GROUP BY " +
            "e.ElementType, t.ElementCode", 4,
            [
                SqlioParm.Text(a.Ca.Company), SqlioParm.Text(a.JobRef), SqlioParm.Text(a.KitRef), SqlioParm.Digits(a.FiscalYear, 4),
                SqlioParm.Digits(a.PeriodNo, 2),
            ], ct);
        if (rows is null) return;
        foreach (var row in rows)
        {
            var amount = Pic.N(row.NumVal(2), 13, 6);
            a.TxnCount = (int)((Cd)a.TxnCount + row.NumVal(3)).Store(9, 0);
            Bucket(a, row.Raw(0)[..8], row.Raw(1)[..10], amount);
        }
    }

    private static void Bucket(WipCalcArea a, string elementType, string elementCode, decimal amount)
    {
        decimal Add(decimal field, decimal value) => ((Cd)field + value).Store(13, 2);
        switch (elementCode)
        {
            case "TRANSFER  ":
                a.TransfersGbp = Add(a.TransfersGbp, amount);
                return;
            case "MATRETURN ":
                a.MatReturnsGbp = Add(a.MatReturnsGbp, amount);
                return;
            case "SCRAP     ":
                a.ScrapGbp = Add(a.ScrapGbp, amount);
                return;
            case "WRITEOFF  ":
                a.WriteoffGbp = Add(a.WriteoffGbp, amount);
                return;
        }
        switch (elementType)
        {
            case "MATERIAL":
                if (amount < 0) a.MatReturnsGbp = ((Cd)a.MatReturnsGbp - amount).Store(13, 2);
                else a.MatIssuesGbp = Add(a.MatIssuesGbp, amount);
                break;
            case "LABOUR  ":
                a.LabourGbp = Add(a.LabourGbp, amount);
                break;
            case "MACHINE ":
                a.MachineGbp = Add(a.MachineGbp, amount);
                break;
            case "SUBCON  ":
                a.SubconGbp = Add(a.SubconGbp, amount);
                break;
            case "OVHDVAR " or "OVHDFIX ":
                a.OverheadGbp = Add(a.OverheadGbp, amount);
                break;
            case "LANDED  " or "VARIANCE":
                a.AdjustmentGbp = Add(a.AdjustmentGbp, amount);
                break;
            case "RECOVERY":
                a.AdjustmentGbp = ((Cd)a.AdjustmentGbp - amount).Store(13, 2);
                break;
            default:
                a.AdjustmentGbp = Add(a.AdjustmentGbp, amount);
                break;
        }
    }

    /// <summary>2000-POST: the roll-forward, then UPDATE of the period's balance, INSERT when no row was updated.</summary>
    private static async Task PostAsync(WipCalcArea a, CstSqlio db, bool write, CancellationToken ct)
    {
        await CalculateAsync(a, db, ct);
        if (db.SqlFailed) return;
        if (!write)
        {
            a.Warning = "Preview only - no WIP balance was written";
            return;
        }
        await db.ExecuteAsync(
            "UPDATE cst.WipBalance SET OpeningValueGBP = ?, MaterialIssuesGBP = ?, MaterialReturnsGBP = ?, LabourGBP = ?, OverheadGBP = ?, " +
            "SubcontractGBP = ?, CompletionTransfersGBP = ?, ScrapGBP = ?, WriteOffGBP = ?, ValueAdjustmentGBP = ?, ClosingValueGBP = ?, " +
            "LastUpdatedAt = SYSDATETIME() WHERE CompanyCode = ? AND ISNULL(JobRef,'') = ? AND ISNULL(KitRef,'') = ? AND FiscalYear = ? " +
            "AND PeriodNo = ?", BindBalance(a, false), ct);
        if (db.SqlFailed) return;
        if (db.RowCount > 0) return;
        await db.ExecuteAsync(
            "INSERT INTO cst.WipBalance (OpeningValueGBP, MaterialIssuesGBP, MaterialReturnsGBP, LabourGBP, OverheadGBP, SubcontractGBP, " +
            "CompletionTransfersGBP, ScrapGBP, WriteOffGBP, ValueAdjustmentGBP, ClosingValueGBP, LastUpdatedAt, CompanyCode, JobRef, " +
            "KitRef, FiscalYear, PeriodNo) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, SYSDATETIME(), ?, ?, ?, ?, ?)", BindBalance(a, true), ct);
    }

    /// <summary>2100-BIND-BALANCE (with the INSERT null flags for a blank job or kit).</summary>
    private static SqlioParm[] BindBalance(WipCalcArea a, bool insert) =>
    [
        SqlioParm.Number(a.OpeningGbp), SqlioParm.Number(a.MatIssuesGbp), SqlioParm.Number(a.MatReturnsGbp),
        SqlioParm.Number(a.LabourGbp), SqlioParm.Number(a.OverheadGbp), SqlioParm.Number(a.SubconGbp),
        SqlioParm.Number(a.TransfersGbp), SqlioParm.Number(a.ScrapGbp), SqlioParm.Number(a.WriteoffGbp),
        SqlioParm.Number(a.AdjustmentGbp), SqlioParm.Number(a.ClosingGbp), SqlioParm.Text(a.Ca.Company),
        insert && Pic.Blank(a.JobRef) ? SqlioParm.Null : SqlioParm.Text(a.JobRef),
        insert && Pic.Blank(a.KitRef) ? SqlioParm.Null : SqlioParm.Text(a.KitRef),
        SqlioParm.Digits(a.FiscalYear, 4), SqlioParm.Digits(a.PeriodNo, 2),
    ];
}
