using Sterp.Modules.Bom.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>One BN-SUPPLY entry (declared TRIAL supply).</summary>
internal sealed class TrialSupply
{
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    public decimal FreeQty { get; set; }
    public decimal Inbound { get; set; }
    public decimal Used { get; set; }
}

/// <summary>One BN-LINES entry.</summary>
internal sealed class NetLine
{
    public int Seq;
    public int Depth;
    public string Path = "";
    public string Item = "";
    public string Rev = "";
    public string Desc = "";
    public string Uom = "";
    public string NodeType = "";
    public string Supply = "";
    public string Stop = "";
    public decimal ReqQty;
    public decimal GoodQty;
    public decimal Gross;
    public int Batches;
    public decimal StockUsed;
    public decimal Inbound;
    public decimal ToMake;
    public decimal Shortage;
    public decimal PackQty;
    public decimal Excess;
    public string Warning = "";
}

/// <summary>BOMNETCA (cobol/copy/BOMNETCA.cpy).</summary>
internal sealed class NetArea
{
    public string Function = "";
    public string Company = "";
    public string Site = "";
    public string User = "";
    public int BusinessDate;
    public int ReturnCode;
    public string Reason = "";
    public string Message = "";
    public int MessageCount;

    public string ParentItem = "";
    public string ParentRev = "";
    public string Purpose = "";
    public string Variant = "";
    public decimal ReqQty;
    public int EffDate;
    public int MaxDepth;
    public string CalcRef = "";
    public string StockSource = "";
    public string UseReserved = "";
    public string UseInbound = "";
    public readonly List<TrialSupply> Supply = [];

    public decimal GrossStarts;
    public int RunCount;
    public int BatchCount;
    public int OccCount;
    public int LeafCount;
    public int ShortageCount;
    public string ShortageItem = "";
    public decimal ShortageQty;
    public decimal MaxBuildable;
    public int Trials;
    public string BuildableNow = "";
    public string Completeness = "";
    public string ErrorPath = "";
    public int FromLine;
    public int WinMax;
    public int WinCnt;
    public string MoreLines = "";
    public readonly NetLine[] Lines = Enumerable.Range(0, 25).Select(_ => new NetLine()).ToArray();

    public BomCalculation? Calculation;
    public SupplyPool? Pool;

    public LegacyResult Outcome => new(ReturnCode, Reason, Message, MessageCount);
}

/// <summary>
/// BOMNET (cobol/online/bom/BOMNET.cob): netted plan against eligible supply (FR-STERP-BOM-012, spec 4.7). NET clears the
/// supply pool, takes free stock of the finished parent first and explodes only the remainder in NET mode so every stock
/// unit is used once; GETLINE pages the plan. MAXBUILD is deferred (SME-625).
/// </summary>
internal sealed class NetPlanner(ExplosionEngine explosion, ISqlRunner sql)
{
    private decimal _trialQty;
    private string _fits = "N";

    public async Task CallAsync(NetArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        switch (a.Function)
        {
            case "NET":
                await PlanOneQuantityAsync(a, ct);
                break;
            case "GETLINE":
                PageThePlan(a);
                break;
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM0701";
                a.Message = Pic.Str(200, "BOMNET does not support the function ", Pic.T(a.Function));
                break;
        }
    }

    private void LoadEligibleSupply(NetArea a)
    {
        a.Pool ??= new SupplyPool(sql);
        a.Pool.Clear();
        if (Pic.Blank(a.StockSource)) a.StockSource = "LIVE";
        if (a.StockSource != "TRIAL") return;
        foreach (var s in a.Supply)
        {
            a.Pool.Set(new SupplyCall
            {
                Company = a.Company,
                Site = a.Site,
                Source = "TRIAL",
                Item = s.Item,
                Rev = s.Rev,
                Free = s.FreeQty,
                Inbound = s.Inbound,
            });
            s.Used = 0m;
        }
    }

    private async Task TakeFinishedStockAsync(NetArea a, CancellationToken ct)
    {
        var c = new SupplyCall
        {
            Company = a.Company,
            Site = a.Site,
            Source = a.StockSource,
            Item = Pic.X(a.ParentItem, 40),
            Rev = Pic.X(a.ParentRev, 4),
            Want = _trialQty,
            UseInbound = a.UseInbound,
        };
        await a.Pool!.TakeAsync(c, ct);
        _trialQty = (Cd.F(_trialQty, 6) - Cd.F(c.Taken, 6)).Store(15, 6);
    }

