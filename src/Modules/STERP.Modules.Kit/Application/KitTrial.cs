using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Kit.Application;

/// <summary>KITTRIALCA (cobol/copy/KITTRIALCA.cpy).</summary>
internal sealed class KitTrialArea : KitArea
{
    public string TrialRef { get; set; } = "";
    public int ScenarioNo { get; set; }
    public string KitRef { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string KtSite { get; set; } = "";
    public int BomId { get; set; }
    public decimal RequestedQty { get; set; }
    public int ReqDate { get; set; }
    public int SelectionDate { get; set; }
    public string Snapshot { get; set; } = "";
    public string UseInbound { get; set; } = "";
    public string UseSubassy { get; set; } = "";
    public string EligPolicy { get; set; } = "";
    public string PrioritySeq { get; set; } = "";
    public string DemandType { get; set; } = "";
    public string DemandRef { get; set; } = "";
    public long TrialId { get; set; }
    public decimal MaxBuildable { get; set; }
    public string LimitingItem { get; set; } = "";
    public decimal LimitingShort { get; set; }
    public int ShortageCnt { get; set; }
    public int LineTotal { get; set; }
    public string Completeness { get; set; } = "";
    public string Feasible { get; set; } = "";
    public string Committed { get; set; } = "";
    public string Warnings { get; set; } = "";
    public string ActionHint { get; set; } = "";
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public KitTrialLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new KitTrialLine()).ToArray();

    public bool IsFeasible => Feasible == "Y";
}

/// <summary>One KT-LINES occurrence (also the shape of a retained Ws-Res row).</summary>
internal sealed class KitTrialLine
{
    public int LineNum { get; set; }
    public string Path { get; set; } = "";
    public int Depth { get; set; }
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Uom { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal NetReq { get; set; }
    public decimal Free { get; set; }
    public decimal OwnResv { get; set; }
    public decimal OtherResv { get; set; }
    public decimal Inbound { get; set; }
    public int InbDate { get; set; }
    public decimal Allocated { get; set; }
    public decimal Shortage { get; set; }
    public int ShortDate { get; set; }
    public string Limiting { get; set; } = "";
    public string Action { get; set; } = "";
    public string Warning { get; set; } = "";

    public KitTrialLine Copy() => (KitTrialLine)MemberwiseClone();
}

/// <summary>
/// KITTRIAL (cobol/online/kit/KITTRIAL.cob:116-650): TRIAL, MAXBUILD and GETLINE (SAVE is not exposed, D-KIT-04). The
/// retained answer (Ws-Result, 150 lines) and the availability cache (200 items) live in this instance, one per request
/// (D-KIT-02): GETLINE pages the result of the TRIAL/MAXBUILD of the same request. Nothing is written.
/// </summary>
internal sealed class KitTrial(IBomExplosionEngine bomExplosion, IAvailabilityService availq)
{
    private const int ResMax = 150;
    private const int AvMax = 200;

    private readonly List<KitTrialLine> _res = [];
    private readonly List<AvailEntry> _av = [];
    private decimal _probeQty;
    private string _probeFeasible = "N";
    private int _probeCount;
    private decimal _lo;
    private decimal _hi;
    private decimal _best;
    private decimal _worstShort;
    private string _keepResult = "Y";

    private sealed class AvailEntry
    {
        public string Item = "";
        public string Uom = "";
        public decimal Free;
        public decimal OwnResv;
        public decimal OtherResv;
        public decimal Inbound;
        public int InbDate;
        public decimal Remain;
        public decimal InbRemain;
    }

