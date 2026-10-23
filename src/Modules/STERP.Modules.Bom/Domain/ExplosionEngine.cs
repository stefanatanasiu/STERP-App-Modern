using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>
/// BOMEXPL (cobol/online/bom/BOMEXPL.cob, IS RECURSIVE): nested explosion of a bill of materials. The run context, abort
/// flag, completeness, warnings, options, ancestry and the result pool are the calculation's shared state
/// (<see cref="Run"/>, the legacy WORKING-STORAGE); each node keeps its bill, counters and child demands in
/// <see cref="NodeState"/> (the legacy LOCAL-STORAGE) and recurses for every manufactured or phantom child (CS-BOM-07).
/// Every statement is a SELECT on the caller's unit of work.
/// </summary>
internal sealed class ExplosionEngine(BillReader reader, BillSelector selector, ISqlRunner sql) : IBomExplosionEngine
{
    private const string ByProductSql =
        "SELECT p.LineNum, p.ByProductItemCode, p.RecoveryQty, p.UomCode, p.ConversionFactor, p.ValuationBasis, ISNULL(p.RecoveryValue,0), " +
        "ISNULL(p.DispositionRule,''), ISNULL(p.ApprovedBy,'') FROM bom.BomByProduct p WHERE p.BomHeaderId = ? ORDER BY p.LineNum";

    /// <summary>One calculation context (Ws-Run, Ws-Ancestry, Ws-Shared-Work).</summary>
    private sealed class Run
    {
        public string Company = "";
        public string Site = "";
        public string User = "";
        public int EffDate;
        public string Purpose = "";
        public string Variant = "";
        public string JobRef = "";
        public string CalcType = "";
        public int MaxDepth = 25;
        public string Consolidate = "N";
        public string ExpandPhantom = "Y";
        public string IncludeNotes = "Y";
        public string IncludeByprod = "Y";
        public string NetMode = "N";
        public string UseInbound = "N";
        public string StockSource = "";
        public string Completeness = "COMPLETE";
        public string Reason = "";
        public string Message = "";
        public string ErrorPath = "";
        public string CyclePath = "";
        public int WarningCnt;
        public bool Abort;
        public readonly List<BomOptionChoice> Options = [];
        public readonly string[] AncItem = Enumerable.Repeat("", 31).ToArray();
        public readonly string[] AncRev = Enumerable.Repeat("", 31).ToArray();
        public readonly int[] AncBomId = new int[31];
        public int DepthReached;
        public OccurrencePool Pool = new();
        public SupplyPool Supply = null!;
        public BillRead RootItem = new();
        public CancellationToken Ct;

        public void Fail()
        {
            Completeness = "ERROR";
            Abort = true;
        }

        public void Warn() => WarningCnt = Pic.I(WarningCnt + 1, 4);
    }

    /// <summary>The NODE call area fields the recursion passes down (CX-...).</summary>
    private sealed record NodeArgs(string ParentItem, string ParentRev, int BomId, string BomRev, decimal ReqQty, int Seq, int Depth,
        string Path, string Phantom, int SplitCnt, decimal[] SplitQty);

    private sealed class Demand
    {
        public int Line;
        public int Seq;
        public string Item = "";
        public string Rev = "";
        public decimal Qty;
        public string Phantom = "N";
        public string Expand = "Y";
    }

    /// <summary>LOCAL-STORAGE of one NODE invocation.</summary>
    private sealed class NodeState
    {
        public int Depth;
        public int MySeq;
        public int ChildSeq;
        public decimal GoodQty;
        public decimal Gross;
        public int Runs;
        public int Batches;
        public decimal Basis;
        public string Path = "";
        public string IsPhantom = "N";
        public string Skip = "N";
        public string Expand = "N";
        public string NodeType = "";
        public string Stop = "";
        public decimal Unrounded;
        public decimal Required;
        public decimal RoundInc;
        public decimal Taken;
        public decimal ToMake;
        public int GroupHit;
        public int ChosenLine;
        public bool Cycle;
        public bool BillOk;
        public decimal YieldContrib;
        public decimal ScrapContrib;
        public decimal PackQty;
        public decimal Excess;
        public string Warning = "";
        public BillRead Bill = new();
        public readonly List<Demand> Demands = [];
        public SelectArea? Selected;
        public NodeArgs Args = null!;
        // node outputs (CX-GROSS-STARTS, CX-RUN-COUNT, CX-BATCH-COUNT)
        public decimal OutGross;
        public int OutRuns;
        public int OutBatches;
    }

    public Task<LegacyResult> LinkAsync(BomExplosionArea area, CancellationToken ct = default) => LinkAsync(area, null, ct);

    /// <summary>CALL BOMEXPL; <paramref name="supply"/> is the BOMSUPPL pool of a netted caller (BOMNET), else a new one.</summary>
    public async Task<LegacyResult> LinkAsync(BomExplosionArea area, SupplyPool? supply, CancellationToken ct)
    {
        switch (area.Function.Trim())
        {
            case "EXPLODE":
                await RunExplosionAsync(area, supply ?? new SupplyPool(sql), ct);
                break;
            case "GETLINE":
                PageTheResult(area);
                break;
            case "LEAFQTY":
                TotalOneItem(area);
                break;
            default:
                area.Program = "BOMEXPL";
                area.Result = new LegacyResult(8, "BOM0501", Pic.Str(200, "BOMEXPL does not support the function ", Pic.T(area.Function)),
                    area.Result.MessageCount, area.Result.DocumentRef);
                break;
        }
        return area.Result;
    }

