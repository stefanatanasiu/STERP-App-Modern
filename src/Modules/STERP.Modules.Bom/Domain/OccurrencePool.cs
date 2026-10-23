namespace Sterp.Modules.Bom.Domain;

/// <summary>
/// BOM-OCC-ROW (cobol/copy/BOMOCCUR.cpy): one expanded component occurrence, the unit of currency of every BOM calculation.
/// The same record is reused for an aggregated leaf line, where <see cref="Seq"/> holds the number of contributing
/// occurrences. Text values are carried right-trimmed within their PIC X(n) length; numbers carry the PIC scale.
/// </summary>
public sealed class BomOccurrence
{
    public int Seq { get; set; }
    public int ParentSeq { get; set; }
    public int Depth { get; set; }
    public int LineNum { get; set; }
    /// <summary>BO-PATH X(160).</summary>
    public string Path { get; set; } = "";
    /// <summary>BO-OCC-REF X(40).</summary>
    public string OccRef { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string Item { get; set; } = "";
    public string ItemRev { get; set; } = "";
    /// <summary>BO-DESCRIPTION X(60).</summary>
    public string Description { get; set; } = "";
    public int BomId { get; set; }
    public string BomRev { get; set; } = "";
    /// <summary>BO-NODE-TYPE: LEAF, INTERMED, PHANTOM, BYPROD, NOTE.</summary>
    public string NodeType { get; set; } = "";
    public string Supply { get; set; } = "";
    public string StopReason { get; set; } = "";
    public string UsageBasis { get; set; } = "";
    public string Uom { get; set; } = "";
    public string StockUom { get; set; } = "";
    public string AltGroup { get; set; } = "";
    public string Optional { get; set; } = "";
    public string Selected { get; set; } = "";
    /// <summary>BO-USAGE-QTY S9(13)V9(6).</summary>
    public decimal UsageQty { get; set; }
    /// <summary>BO-CONV-FACT S9(9)V9(9).</summary>
    public decimal ConvFact { get; set; }
    /// <summary>BO-SCRAP-PCT S9(5)V9(6).</summary>
    public decimal ScrapPct { get; set; }
    /// <summary>BO-UNROUNDED S9(15)V9(9).</summary>
    public decimal Unrounded { get; set; }
    public decimal YieldContrib { get; set; }
    public decimal ScrapContrib { get; set; }
    /// <summary>BO-ROUND-INC S9(13)V9(6).</summary>
    public decimal RoundInc { get; set; }
    /// <summary>BO-REQ-QTY S9(15)V9(6).</summary>
    public decimal ReqQty { get; set; }
    public decimal GoodQty { get; set; }
    public decimal GrossStarts { get; set; }
    public decimal NominalBasis { get; set; }
    public decimal Yield { get; set; }
    public decimal Quantum { get; set; }
    public decimal BatchSize { get; set; }
    public int RunCount { get; set; }
    public int BatchCount { get; set; }
    public decimal StockUsed { get; set; }
    public decimal ReservedUsed { get; set; }
    public decimal InboundUsed { get; set; }
    public decimal Shortage { get; set; }
    public decimal PackQty { get; set; }
    /// <summary>BO-PACK-MULT S9(13)V9(6).</summary>
    public decimal PackMult { get; set; }
    public decimal Excess { get; set; }
    /// <summary>BO-UNIT-COST S9(11)V9(6).</summary>
    public decimal UnitCost { get; set; }
    /// <summary>BO-EXT-COST S9(13)V9(2).</summary>
    public decimal ExtCost { get; set; }
    /// <summary>BO-BRANCH-COST S9(13)V9(2).</summary>
    public decimal BranchCost { get; set; }
    public string CostElement { get; set; } = "";
    public string CostSource { get; set; } = "";
    public string FixedCost { get; set; } = "";
    public int NeedDate { get; set; }
    public string Operation { get; set; } = "";
    /// <summary>BO-WARNING X(100).</summary>
    public string Warning { get; set; } = "";

    /// <summary>INITIALIZE of the record: spaces and zeros.</summary>
    public static BomOccurrence Initialized() => new();

    public BomOccurrence Clone() => (BomOccurrence)MemberwiseClone();
}

/// <summary>
/// BOMWORK (cobol/online/bom/BOMWORK.cob): the work pool of one calculation - 2,000 occurrence slots and 500 aggregated
/// leaf slots, copy-in/copy-out like the legacy fixed slots. Owned by one calculation (BC-BOM-01, CS-BOM-01).
/// </summary>
public sealed class OccurrencePool
{
    public const int OccMax = 2000;
    public const int LeafMax = 500;

    private readonly List<BomOccurrence> _occ = [];
    private readonly List<BomOccurrence> _leaf = [];

    /// <summary>Ws-Deepest: the deepest BO-DEPTH added.</summary>
    public int Deepest { get; private set; }

    public int Count => _occ.Count;

    public int LeafCount => _leaf.Count;

    /// <summary>CLEAR.</summary>
    public void Clear()
    {
        _occ.Clear();
        _leaf.Clear();
        Deepest = 0;
    }

