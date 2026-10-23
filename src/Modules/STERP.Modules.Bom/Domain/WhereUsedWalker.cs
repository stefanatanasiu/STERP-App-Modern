using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>One Ws-Res row / WU-LINES entry of WHEREUSD.</summary>
internal sealed class WhereUsedRow
{
    /// <summary>WU-L-SEQ: the result row number.</summary>
    public int Seq;
    public int Level;
    public string Parent = "";
    public string ParentRev = "";
    public string Desc = "";
    public string BomRef = "";
    public string BomRev = "";
    public string Status = "";
    public string Purpose = "";
    public string Variant = "";
    public string Site = "";
    public int LineNum;
    /// <summary>S9(13)V9(6).</summary>
    public decimal Usage;
    public string Uom = "";
    public string Basis = "";
    public int EffFrom;
    public int EffTo;
    public string Certainty = "";
    public string HasParent = "";
    /// <summary>X(120).</summary>
    public string Path = "";
    public string Demand = "";

    public WhereUsedRow Clone() => (WhereUsedRow)MemberwiseClone();
}

/// <summary>WHEREUSDCA (cobol/copy/WHEREUSDCA.cpy).</summary>
internal sealed class WhereUsedArea
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

    public string Item { get; set; } = "";
    public string ItemRev { get; set; } = "";
    public int EffDate { get; set; }
    public string Purpose { get; set; } = "";
    public int MaxDepth { get; set; }
    public string IncludeDraft { get; set; } = "";
    public string IncludeOpt { get; set; } = "";
    public int ParentCount { get; set; }
    public int TopCount { get; set; }
    public int DemandCount { get; set; }
    public int DepthReached { get; set; }
    public string Completeness { get; set; } = "";
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public List<WhereUsedRow> Lines { get; } = [];
}

/// <summary>
/// WHEREUSD (cobol/online/bom/WHEREUSD.cob): where-used walked upwards breadth first (released bills, or drafts too when
/// asked; 25 levels, 200 per frontier, 400 rows), the top-level assemblies and the open execution demand. Used by the bulk
/// change proposals (ancestry test) and BOMRP030 (SME-641: the online enquiry uses bom.usp_BomWhereUsed). The one-level
/// function DIRECT is never sent and is deferred (dispositions cobol:WHEREUSD#DIRECT).
/// </summary>
internal sealed class WhereUsedWalker(ISqlRunner sql)
{
    private const int ResMax = 400;
    private const int FrontMax = 200;

    private const string ParentSql =
        "SELECT h.BomHeaderId, h.ParentItemCode, ISNULL(h.ParentRevision,''), ISNULL(i.Description,''), h.BomRef, h.BomRevision, " +
        "h.Status, h.Purpose, ISNULL(h.VariantCode,''), ISNULL(h.SiteCode,''), c.LineNum, c.UsageQty, c.UomCode, c.UsageBasis, " +
        "ISNULL(CONVERT(char(8),c.EffectiveFrom,112),'00000000'), ISNULL(CONVERT(char(8),c.EffectiveTo,112),'00000000'), " +
        "CASE WHEN c.IsOptional = 1 AND c.IsSelected = 0 THEN 'POSSIBLE' WHEN c.AlternateGroup IS NOT NULL AND c.IsSelected = 0 " +
        "THEN 'POSSIBLE' ELSE 'CONFIRMED' END FROM bom.BomComponent c JOIN bom.BomHeader h ON h.BomHeaderId = c.BomHeaderId " +
        "LEFT JOIN inv.Item i ON i.CompanyCode = h.CompanyCode AND i.ItemCode = h.ParentItemCode WHERE h.CompanyCode = ? " +
        "AND c.ComponentItemCode = ? AND (h.Status = 'RELEASED' OR ? = 'Y') AND h.Status <> 'RETIRED' " +
        "AND h.EffectiveFrom <= CONVERT(date,?,112) AND (h.EffectiveTo IS NULL OR h.EffectiveTo > CONVERT(date,?,112)) " +
        "ORDER BY h.ParentItemCode, c.LineNum";

    private const string DemandSql =
        "SELECT 'EXECUTIONKIT', k.KitRef, k.Status, k.ParentItemCode, k.PlannedGoodQty, ISNULL(CONVERT(char(8),k.NeedDate,112),'00000000') " +
        "FROM kit.ExecutionKit k WHERE k.CompanyCode = ? AND k.Status IN ('PLANNED','RELEASED','PICKED','INPROGRESS') " +
        "AND (k.ParentItemCode = ? OR EXISTS (SELECT 1 FROM kit.ExecutionKitLine l WHERE l.ExecutionKitId = k.ExecutionKitId " +
        "AND l.ComponentItemCode = ?)) UNION ALL SELECT 'RESERVATION', r.DemandRef, r.Status, r.ItemCode, r.QtyReserved, " +
        "ISNULL(CONVERT(char(8),r.RequiredDate,112),'00000000') FROM inv.StockReservation r WHERE r.CompanyCode = ? " +
        "AND r.ItemCode = ? AND r.Status = 'ACTIVE'";

