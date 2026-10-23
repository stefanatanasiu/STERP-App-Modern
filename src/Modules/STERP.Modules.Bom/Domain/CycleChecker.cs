using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Bom.Domain;

/// <summary>CYCLECHKCA (cobol/copy/CYCLECHKCA.cpy).</summary>
internal sealed class CycleArea
{
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public int ReturnCode { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public int MessageCount { get; set; }

    public string RootItem { get; set; } = "";
    public string RootRev { get; set; } = "";
    public string ChildItem { get; set; } = "";
    public string ChildRev { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Variant { get; set; } = "";
    public int EffDate { get; set; }
    public int MaxDepth { get; set; }
    public string IncludeOpt { get; set; } = "";
    public string CycleFound { get; set; } = "";
    public string CycleItem { get; set; } = "";
    /// <summary>CC-CYCLE-PATH X(240).</summary>
    public string CyclePath { get; set; } = "";
    public int CycleLine { get; set; }
    public int NodesVisited { get; set; }
    public int DepthReached { get; set; }
    public int SharedCount { get; set; }
    public string LimitHit { get; set; } = "";
}

/// <summary>
/// CYCLECHK (cobol/online/bom/CYCLECHK.cob, IS RECURSIVE): structure cycle check kernel (FR-STERP-BOM-042). CHECK walks the
/// structure selected for the root (drafts allowed, alternates when requested; notes, services, cost-only and
/// out-of-effectivity lines ignored) and reports a component already on its ancestry; EDGE reports a direct self use or
/// walks the child's structure (without comparing it against the proposed parent, SME-630). Read-only.
/// </summary>
internal sealed class CycleChecker(BillSelector selector, BillReader reader)
{
    private sealed class Run
    {
        public string Company = "";
        public string Site = "";
        public string User = "";
        public int EffDate;
        public string Variant = "";
        public string Purpose = "";
        public string Target = "";
        public string IncludeOpt = "N";
        public int MaxDepth = 25;
        public int Visited;
        public int Deepest;
        public int Shared;
        public string Cycle = "N";
        public string LimitHit = "N";
        public string Stop = "N";
        public string CyclePath = "";
        public string CycleItem = "";
        /// <summary>Ws-Cycle-Line: also the loop variable of Test-Ancestry (left one past the cycle line).</summary>
        public int CycleLine;
        public readonly string[] Anc = Enumerable.Repeat("", 31).ToArray();
        public readonly List<string> Seen = [];
        public CancellationToken Ct;
    }

    public async Task CallAsync(CycleArea a, CancellationToken ct)
    {
        switch (a.Function)
        {
            case "CHECK":
            {
                var run = InitialiseRun(a, ct);
                run.Target = a.RootItem;
                await WalkFromRootAsync(run, a.RootItem, a.RootRev);
                PublishOutcome(a, run);
                break;
            }
            case "EDGE":
            {
                var run = InitialiseRun(a, ct);
                run.Target = a.RootItem;
                if (a.ChildItem == a.RootItem)
                {
                    run.Cycle = "Y";
                    run.CycleItem = a.RootItem;
                    run.CyclePath = Pic.Str(240, Pic.T(a.RootItem), " uses itself directly");
                    PublishOutcome(a, run);
                    break;
                }
                await WalkFromRootAsync(run, a.ChildItem, a.RootRev);
                PublishOutcome(a, run);
                break;
            }
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM0401";
                a.Message = Pic.Str(200, "CYCLECHK does not support the function ", Pic.T(a.Function));
                break;
        }
    }

    private static Run InitialiseRun(CycleArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Message = "";
        var run = new Run { Ct = ct, Company = Pic.X(a.Company, 10), Site = Pic.X(a.Site, 10), User = Pic.X(a.User, 20) };
        a.RootItem = Pic.X(Pic.Upper(a.RootItem), 40);
        a.ChildItem = Pic.X(Pic.Upper(a.ChildItem), 40);
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        run.EffDate = a.EffDate;
        if (Pic.Blank(a.Purpose)) a.Purpose = "STANDARD";
        run.Purpose = Pic.X(a.Purpose, 10);
        run.Variant = Pic.X(a.Variant, 20);
        run.IncludeOpt = Pic.X(a.IncludeOpt, 1);
        if (a.MaxDepth < 1 || a.MaxDepth > 25) a.MaxDepth = 25;
        run.MaxDepth = a.MaxDepth;
        return run;
    }

    /// <summary>Walk-From-Root: the WALK call for the root at depth 0 with the root item as its path.</summary>
    private Task WalkFromRootAsync(Run run, string rootItem, string rootRev) =>
        WalkOneNodeAsync(run, Pic.X(rootItem, 40), Pic.X(rootRev, 4), 0, Pic.X(rootItem, 240));

    /// <summary>One node of the walk (WALK); depth and path belong to this level only.</summary>
    private async Task WalkOneNodeAsync(Run run, string item, string rev, int depth, string path)
    {
        if (run.Stop == "Y") return;
        run.Visited++;
        if (depth > run.Deepest) run.Deepest = depth;
        run.Anc[depth + 1] = item;
        NoteIfShared(run, item);
        var billId = await SelectThisBillAsync(run, item, rev);
        if (billId == 0) return;
        var bill = await reader.BillAsync(run.Company, run.Site, billId, 500, run.Ct);
        if (bill.Status != "OK") return;
        for (var i = 1; i <= bill.LineCount && run.Stop != "Y"; i++)
        {
            await ExamineOneComponentAsync(run, bill.Lines[i - 1], depth, path);
        }
    }

    /// <summary>Reaching the same item twice on different paths is counted, not refused.</summary>
    private static void NoteIfShared(Run run, string item)
    {
        if (run.Seen.Contains(item))
        {
            run.Shared = Pic.I(run.Shared + 1, 4);
            return;
        }
        if (run.Seen.Count < 300) run.Seen.Add(item);
    }

    private async Task<int> SelectThisBillAsync(Run run, string item, string rev)
    {
        var s = new SelectArea
        {
            Function = "SELECT",
            Company = run.Company,
            Site = run.Site,
            User = run.User,
            BusinessDate = run.EffDate,
            ParentItem = item,
            ParentRev = rev,
            Purpose = run.Purpose,
            Variant = run.Variant,
            EffDate = run.EffDate,
            AllowDraft = "Y",
        };
        await selector.CallAsync(s, run.Ct);
        return s.ReturnCode <= 4 ? s.BomId : 0;
    }

    private async Task ExamineOneComponentAsync(Run run, BillLine l, int depth, string path)
    {
        if (l.Supply is "NOTE" or "SERVICE" or "COSTONLY") return;
        // an unselected alternative is walked only when the caller asked for every permitted configuration (CR2588)
        if (!Pic.Blank(l.AltGroup) && l.Selected != "Y" && run.IncludeOpt != "Y") return;
        if (l.EffFrom != 0 && l.EffFrom > run.EffDate) return;
        if (l.EffTo != 0 && l.EffTo <= run.EffDate) return;
        TestAncestry(run, l, depth, path);
        if (run.Cycle == "Y") return;
        if (l.Supply != "MAKE" && l.Supply != "PHANTOM") return;
        if (depth + 1 >= run.MaxDepth)
        {
            run.LimitHit = "Y";
            return;
        }
        var childPath = Pic.Str(240, Pic.T(path), " -> ", Pic.T(l.Item));
        await WalkOneNodeAsync(run, l.Item, l.Rev, depth + 1, childPath);
    }

    /// <summary>
    /// Test-Ancestry. Ws-Cycle-Line is the PERFORM VARYING control variable: on a hit it is set to the component line and
    /// then incremented once more by the PERFORM before the UNTIL test, so the published line is the line number + 1.
    /// </summary>
    private static void TestAncestry(Run run, BillLine l, int depth, string path)
    {
        var j = depth + 1;
        run.CycleLine = 1;
        while (!(run.CycleLine > j || run.Cycle == "Y"))
        {
            if (run.Anc[run.CycleLine] == l.Item)
            {
                run.Cycle = "Y";
                run.Stop = "Y";
                run.CycleItem = l.Item;
                run.CyclePath = Pic.Str(240, Pic.T(path), " -> ", Pic.T(l.Item), " which is already on this path");
                run.CycleLine = l.LineNum;
            }
            run.CycleLine = Pic.I(run.CycleLine + 1, 4);
        }
    }

    private static void PublishOutcome(CycleArea a, Run run)
    {
        a.NodesVisited = run.Visited;
        a.DepthReached = run.Deepest;
        a.SharedCount = run.Shared;
        a.LimitHit = run.LimitHit;
        a.CycleFound = run.Cycle;
        a.CycleItem = run.CycleItem;
        a.CyclePath = run.CyclePath;
        a.CycleLine = run.CycleLine;
        if (run.Cycle == "Y")
        {
            a.ReturnCode = 8;
            a.Reason = "BOM0402";
            a.Message = Pic.Str(200, "Cyclic structure: ", Pic.T(run.CyclePath));
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        if (run.LimitHit == "Y")
        {
            a.ReturnCode = 4;
            a.Reason = "BOM0403";
            a.Message = "The walk reached its depth limit before the end";
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        a.ReturnCode = 0;
        a.Message = "No cyclic path found; shared descendants are valid";
    }
}