    // ================================================================ EXPLODE
    private async Task RunExplosionAsync(BomExplosionArea a, SupplyPool supply, CancellationToken ct)
    {
        var run = new Run { Supply = supply, Ct = ct };
        InitialiseRun(a, run);
        if (run.Abort)
        {
            // the legacy pool is not cleared on this path: the area keeps the result it already had
            a.Calculation ??= new BomCalculation(new OccurrencePool());
            PublishSummary(a, run);
            return;
        }
        a.Calculation = new BomCalculation(run.Pool);
        await ResolveRootItemAsync(a, run);
        if (run.Abort)
        {
            PublishSummary(a, run);
            return;
        }
        await SelectRootBillAsync(a, run);
        if (run.Abort)
        {
            PublishSummary(a, run);
            return;
        }
        var rootSeq = SeedRootOccurrence(a, run);
        if (!run.Abort) await CallRootNodeAsync(a, run, rootSeq);
        AggregateAndPublish(a, run);
    }

    private static void InitialiseRun(BomExplosionArea a, Run run)
    {
        a.Program = "BOMEXPL";
        a.Result = new LegacyResult(0, "", "", 0, "BOM-012");
        run.Abort = false;
        run.Completeness = "COMPLETE";
        run.Company = Pic.X(a.Company, 10);
        run.Site = Pic.X(a.Site, 10);
        run.User = Pic.X(a.User, 20);
        a.ParentItem = Pic.X(Pic.Upper(a.ParentItem), 40);
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        run.EffDate = a.EffDate;
        if (Pic.Blank(a.Purpose)) a.Purpose = "STANDARD";
        run.Purpose = Pic.X(a.Purpose, 10);
        run.Variant = Pic.X(a.Variant, 20);
        run.JobRef = Pic.X(a.JobRef, 20);
        if (Pic.Blank(a.CalcType)) a.CalcType = "GROSS";
        run.CalcType = Pic.X(a.CalcType, 10);
        run.NetMode = run.CalcType == "NET" ? "Y" : "N";
        if (a.MaxDepth < 1 || a.MaxDepth > 25) a.MaxDepth = 25;
        run.MaxDepth = a.MaxDepth;
        run.Consolidate = Pic.X(a.Consolidate, 1);
        if (Pic.Blank(a.ExpandPhantom)) a.ExpandPhantom = "Y";
        run.ExpandPhantom = Pic.X(a.ExpandPhantom, 1);
        run.IncludeNotes = Pic.X(a.IncludeNotes, 1);
        run.IncludeByprod = Pic.X(a.IncludeByprod, 1);
        if (Pic.Blank(a.StockSource)) a.StockSource = "LIVE";
        run.StockSource = Pic.X(a.StockSource, 8);
        run.UseInbound = Pic.X(a.UseInbound, 1);
        run.Options.Clear();
        if (a.OptionCount > 0 && a.OptionCount <= 10)
        {
            for (var i = 0; i < a.OptionCount; i++)
            {
                run.Options.Add(new BomOptionChoice(Pic.X(a.Options[i].Group, 10), Pic.X(a.Options[i].Choice, 40)));
            }
        }
        if (Pic.Blank(a.ParentItem))
        {
            run.Reason = "BOM0502";
            run.Message = "A parent item is required";
            run.Fail();
            return;
        }
        if (a.ReqQty < 0)
        {
            run.Reason = "BOM0503";
            run.Message = "Required good output cannot be negative";
            run.Fail();
        }
    }

    /// <summary>The root parent must exist in the item master before anything is calculated against it.</summary>
    private async Task ResolveRootItemAsync(BomExplosionArea a, Run run)
    {
        run.RootItem = await reader.ItemAsync(run.Company, run.Site, a.ParentItem, run.Ct);
        switch (run.RootItem.Status)
        {
            case "OK":
                break;
            case "SQLFAIL":
                run.Reason = "SQL0001";
                run.Message = run.RootItem.Message;
                run.Fail();
                break;
            default:
                run.Reason = "BOM0504";
                run.Message = run.RootItem.Message;
                run.ErrorPath = Pic.X(a.ParentItem, 200);
                run.Fail();
                break;
        }
    }

    private async Task SelectRootBillAsync(BomExplosionArea a, Run run)
    {
        var s = new SelectArea
        {
            Function = "SELECT",
            Company = run.Company,
            Site = run.Site,
            User = run.User,
            BusinessDate = run.EffDate,
            ParentItem = a.ParentItem,
            ParentRev = Pic.X(a.ParentRev, 4),
            Purpose = run.Purpose,
            JobRef = run.JobRef,
            Variant = run.Variant,
            EffDate = run.EffDate,
        };
        await selector.CallAsync(s, run.Ct);
        if (s.ReturnCode > 4)
        {
            run.Reason = s.Reason;
            run.Message = s.Message;
            run.ErrorPath = Pic.X(a.ParentItem, 200);
            run.Fail();
            return;
        }
        a.BomId = s.BomId;
        a.BomRef = s.BomRef;
        a.BomRev = s.BomRev;
        a.NominalBasis = s.NominalBasis;
        a.Yield = s.Yield;
        a.Quantum = s.Quantum;
        a.BatchSize = s.BatchSize;
        if (s.Consolidate == "Y") run.Consolidate = "Y";
    }