    private sealed class Walk
    {
        public int Level;
        public string LimitHit = "N";
        public List<(string Item, string Path)> Cur = [];
        public List<(string Item, string Path)> Next = [];
    }

    /// <summary>
    /// Ws-Results: the rows of the last walk, paged by GETLINE (per call object, spec CS-BOM-06: a new row starts without
    /// the has-parent flag a slot may have carried from an earlier walk of the legacy run unit).
    /// </summary>
    private List<WhereUsedRow> res = [];

    public async Task CallAsync(WhereUsedArea a, CancellationToken ct)
    {
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Item = Pic.X(Pic.Upper(a.Item), 40);
        if (a.EffDate == 0) a.EffDate = a.BusinessDate;
        if (a.MaxDepth < 1 || a.MaxDepth > 25) a.MaxDepth = 25;
        if (a.WinMax < 1 || a.WinMax > 25) a.WinMax = 25;
        a.WinCnt = 0;
        a.MoreLines = "N";
        switch (a.Function)
        {
            case "ALL":
                await WalkUpwardsFullyAsync(a, ct);
                PageTheResult(a);
                break;
            case "TOPLEVEL":
                await WalkUpwardsFullyAsync(a, ct);
                KeepOnlyTopLevel(a);
                PageTheResult(a);
                break;
            case "OPENUSE":
                await FindOpenDemandAsync(a, ct);
                PageTheResult(a);
                break;
            case "GETLINE":
                PageTheResult(a);
                break;
            default:
                a.ReturnCode = 8;
                a.Reason = "BOM0901";
                a.Message = Pic.Str(200, "WHEREUSD does not support the function ", Pic.T(a.Function));
                break;
        }
    }

    private Walk StartTheWalk(WhereUsedArea a)
    {
        res = [];
        a.ParentCount = 0;
        a.TopCount = 0;
        a.DemandCount = 0;
        a.DepthReached = 0;
        a.Completeness = "COMPLETE";
        var w = new Walk();
        w.Cur.Add((a.Item, Pic.X(a.Item, 120)));
        w.Level = 0;
        return w;
    }

    private async Task WalkUpwardsFullyAsync(WhereUsedArea a, CancellationToken ct)
    {
        var w = StartTheWalk(a);
        while (!(w.Cur.Count == 0 || w.Level >= a.MaxDepth || w.LimitHit == "Y"))
        {
            await OneLevelUpAsync(a, w, ct);
            w.Cur = w.Next;
            w.Next = [];
        }
        FinishTheWalk(a, w);
    }

    private void FinishTheWalk(WhereUsedArea a, Walk w)
    {
        a.ParentCount = res.Count;
        a.DepthReached = w.Level;
        if (w.LimitHit == "Y")
        {
            a.Completeness = "INCOMPLETE";
            a.ReturnCode = 4;
            a.Reason = "BOM0902";
            a.Message = "More parents exist than this enquiry can carry";
            a.MessageCount = Pic.I(a.MessageCount + 1, 2);
            return;
        }
        a.ReturnCode = 0;
        if (res.Count == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0903";
            a.Message = Pic.Str(200, Pic.T(a.Item), " is not used on any bill for that date");
        }
    }

    private async Task OneLevelUpAsync(WhereUsedArea a, Walk w, CancellationToken ct)
    {
        w.Level++;
        w.Next = [];
        foreach (var (item, path) in w.Cur)
        {
            await ReadDirectParentsAsync(a, w, item, path, ct);
        }
    }

    /// <summary>One level of parents; an unselected optional or alternate line is POSSIBLE usage.</summary>
    private async Task ReadDirectParentsAsync(WhereUsedArea a, Walk w, string curItem, string curPath, CancellationToken ct)
    {
        var effText = BomSql.D8(a.EffDate);
        List<string[]> rows;
        try
        {
            rows = await sql.WithReaderAsync(BomSql.Q(ParentSql),
                BomSql.Args(Pic.X(a.Company, 10), curItem, a.IncludeDraft == "Y" ? "Y" : "N", effText, effText), async r =>
                {
                    var list = new List<string[]>();
                    while (await r.ReadAsync(ct))
                    {
                        list.Add(
                        [
                            BomSql.Text(r, 0, 12), BomSql.Text(r, 1, 40), BomSql.Text(r, 2, 4), BomSql.Text(r, 3, 40), BomSql.Text(r, 4, 30),
                            BomSql.Text(r, 5, 4), BomSql.Text(r, 6, 12), BomSql.Text(r, 7, 10), BomSql.Text(r, 8, 20), BomSql.Text(r, 9, 10),
                            BomSql.Text(r, 10, 8), BomSql.Text(r, 11, 24), BomSql.Text(r, 12, 10), BomSql.Text(r, 13, 12),
                            BomSql.Text(r, 14, 8), BomSql.Text(r, 15, 8), BomSql.Text(r, 16, 10),
                        ]);
                    }
                    return list;
                }, ct);
        }
        catch (SqlioException ex)
        {
            a.ReturnCode = 12;
            a.Reason = "SQL0001";
            a.Message = BomSql.ErrMsg(ex, 200);
            return;
        }
        var yielded = false;
        foreach (var rw in rows)
        {
            yielded = true;
            KeepOneParent(a, w, rw, curPath);
        }
        if (!yielded) return;
        for (var j = 0; j < res.Count; j++)
        {
            if (res[j].Parent != curItem) continue;
            res[j].HasParent = "Y";
        }
    }

