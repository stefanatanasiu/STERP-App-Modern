using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Bom.Application;

/// <summary>One BL-LINES entry (cobol/copy/BOMLEAFCA.cpy).</summary>
internal sealed class LeafLine
{
    public int Seq;
    public int Depth;
    public string Indent = "";
    /// <summary>BL-L-PATH X(80).</summary>
    public string Path = "";
    public string Item = "";
    public string Rev = "";
    /// <summary>BL-L-DESC X(40).</summary>
    public string Desc = "";
    public string Uom = "";
    public string NodeType = "";
    public string Supply = "";
    public string Stop = "";
    public string Basis = "";
    public decimal Usage;
    public decimal Unrounded;
    public decimal RoundInc;
    public decimal ReqQty;
    public decimal GoodQty;
    public decimal Gross;
    public decimal StockUsed;
    public decimal Shortage;
    public decimal PackQty;
    public decimal Excess;
    public decimal ExtCost;
    public int Contrib;
    /// <summary>BL-L-WARNING X(60).</summary>
    public string Warning = "";
}

/// <summary>BOMLEAFCA (cobol/copy/BOMLEAFCA.cpy).</summary>
internal sealed class LeafArea
{
    public string Function = "";
    public string Company = "";
    public string Site = "";
    public string User = "";
    public int BusinessDate;
    public string CorrelationId = "";
    public int ReturnCode;
    public string Reason = "";
    public string Message = "";
    public int MessageCount;

    public string ParentItem = "";
    public string ParentRev = "";
    public string Purpose = "";
    public string JobRef = "";
    public string Variant = "";
    public decimal ReqQty;
    public int EffDate;
    public int MaxDepth;
    public string Reexplode = "";
    public string CalcRef = "";
    public int OccCount;
    public int LeafCount;
    public int IntermedCount;
    public int DepthReached;
    public string Completeness = "";
    /// <summary>BL-STOP-SUMMARY X(120).</summary>
    public string StopSummary = "";
    public string KeyItem = "";
    public decimal KeyTotal;
    public string KeyUom = "";
    public int FromLine;
    public int WinMax;
    public int WinCnt;
    public string MoreLines = "";
    public readonly LeafLine[] Lines = Enumerable.Range(0, 25).Select(_ => new LeafLine()).ToArray();

    /// <summary>The retained calculation the page calls read (the BOMWORK pool).</summary>
    public BomCalculation? Calculation;

    /// <summary>The CobolResult of the call (the facade never exposes CA-REQ-REF).</summary>
    public LegacyResult Outcome => new(ReturnCode, Reason, Message, MessageCount);
}

/// <summary>
/// BOMLEAF (cobol/online/bom/BOMLEAF.cob): three readings of one gross explosion - single level, indented and flattened
/// (aggregated leaves) - with the stop-reason summary. BL-REEXPLODE = "N" pages the result already held.
/// </summary>
internal sealed class LeafViews(ExplosionEngine engine)
{
    public async Task CallAsync(LeafArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        switch (a.Function)
        {
            case "SINGLE":
                await EnsureCalculationAsync(a, ct);
                Page(a, false, o => o.Depth == 1);
                break;
            case "INDENT":
                await EnsureCalculationAsync(a, ct);
                Page(a, false, _ => true);
                break;
            case "FLATTEN":
                await EnsureCalculationAsync(a, ct);
                Page(a, true, _ => true);
                break;
            case "ITEMQTY":
                await EnsureCalculationAsync(a, ct);
                TotalOneItem(a);
                break;
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM0601";
                a.Message = Pic.Str(200, "BOMLEAF does not support the function ", Pic.T(a.Function));
                break;
        }
    }

    private async Task EnsureCalculationAsync(LeafArea a, CancellationToken ct)
    {
        if (a.Reexplode == "N")
        {
            CountThePool(a);
            return;
        }
        var ex = new BomExplosionArea
        {
            Function = "EXPLODE",
            Company = a.Company,
            Site = a.Site,
            User = a.User,
            BusinessDate = a.BusinessDate,
            CalcType = "GROSS",
            CalcRef = a.CalcRef,
            ParentItem = a.ParentItem,
            ParentRev = a.ParentRev,
            Purpose = a.Purpose,
            JobRef = a.JobRef,
            Variant = a.Variant,
            ReqQty = a.ReqQty,
            EffDate = a.EffDate,
            MaxDepth = a.MaxDepth,
            ExpandPhantom = "Y",
            IncludeNotes = "Y",
            IncludeByprod = "Y",
            Calculation = a.Calculation,
        };
        await engine.LinkAsync(ex, ct);
        a.Calculation = ex.Calculation;
        a.Completeness = ex.Completeness;
        a.DepthReached = ex.DepthReached;
        a.ReturnCode = ex.Result.ReturnCode;
        a.Reason = ex.Result.ReasonCode;
        a.Message = ex.Result.Message;
        CountThePool(a);
    }