    /// <summary>The root occurrence is the calculation's own demand (depth 0, INTERMED, MAKE, ROOT).</summary>
    private int SeedRootOccurrence(BomExplosionArea a, Run run)
    {
        var item = run.RootItem.Lines.Count > 0 ? run.RootItem.Lines[0] : new BillLine();
        var reqQty = Pic.N(a.ReqQty, 15, 6);
        var occ = new BomOccurrence
        {
            ParentSeq = 0,
            Depth = 0,
            LineNum = 0,
            Path = Pic.X(a.ParentItem, 160),
            OccRef = Pic.X(a.ParentItem, 40),
            ParentItem = "",
            Item = a.ParentItem,
            ItemRev = Pic.X(a.ParentRev, 4),
            Description = Pic.X(item.Desc, 60),
            BomId = a.BomId,
            BomRev = a.BomRev,
            NodeType = "INTERMED",
            Supply = "MAKE",
            StopReason = "ROOT",
            UsageBasis = "PROPORTION",
            Uom = item.StockUom,
            StockUom = item.StockUom,
            UsageQty = Pic.N(1m, 13, 6),
            ConvFact = Pic.N(1m, 9, 9),
            ScrapPct = Pic.N(0m, 5, 6),
            Unrounded = Pic.N(reqQty, 15, 9),
            ReqQty = reqQty,
            GoodQty = reqQty,
            NominalBasis = a.NominalBasis,
            Yield = a.Yield,
            Quantum = a.Quantum,
            BatchSize = a.BatchSize,
        };
        var seq = run.Pool.Add(occ);
        if (seq == 0)
        {
            ReportCapacity(run);
            return 0;
        }
        return seq;
    }

    private static void ReportCapacity(Run run)
    {
        run.Reason = "BOM0524";
        run.Message = "The calculation exceeded the supported occurrence capacity and is incomplete";
        run.Completeness = "INCOMPLETE";
        run.Abort = true;
    }

    private async Task CallRootNodeAsync(BomExplosionArea a, Run run, int rootSeq)
    {
        var split = new decimal[10];
        for (var i = 0; i < 10; i++) split[i] = Pic.N(a.SplitQty[i], 15, 6);
        var args = new NodeArgs(a.ParentItem, Pic.X(a.ParentRev, 4), a.BomId, a.BomRev, Pic.N(a.ReqQty, 15, 6), rootSeq, 0,
            Pic.X(a.ParentItem, 160), "N", a.SplitCount, split);
        var node = await ExpandOneNodeAsync(run, args);
        a.GrossStarts = node.OutGross;
        a.RunCount = node.OutRuns;
        a.BatchCount = node.OutBatches;
    }

    private static void AggregateAndPublish(BomExplosionArea a, Run run)
    {
        a.LeafCount = run.Pool.Aggregate();
        ProposeLeafPacks(a, run);
        a.OccCount = run.Pool.Count;
        a.DepthReached = run.Pool.Deepest;
        PublishSummary(a, run);
    }

    private static void ProposeLeafPacks(BomExplosionArea a, Run run)
    {
        a.LeafCount = run.Pool.LeafCount;
        for (var i = 1; i <= a.LeafCount; i++)
        {
            var leaf = run.Pool.GetLeaf(i);
            if (leaf is null || leaf.PackMult <= 0) continue;
            var m = new MathArea { Function = "PACK", ReqQty = leaf.ReqQty, FreeStock = leaf.StockUsed, PackMult = leaf.PackMult };
            BomMath.Call(m);
            leaf.PackQty = m.PackQty;
            leaf.Excess = m.Excess;
            run.Pool.PutLeaf(i, leaf);
        }
    }

    private static void PublishSummary(BomExplosionArea a, Run run)
    {
        a.Completeness = run.Completeness;
        a.ErrorPath = Pic.X(run.ErrorPath, 200);
        a.CyclePath = Pic.X(run.CyclePath, 200);
        a.WarningCount = run.WarningCnt;
        var r = a.Result;
        int rc;
        string reason = r.ReasonCode;
        string message = r.Message;
        var count = r.MessageCount;
        switch (run.Completeness)
        {
            case "COMPLETE":
                rc = run.WarningCnt > 0 ? 4 : 0;
                if (Pic.Blank(message)) message = Pic.Str(200, "Explosion complete - ", Pic.Z(Pic.I(a.OccCount, 4)), " occurrence(s)");
                break;
            case "INCOMPLETE":
                rc = 4;
                reason = run.Reason;
                message = run.Message;
                count = Pic.I(count + 1, 2);
                break;
            default:
                rc = 8;
                reason = run.Reason;
                message = run.Message;
                count = Pic.I(count + 1, 2);
                break;
        }
        if (run.Reason == "SQL0001") rc = 12;
        a.Result = new LegacyResult(rc, Pic.X(reason, 8), Pic.X(message, 200), count, r.DocumentRef);
    }

    // ================================================================ NODE
    private async Task<NodeState> ExpandOneNodeAsync(Run run, NodeArgs args)
    {
        var n = new NodeState
        {
            Args = args,
            Depth = args.Depth,
            MySeq = args.Seq,
            Path = args.Path,
            IsPhantom = args.Phantom,
            GoodQty = args.ReqQty,
        };
        if (n.Depth > run.DepthReached) run.DepthReached = n.Depth;
        if (run.Abort) return n;
        var j = n.Depth + 1;
        run.AncItem[j] = args.ParentItem;
        run.AncRev[j] = args.ParentRev;
        run.AncBomId[j] = args.BomId;
        await ReadThisBillAsync(run, n);
        if (run.Abort) return n;
        ComputeParentQuantities(run, n);
        if (run.Abort) return n;
        UpdateNodeOccurrence(run, n);
        await BuildChildDemandsAsync(run, n);
        if (run.IncludeByprod == "Y" && n.IsPhantom != "Y") await AddByProductsAsync(run, n);
        if (run.Consolidate == "Y") ConsolidateSiblingRuns(run, n);
        await ExpandChildDemandsAsync(run, n);
        return n;
    }

