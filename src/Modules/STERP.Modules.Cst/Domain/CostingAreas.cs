namespace Sterp.Modules.Cst.Domain;

/// <summary>COSTROLLCA (cobol/copy/COSTROLLCA.cpy): ROLLUP and GETLINE.</summary>
internal sealed class CostRollArea
{
    public CaHeader Ca { get; set; } = new();

    /// <summary>CR-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";

    /// <summary>CR-REVISION X(4).</summary>
    public string Revision { get; set; } = "";

    /// <summary>CR-COST-SET X(20).</summary>
    public string CostSet { get; set; } = "";

    /// <summary>CR-TARGET-SET X(20).</summary>
    public string TargetSet { get; set; } = "";

    /// <summary>CR-EFF-DATE 9(8).</summary>
    public int EffDate { get; set; }

    /// <summary>CR-COST-DATE 9(8).</summary>
    public int CostDate { get; set; }

    /// <summary>CR-LOT-SIZE S9(13)V9(6).</summary>
    public decimal LotSize { get; set; }

    /// <summary>CR-CURRENCY X(3).</summary>
    public string Currency { get; set; } = "";

    /// <summary>CR-BOUNDARY X(10): STOP or EXPAND.</summary>
    public string Boundary { get; set; } = "";

    /// <summary>CR-MAX-DEPTH S9(4).</summary>
    public int MaxDepth { get; set; }

    /// <summary>CR-NETTED X.</summary>
    public string Netted { get; set; } = "";

    /// <summary>CR-WORK-CENTRE X(20).</summary>
    public string WorkCentre { get; set; } = "";

    public decimal GrossStarts { get; set; }
    public decimal GoodOutput { get; set; }
    public decimal MaterialCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal MachineCost { get; set; }
    public decimal SetupCost { get; set; }
    public decimal SubconCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal OtherCost { get; set; }
    public decimal RecoveryCredit { get; set; }
    public decimal TotalCost { get; set; }

    /// <summary>CR-UNIT-COST S9(11)V9(6).</summary>
    public decimal UnitCost { get; set; }

    public decimal BranchValue { get; set; }

    /// <summary>CR-OCC-COUNT S9(9).</summary>
    public int OccCount { get; set; }

    /// <summary>CR-MISSING-COUNT S9(4).</summary>
    public int MissingCount { get; set; }

    /// <summary>CR-COMPLETENESS X(12).</summary>
    public string Completeness { get; set; } = "";

    /// <summary>CR-ERROR-PATH X(200).</summary>
    public string ErrorPath { get; set; } = "";

    /// <summary>CR-SET-WRITTEN S9(9).</summary>
    public int SetWritten { get; set; }

    /// <summary>CR-WARNING X(80).</summary>
    public string Warning { get; set; } = "";

    /// <summary>CR-FROM-LINE S9(9).</summary>
    public int FromLine { get; set; }

    /// <summary>CR-WIN-MAX S9(4).</summary>
    public int WinMax { get; set; }

    /// <summary>CR-LINE-CNT S9(4).</summary>
    public int LineCnt { get; set; }

    /// <summary>CR-MORE-LINES X.</summary>
    public string MoreLines { get; set; } = "";

    /// <summary>CR-LINES OCCURS 20.</summary>
    public CostRollLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new CostRollLine()).ToArray();
}

