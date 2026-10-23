using Sterp.Modules.Bom.Contracts;
using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>One Ws-Res row / BP-LINES entry of BOMCOMP.</summary>
internal sealed class CompareLine
{
    public int Seq;
    /// <summary>X(10): CHANGED, SEQUENCE, REMOVED or ADDED.</summary>
    public string Action = "";
    public string Item = "";
    public string Rev = "";
    public string Desc = "";
    public int FromLine;
    public int ToLine;
    public decimal FromQty;
    public decimal ToQty;
    public string FromUom = "";
    public string ToUom = "";
    public string FromBasis = "";
    public string ToBasis = "";
    public decimal FromScrap;
    public decimal ToScrap;
    /// <summary>BP-L-QTY-DELTA S9(15)V9(6).</summary>
    public decimal QtyDelta;
    /// <summary>X(100).</summary>
    public string Detail = "";

    public CompareLine Clone() => (CompareLine)MemberwiseClone();
}

/// <summary>BOMCOMPCA (cobol/copy/BOMCOMPCA.cpy).</summary>
internal sealed class CompareArea
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

    public string BomRef { get; set; } = "";
    public string FromRev { get; set; } = "";
    public string ToRev { get; set; } = "";
    public int FromBomId { get; set; }
    public int ToBomId { get; set; }
    public string ParentItem { get; set; } = "";
    /// <summary>BP-REQ-QTY S9(15)V9(6).</summary>
    public decimal ReqQty { get; set; }
    public int EffDate { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ChangedCount { get; set; }
    public int SeqOnlyCount { get; set; }
    public int SameCount { get; set; }
    /// <summary>BP-HEADER-DIFF X(200).</summary>
    public string HeaderDiff { get; set; } = "";
    public string MaterialChange { get; set; } = "";
    /// <summary>S9(13)V9(2).</summary>
    public decimal CostDelta { get; set; }
    public decimal FromCost { get; set; }
    public decimal ToCost { get; set; }
    public string Completeness { get; set; } = "";
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public List<CompareLine> Lines { get; } = [];
}

/// <summary>
/// BOMCOMP (cobol/online/bom/BOMCOMP.cob): the batch revision comparison (SME-641; the online comparison is
/// bom.usp_BomCompareRevisions) - BYREF resolves the two revisions of a bill, IMPACT compares and costs both through
/// BOMCOST, GETLINE pages the retained differences. COMPARE is never sent and is deferred (dispositions
/// cobol:BOMCOMP#COMPARE). Lines match on item and alternate group, first unused occurrence (BOM-002).
/// </summary>
internal sealed class RevisionComparer(ISqlRunner sql, BillReader reader, CostEngine cost)
{
    private const int ResMax = 300;

    private const string RevisionSql = "SELECT h.BomHeaderId FROM bom.BomHeader h WHERE h.CompanyCode = ? AND h.BomRef = ? AND h.BomRevision = ?";

    /// <summary>Ws-Results: the differences of the last comparison of this run unit, paged by GETLINE.</summary>
    private readonly List<CompareLine> res = [];

    private BillRead billA = new();
    private BillRead billB = new();

    public async Task CallAsync(CompareArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        a.Lines.Clear();
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        switch (a.Function)
        {
            case "BYREF":
                await ResolveByReferenceAsync(a, ct);
                if (a.ReturnCode <= 4)
                {
                    await CompareTwoBillsAsync(a, ct);
                    PageTheDifferences(a);
                }
                break;
            case "IMPACT":
                await CompareTwoBillsAsync(a, ct);
                await CostBothRevisionsAsync(a, ct);
                PageTheDifferences(a);
                break;
            case "GETLINE":
                PageTheDifferences(a);
                break;
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM1001";
                a.Message = Pic.Str(200, "BOMCOMP does not support the function ", Pic.T(a.Function));
                break;
        }
    }