    private async Task ReadThisBillAsync(Run run, NodeState n)
    {
        n.Bill = await reader.BillAsync(run.Company, run.Site, n.Args.BomId, 500, run.Ct);
        switch (n.Bill.Status)
        {
            case "OK":
                break;
            case "TOOMANY":
                run.Reason = "BOM0525";
                run.Message = n.Bill.Message;
                run.ErrorPath = Pic.X(n.Path, 200);
                run.Completeness = "INCOMPLETE";
                run.Abort = true;
                break;
            case "SQLFAIL":
                run.Reason = "SQL0001";
                run.Message = n.Bill.Message;
                run.Fail();
                break;
            default:
                run.Reason = "BOM0505";
                run.Message = n.Bill.Message;
                run.ErrorPath = Pic.X(n.Path, 200);
                run.Fail();
                break;
        }
    }

    /// <summary>Gross starts, runs and batches of this node; a phantom has none of its own (BOM-017).</summary>
    private static void ComputeParentQuantities(Run run, NodeState n)
    {
        var h = n.Bill.Header;
        n.Basis = h.NominalBasis;
        if (n.IsPhantom == "Y")
        {
            n.Gross = n.GoodQty;
            n.Runs = 0;
            n.Batches = 0;
            return;
        }
        n.Gross = 0m;
        n.Runs = 0;
        n.Batches = 0;
        if (n.Args.SplitCnt > 1)
        {
            for (var i = 1; i <= n.Args.SplitCnt && i <= 10; i++)
            {
                var split = n.Args.SplitQty[i - 1];
                if (split <= 0) continue;
                var m = ParentMaths(run, n, split);
                if (run.Abort) return;
                n.Gross = Pic.N(n.Gross + m.GrossStarts, 15, 6);
                n.Batches += m.BatchCount;
                n.Runs += 1;
            }
        }
        else
        {
            var m = ParentMaths(run, n, n.GoodQty);
            if (run.Abort) return;
            n.Gross = m.GrossStarts;
            n.Batches = m.BatchCount;
            if (n.GoodQty > 0) n.Runs = 1;
        }
        n.OutGross = n.Gross;
        n.OutRuns = n.Runs;
        n.OutBatches = n.Batches;
    }

    private static MathArea ParentMaths(Run run, NodeState n, decimal qty)
    {
        var h = n.Bill.Header;
        var m = new MathArea
        {
            Function = "PARENT",
            GoodQty = qty,
            Yield = h.Yield,
            Quantum = h.Quantum,
            BatchSize = h.BatchSize,
            RunCount = 1,
        };
        BomMath.Call(m);
        if (m.ReturnCode > 4)
        {
            run.Reason = m.Reason;
            run.Message = m.Message;
            run.ErrorPath = Pic.X(n.Path, 200);
            run.Fail();
        }
        return m;
    }

    private static void UpdateNodeOccurrence(Run run, NodeState n)
    {
        var occ = run.Pool.Get(n.MySeq);
        if (occ is null) return;
        var h = n.Bill.Header;
        occ.GrossStarts = n.Gross;
        occ.RunCount = n.Runs;
        occ.BatchCount = n.Batches;
        occ.NominalBasis = h.NominalBasis;
        occ.Yield = h.Yield;
        occ.Quantum = h.Quantum;
        occ.BatchSize = h.BatchSize;
        occ.BomRev = n.Args.BomRev;
        occ.UnitCost = Pic.N(h.SetupCost, 11, 6);
        occ.BranchCost = Pic.N(h.ConvCost, 13, 2);
        run.Pool.Put(n.MySeq, occ);
    }

    // ---------------------------------------------------------------- component lines
    private async Task BuildChildDemandsAsync(Run run, NodeState n)
    {
        for (var i = 1; i <= n.Bill.LineCount; i++)
        {
            if (!run.Abort) await ExamineOneLineAsync(run, n, i);
        }
    }

    private async Task ExamineOneLineAsync(Run run, NodeState n, int i)
    {
        n.Skip = "N";
        n.Expand = "N";
        n.Warning = "";
        n.Stop = "";
        n.NodeType = "";
        n.Unrounded = 0m;
        n.Required = 0m;
        n.RoundInc = 0m;
        n.Taken = 0m;
        n.ToMake = 0m;
        TestLineEffectivity(run, n, i);
        if (n.Skip == "Y") return;
        TestOptionSelection(run, n, i);
        if (n.Skip == "Y") return;
        ClassifyTheLine(run, n, i);
        if (n.Skip == "Y") return;
        ComputeLineRequirement(run, n, i);
        ApplyMaterialRounding(run, n, i);
        if (run.NetMode == "Y") await ConsumeEligibleSupplyAsync(run, n, i);
        n.PackQty = 0m;
        n.Excess = 0m;
        RecordTheOccurrence(run, n, i);
    }

    private static BillLine Line(NodeState n, int i) => n.Bill.Lines[i - 1];

    /// <summary>Half open [start, end): in force on the start date, out of force on the end date (D-10).</summary>
    private static void TestLineEffectivity(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        if (l.EffFrom != 0 && l.EffFrom > run.EffDate)
        {
            n.Skip = "Y";
            return;
        }
        if (l.EffTo != 0 && l.EffTo <= run.EffDate) n.Skip = "Y";
    }