    /// <summary>ADD: appends a copy and returns its sequence, or 0 when the pool is FULL.</summary>
    public int Add(BomOccurrence row)
    {
        if (_occ.Count >= OccMax) return 0;
        var copy = row.Clone();
        copy.Seq = _occ.Count + 1;
        row.Seq = copy.Seq;
        _occ.Add(copy);
        if (copy.Depth > Deepest) Deepest = copy.Depth;
        return copy.Seq;
    }

    /// <summary>GET: a copy of occurrence <paramref name="index"/> (1-based), or null (NOTFOUND).</summary>
    public BomOccurrence? Get(int index) => index < 1 || index > _occ.Count ? null : _occ[index - 1].Clone();

    /// <summary>PUT: overwrites occurrence <paramref name="index"/> (BO-SEQ forced to the index).</summary>
    public bool Put(int index, BomOccurrence row)
    {
        if (index < 1 || index > _occ.Count) return false;
        var copy = row.Clone();
        copy.Seq = index;
        _occ[index - 1] = copy;
        return true;
    }

    /// <summary>GETLEAF.</summary>
    public BomOccurrence? GetLeaf(int index) => index < 1 || index > _leaf.Count ? null : _leaf[index - 1].Clone();

    /// <summary>PUTLEAF.</summary>
    public bool PutLeaf(int index, BomOccurrence row)
    {
        if (index < 1 || index > _leaf.Count) return false;
        _leaf[index - 1] = row.Clone();
        return true;
    }

    /// <summary>
    /// AGGREG: flatten the LEAF occurrences, merging the same item, revision, stock unit and supply (CR2259); BO-SEQ of a
    /// leaf line counts the contributing occurrences and the first path is kept. Returns the leaf count.
    /// </summary>
    public int Aggregate()
    {
        _leaf.Clear();
        foreach (var slot in _occ)
        {
            if (slot.NodeType != "LEAF") continue;
            var a = slot.Clone();
            var hit = _leaf.FindIndex(b => b.Item == a.Item && b.ItemRev == a.ItemRev && b.StockUom == a.StockUom && b.Supply == a.Supply);
            if (hit >= 0)
            {
                var b = _leaf[hit];
                b.Seq += 1;
                b.ReqQty = Pic.N(b.ReqQty + a.ReqQty, 15, 6);
                b.Unrounded = Pic.N(b.Unrounded + a.Unrounded, 15, 9);
                b.RoundInc = Pic.N(b.RoundInc + a.RoundInc, 13, 6);
                b.ScrapContrib = Pic.N(b.ScrapContrib + a.ScrapContrib, 15, 9);
                b.YieldContrib = Pic.N(b.YieldContrib + a.YieldContrib, 15, 9);
                b.StockUsed = Pic.N(b.StockUsed + a.StockUsed, 15, 6);
                b.InboundUsed = Pic.N(b.InboundUsed + a.InboundUsed, 15, 6);
                b.Shortage = Pic.N(b.Shortage + a.Shortage, 15, 6);
                b.PackQty = Pic.N(b.PackQty + a.PackQty, 15, 6);
                b.Excess = Pic.N(b.Excess + a.Excess, 15, 6);
                b.ExtCost = Pic.N(b.ExtCost + a.ExtCost, 13, 2);
                if (a.Depth < b.Depth) b.Depth = a.Depth;
                continue;
            }
            if (_leaf.Count >= LeafMax) continue;
            a.Seq = 1;
            a.ParentSeq = 0;
            _leaf.Add(a);
        }
        return _leaf.Count;
    }

    /// <summary>TOTAL: sum of BO-REQ-QTY of one item over the pool (filter blank/ALL or a node type) and the hit count.</summary>
    public (decimal Total, int Count) Total(string item, string filter)
    {
        var total = 0m;
        var n = 0;
        foreach (var o in _occ)
        {
            if (o.Item != item) continue;
            if (!(Pic.Blank(filter) || filter == "ALL" || filter == o.NodeType)) continue;
            total = Pic.N(total + o.ReqQty, 15, 6);
            n++;
        }
        return (total, n);
    }

    /// <summary>COSTSUM: sum of BO-EXT-COST filtered by LEAF / INTERMED / BRANCH (sub-tree of <paramref name="branchRoot"/>) / ALL.</summary>
    public decimal CostSum(string filter, int branchRoot)
    {
        var money = 0m;
        for (var i = 1; i <= _occ.Count; i++)
        {
            var a = _occ[i - 1];
            var take = filter switch
            {
                "LEAF" => a.NodeType == "LEAF",
                "INTERMED" => a.NodeType == "INTERMED",
                "BRANCH" => InBranch(i, branchRoot),
                _ => true,
            };
            if (take) money = Pic.N(money + a.ExtCost, 13, 2);
        }
        return money;
    }

    private bool InBranch(int index, int root)
    {
        var walk = index;
        var guard = 0;
        while (!(walk < 1 || guard > OccMax))
        {
            if (walk == root) return true;
            walk = walk <= _occ.Count ? _occ[walk - 1].ParentSeq : 0;
            guard++;
        }
        return false;
    }

    /// <summary>Read-only view of the occurrences in generation order.</summary>
    public IReadOnlyList<BomOccurrence> Occurrences => _occ;

    /// <summary>Read-only view of the aggregated leaf lines.</summary>
    public IReadOnlyList<BomOccurrence> Leaves => _leaf;
}