    /// <summary>BYREF names the two revisions the way an engineer does.</summary>
    private async Task ResolveByReferenceAsync(CompareArea a, CancellationToken ct)
    {
        a.FromBomId = await FindOneRevisionAsync(a, a.FromRev, ct);
        if (a.FromBomId == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM1004";
            a.Message = Pic.Str(200, "Revision ", Pic.T(a.FromRev), " of ", Pic.T(a.BomRef), " does not exist");
            return;
        }
        a.ToBomId = await FindOneRevisionAsync(a, a.ToRev, ct);
        if (a.ToBomId == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM1005";
            a.Message = Pic.Str(200, "Revision ", Pic.T(a.ToRev), " of ", Pic.T(a.BomRef), " does not exist");
        }
    }

    /// <summary>SQL failure or no row: identity 0.</summary>
    private async Task<int> FindOneRevisionAsync(CompareArea a, string rev, CancellationToken ct)
    {
        try
        {
            var id = await sql.WithReaderAsync(BomSql.Q(RevisionSql), BomSql.Args(Pic.X(a.Company, 10), Pic.X(a.BomRef, 30), Pic.X(rev, 4)),
                async r => await r.ReadAsync(ct) ? BomSql.Text(r, 0, 12) : null, ct);
            return id is null ? 0 : (int)decimal.Truncate(BomSql.NumVal(id));
        }
        catch (SqlioException)
        {
            return 0;
        }
    }

    private async Task CompareTwoBillsAsync(CompareArea a, CancellationToken ct)
    {
        billA = await reader.BillAsync(Pic.X(a.Company, 10), Pic.X(a.Site, 10), a.FromBomId, 500, ct);
        if (billA.Status != "OK")
        {
            a.ReturnCode = 16;
            a.Reason = "BOM1002";
            a.Message = Pic.X(billA.Message, 200);
            return;
        }
        a.BomRef = billA.Header.BomRef;
        a.FromRev = billA.Header.BomRev;
        a.ParentItem = billA.Header.ParentItem;
        billB = await reader.BillAsync(Pic.X(a.Company, 10), Pic.X(a.Site, 10), a.ToBomId, 500, ct);
        if (billB.Status != "OK")
        {
            a.ReturnCode = 16;
            a.Reason = "BOM1003";
            a.Message = Pic.X(billB.Message, 200);
            return;
        }
        a.ToRev = billB.Header.BomRev;
        res.Clear();
        a.AddedCount = 0;
        a.RemovedCount = 0;
        a.ChangedCount = 0;
        a.SeqOnlyCount = 0;
        a.SameCount = 0;
        a.MaterialChange = "N";
        a.Completeness = "COMPLETE";
        var used = new bool[billB.LineCount + 1];
        CompareHeaders(a);
        for (var i = 1; i <= billA.LineCount; i++) MatchOneFromLine(a, i, used);
        for (var j = 1; j <= billB.LineCount; j++)
        {
            if (!used[j]) RecordAnAddition(a, billB.Lines[j - 1]);
        }
        PublishVerdict(a);
    }

    /// <summary>STRING FUNCTION TRIM(diff) ... INTO diff: each append drops the trailing space of the previous "; ".</summary>
    private void CompareHeaders(CompareArea a)
    {
        var x = billA.Header;
        var z = billB.Header;
        var diff = "";
        if (x.NominalBasis != z.NominalBasis) diff = Pic.Str(200, "Basis now ", Ed(z.NominalBasis), "; ");
        if (x.Yield != z.Yield) diff = Pic.Str(200, Pic.T(diff), "yield now ", Ed(z.Yield), "; ");
        if (x.Quantum != z.Quantum) diff = Pic.Str(200, Pic.T(diff), "quantum now ", Ed(z.Quantum), "; ");
        if (x.BatchSize != z.BatchSize) diff = Pic.Str(200, Pic.T(diff), "batch size now ", Ed(z.BatchSize));
        a.HeaderDiff = diff;
        if (!Pic.Blank(diff)) a.MaterialChange = "Y";
    }