    /// <summary>Optional and alternate lines: an alternate group needs exactly one selected choice (BOM-018).</summary>
    private static void TestOptionSelection(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        if (Pic.Blank(l.AltGroup))
        {
            if (l.Optional == "Y" && l.Selected != "Y") n.Skip = "Y";
            return;
        }
        FindGroupChoice(run, n, i);
        if (n.ChosenLine == 0)
        {
            if (n.GroupHit == i)
            {
                run.Reason = "BOM0518";
                run.Message = Pic.Str(200, "Alternate group ", Pic.T(l.AltGroup), " has no selected choice - the configuration ", "is incomplete");
                run.ErrorPath = Pic.X(n.Path, 200);
                run.Completeness = "INCOMPLETE";
                run.Warn();
                n.Warning = "Unconfigured alternate group";
                n.Stop = "NOCHOICE";
                n.NodeType = "NOTE";
                RecordUnusableLine(run, n, i);
            }
            n.Skip = "Y";
            return;
        }
        if (n.ChosenLine != i) n.Skip = "Y";
    }

    private static void FindGroupChoice(Run run, NodeState n, int i)
    {
        n.ChosenLine = 0;
        n.GroupHit = 0;
        var group = Line(n, i).AltGroup;
        var groupSet = run.Options.Any(o => Pic.Eq(o.Group, group));
        for (var j = 1; j <= n.Bill.LineCount; j++)
        {
            var lj = Line(n, j);
            if (!Pic.Eq(lj.AltGroup, group)) continue;
            if (n.GroupHit == 0) n.GroupHit = j;
            if (groupSet)
            {
                foreach (var o in run.Options)
                {
                    if (Pic.Eq(o.Group, lj.AltGroup) && (Pic.Eq(o.Choice, lj.Item) || Pic.Eq(o.Choice, lj.OptionRef))) n.ChosenLine = j;
                }
            }
            else if (lj.Selected == "Y")
            {
                n.ChosenLine = j;
            }
        }
    }

    /// <summary>Supply behaviour decides the node type and whether expansion continues (BOM-016).</summary>
    private static void ClassifyTheLine(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        if (l.ItemFound != "Y")
        {
            run.Reason = "BOM0510";
            run.Message = Pic.Str(200, "Component ", Pic.T(l.Item), " is not held in the item master");
            run.ErrorPath = Pic.X(n.Path, 200);
            run.Completeness = "INCOMPLETE";
            run.Warn();
            n.Warning = "Missing component identity";
            n.Stop = "MISSINGITEM";
            n.NodeType = "LEAF";
            RecordUnusableLine(run, n, i);
            n.Skip = "Y";
            return;
        }
        if (Pic.Blank(l.StockUom) || l.ConvFact <= 0)
        {
            run.Reason = "BOM0509";
            run.Message = Pic.Str(200, "Component ", Pic.T(l.Item), " has no usable unit conversion");
            run.ErrorPath = Pic.X(n.Path, 200);
            run.Completeness = "INCOMPLETE";
            run.Warn();
            n.Warning = "Missing or zero conversion factor";
            n.Stop = "BADUNIT";
            n.NodeType = "LEAF";
            RecordUnusableLine(run, n, i);
            n.Skip = "Y";
            return;
        }
        switch (l.Supply)
        {
            case "NOTE":
                n.NodeType = "NOTE";
                n.Stop = "NOTE";
                if (run.IncludeNotes != "Y") n.Skip = "Y";
                break;
            case "SERVICE":
                n.NodeType = "LEAF";
                n.Stop = "SERVICE";
                break;
            case "COSTONLY":
                n.NodeType = "LEAF";
                n.Stop = "COSTONLY";
                break;
            case "NONSTOCK":
                n.NodeType = "LEAF";
                n.Stop = "NONSTOCK";
                break;
            case "PHANTOM":
                n.NodeType = "PHANTOM";
                if (run.ExpandPhantom == "Y")
                {
                    n.Expand = "Y";
                    n.Stop = "PHANTOM";
                }
                else
                {
                    n.NodeType = "LEAF";
                    n.Stop = "PHANTOMHELD";
                }
                break;
            case "MAKE":
                n.NodeType = "INTERMED";
                n.Expand = "Y";
                n.Stop = "MAKE";
                break;
            case "USESTOCK":
                n.NodeType = "LEAF";
                n.Stop = "USESTOCK";
                break;
            default:
                n.NodeType = "LEAF";
                n.Stop = "PURCHASED";
                break;
        }
    }

    private static void ComputeLineRequirement(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        var m = new MathArea
        {
            Function = "COMPON",
            GoodQty = n.GoodQty,
            GrossStarts = n.Gross,
            NominalBasis = n.Basis,
            UsageQty = l.UsageQty,
            ConvFact = l.ConvFact,
            ScrapPct = l.ScrapPct,
            UsageBasis = l.UsageBasis,
            RunCount = n.Runs,
            BatchCount = n.Batches,
        };
        BomMath.Call(m);
        if (m.ReturnCode > 4)
        {
            run.Reason = m.Reason;
            run.Message = m.Message;
            run.ErrorPath = Pic.X(n.Path, 200);
            run.Completeness = "INCOMPLETE";
            run.Warn();
            n.Warning = Pic.X(m.Message, 100);
            n.Unrounded = 0m;
            return;
        }
        n.Unrounded = m.Unrounded;
        n.YieldContrib = m.YieldContrib;
        n.ScrapContrib = m.ScrapContrib;
    }