/// <summary>One CR-LINES entry of the costed tree window.</summary>
internal sealed class CostRollLine
{
    public int Seq { get; set; }
    public int Depth { get; set; }
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Element { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>CR-L-BOUNDARY X(8): BRANCH, LEAF or STOPPED.</summary>
    public string Boundary { get; set; } = "";

    public decimal ReqQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ExtCost { get; set; }

    /// <summary>CR-L-CONTRIB X.</summary>
    public string Contrib { get; set; } = "";

    public string Warning { get; set; } = "";

    public CostRollLine Copy() => (CostRollLine)MemberwiseClone();
}

/// <summary>LABOVHCA (cobol/copy/LABOVHCA.cpy): the fields of OVERHEAD (and the RATES/ABSORB sections it performs).</summary>
internal sealed class LabOvhArea
{
    public CaHeader Ca { get; set; } = new();
    public string WorkCentre { get; set; } = "";
    public int EffDate { get; set; }
    public string ItemCode { get; set; } = "";
    public decimal GrossUnits { get; set; }
    public int RunCount { get; set; }
    public decimal SetupHours { get; set; }
    public decimal RunHoursUnit { get; set; }
    public decimal MachineHrsUnit { get; set; }
    public string RateBasis { get; set; } = "";
    public decimal SetupRate { get; set; }
    public decimal RunRate { get; set; }
    public decimal MachineRate { get; set; }
    public decimal OvhdRate { get; set; }
    public string OvhdBasis { get; set; } = "";
    public decimal OvhdPct { get; set; }
    public decimal BaseCost { get; set; }
    public string BaseExclOvhd { get; set; } = "";
    public decimal EmbeddedOvhd { get; set; }
    public decimal NormalCapHrs { get; set; }
    public decimal ActualHours { get; set; }
    public decimal SetupCost { get; set; }
    public decimal RunCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal MachineCost { get; set; }
    public decimal OvhdCost { get; set; }
    public decimal TotalCost { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalHours { get; set; }
    public decimal DerivedRate { get; set; }
    public decimal AbsorbedGbp { get; set; }
    public decimal IncurredGbp { get; set; }
    public decimal RecoveryGbp { get; set; }
    public string UnderRecovered { get; set; } = "";
    public decimal CapacityPct { get; set; }
    public string BasisText { get; set; } = "";
    public string Warning { get; set; } = "";
}

/// <summary>WHATIFCA (cobol/copy/WHATIFCA.cpy): SCENARIO and MARGINWI.</summary>
internal sealed class WhatIfArea
{
    public CaHeader Ca { get; set; } = new();

    /// <summary>WI-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";

    /// <summary>WI-REVISION X(4).</summary>
    public string Revision { get; set; } = "";

    /// <summary>WI-SCENARIO-REF X(30).</summary>
    public string ScenarioRef { get; set; } = "";

    /// <summary>WI-EFF-DATE 9(8).</summary>
    public int EffDate { get; set; }

    /// <summary>WI-BASE-COST-SET X(20).</summary>
    public string BaseCostSet { get; set; } = "";

    /// <summary>WI-BASE-LOT-SIZE S9(13)V9(6).</summary>
    public decimal BaseLotSize { get; set; }

    /// <summary>WI-BASE-WORK-CTR X(20).</summary>
    public string BaseWorkCtr { get; set; } = "";

    /// <summary>WI-TARGET-PRICE S9(11)V9(6).</summary>
    public decimal TargetPrice { get; set; }

    /// <summary>WI-SIDE-EFFECTS X.</summary>
    public string SideEffects { get; set; } = "";

    /// <summary>WI-BASE-TOTAL S9(13)V9(2).</summary>
    public decimal BaseTotal { get; set; }

    /// <summary>WI-BASE-UNIT S9(11)V9(6).</summary>
    public decimal BaseUnit { get; set; }

    /// <summary>WI-BASE-COMPLETE X(12).</summary>
    public string BaseComplete { get; set; } = "";

    /// <summary>WI-VARIANT-CNT S9(4).</summary>
    public int VariantCnt { get; set; }

    /// <summary>WI-VARIANTS OCCURS 6.</summary>
    public WhatIfVariant[] Variants { get; } = Enumerable.Range(0, 6).Select(_ => new WhatIfVariant()).ToArray();

    /// <summary>WI-BEST-VARIANT S9(4).</summary>
    public int BestVariant { get; set; }

