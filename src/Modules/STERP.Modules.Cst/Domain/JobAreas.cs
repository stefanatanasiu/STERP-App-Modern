namespace Sterp.Modules.Cst.Domain;

/// <summary>JOBCOSTCA (cobol/copy/JOBCOSTCA.cpy): the fields of the routed functions ENQUIRE and FORECAST.</summary>
internal sealed class JobCostArea
{
    public CaHeader Ca { get; set; } = new();

    /// <summary>JC-JOB-REF X(20).</summary>
    public string JobRef { get; set; } = "";

    /// <summary>JC-PARENT-JOB X(20).</summary>
    public string ParentJob { get; set; } = "";

    /// <summary>JC-JOB-LEVEL 9(1).</summary>
    public int JobLevel { get; set; }

    /// <summary>JC-DESCRIPTION X(150).</summary>
    public string Description { get; set; } = "";

    /// <summary>JC-INCLUDE-SUBJOBS X.</summary>
    public string IncludeSubjobs { get; set; } = "";

    /// <summary>JC-BUDGET-TOTAL S9(13)V9(2).</summary>
    public decimal BudgetTotal { get; set; }

    /// <summary>JC-ORIGINAL-BUDGET S9(13)V9(2).</summary>
    public decimal OriginalBudget { get; set; }

    /// <summary>JC-APPROVED-BUDGET S9(13)V9(2).</summary>
    public decimal ApprovedBudget { get; set; }

    /// <summary>JC-ELEMENT-CODE X(10).</summary>
    public string ElementCode { get; set; } = "";

    /// <summary>JC-CURRENCY X(3).</summary>
    public string Currency { get; set; } = "";

    /// <summary>JC-COST-NATURE X(10).</summary>
    public string CostNature { get; set; } = "";

    /// <summary>JC-RECOGNITION-DT 9(8).</summary>
    public int RecognitionDate { get; set; }

    /// <summary>JC-COMMIT X.</summary>
    public string Commit { get; set; } = "";

    /// <summary>JC-ACTUAL-GBP S9(13)V9(2).</summary>
    public decimal ActualGbp { get; set; }

    /// <summary>JC-ACCRUED-GBP S9(13)V9(2).</summary>
    public decimal AccruedGbp { get; set; }

    /// <summary>JC-COMMITTED-GBP S9(13)V9(2).</summary>
    public decimal CommittedGbp { get; set; }

    /// <summary>JC-ETC-GBP S9(13)V9(2).</summary>
    public decimal EtcGbp { get; set; }

    /// <summary>JC-EAC-GBP S9(13)V9(2).</summary>
    public decimal EacGbp { get; set; }

    /// <summary>JC-FCST-VAR-GBP S9(13)V9(2).</summary>
    public decimal FcstVarGbp { get; set; }

    /// <summary>JC-FCST-ADVERSE X.</summary>
    public string FcstAdverse { get; set; } = "";

    /// <summary>JC-REVENUE-GBP S9(13)V9(2).</summary>
    public decimal RevenueGbp { get; set; }

    /// <summary>JC-WIP-GBP S9(13)V9(2).</summary>
    public decimal WipGbp { get; set; }

    /// <summary>JC-SUBJOB-COUNT S9(4).</summary>
    public int SubjobCount { get; set; }

    /// <summary>JC-TXN-COUNT S9(9).</summary>
    public int TxnCount { get; set; }

    /// <summary>JC-OPER-STATUS X(14).</summary>
    public string OperStatus { get; set; } = "";

    /// <summary>JC-FIN-STATUS X(14).</summary>
    public string FinStatus { get; set; } = "";

    /// <summary>JC-DISPOSITION X(60).</summary>
    public string Disposition { get; set; } = "";

    /// <summary>JC-WARNING X(80).</summary>
    public string Warning { get; set; } = "";

    /// <summary>JC-FROM-LINE S9(9).</summary>
    public int FromLine { get; set; }

    /// <summary>JC-LINE-CNT S9(4).</summary>
    public int LineCnt { get; set; }

    /// <summary>JC-MORE-LINES X.</summary>
    public string MoreLines { get; set; } = "";

    /// <summary>JC-LINES OCCURS 20.</summary>
    public JobCostLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new JobCostLine()).ToArray();
}

/// <summary>One JC-LINES entry.</summary>
internal sealed class JobCostLine
{
    /// <summary>JC-L-KEY X(20).</summary>
    public string Key { get; set; } = "";

    /// <summary>JC-L-DESC X(60).</summary>
    public string Desc { get; set; } = "";

    /// <summary>JC-L-ELEMENT X(10).</summary>
    public string Element { get; set; } = "";

    public decimal Budget { get; set; }

    public decimal Actual { get; set; }

    public decimal Committed { get; set; }

    public decimal Etc { get; set; }

    public decimal Eac { get; set; }

    public decimal Variance { get; set; }

    /// <summary>JC-L-ADVERSE X.</summary>
    public string Adverse { get; set; } = "";
}

/// <summary>WIPCALCCA (cobol/copy/WIPCALCCA.cpy): CALC and POST.</summary>
internal sealed class WipCalcArea
{
    public CaHeader Ca { get; set; } = new();

    /// <summary>WC-JOB-REF X(20).</summary>
    public string JobRef { get; set; } = "";

    /// <summary>WC-KIT-REF X(30).</summary>
    public string KitRef { get; set; } = "";

    /// <summary>WC-FISCAL-YEAR 9(4).</summary>
    public int FiscalYear { get; set; }

    /// <summary>WC-PERIOD-NO 9(2).</summary>
    public int PeriodNo { get; set; }

    /// <summary>WC-INCLUDE-SUBJOBS X (never read by WIPCALC, SME-818).</summary>
    public string IncludeSubjobs { get; set; } = "";

    /// <summary>WC-COMMIT X.</summary>
    public string Commit { get; set; } = "";

    public decimal OpeningGbp { get; set; }
    public decimal MatIssuesGbp { get; set; }
    public decimal MatReturnsGbp { get; set; }
    public decimal LabourGbp { get; set; }
    public decimal MachineGbp { get; set; }
    public decimal OverheadGbp { get; set; }
    public decimal SubconGbp { get; set; }
    public decimal AdjustmentGbp { get; set; }
    public decimal TransfersGbp { get; set; }
    public decimal ScrapGbp { get; set; }
    public decimal WriteoffGbp { get; set; }
    public decimal ClosingGbp { get; set; }
    public decimal AdditionsGbp { get; set; }
    public decimal DeductionsGbp { get; set; }
    public decimal ResidualGbp { get; set; }

    /// <summary>WC-HAS-RESIDUAL X.</summary>
    public string HasResidual { get; set; } = "";

    /// <summary>WC-TXN-COUNT S9(9).</summary>
    public int TxnCount { get; set; }

    /// <summary>WC-BALANCED X (always Y, SME-822).</summary>
    public string Balanced { get; set; } = "";

    /// <summary>WC-WIP-ID S9(9).</summary>
    public int WipId { get; set; }

    /// <summary>WC-PERIOD-STATUS X(8).</summary>
    public string PeriodStatus { get; set; } = "";

    /// <summary>WC-WARNING X(80).</summary>
    public string Warning { get; set; } = "";
}