    /// <summary>Round upward once at an independently supplied or issued material occurrence (BOM-011).</summary>
    private static void ApplyMaterialRounding(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        n.Required = Pic.N(n.Unrounded, 15, 6);
        n.RoundInc = 0m;
        if (n.NodeType == "NOTE")
        {
            n.Required = 0m;
            return;
        }
        if (n.NodeType == "PHANTOM") return;
        if (l.Supply is "SERVICE" or "COSTONLY") return;
        if (l.Quantum <= 0)
        {
            run.Warn();
            n.Warning = "No allowed stock quantum - unrounded amount kept";
            return;
        }
        var m = new MathArea { Function = "ROUNDUP", Unrounded = n.Unrounded, Quantum = l.Quantum };
        BomMath.Call(m);
        if (m.ReturnCode > 4) return;
        n.Required = m.ReqQty;
        n.RoundInc = m.RoundInc;
    }

    /// <summary>Netting: eligible supply is consumed once for the whole demand scope (BOM-020).</summary>
    private static async Task ConsumeEligibleSupplyAsync(Run run, NodeState n, int i)
    {
        var l = Line(n, i);
        n.ToMake = n.Required;
        if (n.Required <= 0 || n.NodeType == "NOTE") return;
        if (l.Stocked != "Y") return;
        var c = new SupplyCall
        {
            Company = run.Company,
            Site = run.Site,
            Source = run.StockSource,
            Item = l.Item,
            Rev = l.Rev,
            Want = n.Required,
            UseInbound = run.UseInbound,
        };
        await run.Supply.TakeAsync(c, run.Ct);
        n.Taken = c.Taken;
        n.ToMake = (Cd.F(n.Required, 6) - Cd.F(c.Taken, 6) - Cd.F(c.TakenInbound, 6)).Store(15, 6);
        if (n.ToMake < 0) n.ToMake = 0m;
        if (n.Expand == "Y" && n.ToMake == 0)
        {
            n.Expand = "N";
            n.Stop = "FROMSTOCK";
        }
    }

    private static void RecordTheOccurrence(Run run, NodeState n, int i)
    {
        var occ = BuildOccurrenceShell(n, i);
        occ.Unrounded = n.Unrounded;
        occ.YieldContrib = n.YieldContrib;
        occ.ScrapContrib = n.ScrapContrib;
        occ.RoundInc = n.RoundInc;
        occ.ReqQty = Pic.N(n.Required, 15, 6);
        occ.GoodQty = Pic.N(n.Required, 15, 6);
        occ.StockUsed = Pic.N(n.Taken, 15, 6);
        if (run.NetMode == "Y" && n.NodeType == "LEAF")
        {
            occ.Shortage = (Cd.F(n.Required, 6) - Cd.F(n.Taken, 6)).Store(15, 6);
            if (occ.Shortage < 0) occ.Shortage = Pic.N(0m, 15, 6);
        }
        if (run.NetMode == "Y" && n.Expand == "Y") occ.GoodQty = Pic.N(n.ToMake, 15, 6);
        occ.PackQty = n.PackQty;
        occ.Excess = n.Excess;
        if (!AddOccurrenceToPool(run, n, occ)) return;
        if (n.Expand == "Y") RememberChildDemand(run, n, i);
    }

    private static void RecordUnusableLine(Run run, NodeState n, int i)
    {
        var occ = BuildOccurrenceShell(n, i);
        occ.Unrounded = 0m;
        occ.ReqQty = 0m;
        occ.GoodQty = 0m;
        occ.Warning = Pic.X(n.Warning, 100);
        AddOccurrenceToPool(run, n, occ);
    }

    private static BomOccurrence BuildOccurrenceShell(NodeState n, int i)
    {
        var l = Line(n, i);
        var childPath = Pic.Str(160, Pic.T(n.Path), "/", Pic.Z(Math.Abs(l.LineNum)), ":", Pic.T(l.Item));
        return new BomOccurrence
        {
            ParentSeq = n.MySeq,
            Depth = Pic.I(n.Depth + 1, 4),
            LineNum = l.LineNum,
            Path = childPath,
            OccRef = Pic.X(childPath, 40),
            ParentItem = n.Args.ParentItem,
            ParentRev = n.Args.ParentRev,
            Item = l.Item,
            ItemRev = l.Rev,
            Description = Pic.X(l.Desc, 60),
            NodeType = Pic.X(n.NodeType, 8),
            Supply = l.Supply,
            StopReason = Pic.X(n.Stop, 20),
            UsageBasis = l.UsageBasis,
            Uom = l.Uom,
            StockUom = l.StockUom,
            AltGroup = l.AltGroup,
            Optional = l.Optional,
            Selected = l.Selected,
            UsageQty = l.UsageQty,
            ConvFact = l.ConvFact,
            ScrapPct = l.ScrapPct,
            Quantum = Pic.N(l.Quantum, 13, 6),
            PackMult = Pic.N(l.PackQty, 13, 6),
            UnitCost = Pic.N(l.CostRate, 11, 6),
            CostElement = l.CostElement,
            Operation = l.Operation,
            Warning = Pic.X(n.Warning, 100),
        };
    }

    private static bool AddOccurrenceToPool(Run run, NodeState n, BomOccurrence occ)
    {
        var seq = run.Pool.Add(occ);
        if (seq == 0)
        {
            ReportCapacity(run);
            return false;
        }
        n.ChildSeq = seq;
        return true;
    }

    private static void RememberChildDemand(Run run, NodeState n, int i)
    {
        if (n.Demands.Count >= 500)
        {
            ReportCapacity(run);
            return;
        }
        var l = Line(n, i);
        n.Demands.Add(new Demand
        {
            Line = l.LineNum,
            Seq = n.ChildSeq,
            Item = l.Item,
            Rev = l.Rev,
            Qty = Pic.N(run.NetMode == "Y" ? n.ToMake : n.Required, 15, 6),
            Phantom = n.NodeType == "PHANTOM" ? "Y" : "N",
            Expand = "Y",
        });
    }

