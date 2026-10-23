namespace Sterp.Modules.Cst.Domain;

/// <summary>STDCOSTCA (cobol/copy/STDCOSTCA.cpy): the fields of the routed functions REVIEW, APPROVE, ACTIVATE, IMPACT, COMPARE.</summary>
internal sealed class StdCostArea
{
    public CaHeader Ca { get; set; } = new();

    /// <summary>SC-COST-SET X(20).</summary>
    public string CostSet { get; set; } = "";

    /// <summary>SC-COMPARE-SET X(20).</summary>
    public string CompareSet { get; set; } = "";

    /// <summary>SC-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";

    /// <summary>SC-EFF-DATE 9(8).</summary>
    public int EffDate { get; set; }

    /// <summary>SC-QUANTITY S9(13)V9(6).</summary>
    public decimal Quantity { get; set; }

    /// <summary>SC-LOT-SIZE S9(13)V9(6).</summary>
    public decimal LotSize { get; set; }

    /// <summary>SC-COMMIT X.</summary>
    public string Commit { get; set; } = "";

    /// <summary>SC-REVAL-QTY S9(13)V9(6).</summary>
    public decimal RevalQty { get; set; }

    /// <summary>SC-REVAL-GBP S9(13)V9(2).</summary>
    public decimal RevalGbp { get; set; }

    /// <summary>SC-ITEM-COUNT S9(9).</summary>
    public int ItemCount { get; set; }

    /// <summary>SC-ADVERSE X.</summary>
    public string Adverse { get; set; } = "";

    /// <summary>SC-STATUS X(10).</summary>
    public string Status { get; set; } = "";

    /// <summary>SC-PURPOSE X(12).</summary>
    public string Purpose { get; set; } = "";

    /// <summary>SC-PREPARED-BY X(20).</summary>
    public string PreparedBy { get; set; } = "";

    /// <summary>SC-APPROVED-BY X(20).</summary>
    public string ApprovedBy { get; set; } = "";

    /// <summary>SC-COMPLETENESS X(12).</summary>
    public string Completeness { get; set; } = "";

    /// <summary>SC-WARNING X(80).</summary>
    public string Warning { get; set; } = "";

    /// <summary>SC-FROM-LINE S9(9).</summary>
    public int FromLine { get; set; }

    /// <summary>SC-LINE-CNT S9(4).</summary>
    public int LineCnt { get; set; }

    /// <summary>SC-MORE-LINES X.</summary>
    public string MoreLines { get; set; } = "";

    /// <summary>SC-LINES OCCURS 20 (entries 0..LineCnt-1 are valid).</summary>
    public StdCostLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new StdCostLine()).ToArray();
}

/// <summary>One SC-LINES entry of the comparison window.</summary>
internal sealed class StdCostLine
{
    /// <summary>SC-L-ITEM X(40).</summary>
    public string Item { get; set; } = "";

    /// <summary>SC-L-REV X(4).</summary>
    public string Rev { get; set; } = "";

    /// <summary>SC-L-OLD-COST S9(11)V9(6).</summary>
    public decimal OldCost { get; set; }

    /// <summary>SC-L-NEW-COST S9(11)V9(6).</summary>
    public decimal NewCost { get; set; }

    /// <summary>SC-L-DIFF S9(11)V9(6).</summary>
    public decimal Diff { get; set; }

    /// <summary>SC-L-PCT S9(5)V9(4).</summary>
    public decimal Pct { get; set; }

    /// <summary>SC-L-QTY-HELD S9(13)V9(6).</summary>
    public decimal QtyHeld { get; set; }

    /// <summary>SC-L-REVAL S9(13)V9(2).</summary>
    public decimal Reval { get; set; }

    /// <summary>SC-L-NOTE X(40).</summary>
    public string Note { get; set; } = "";
}
