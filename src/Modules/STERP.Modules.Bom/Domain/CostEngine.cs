using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>
/// BOMCOST (cobol/online/bom/BOMCOST.cob): costed explosion. COST explodes through BOMEXPL and values every occurrence
/// exactly once (notes and phantoms 0, by-products as a recovery credit, manufactured nodes conversion, services and
/// cost-only lines from their line rate, material from the cost set or the item standard cost, cached per item for the
/// call); GETLINE pages the valued result; BRANCH values one sub-tree (FR-STERP-BOM-011/041, spec 4.6).
/// </summary>
internal sealed class CostEngine(ExplosionEngine explosion, ISqlRunner sql) : IBomCostEngine
{
    private const string RateSql =
        "SELECT TOP (1) r.UnitCost, r.Src FROM (SELECT sc.TotalUnitCost AS UnitCost, 'COSTSET' AS Src, " +
        "CASE WHEN sc.Revision = '' THEN 1 ELSE 2 END AS Pref, sc.Revision AS Rv, '' AS St FROM cst.StandardCost sc " +
        "WHERE sc.CompanyCode = ? AND sc.ItemCode = ? AND sc.CostSetCode = ? UNION ALL SELECT ic.StandardUnitCost, 'ITEMSTD', " +
        "3 + CASE WHEN ic.SiteCode = ? THEN 0 ELSE 2 END + CASE WHEN ic.Revision = '' THEN 0 ELSE 1 END, ic.Revision, ic.SiteCode " +
        "FROM cst.ItemCost ic WHERE ic.CompanyCode = ? AND ic.ItemCode = ? AND ic.StandardUnitCost IS NOT NULL) r " +
        "ORDER BY r.Pref, r.St, r.Rv";

    private const int CacheMax = 300;

    private sealed class Valuation
    {
        public decimal Material;
        public decimal Conversion;
        public decimal Setup;
        public decimal Service;
        public decimal Recovery;
        public int Missing;
        public readonly List<(string Item, decimal Rate, string Source)> Cache = [];
    }

    public async Task<LegacyResult> LinkAsync(BomCostArea area, CancellationToken ct = default)
    {
        // Main-Entry
        area.Program = "BOMCOST";
        area.Result = area.Result with { ReturnCode = 0, MessageCount = 0, ReasonCode = "", DocumentRef = "BOM-021" };
        if (area.WinMax < 1 || area.WinMax > 25) area.WinMax = 25;
        area.WinCnt = 0;
        area.MoreLines = "N";
        if (Pic.Blank(area.Currency)) area.Currency = "GBP";
        switch (area.Function.Trim())
        {
            case "COST":
                await RunCostRollupAsync(area, ct);
                break;
            case "GETLINE":
                PageTheCost(area);
                break;
            case "BRANCH":
                ValueOneBranch(area);
                break;
            default:
                area.Result = area.Result with
                {
                    ReturnCode = 8,
                    ReasonCode = "BOM0801",
                    Message = Pic.Str(200, "BOMCOST does not support the function ", Pic.T(area.Function)),
                };
                break;
        }
        return area.Result;
    }

    private async Task RunCostRollupAsync(BomCostArea a, CancellationToken ct)
    {
        var v = new Valuation();
        await ExplodeForCostingAsync(a, ct);
        if (a.Result.ReturnCode > 8) return;
        await ValueEveryOccurrenceAsync(a, v, ct);
        PublishTotals(a, v);
    }

    private async Task ExplodeForCostingAsync(BomCostArea a, CancellationToken ct)
    {
        var ex = new BomExplosionArea
        {
            Function = "EXPLODE",
            Company = a.Company,
            Site = a.Site,
            User = a.User,
            BusinessDate = a.BusinessDate,
            CalcType = a.Netted == "Y" ? "NET" : "GROSS",
            CalcRef = a.CalcRef,
            ParentItem = a.ParentItem,
            ParentRev = a.ParentRev,
            Purpose = a.Purpose,
            Variant = a.Variant,
            ReqQty = a.ReqQty,
            EffDate = a.EffDate,
            MaxDepth = a.MaxDepth,
            ExpandPhantom = "Y",
            IncludeNotes = "Y",
            IncludeByprod = "Y",
            CostSet = a.CostSet,
            Currency = a.Currency,
            Calculation = a.Calculation,
        };
        await explosion.LinkAsync(ex, ct);
        a.Calculation = ex.Calculation;
        a.Completeness = ex.Completeness;
        a.ErrorPath = ex.ErrorPath;
        a.GrossStarts = ex.GrossStarts;
        a.RunCount = ex.RunCount;
        a.BatchCount = ex.BatchCount;
        a.OccCount = ex.OccCount;
        a.Result = a.Result with { ReturnCode = ex.Result.ReturnCode, ReasonCode = ex.Result.ReasonCode, Message = ex.Result.Message };
    }