    /// <summary>An APPROVED execution consolidation merges sibling demands for the same made child (D-10, BOM-019).</summary>
    private static void ConsolidateSiblingRuns(Run run, NodeState n)
    {
        var d = n.Demands;
        for (var i = 0; i < d.Count; i++)
        {
            if (d[i].Expand != "Y" || d[i].Phantom != "N") continue;
            for (var j = i; j < d.Count - 1; j++)
            {
                var c = d[j + 1];
                if (c.Expand == "Y" && c.Phantom == "N" && c.Item == d[i].Item && c.Rev == d[i].Rev)
                {
                    d[i].Qty = Pic.N(d[i].Qty + c.Qty, 15, 6);
                    c.Expand = "N";
                    var occ = run.Pool.Get(c.Seq);
                    if (occ is null) continue;
                    occ.StopReason = "CONSOLIDATED";
                    occ.Warning = "Execution consolidated into an earlier run";
                    run.Pool.Put(c.Seq, occ);
                }
            }
        }
    }

    // ---------------------------------------------------------------- expansion
    private async Task ExpandChildDemandsAsync(Run run, NodeState n)
    {
        foreach (var d in n.Demands)
        {
            if (d.Expand == "Y" && !run.Abort) await ExpandOneDemandAsync(run, n, d);
        }
    }

    private async Task ExpandOneDemandAsync(Run run, NodeState n, Demand d)
    {
        TestForCycle(run, n, d);
        if (run.Abort || n.Cycle) return;
        if (n.Depth + 1 >= run.MaxDepth)
        {
            n.Stop = "DEPTHLIMIT";
            run.Reason = "BOM0520";
            run.Message = "The calculation stopped at the stated depth limit and is not a complete leaf requirement";
            run.Completeness = "INCOMPLETE";
            run.Warn();
            n.Warning = "Depth limit reached";
            StampStopReason(run, n, d);
            return;
        }
        await SelectChildBillAsync(run, n, d);
        if (!n.BillOk) return;
        await RecurseIntoChildAsync(run, n, d);
    }

    /// <summary>A cycle is an ancestry path that returns to the same item; two independent paths are not a cycle.</summary>
    private static void TestForCycle(Run run, NodeState n, Demand d)
    {
        n.Cycle = false;
        var j = n.Depth + 1;
        for (var k = 1; k <= j && !n.Cycle; k++)
        {
            if (run.AncItem[k] != d.Item) continue;
            n.Cycle = true;
            run.Reason = "BOM0513";
            run.CyclePath = Pic.Str(200, Pic.T(n.Path), "/", Pic.T(d.Item), " returns to ", Pic.T(run.AncItem[k]));
            run.Message = Pic.Str(200, "Cycle rejected: ", Pic.T(run.CyclePath));
            run.ErrorPath = run.CyclePath;
            run.Completeness = "ERROR";
            n.Stop = "CYCLE";
            n.Warning = "Cyclic structure";
            StampStopReason(run, n, d);
            run.Abort = true;
        }
    }

    private static void StampStopReason(Run run, NodeState n, Demand d)
    {
        var occ = run.Pool.Get(d.Seq);
        if (occ is null) return;
        occ.StopReason = Pic.X(n.Stop, 20);
        occ.Warning = Pic.X(n.Warning, 100);
        run.Pool.Put(d.Seq, occ);
    }

    private async Task SelectChildBillAsync(Run run, NodeState n, Demand d)
    {
        n.BillOk = false;
        var s = new SelectArea
        {
            Function = "SELECT",
            Company = run.Company,
            Site = run.Site,
            User = run.User,
            BusinessDate = run.EffDate,
            ParentItem = d.Item,
            ParentRev = d.Rev,
            Purpose = "STANDARD",
            Variant = run.Variant,
            EffDate = run.EffDate,
        };
        await selector.CallAsync(s, run.Ct);
        n.Selected = s;
        if (s.ReturnCode <= 4 && s.BomId != 0)
        {
            n.BillOk = true;
            return;
        }
        run.Reason = "BOM0512";
        run.Message = Pic.Str(200, "No usable bill for the manufactured component ", Pic.T(d.Item), " - ", Pic.T(s.Message));
        run.ErrorPath = Pic.Str(200, Pic.T(n.Path), "/", Pic.T(d.Item));
        run.Completeness = "INCOMPLETE";
        run.Warn();
        n.Stop = "NOCHILDBOM";
        n.Warning = "Missing manufactured child bill";
        StampStopReason(run, n, d);
    }

    private async Task RecurseIntoChildAsync(Run run, NodeState n, Demand d)
    {
        var s = n.Selected!;
        var occ = run.Pool.Get(d.Seq) ?? new BomOccurrence();
        var childPath = occ.Path;
        occ.BomRev = s.BomRev;
        occ.BomId = s.BomId;
        run.Pool.Put(d.Seq, occ);
        var args = new NodeArgs(d.Item, d.Rev, s.BomId, s.BomRev, d.Qty, d.Seq, n.Depth + 1, childPath, d.Phantom, 0, new decimal[10]);
        await ExpandOneNodeAsync(run, args);
    }