    private async Task ExplodeRemainingAsync(NetArea a, CancellationToken ct)
    {
        var ex = new BomExplosionArea
        {
            Function = "EXPLODE",
            Company = a.Company,
            Site = a.Site,
            User = a.User,
            BusinessDate = a.BusinessDate,
            CalcType = "NET",
            CalcRef = a.CalcRef,
            ParentItem = a.ParentItem,
            ParentRev = a.ParentRev,
            Purpose = a.Purpose,
            Variant = a.Variant,
            ReqQty = _trialQty,
            EffDate = a.EffDate,
            MaxDepth = a.MaxDepth,
            ExpandPhantom = "Y",
            IncludeNotes = "Y",
            StockSource = a.StockSource,
            UseInbound = a.UseInbound,
            Calculation = a.Calculation,
        };
        await explosion.LinkAsync(ex, a.Pool, ct);
        a.Calculation = ex.Calculation;
        a.Completeness = ex.Completeness;
        a.ErrorPath = ex.ErrorPath;
        a.GrossStarts = ex.GrossStarts;
        a.RunCount = ex.RunCount;
        a.BatchCount = ex.BatchCount;
        a.OccCount = ex.OccCount;
        a.LeafCount = ex.LeafCount;
    }

    /// <summary>Shortage first: count short aggregated leaves and remember the first one.</summary>
    private void MeasureShortages(NetArea a)
    {
        var shortages = 0;
        a.ShortageItem = "";
        a.ShortageQty = 0m;
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        for (var i = 1; i <= pool.LeafCount; i++)
        {
            var o = pool.GetLeaf(i)!;
            if (o.Shortage <= 0) continue;
            shortages++;
            if (Pic.Blank(a.ShortageItem))
            {
                a.ShortageItem = o.Item;
                a.ShortageQty = o.Shortage;
            }
        }
        a.ShortageCount = Pic.I(shortages, 4);
        _fits = shortages == 0 ? "Y" : "N";
    }

    private static void RecordSupplyUsed(NetArea a)
    {
        foreach (var s in a.Supply)
        {
            var c = new SupplyCall { Item = s.Item, Rev = s.Rev };
            a.Pool!.Available(c);
            if (c.Status == "OK") s.Used = (Cd.F(s.FreeQty, 6) - Cd.F(c.Free, 6)).Store(15, 6);
        }
    }

    private async Task PlanOneQuantityAsync(NetArea a, CancellationToken ct)
    {
        LoadEligibleSupply(a);
        a.Pool!.Snapshot();
        _trialQty = a.ReqQty;
        await TakeFinishedStockAsync(a, ct);
        await ExplodeRemainingAsync(a, ct);
        MeasureShortages(a);
        RecordSupplyUsed(a);
        a.BuildableNow = _fits;
        a.Trials = 1;
        a.MaxBuildable = 0m;
        PublishPlanMessage(a);
    }

    private static void PublishPlanMessage(NetArea a)
    {
        if (a.Completeness == "ERROR")
        {
            a.ReturnCode = 8;
            a.Reason = "BOM0710";
            a.Message = Pic.Str(200, "The netted plan could not be completed: ", Pic.T(a.ErrorPath));
        }
        else if (a.ShortageCount > 0)
        {
            a.ReturnCode = 4;
            a.Reason = "BOM0711";
            a.Message = Pic.Str(200, "Short of ", Pic.T(a.ShortageItem), " by ", Pic.E(a.ShortageQty, 6), " - the plan is not buildable now");
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
        }
        else
        {
            a.ReturnCode = 0;
            a.Message = "Netted plan complete with no shortage";
        }
    }

    private static void PageThePlan(NetArea a)
    {
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        var total = pool.Count;
        a.OccCount = total;
        if (a.FromLine < 1) a.FromLine = 1;
        var i = a.FromLine;
        while (!(i > total || a.WinCnt >= a.WinMax))
        {
            var o = pool.Get(i)!;
            a.WinCnt++;
            var w = a.Lines[a.WinCnt - 1];
            w.Seq = o.Seq;
            w.Depth = o.Depth;
            w.Path = Pic.X(o.Path, 80);
            w.Item = o.Item;
            w.Rev = o.ItemRev;
            w.Desc = Pic.X(o.Description, 40);
            w.Uom = o.StockUom;
            w.NodeType = o.NodeType;
            w.Supply = o.Supply;
            w.Stop = o.StopReason;
            w.ReqQty = o.ReqQty;
            w.GoodQty = o.GoodQty;
            w.Gross = o.GrossStarts;
            w.Batches = o.BatchCount;
            w.StockUsed = o.StockUsed;
            w.Inbound = o.InboundUsed;
            w.ToMake = o.GoodQty;
            w.Shortage = o.Shortage;
            w.PackQty = o.PackQty;
            w.Excess = o.Excess;
            w.Warning = Pic.X(o.Warning, 60);
            i++;
        }
        a.FromLine = i;
        if (i <= total) a.MoreLines = "Y";
    }
}