    /// <summary>One pass over the occurrence pool; each kind of node is valued in exactly one place.</summary>
    private async Task ValueEveryOccurrenceAsync(BomCostArea a, Valuation v, CancellationToken ct)
    {
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        var total = pool.Count;
        for (var i = 1; i <= total; i++)
        {
            var o = pool.Get(i)!;
            await ValueOneOccurrenceAsync(a, v, o, ct);
            pool.Put(i, o);
        }
    }

    private async Task ValueOneOccurrenceAsync(BomCostArea a, Valuation v, BomOccurrence o, CancellationToken ct)
    {
        var ext = 0m;
        if (o.NodeType == "NOTE")
        {
            o.UnitCost = 0m;
            o.CostSource = "NONE";
        }
        else if (o.NodeType == "BYPROD")
        {
            o.CostElement = "RECY";
            if (Pic.Blank(o.CostSource)) o.CostSource = "NRV";
            ext = (Cd.F(o.ReqQty, 6) * Cd.F(o.UnitCost, 6)).Store(13, 2, rounded: true);
            v.Recovery = Pic.N(v.Recovery + ext, 13, 2);
            ext = 0m;
        }
        else if (o.NodeType == "PHANTOM")
        {
            o.UnitCost = 0m;
            o.CostSource = "PHANTOM";
        }
        else if (o.NodeType == "INTERMED")
        {
            o.CostElement = "LAB";
            o.CostSource = "BOMHEADER";
            o.FixedCost = "Y";
            ext = (Cd.I(o.RunCount) * Cd.F(o.UnitCost, 6) + Cd.F(o.GrossStarts, 6) * Cd.F(o.BranchCost, 2)).Store(13, 2, rounded: true);
            if (o.RunCount > 0 && o.UnitCost > 0)
            {
                v.Setup = (Cd.F(v.Setup, 2) + Cd.I(o.RunCount) * Cd.F(o.UnitCost, 6)).Store(13, 2, rounded: true);
            }
            v.Conversion = Pic.N(v.Conversion + ext, 13, 2);
        }
        else if (o.Supply is "SERVICE" or "COSTONLY")
        {
            o.CostSource = "CSTONLY";
            if (Pic.Blank(o.CostElement)) o.CostElement = "SUBC";
            o.FixedCost = "N";
            if (o.UnitCost <= 0)
            {
                v.Missing = Pic.I(v.Missing + 1, 4);
                o.CostSource = "MISSING";
                o.Warning = "No rate is held for this resource line";
            }
            else
            {
                ext = (Cd.F(o.ReqQty, 6) * Cd.F(o.UnitCost, 6)).Store(13, 2, rounded: true);
                v.Service = Pic.N(v.Service + ext, 13, 2);
            }
        }
        else
        {
            var (rate, source) = await FindMaterialRateAsync(a, v, o.Item, ct);
            o.UnitCost = rate;
            o.CostSource = source;
            o.CostElement = "MAT";
            o.FixedCost = "N";
            if (source == "MISSING")
            {
                v.Missing = Pic.I(v.Missing + 1, 4);
                o.Warning = "No cost held for this item in the stated cost set";
            }
            else
            {
                ext = (Cd.F(o.ReqQty, 6) * Cd.F(rate, 6)).Store(13, 2, rounded: true);
                v.Material = Pic.N(v.Material + ext, 13, 2);
            }
        }
        o.ExtCost = Pic.N(ext, 13, 2);
    }