    private void KeepOneParent(WhereUsedArea a, Walk w, string[] rw, string curPath)
    {
        if (res.Count >= ResMax)
        {
            w.LimitHit = "Y";
            return;
        }
        var row = new WhereUsedRow
        {
            Level = w.Level,
            Parent = rw[1],
            ParentRev = rw[2],
            Desc = rw[3],
            BomRef = rw[4],
            BomRev = rw[5],
            Status = rw[6],
            Purpose = rw[7],
            Variant = rw[8],
            Site = rw[9],
            LineNum = Pic.I((long)decimal.Truncate(BomSql.NumVal(rw[10])), 4),
            Usage = Pic.N(BomSql.NumVal(rw[11]), 13, 6),
            Uom = rw[12],
            Basis = rw[13],
            EffFrom = Date8(rw[14]),
            EffTo = Date8(rw[15]),
            Certainty = rw[16],
            Path = Pic.Str(120, Pic.T(rw[1]), " <- ", Pic.T(curPath)),
            Demand = "",
        };
        res.Add(row);
        // a parent appears on the next frontier once, however many lines reach it
        if (w.Next.Any(n => n.Item == row.Parent) || w.Next.Count >= FrontMax) return;
        w.Next.Add((row.Parent, row.Path));
    }

    private static int Date8(string t) => t.Length == 8 && t.All(char.IsAsciiDigit) ? int.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private void KeepOnlyTopLevel(WhereUsedArea a)
    {
        res = res.Where(r => r.HasParent != "Y").ToList();
        a.TopCount = res.Count;
        a.ParentCount = res.Count;
    }

    /// <summary>Released kits and open stock reservations that use the item (BOM-023, BOM-031).</summary>
    private async Task FindOpenDemandAsync(WhereUsedArea a, CancellationToken ct)
    {
        var w = StartTheWalk(a);
        List<string[]> rows;
        try
        {
            rows = await sql.WithReaderAsync(BomSql.Q(DemandSql),
                BomSql.Args(Pic.X(a.Company, 10), a.Item, a.Item, Pic.X(a.Company, 10), a.Item), async r =>
                {
                    var list = new List<string[]>();
                    while (await r.ReadAsync(ct))
                    {
                        list.Add([BomSql.Text(r, 0, 20), BomSql.Text(r, 1, 40), BomSql.Text(r, 2, 12), BomSql.Text(r, 3, 40), BomSql.Text(r, 4, 24), BomSql.Text(r, 5, 8)]);
                    }
                    return list;
                }, ct);
        }
        catch (SqlioException ex)
        {
            a.ReturnCode = 12;
            a.Reason = "SQL0001";
            a.Message = BomSql.ErrMsg(ex, 200);
            return;
        }
        foreach (var d in rows)
        {
            if (res.Count >= ResMax)
            {
                w.LimitHit = "Y";
                continue;
            }
            var path = Pic.Str(120, Pic.T(d[0]), " ", Pic.T(d[1]));
            res.Add(new WhereUsedRow
            {
                Level = 0,
                    Parent = d[3],
                ParentRev = "",
                Desc = d[0],
                BomRef = Pic.X(d[1], 30),
                BomRev = "",
                Status = d[2],
                Purpose = "DEMAND",
                Variant = "",
                Site = Pic.X(a.Site, 10),
                LineNum = 0,
                Usage = Pic.N(BomSql.NumVal(d[4]), 13, 6),
                Uom = "",
                Basis = "DEMAND",
                EffFrom = Date8(d[5]),
                EffTo = 0,
                Certainty = "CONFIRMED",
                Path = path,
                Demand = Pic.X(path, 60),
            });
        }
        a.DemandCount = res.Count;
        if (res.Count == 0)
        {
            a.ReturnCode = 16;
            a.Reason = "BOM0904";
            a.Message = "No open execution demand uses that item";
        }
    }

    private void PageTheResult(WhereUsedArea a)
    {
        a.Lines.Clear();
        if (a.FromLine < 1) a.FromLine = 1;
        var i = a.FromLine;
        while (!(i > res.Count || a.WinCnt >= a.WinMax))
        {
            a.WinCnt++;
            var line = res[i - 1].Clone();
            line.Seq = i;
            a.Lines.Add(line);
            i++;
        }
        a.FromLine = i;
        if (i <= res.Count) a.MoreLines = "Y";
    }
}