    /// <summary>WI-WARNING X(80).</summary>
    public string Warning { get; set; } = "";
}

/// <summary>One WI-VARIANTS entry.</summary>
internal sealed class WhatIfVariant
{
    /// <summary>WI-V-LABEL X(30).</summary>
    public string Label { get; set; } = "";

    /// <summary>WI-V-COST-SET X(20).</summary>
    public string CostSet { get; set; } = "";

    /// <summary>WI-V-LOT-SIZE S9(13)V9(6).</summary>
    public decimal LotSize { get; set; }

    /// <summary>WI-V-WORK-CTR X(20).</summary>
    public string WorkCtr { get; set; } = "";

    /// <summary>WI-V-MAT-FACTOR S9(5)V9(4).</summary>
    public decimal MatFactor { get; set; }

    /// <summary>WI-V-RATE-FACTR S9(5)V9(4).</summary>
    public decimal RateFactor { get; set; }

    /// <summary>WI-V-BOUNDARY X(8).</summary>
    public string Boundary { get; set; } = "";

    public decimal Material { get; set; }
    public decimal Conversion { get; set; }
    public decimal Overhead { get; set; }
    public decimal Total { get; set; }

    /// <summary>WI-V-UNIT S9(11)V9(6).</summary>
    public decimal Unit { get; set; }

    public decimal Diff { get; set; }

    /// <summary>WI-V-DIFF-PCT S9(7)V9(4).</summary>
    public decimal DiffPct { get; set; }

    /// <summary>WI-V-MARGIN-PCT S9(7)V9(4).</summary>
    public decimal MarginPct { get; set; }

    /// <summary>WI-V-COMPLETE X(12).</summary>
    public string Complete { get; set; } = "";

    /// <summary>WI-V-WARNING X(60).</summary>
    public string Warning { get; set; } = "";
}

/// <summary>MARGINCA (cobol/copy/MARGINCA.cpy): the fields of EXPECTED.</summary>
internal sealed class MarginArea
{
    public CaHeader Ca { get; set; } = new();
    public string DocType { get; set; } = "";
    public string DocRef { get; set; } = "";
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public int EffDate { get; set; }
    public string Currency { get; set; } = "";
    public decimal FxRate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal Discount { get; set; }
    public decimal VatRate { get; set; }
    public string RevenueInclVat { get; set; } = "";
    public decimal NetRevenue { get; set; }
    public decimal VatAmount { get; set; }
    public string CostBasis { get; set; } = "";
    public decimal UnitCost { get; set; }
    public decimal CostAmount { get; set; }
    public decimal Adjustments { get; set; }
    public string CostBasisUsed { get; set; } = "";
    public decimal MarginValue { get; set; }
    public decimal MarginPct { get; set; }
    public decimal MarkupPct { get; set; }
    public string MarginState { get; set; } = "";
    public string MarkupState { get; set; } = "";
    public string ResultNature { get; set; } = "";
    public string Completeness { get; set; } = "";
    public int LinesCounted { get; set; }
    public int LinesNoCost { get; set; }
    public string Warning { get; set; } = "";
}

/// <summary>COSTNORMCA (cobol/copy/COSTNORMCA.cpy): the fields of CONTEXT.</summary>
internal sealed class CostNormArea
{
    public CaHeader Ca { get; set; } = new();
    public string ItemCode { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Basis { get; set; } = "";
    public string BasisUsed { get; set; } = "";
    public string Currency { get; set; } = "";
    public int EffDate { get; set; }
    public decimal Quantity { get; set; }
    public string ApprovalState { get; set; } = "";
    public string CostSet { get; set; } = "";
    public decimal PricePerQty { get; set; }
    public decimal VatRecoverPct { get; set; }
    public decimal UnitCostGbp { get; set; }
    public decimal ExtCostGbp { get; set; }
    public decimal ElMaterial { get; set; }
    public string Provisional { get; set; } = "";
    public string Completeness { get; set; } = "";
    public string Source { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Warning { get; set; } = "";
}