    private static void PublishTotals(BomCostArea a, Valuation v)
    {
        a.MaterialCost = v.Material;
        a.ConversionCost = v.Conversion;
        a.SetupCost = v.Setup;
        a.ServiceCost = v.Service;
        a.RecoveryCredit = v.Recovery;
        a.TotalCost = (Cd.F(v.Material, 2) + Cd.F(v.Conversion, 2) + Cd.F(v.Service, 2) - Cd.F(v.Recovery, 2)).Store(13, 2);
        a.MissingRates = v.Missing;
        if (a.ReqQty > 0)
        {
            a.UnitDenom = Pic.N(a.ReqQty, 15, 6);
            a.UnitCost = (Cd.F(a.TotalCost, 2) / Cd.F(Pic.N(a.ReqQty, 15, 6), 6)).Store(11, 6, rounded: true, previous: a.UnitCost);
        }
        else
        {
            a.UnitDenom = Pic.N(0m, 15, 6);
            a.UnitCost = Pic.N(0m, 11, 6);
        }
        if (v.Missing > 0)
        {
            a.Completeness = "INCOMPLETE";
            a.Result = a.Result with
            {
                ReturnCode = 4,
                ReasonCode = "BOM0810",
                Message = "A missing rate prevents a complete approved cost",
                MessageCount = Pic.I(a.Result.MessageCount + 1, 2),
            };
            return;
        }
        if (a.Completeness == "COMPLETE")
        {
            a.Result = a.Result with
            {
                ReturnCode = 0,
                Message = Pic.Str(200, "Cost roll-up total ", Pic.E(a.TotalCost, 2), " ", Pic.T(a.Currency)),
            };
        }
    }

    /// <summary>BRANCH: the roll-up value of one sub-tree, for presentation (contains its children once).</summary>
    private static void ValueOneBranch(BomCostArea a)
    {
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        var o = pool.Get(a.BranchSeq);
        if (o is null)
        {
            a.Result = a.Result with { ReturnCode = 16, ReasonCode = "BOM0811", Message = "That branch is not part of the retained calculation" };
            return;
        }
        a.BranchItem = o.Item;
        a.BranchQty = o.ReqQty;
        a.BranchCost = pool.CostSum("BRANCH", a.BranchSeq);
        a.BranchUnit = a.BranchQty > 0
            ? (Cd.F(a.BranchCost, 2) / Cd.F(a.BranchQty, 6)).Store(11, 6, rounded: true, previous: a.BranchUnit)
            : Pic.N(0m, 11, 6);
        a.Result = a.Result with
        {
            Message = Pic.Str(200, "Branch ", Pic.T(a.BranchItem), " rolls up to ", Pic.E(a.BranchCost, 2), " - included once, never added again"),
        };
    }

    private static void PageTheCost(BomCostArea a)
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
            w.Element = o.CostElement;
            w.Source = o.CostSource;
            w.ReqQty = o.ReqQty;
            w.UnitCost = o.UnitCost;
            w.ExtCost = o.ExtCost;
            w.Branch = Pic.N(0m, 13, 2);
            w.Fixed = o.FixedCost;
            w.Warning = Pic.X(o.Warning, 60);
            i++;
        }
        a.FromLine = i;
        if (i <= total) a.MoreLines = "Y";
    }

    /// <summary>The rate cache (CR2701): one reading per item per COST call.</summary>
    private async Task<(decimal Rate, string Source)> FindMaterialRateAsync(BomCostArea a, Valuation v, string item, CancellationToken ct)
    {
        foreach (var c in v.Cache)
        {
            if (c.Item == item) return (c.Rate, c.Source);
        }
        var (rate, source) = await ReadMaterialRateAsync(a, item, ct);
        if (v.Cache.Count < CacheMax) v.Cache.Add((item, rate, source));
        return (rate, source);
    }

    /// <summary>Ranked rate: cost set (item level first), then item standard for this site, then another site.</summary>
    private async Task<(decimal Rate, string Source)> ReadMaterialRateAsync(BomCostArea a, string item, CancellationToken ct)
    {
        try
        {
            var row = await sql.WithReaderAsync(BomSql.Q(RateSql),
                BomSql.Args(Pic.X(a.Company, 10), item, Pic.X(a.CostSet, 20), Pic.X(a.Site, 10), Pic.X(a.Company, 10), item), async r =>
                {
                    if (!await r.ReadAsync(ct)) return ((decimal, string)?)null;
                    return (BomSql.NumVal(BomSql.Text(r, 0, 24)), BomSql.Text(r, 1, 12));
                }, ct);
            if (row is null) return (0m, "MISSING");
            var rate = Pic.N(row.Value.Item1, 11, 6);
            var source = row.Value.Item2;
            if (rate == 0) source = "MISSING";
            return (rate, source);
        }
        catch (SqlioException)
        {
            return (0m, "MISSING");
        }
    }
}