    /// <summary>Ws-Edit PIC -(12)9.9(6) then FUNCTION TRIM.</summary>
    private static string Ed(decimal v) => CobolEdit.Format(v, "-(12)9.9(6)");

    /// <summary>Match on item and alternate group, taking the first unused occurrence (BOM-002).</summary>
    private void MatchOneFromLine(CompareArea a, int i, bool[] used)
    {
        var x = billA.Lines[i - 1];
        var hit = 0;
        for (var j = 1; j <= billB.LineCount && hit == 0; j++)
        {
            var z = billB.Lines[j - 1];
            if (!used[j] && z.Item == x.Item && z.AltGroup == x.AltGroup) hit = j;
        }
        if (hit == 0)
        {
            RecordARemoval(a, x);
            return;
        }
        used[hit] = true;
        CompareMatchedLines(a, x, billB.Lines[hit - 1]);
    }

    private void CompareMatchedLines(CompareArea a, BillLine x, BillLine z)
    {
        var changed = false;
        var detail = "";
        if (x.UsageQty != z.UsageQty)
        {
            changed = true;
            detail = Pic.Str(100, "Quantity now ", Ed(z.UsageQty), "; ");
        }
        if (x.Uom != z.Uom)
        {
            changed = true;
            detail = Pic.Str(100, Pic.T(detail), "unit now ", Pic.T(z.Uom), "; ");
        }
        if (x.UsageBasis != z.UsageBasis)
        {
            changed = true;
            detail = Pic.Str(100, Pic.T(detail), "basis now ", Pic.T(z.UsageBasis), "; ");
        }
        if (x.ScrapPct != z.ScrapPct)
        {
            changed = true;
            detail = Pic.Str(100, Pic.T(detail), "allowance now ", Ed(z.ScrapPct), "; ");
        }
        if (x.EffFrom != z.EffFrom || x.EffTo != z.EffTo)
        {
            changed = true;
            detail = Pic.Str(100, Pic.T(detail), "effectivity changed; ");
        }
        if (x.Supply != z.Supply)
        {
            changed = true;
            detail = Pic.Str(100, Pic.T(detail), "supply now ", Pic.T(z.Supply));
        }
        var seqOnly = false;
        if (!changed && x.LineNum != z.LineNum)
        {
            seqOnly = true;
            detail = "Sequence only - no material change";
        }
        if (changed)
        {
            a.ChangedCount = Pic.I(a.ChangedCount + 1, 4);
            a.MaterialChange = "Y";
            RecordADifference(a, x, z, "CHANGED", detail);
        }
        else if (seqOnly)
        {
            a.SeqOnlyCount = Pic.I(a.SeqOnlyCount + 1, 4);
            RecordADifference(a, x, z, "SEQUENCE", detail);
        }
        else
        {
            a.SameCount = Pic.I(a.SameCount + 1, 4);
        }
    }

    private void RecordADifference(CompareArea a, BillLine x, BillLine z, string action, string detail)
    {
        if (res.Count >= ResMax)
        {
            a.Completeness = "INCOMPLETE";
            return;
        }
        res.Add(new CompareLine
        {
            Action = action,
            Item = x.Item,
            Rev = x.Rev,
            Desc = x.Desc,
            FromLine = x.LineNum,
            ToLine = z.LineNum,
            FromQty = x.UsageQty,
            ToQty = z.UsageQty,
            FromUom = x.Uom,
            ToUom = z.Uom,
            FromBasis = x.UsageBasis,
            ToBasis = z.UsageBasis,
            FromScrap = x.ScrapPct,
            ToScrap = z.ScrapPct,
            Detail = Pic.X(detail, 100),
        });
    }