    // ---------------------------------------------------------------- by-products
    /// <summary>By-products are expected recoveries: separate positive outputs, never negative consumption (BOM-026).</summary>
    private async Task AddByProductsAsync(Run run, NodeState n)
    {
        List<string[]> rows;
        try
        {
            rows = await sql.WithReaderAsync(BomSql.Q(ByProductSql), BomSql.Args(n.Args.BomId), async r =>
            {
                var list = new List<string[]>();
                while (await r.ReadAsync(run.Ct))
                {
                    list.Add(
                    [
                        BomSql.Text(r, 0, 8), BomSql.Text(r, 1, 40), BomSql.Text(r, 2, 24), BomSql.Text(r, 3, 10), BomSql.Text(r, 4, 24),
                        BomSql.Text(r, 5, 12), BomSql.Text(r, 6, 24), BomSql.Text(r, 7, 60), BomSql.Text(r, 8, 20),
                    ]);
                }
                return list;
            }, run.Ct);
        }
        catch (SqlioException)
        {
            return;
        }
        foreach (var by in rows) RecordOneByProduct(run, n, by);
    }

    private static void RecordOneByProduct(Run run, NodeState n, string[] by)
    {
        var lineNum = Pic.I((long)decimal.Truncate(BomSql.NumVal(by[0])), 4);
        var path = Pic.Str(160, Pic.T(n.Path), "/BP", Pic.Z(Math.Abs(lineNum)), ":", Pic.T(by[1]));
        var usage = Pic.N(BomSql.NumVal(by[2]), 13, 6);
        var conv = Pic.N(BomSql.NumVal(by[4]), 9, 9);
        var unrounded = (Cd.F(n.Gross, 6) / Cd.F(n.Basis, 6) * Cd.F(usage, 6) * Cd.F(conv, 9)).Store(15, 9);
        var occ = new BomOccurrence
        {
            ParentSeq = n.MySeq,
            Depth = Pic.I(n.Depth + 1, 4),
            LineNum = lineNum,
            Path = path,
            OccRef = Pic.X(path, 40),
            ParentItem = n.Args.ParentItem,
            Item = by[1],
            Description = Pic.X(by[7], 60),
            NodeType = "BYPROD",
            Supply = "BYPRODUCT",
            StopReason = "RECOVERY",
            UsageBasis = "PROPORTION",
            Uom = by[3],
            StockUom = by[3],
            UsageQty = usage,
            ConvFact = conv,
            Unrounded = unrounded,
            ReqQty = Pic.N(unrounded, 15, 6),
            GoodQty = Pic.N(unrounded, 15, 6),
            UnitCost = Pic.N(BomSql.NumVal(by[6]), 11, 6),
            CostSource = Pic.X(by[5], 12),
        };
        if (Pic.Blank(by[8]))
        {
            occ.Warning = "Recovery valuation is not approved";
            run.Warn();
        }
        run.Pool.Add(occ);
    }

    // ================================================================ GETLINE / LEAFQTY
    /// <summary>Reading the retained result back (a read-only repeat has no live effect, BOM-030).</summary>
    private static void PageTheResult(BomExplosionArea a)
    {
        a.Program = "BOMEXPL";
        a.Result = a.Result with { ReturnCode = 0, ReasonCode = "" };
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        a.OccCount = pool.Count;
        if (a.FromLine < 1) a.FromLine = 1;
        var i = a.FromLine;
        while (!(i > a.OccCount || a.WinCnt >= a.WinMax))
        {
            var o = pool.Get(i)!;
            if (Keep(a.View, o))
            {
                a.WinCnt++;
                Fill(a.Lines[a.WinCnt - 1], o);
            }
            i++;
        }
        a.FromLine = i;
        if (i <= a.OccCount) a.MoreLines = "Y";
    }

    private static bool Keep(string view, BomOccurrence o) => Pic.X(view, 8) switch
    {
        "LEAF" => o.NodeType == "LEAF",
        "INTERMED" => o.NodeType == "INTERMED",
        "LEVEL1" => o.Depth == 1,
        _ => true,
    };

    private static void Fill(BomExplosionWindowLine w, BomOccurrence o)
    {
        w.Seq = o.Seq;
        w.Depth = o.Depth;
        w.Path = Pic.X(o.Path, 80);
        w.Item = o.Item;
        w.Rev = o.ItemRev;
        w.Desc = Pic.X(o.Description, 40);
        w.NodeType = o.NodeType;
        w.Supply = o.Supply;
        w.Stop = o.StopReason;
        w.Basis = o.UsageBasis;
        w.Uom = o.StockUom;
        w.BomRev = o.BomRev;
        w.Usage = o.UsageQty;
        w.Unrounded = o.Unrounded;
        w.RoundInc = o.RoundInc;
        w.ReqQty = o.ReqQty;
        w.GoodQty = o.GoodQty;
        w.Gross = o.GrossStarts;
        w.Runs = o.RunCount;
        w.Batches = o.BatchCount;
        w.StockUsed = o.StockUsed;
        w.Shortage = o.Shortage;
        w.PackQty = o.PackQty;
        w.Excess = o.Excess;
        w.UnitCost = o.UnitCost;
        w.ExtCost = o.ExtCost;
        w.Branch = o.BranchCost;
        w.Element = o.CostElement;
        w.Warning = Pic.X(o.Warning, 60);
    }

    private static void TotalOneItem(BomExplosionArea a)
    {
        a.Program = "BOMEXPL";
        a.Result = a.Result with { ReturnCode = 0 };
        a.KeyItem = Pic.X(Pic.Upper(a.KeyItem), 40);
        var pool = a.Calculation?.Pool ?? new OccurrencePool();
        var (total, count) = pool.Total(a.KeyItem, "ALL");
        a.KeyTotal = total;
        if (count == 0)
        {
            a.Result = a.Result with
            {
                ReturnCode = 16,
                ReasonCode = "BOM0530",
                Message = Pic.Str(200, "No occurrence of ", Pic.T(a.KeyItem), " in the retained calculation"),
            };
        }
    }
}