    public async Task LinkAsync(KitTrialArea ca, CancellationToken ct)
    {
        ca.Program = "KITTRIAL";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-006";
        ca.Committed = "N";
        switch (ca.Function)
        {
            case "TRIAL": await RunTheTrialAsync(ca, ct); break;
            case "MAXBUILD": await RunMaxBuildableAsync(ca, ct); break;
            case "GETLINE": FillWindow(ca); break;
            default:
                // SAVE is not exposed (D-KIT-04); anything else is Reject-Function
                ca.Set(8, "KIT0002", Pic.Msg("KITTRIAL does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
        ca.Committed = "N";
    }

    /// <summary>STRING ... INTO CA-MESSAGE without clearing it first: the text overwrites from position 1, the rest stays.</summary>
    private static void StringIntoMessage(KitTrialArea ca, string text)
    {
        var old = Pic.Pad(ca.Message, 200);
        var src = text.Length > 200 ? text[..200] : text;
        ca.Message = Pic.X(src + old[src.Length..], 200);
    }

    private static void ValidateRequest(KitTrialArea ca)
    {
        if (Pic.Blank(ca.ParentItem))
        {
            ca.Set(8, "KIT0003", "A parent item is required for a trial.");
            return;
        }
        if (ca.RequestedQty <= 0)
        {
            ca.Set(8, "KIT0003", "The requested quantity must be greater than zero.");
            return;
        }
        if (Pic.Blank(ca.KtSite)) ca.KtSite = ca.Site;
        if (ca.ReqDate == 0) ca.ReqDate = ca.BusinessDate;
        if (ca.SelectionDate == 0) ca.SelectionDate = ca.BusinessDate;
        if (ca.ScenarioNo <= 0) ca.ScenarioNo = 1;
        if (ca.WinMax is <= 0 or > 20) ca.WinMax = 20;
    }

    private void ClearAnswer(KitTrialArea ca)
    {
        _res.Clear();
        _av.Clear();
        _probeCount = 0;
        ca.LineTotal = 0;
        ca.ShortageCnt = 0;
        ca.MaxBuildable = 0m;
        ca.LimitingShort = 0m;
        ca.LimitingItem = "";
        ca.Warnings = "";
        ca.ActionHint = "";
        ca.Feasible = "N";
        ca.Completeness = "COMPLETE";
        ca.WinCnt = 0;
        ca.MoreLines = "N";
    }

    // ------------------------------------------------------------------ TRIAL / MAXBUILD

    private async Task RunTheTrialAsync(KitTrialArea ca, CancellationToken ct)
    {
        ValidateRequest(ca);
        if (ca.ReturnCode != 0) return;
        ClearAnswer(ca);
        _probeQty = ca.RequestedQty;
        _keepResult = "Y";
        await ProbeOneQuantityAsync(ca, ct);
        if (ca.ReturnCode > 4) return;
        ca.Feasible = _probeFeasible;
        if (ca.IsFeasible) ca.MaxBuildable = ca.RequestedQty;
        DescribeTheAnswer(ca);
        FillWindow(ca);
    }

    private async Task RunMaxBuildableAsync(KitTrialArea ca, CancellationToken ct)
    {
        await RunTheTrialAsync(ca, ct);
        if (ca.ReturnCode > 4) return;
        _keepResult = "N";
        if (ca.IsFeasible)
        {
            _best = ca.RequestedQty;
            _lo = ca.RequestedQty;
            await GrowUpperBoundAsync(ca, ct);
        }
        else
        {
            _best = 0m;
            _lo = 0m;
            _hi = ca.RequestedQty;
        }
        await NarrowTheBoundAsync(ca, ct);
        ca.MaxBuildable = _best;
        if (_best == 0)
        {
            ca.Set(4, "KIT0021", "No complete output can be built from the material that is free today.");
        }
        _probeQty = ca.RequestedQty;
        _keepResult = "Y";
        _res.Clear();
        await ProbeOneQuantityAsync(ca, ct);
        ca.Feasible = _probeFeasible;
        DescribeTheAnswer(ca);
        FillWindow(ca);
    }

    private async Task GrowUpperBoundAsync(KitTrialArea ca, CancellationToken ct)
    {
        _hi = Pic.Q(_lo * 2);
        _probeFeasible = "Y";
        while (!(_probeFeasible == "N" || _probeCount > 8 || ca.ReturnCode > 4))
        {
            _probeQty = _hi;
            await ProbeOneQuantityAsync(ca, ct);
            if (_probeFeasible == "Y")
            {
                _lo = _hi;
                _best = _hi;
                _hi = Pic.Q(_hi * 2);
            }
        }
    }

    private async Task NarrowTheBoundAsync(KitTrialArea ca, CancellationToken ct)
    {
        while (!(_hi - _lo <= 1 || _probeCount > 20 || ca.ReturnCode > 4))
        {
            var mid = Pic.Q(decimal.Floor((_lo + _hi) / 2));
            if (mid <= _lo || mid >= _hi) break;
            _probeQty = mid;
            await ProbeOneQuantityAsync(ca, ct);
            if (_probeFeasible == "Y")
            {
                _lo = mid;
                _best = mid;
            }
            else
            {
                _hi = mid;
            }
        }
    }

    private async Task ProbeOneQuantityAsync(KitTrialArea ca, CancellationToken ct)
    {
        _probeCount++;
        _probeFeasible = "Y";
        _worstShort = 0m;
        if (_keepResult == "Y")
        {
            _res.Clear();
            ca.ShortageCnt = 0;
        }
        foreach (var a in _av)
        {
            a.Remain = a.Free;
            a.InbRemain = a.Inbound;
        }
        await ExplodeAtProbeQtyAsync(ca, ct);
    }

    /// <summary>BOMEXPL owns yield, fixed usage, batch steps, rounding and phantom expansion; KITTRIAL never reimplements any of it.</summary>
    private async Task ExplodeAtProbeQtyAsync(KitTrialArea ca, CancellationToken ct)
    {
        var ea = new BomExplosionArea
        {
            Function = "EXPLODE",
            Company = ca.Company,
            Site = ca.KtSite,
            User = ca.User,
            BusinessDate = ca.BusinessDate,
            CalcType = "EXECUTION",
            ParentItem = ca.ParentItem,
            ParentRev = ca.ParentRev,
            ReqQty = _probeQty,
            EffDate = ca.SelectionDate,
            ExpandPhantom = "Y",
            IncludeNotes = "N",
            IncludeByprod = "N",
            MaxDepth = 25,
            View = ca.UseSubassy == "Y" ? "LEVEL1" : "LEAF",
            WinMax = 25,
            FromLine = 1,
        };
        if (ca.DemandType == "JOB" && !Pic.Blank(ca.DemandRef))
        {
            ea.Purpose = "JOB";
            ea.JobRef = Pic.X(ca.DemandRef, 20);
        }
        else if (ca.DemandType == "SALESORDER" && !Pic.Blank(ca.DemandRef))
        {
            ea.Purpose = "ORDER";
            ea.JobRef = Pic.X(ca.DemandRef, 20);
        }
        else
        {
            ea.Purpose = "STANDARD";
        }
        var r = await bomExplosion.LinkAsync(ea, ct);
        if (r.ReturnCode > 4)
        {
            ca.Set(8, "KIT0004", r.Message);
            _probeFeasible = "N";
            return;
        }
        if (ea.Completeness != "COMPLETE")
        {
            ca.Completeness = "INCOMPLETE";
            ca.Set(4, "KIT0004", "The bill of material is not complete, so the trial cannot be relied on.");
            ca.Warnings = Pic.X(ea.ErrorPath, 300);
            _probeFeasible = "N";
            return;
        }
        ca.BomId = ea.BomId;
        ea.Function = "GETLINE";
        ea.FromLine = 1;
        var morePages = true;
        while (morePages && ca.ReturnCode <= 4)
        {
            r = await bomExplosion.LinkAsync(ea, ct);
            if (r.ReturnCode > 4)
            {
                ca.Set(8, "KIT0004", r.Message);
                _probeFeasible = "N";
                break;
            }
            for (var i = 0; i < ea.WinCnt; i++) await AssessOneComponentAsync(ca, ea.Lines[i], ct);
            if (ea.MoreLines != "Y") morePages = false;
        }
        if (_keepResult == "Y") ca.LineTotal = _res.Count;
    }

    /// <summary>Netting: free stock first, expected inbound only when allowed; a residual makes the probe infeasible.</summary>
    private async Task AssessOneComponentAsync(KitTrialArea ca, BomExplosionWindowLine bl, CancellationToken ct)
    {
        if (bl.NodeType is "NOTE" or "RESOURCE" or "BYPROD") return;
        if (bl.Supply is "SERVICE" or "COSTONLY" or "NONSTOCK" or "NOTE") return;
        if (bl.ReqQty == 0) return;
        var hit = await FindAvailabilityAsync(ca, bl, ct);
        if (hit is null) return;
        var netReq = Pic.Q(bl.ReqQty - bl.StockUsed);
        if (netReq < 0) netReq = 0m;
        var fromStock = netReq > hit.Remain ? hit.Remain : netReq;
        hit.Remain = Pic.Q(hit.Remain - fromStock);
        var residual = Pic.Q(netReq - fromStock);
        var fromInbound = 0m;
        if (residual > 0 && ca.UseInbound == "Y")
        {
            fromInbound = residual > hit.InbRemain ? hit.InbRemain : residual;
            hit.InbRemain = Pic.Q(hit.InbRemain - fromInbound);
        }
        if (residual - fromInbound > 0) _probeFeasible = "N";
        if (residual > _worstShort)
        {
            _worstShort = residual;
            if (_keepResult == "Y")
            {
                ca.LimitingItem = Pic.X(bl.Item, 40);
                ca.LimitingShort = residual;
            }
        }
        if (_keepResult == "Y") RecordResultLine(ca, bl, hit, netReq, fromStock, residual, fromInbound);
    }

    private void RecordResultLine(KitTrialArea ca, BomExplosionWindowLine bl, AvailEntry hit, decimal netReq, decimal fromStock,
        decimal residual, decimal fromInbound)
    {
        if (_res.Count >= ResMax)
        {
            ca.Warnings = "More components than the trial table holds; the answer is truncated.";
            return;
        }
        var r = new KitTrialLine
        {
            LineNum = _res.Count + 1,
            Path = Pic.X(Pic.Pad(bl.Path, 80)[..20], 20),
            Depth = bl.Depth,
            Item = Pic.X(bl.Item, 40),
            Rev = Pic.X(bl.Rev, 4),
            Uom = Pic.X(bl.Uom, 10),
            Gross = Pic.Q(bl.ReqQty),
            NetReq = netReq,
            Free = hit.Free,
            OwnResv = hit.OwnResv,
            OtherResv = hit.OtherResv,
            Inbound = hit.Inbound,
            InbDate = hit.InbDate,
            Allocated = fromStock,
            Shortage = residual,
            Limiting = "N",
            Warning = "",
            ShortDate = 0,
        };
        _res.Add(r);
        if (residual > 0)
        {
            ca.ShortageCnt = (int)Pic.N(ca.ShortageCnt + 1, 4, 0);
            if (fromInbound > 0)
            {
                r.ShortDate = hit.InbDate;
                r.Action = "AWAIT INBOUND";
                r.Warning = "Covered only by expected supply, not by free stock.";
            }
            else
            {
                r.Action = "ORDER OR SUBSTITUTE";
            }
        }
        else
        {
            r.Action = "AVAILABLE";
        }
    }

    /// <summary>AVAILQ AVAIL once per item per call; own-demand reservations are added back (AV-QTY-FOR-DEMAND).</summary>
    private async Task<AvailEntry?> FindAvailabilityAsync(KitTrialArea ca, BomExplosionWindowLine bl, CancellationToken ct)
    {
        var item = Pic.X(bl.Item, 40);
        var found = _av.FirstOrDefault(a => a.Item == item);
        if (found is not null) return found;
        if (_av.Count >= AvMax)
        {
            _probeFeasible = "N";
            ca.Warnings = "More distinct components than the trial can assess; not feasible as asked.";
            return null;
        }
        var av = new AvailqCommarea
        {
            ItemCode = item,
            Site = ca.KtSite,
            AsAt = CobolDate.FromYyyymmdd(ca.ReqDate),
            DemandType = Pic.Blank(ca.DemandType) ? "KIT" : ca.DemandType,
            DemandRef = Pic.Blank(ca.DemandRef) ? ca.KitRef : ca.DemandRef,
        };
        var r = await availq.ExecuteAsync(AvailqFunctions.Avail, av,
            new ItmCall(ca.Company, ca.KtSite, ca.User, CobolDate.FromYyyymmdd(ca.BusinessDate), ""), ct);
        var hit = new AvailEntry { Item = item, Uom = Pic.X(av.StockUom, 10) };
        _av.Add(hit);
        if (r.ReturnCode > 4)
        {
            hit.Free = hit.OwnResv = hit.OtherResv = hit.Inbound = 0m;
            hit.InbDate = 0;
        }
        else
        {
            hit.Free = Pic.Q(av.QtyForDemand);
            hit.OwnResv = Pic.Q(av.QtyOwnDemand);
            hit.OtherResv = Pic.Q(av.QtyReserved - av.QtyOwnDemand);
            hit.Inbound = Pic.Q(av.QtyExpected);
            hit.InbDate = CobolDate.ToYyyymmdd(av.ExpectedDate);
        }
        if (hit.Free < 0) hit.Free = 0m;
        if (hit.OtherResv < 0) hit.OtherResv = 0m;
        hit.Remain = hit.Free;
        hit.InbRemain = hit.Inbound;
        return hit;
    }

    private void DescribeTheAnswer(KitTrialArea ca)
    {
        // Mark-Limiting-Line
        foreach (var r in _res)
        {
            if (r.Item == ca.LimitingItem && r.Shortage == ca.LimitingShort && ca.LimitingShort > 0)
            {
                r.Limiting = "Y";
                break;
            }
        }
        if (ca.ShortageCnt == 0) ca.Completeness = "COMPLETE";
        if (ca.IsFeasible)
        {
            ca.ReturnCode = 0;
            ca.ActionHint = "RELEASE OR RESERVE";
            StringIntoMessage(ca, "Feasible. Maximum complete output " + Pic.Ed(ca.MaxBuildable) + ".");
        }
        else
        {
            if (ca.ReturnCode == 0)
            {
                ca.ReturnCode = 4;
                ca.ReasonCode = "KIT0010";
            }
            ca.ActionHint = "REDUCE, SUBSTITUTE OR ORDER";
            StringIntoMessage(ca, "Short on " + Pic.Trim(ca.LimitingItem) + " by " + Pic.Ed(ca.LimitingShort) +
                ". Nothing has been reserved or ordered.");
        }
    }

    /// <summary>GETLINE: pages the retained answer from KT-FROM-LINE (a 0-based offset); no recalculation.</summary>
    private void FillWindow(KitTrialArea ca)
    {
        if (ca.WinMax is <= 0 or > 20) ca.WinMax = 20;
        if (ca.FromLine < 0) ca.FromLine = 0;
        ca.LineTotal = _res.Count;
        ca.WinCnt = 0;
        ca.MoreLines = "N";
        for (var i = 1; i <= 20 && ca.WinCnt < ca.WinMax; i++)
        {
            var j = ca.FromLine + i;
            if (j > _res.Count) break;
            ca.Lines[ca.WinCnt++] = _res[j - 1].Copy();
        }
        if (ca.FromLine + ca.WinCnt < _res.Count) ca.MoreLines = "Y";
    }
}