    private void RecordARemoval(CompareArea a, BillLine x)
    {
        a.RemovedCount = Pic.I(a.RemovedCount + 1, 4);
        a.MaterialChange = "Y";
        if (res.Count >= ResMax)
        {
            a.Completeness = "INCOMPLETE";
            return;
        }
        res.Add(new CompareLine
        {
            Action = "REMOVED",
            Item = x.Item,
            Rev = x.Rev,
            Desc = x.Desc,
            FromLine = x.LineNum,
            FromQty = x.UsageQty,
            FromUom = x.Uom,
            FromBasis = x.UsageBasis,
            FromScrap = x.ScrapPct,
            Detail = "Component no longer used on this revision",
        });
    }

    private void RecordAnAddition(CompareArea a, BillLine z)
    {
        a.AddedCount = Pic.I(a.AddedCount + 1, 4);
        a.MaterialChange = "Y";
        if (res.Count >= ResMax)
        {
            a.Completeness = "INCOMPLETE";
            return;
        }
        res.Add(new CompareLine
        {
            Action = "ADDED",
            Item = z.Item,
            Rev = z.Rev,
            Desc = z.Desc,
            ToLine = z.LineNum,
            ToQty = z.UsageQty,
            ToUom = z.Uom,
            ToBasis = z.UsageBasis,
            ToScrap = z.ScrapPct,
            Detail = "New component occurrence on this revision",
        });
    }

    /// <summary>STRING FUNCTION TRIM(CA-MESSAGE) ... INTO CA-MESSAGE: every append drops the trailing space.</summary>
    private static void PublishVerdict(CompareArea a)
    {
        a.ReturnCode = 0;
        var m = a.MaterialChange == "Y" ? "Material change: " : "No material change; ";
        m = Pic.Str(200, Pic.T(m), Ed(a.AddedCount), " added, ");
        m = Pic.Str(200, Pic.T(m), Ed(a.RemovedCount), " removed, ");
        m = Pic.Str(200, Pic.T(m), Ed(a.ChangedCount), " changed, ");
        m = Pic.Str(200, Pic.T(m), Ed(a.SeqOnlyCount), " resequenced");
        a.Message = m;
    }

    /// <summary>Both revisions costed for the same demand (cost set BOMDEMO, the parent's effective revision).</summary>
    private async Task CostBothRevisionsAsync(CompareArea a, CancellationToken ct)
    {
        a.FromCost = 0;
        a.ToCost = 0;
        a.CostDelta = 0;
        if (a.ReqQty <= 0) return;
        a.FromCost = await CostOneRevisionAsync(a, billA.Header.ParentItem, ct);
        a.ToCost = await CostOneRevisionAsync(a, billB.Header.ParentItem, ct);
        a.CostDelta = Pic.N(a.ToCost - a.FromCost, 13, 2);
    }

    private async Task<decimal> CostOneRevisionAsync(CompareArea a, string parent, CancellationToken ct)
    {
        var c = new BomCostArea
        {
            Function = "COST",
            Company = Pic.X(a.Company, 10),
            Site = Pic.X(a.Site, 10),
            User = Pic.X(a.User, 20),
            BusinessDate = a.EffDate,
            ParentItem = Pic.X(parent, 40),
            Purpose = "STANDARD",
            ReqQty = Pic.N(a.ReqQty, 15, 6),
            EffDate = a.EffDate,
            MaxDepth = 25,
            CostSet = "BOMDEMO",
            Currency = "GBP",
        };
        await cost.LinkAsync(c, ct);
        return c.TotalCost;
    }

    private void PageTheDifferences(CompareArea a)
    {
        if (a.FromLine < 1) a.FromLine = 1;
        var i = a.FromLine;
        while (!(i > res.Count || a.WinCnt >= a.WinMax))
        {
            a.WinCnt++;
            var line = res[i - 1].Clone();
            line.Seq = i;
            line.QtyDelta = Pic.N(line.ToQty - line.FromQty, 15, 6);
            a.Lines.Add(line);
            i++;
        }
        a.FromLine = i;
        if (i <= res.Count) a.MoreLines = "Y";
    }
}