    private static OccurrencePool Pool(LeafArea a) => a.Calculation?.Pool ?? new OccurrencePool();

    private static void CountThePool(LeafArea a)
    {
        var pool = Pool(a);
        a.OccCount = pool.Count;
        a.DepthReached = pool.Deepest;
        a.LeafCount = pool.LeafCount;
        SummariseStopReasons(a, pool);
    }

    /// <summary>Why expansion stopped, counted by reason (BOM-014).</summary>
    private static void SummariseStopReasons(LeafArea a, OccurrencePool pool)
    {
        int purch = 0, depth = 0, nobom = 0, stock = 0, interm = 0;
        foreach (var o in pool.Occurrences)
        {
            if (o.NodeType == "INTERMED") interm++;
            switch (o.StopReason)
            {
                case "PURCHASED": purch++; break;
                case "DEPTHLIMIT": depth++; break;
                case "NOCHILDBOM": nobom++; break;
                case "USESTOCK":
                case "FROMSTOCK": stock++; break;
            }
        }
        a.IntermedCount = interm;
        a.StopSummary = Pic.Str(120, "Stopped: purchased ", Pic.Z(Pic.I(purch, 4)), ", use stock ", Pic.Z(Pic.I(stock, 4)),
            ", depth limit ", Pic.Z(Pic.I(depth, 4)), ", no child bill ", Pic.Z(Pic.I(nobom, 4)));
    }

    private static void Page(LeafArea a, bool leaves, Func<BomOccurrence, bool> keep)
    {
        var pool = Pool(a);
        var total = leaves ? pool.LeafCount : pool.Count;
        if (a.FromLine < 1) a.FromLine = 1;
        var i = a.FromLine;
        while (!(i > total || a.WinCnt >= a.WinMax))
        {
            var o = (leaves ? pool.GetLeaf(i) : pool.Get(i))!;
            if (keep(o))
            {
                a.WinCnt++;
                var w = a.Lines[a.WinCnt - 1];
                Fill(w, o);
                if (leaves)
                {
                    w.Contrib = o.Seq;
                    w.Depth = 0;
                }
            }
            i++;
        }
        a.FromLine = i;
        if (i <= total) a.MoreLines = "Y";
    }

    private static void Fill(LeafLine w, BomOccurrence o)
    {
        w.Seq = o.Seq;
        w.Depth = o.Depth;
        w.Indent = o.Depth <= 0 ? "" : new string('.', Math.Min(o.Depth * 2, 40));
        w.Path = Pic.X(o.Path, 80);
        w.Item = o.Item;
        w.Rev = o.ItemRev;
        w.Desc = Pic.X(o.Description, 40);
        w.Uom = o.StockUom;
        w.NodeType = o.NodeType;
        w.Supply = o.Supply;
        w.Stop = o.StopReason;
        w.Basis = o.UsageBasis;
        w.Usage = o.UsageQty;
        w.Unrounded = o.Unrounded;
        w.RoundInc = o.RoundInc;
        w.ReqQty = o.ReqQty;
        w.GoodQty = o.GoodQty;
        w.Gross = o.GrossStarts;
        w.StockUsed = o.StockUsed;
        w.Shortage = o.Shortage;
        w.PackQty = o.PackQty;
        w.Excess = o.Excess;
        w.ExtCost = o.ExtCost;
        w.Contrib = 1;
        w.Warning = Pic.X(o.Warning, 60);
    }

    private static void TotalOneItem(LeafArea a)
    {
        a.KeyItem = Pic.X(Pic.Upper(a.KeyItem), 40);
        var pool = Pool(a);
        var total = 0m;
        var n = 0;
        foreach (var o in pool.Occurrences)
        {
            if (o.Item != a.KeyItem) continue;
            total = Pic.N(total + o.ReqQty, 15, 6);
            n++;
        }
        a.KeyTotal = total;
        if (n == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0605";
            a.Message = Pic.Str(200, "No occurrence of ", Pic.T(a.KeyItem), " in this calculation");
        }
    }
}
