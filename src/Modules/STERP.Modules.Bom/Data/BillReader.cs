using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Data;

/// <summary>BOM-HDR-REC (cobol/copy/BOMBILL.cpy).</summary>
internal sealed class BillHeader
{
    public int BomId { get; set; }
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string BomRef { get; set; } = "";
    public string BomRev { get; set; } = "";
    public string ParentItem { get; set; } = "";
    public string ParentRev { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string JobRef { get; set; } = "";
    public string Variant { get; set; } = "";
    /// <summary>BH-DESCRIPTION X(60).</summary>
    public string Description { get; set; } = "";
    /// <summary>BH-NOMINAL-BASIS S9(13)V9(6).</summary>
    public decimal NominalBasis { get; set; }
    /// <summary>BH-YIELD S9(5)V9(6).</summary>
    public decimal Yield { get; set; }
    /// <summary>BH-QUANTUM S9(13)V9(6).</summary>
    public decimal Quantum { get; set; }
    /// <summary>BH-BATCH-SIZE S9(13)V9(6).</summary>
    public decimal BatchSize { get; set; }
    public string IsPhantom { get; set; } = "";
    public string Consolidate { get; set; } = "";
    public decimal CostingLot { get; set; }
    /// <summary>BH-SETUP-COST S9(11)V9(6).</summary>
    public decimal SetupCost { get; set; }
    /// <summary>BH-CONV-COST S9(11)V9(6).</summary>
    public decimal ConvCost { get; set; }
    public int EffFrom { get; set; }
    public int EffTo { get; set; }
    public string Status { get; set; } = "";
    public int ValidationId { get; set; }
    /// <summary>BH-LOSS-INTENT X(60).</summary>
    public string LossIntent { get; set; } = "";
}

/// <summary>One BOM-CMP-LINE (cobol/copy/BOMBILL.cpy).</summary>
internal sealed class BillLine
{
    public int LineNum { get; set; }
    public int ComponentId { get; set; }
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    /// <summary>BC-DESC X(40).</summary>
    public string Desc { get; set; } = "";
    /// <summary>BC-USAGE-QTY S9(13)V9(6).</summary>
    public decimal UsageQty { get; set; }
    public string UsageBasis { get; set; } = "";
    public string Uom { get; set; } = "";
    public string StockUom { get; set; } = "";
    /// <summary>BC-CONV-FACT S9(9)V9(9).</summary>
    public decimal ConvFact { get; set; }
    /// <summary>BC-SCRAP-PCT S9(5)V9(6).</summary>
    public decimal ScrapPct { get; set; }
    public string Supply { get; set; } = "";
    public string Optional { get; set; } = "";
    public string Selected { get; set; } = "";
    public string AltGroup { get; set; } = "";
    public string OptionRef { get; set; } = "";
    public int EffFrom { get; set; }
    public int EffTo { get; set; }
    public int NeedOffset { get; set; }
    public string Operation { get; set; } = "";
    /// <summary>BC-COST-RATE S9(11)V9(6).</summary>
    public decimal CostRate { get; set; }
    public string CostElement { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public string MakeBuy { get; set; } = "";
    /// <summary>BC-QUANTUM S9(13)V9(6).</summary>
    public decimal Quantum { get; set; }
    public string Indivisible { get; set; } = "";
    public string Stocked { get; set; } = "";
    /// <summary>BC-PACK-QTY S9(13)V9(6).</summary>
    public decimal PackQty { get; set; }
    public string ItemFound { get; set; } = "";

    public BillLine Clone() => (BillLine)MemberwiseClone();
}

/// <summary>BOM-RD-CTL outcome plus the bill buffer (BOM-HDR-REC, BOM-CMP-TAB).</summary>
internal sealed class BillRead
{
    /// <summary>BR-STATUS: OK, NOTFOUND, TOOMANY, SQLFAIL.</summary>
    public string Status { get; set; } = "OK";
    public string Reason { get; set; } = "";
    /// <summary>BR-MESSAGE X(200).</summary>
    public string Message { get; set; } = "";
    public BillHeader Header { get; set; } = new();
    public List<BillLine> Lines { get; } = [];
    public int LineCount => Lines.Count;
    public bool Ok => Status == "OK";
}

/// <summary>
/// BOMREAD (cobol/online/bom/BOMREAD.cob): reads one bill (header and every component line in line order with the item
/// attributes) or one item, through SQLIO on the caller's unit of work. Component effectivity is not filtered here.
/// </summary>
internal sealed class BillReader(ISqlRunner sql)
{
    private const string HeaderSql =
        "SELECT h.BomHeaderId, h.CompanyCode, ISNULL(h.SiteCode,''), h.BomRef, h.BomRevision, h.ParentItemCode, ISNULL(h.ParentRevision,''), " +
        "h.Purpose, ISNULL(h.JobRef,''), ISNULL(h.VariantCode,''), ISNULL(h.Description,''), h.NominalBasis, h.YieldFactor, h.ProductionQuantum, " +
        "ISNULL(h.BatchSize,0), CASE WHEN h.IsPhantom=1 THEN 'Y' ELSE 'N' END, CASE WHEN h.ConsolidateRuns=1 THEN 'Y' ELSE 'N' END, " +
        "ISNULL(h.CostingLotSize,0), ISNULL(h.SetupCostPerRun,0), ISNULL(h.ConversionCostPerStart,0), CONVERT(char(8),h.EffectiveFrom,112), " +
        "ISNULL(CONVERT(char(8),h.EffectiveTo,112),'00000000'), h.Status, ISNULL(h.ValidationId,0), ISNULL(h.LossIntentNote,'') " +
        "FROM bom.BomHeader h WHERE h.BomHeaderId = ?";

    private const string ComponentSql =
        "SELECT c.LineNum, c.BomComponentId, c.ComponentItemCode, ISNULL(c.ComponentRevision,''), ISNULL(i.Description,''), c.UsageQty, c.UsageBasis, " +
        "c.UomCode, ISNULL(i.StockUomCode,''), c.ConversionFactor, c.ScrapPct, c.SupplyBehaviour, CASE WHEN c.IsOptional=1 THEN 'Y' ELSE 'N' END, " +
        "CASE WHEN c.IsSelected=1 THEN 'Y' ELSE 'N' END, ISNULL(c.AlternateGroup,''), ISNULL(c.OptionChoiceRef,''), " +
        "ISNULL(CONVERT(char(8),c.EffectiveFrom,112),'00000000'), ISNULL(CONVERT(char(8),c.EffectiveTo,112),'00000000'), c.NeedOffsetDays, " +
        "ISNULL(c.OperationRef,''), ISNULL(c.CostOnlyRate,0), ISNULL(c.CostElementCode,''), ISNULL(i.ItemClass,''), ISNULL(i.MakeBuy,''), " +
        "ISNULL(i.QuantumQty,0), CASE WHEN i.IsIndivisible=1 THEN 'Y' ELSE 'N' END, CASE WHEN i.IsStocked=1 THEN 'Y' ELSE 'N' END, " +
        "ISNULL(s.PurchaseMultipleQty,0), CASE WHEN i.ItemCode IS NULL THEN 'N' ELSE 'Y' END FROM bom.BomComponent c " +
        "LEFT JOIN inv.Item i ON i.CompanyCode = ? AND i.ItemCode = c.ComponentItemCode " +
        "LEFT JOIN inv.ItemSite s ON s.CompanyCode = ? AND s.SiteCode = ? AND s.ItemCode = c.ComponentItemCode " +
        "WHERE c.BomHeaderId = ? ORDER BY c.LineNum";

    private const string ItemSql =
        "SELECT i.ItemCode, ISNULL(i.Description,''), i.StockUomCode, i.ItemClass, i.MakeBuy, i.QuantumQty, " +
        "CASE WHEN i.IsIndivisible=1 THEN 'Y' ELSE 'N' END, CASE WHEN i.IsStocked=1 THEN 'Y' ELSE 'N' END, " +
        "ISNULL(s.PurchaseMultipleQty,0), i.CurrentRevision, i.LifecycleStatus FROM inv.Item i LEFT JOIN inv.ItemSite s " +
        "ON s.CompanyCode = i.CompanyCode AND s.SiteCode = ? AND s.ItemCode = i.ItemCode WHERE i.CompanyCode = ? AND i.ItemCode = ?";

    /// <summary>HEADER: the header of bill <paramref name="bomId"/>.</summary>
    public async Task<BillRead> HeaderAsync(int bomId, CancellationToken ct)
    {
        var read = new BillRead();
        await ReadHeaderAsync(read, bomId, ct);
        return read;
    }

    /// <summary>BILL: header then every component line (at most <paramref name="maxLines"/>, else TOOMANY).</summary>
    public async Task<BillRead> BillAsync(string company, string site, int bomId, int maxLines, CancellationToken ct)
    {
        var read = new BillRead();
        await ReadHeaderAsync(read, bomId, ct);
        if (read.Ok) await ReadComponentsAsync(read, company, site, bomId, maxLines, ct);
        return read;
    }

    private async Task ReadHeaderAsync(BillRead read, int bomId, CancellationToken ct)
    {
        try
        {
            var found = await sql.WithReaderAsync(BomSql.Q(HeaderSql), BomSql.Args(bomId), async r =>
            {
                if (!await r.ReadAsync(ct)) return false;
                var h = read.Header;
                h.BomId = (int)BomSql.Num(r, 0);
                h.Company = BomSql.Text(r, 1, 10);
                h.Site = BomSql.Text(r, 2, 10);
                h.BomRef = BomSql.Text(r, 3, 30);
                h.BomRev = BomSql.Text(r, 4, 4);
                h.ParentItem = BomSql.Text(r, 5, 40);
                h.ParentRev = BomSql.Text(r, 6, 4);
                h.Purpose = BomSql.Text(r, 7, 10);
                h.JobRef = BomSql.Text(r, 8, 20);
                h.Variant = BomSql.Text(r, 9, 20);
                h.Description = BomSql.Text(r, 10, 60);
                h.NominalBasis = Pic.N(BomSql.Num(r, 11), 13, 6);
                h.Yield = Pic.N(BomSql.Num(r, 12), 5, 6);
                h.Quantum = Pic.N(BomSql.Num(r, 13), 13, 6);
                h.BatchSize = Pic.N(BomSql.Num(r, 14), 13, 6);
                h.IsPhantom = BomSql.Text(r, 15, 1);
                h.Consolidate = BomSql.Text(r, 16, 1);
                h.CostingLot = Pic.N(BomSql.Num(r, 17), 13, 6);
                h.SetupCost = Pic.N(BomSql.Num(r, 18), 11, 6);
                h.ConvCost = Pic.N(BomSql.Num(r, 19), 11, 6);
                h.EffFrom = BomSql.Date8(r, 20);
                h.EffTo = BomSql.Date8(r, 21);
                h.Status = BomSql.Text(r, 22, 12);
                h.ValidationId = (int)BomSql.Num(r, 23);
                h.LossIntent = BomSql.Text(r, 24, 60);
                return true;
            }, ct);
            if (!found)
            {
                read.Status = "NOTFOUND";
                read.Reason = "BOM0002";
                read.Message = "No bill of materials holds that identity";
            }
        }
        catch (SqlioException ex)
        {
            SqlFailure(read, ex);
        }
    }

    private async Task ReadComponentsAsync(BillRead read, string company, string site, int bomId, int maxLines, CancellationToken ct)
    {
        try
        {
            await sql.WithReaderAsync(BomSql.Q(ComponentSql), BomSql.Args(company, company, site, bomId), async r =>
            {
                while (await r.ReadAsync(ct))
                {
                    if (read.Lines.Count >= maxLines)
                    {
                        read.Status = "TOOMANY";
                        read.Reason = "BOM0003";
                        read.Message = "This bill holds more component lines than the engine can carry";
                        break;
                    }
                    read.Lines.Add(new BillLine
                    {
                        LineNum = Pic.I((long)BomSql.Num(r, 0), 4),
                        ComponentId = (int)BomSql.Num(r, 1),
                        Item = BomSql.Text(r, 2, 40),
                        Rev = BomSql.Text(r, 3, 4),
                        Desc = BomSql.Text(r, 4, 40),
                        UsageQty = Pic.N(BomSql.Num(r, 5), 13, 6),
                        UsageBasis = BomSql.Text(r, 6, 12),
                        Uom = BomSql.Text(r, 7, 10),
                        StockUom = BomSql.Text(r, 8, 10),
                        ConvFact = Pic.N(BomSql.Num(r, 9), 9, 9),
                        ScrapPct = Pic.N(BomSql.Num(r, 10), 5, 6),
                        Supply = BomSql.Text(r, 11, 10),
                        Optional = BomSql.Text(r, 12, 1),
                        Selected = BomSql.Text(r, 13, 1),
                        AltGroup = BomSql.Text(r, 14, 10),
                        OptionRef = BomSql.Text(r, 15, 20),
                        EffFrom = BomSql.Date8(r, 16),
                        EffTo = BomSql.Date8(r, 17),
                        NeedOffset = Pic.I((long)BomSql.Num(r, 18), 4),
                        Operation = BomSql.Text(r, 19, 20),
                        CostRate = Pic.N(BomSql.Num(r, 20), 11, 6),
                        CostElement = BomSql.Text(r, 21, 10),
                        ItemClass = BomSql.Text(r, 22, 12),
                        MakeBuy = BomSql.Text(r, 23, 6),
                        Quantum = Pic.N(BomSql.Num(r, 24), 13, 6),
                        Indivisible = BomSql.Text(r, 25, 1),
                        Stocked = BomSql.Text(r, 26, 1),
                        PackQty = Pic.N(BomSql.Num(r, 27), 13, 6),
                        ItemFound = BomSql.Text(r, 28, 1),
                    });
                }
                return true;
            }, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailure(read, ex);
        }
    }

    /// <summary>ITEM: the attributes of one item in line one of the component table (root parent of a calculation).</summary>
    public async Task<BillRead> ItemAsync(string company, string site, string itemCode, CancellationToken ct)
    {
        var read = new BillRead();
        try
        {
            var found = await sql.WithReaderAsync(BomSql.Q(ItemSql), BomSql.Args(site, company, itemCode), async r =>
            {
                if (!await r.ReadAsync(ct)) return false;
                var stockUom = BomSql.Text(r, 2, 10);
                read.Lines.Add(new BillLine
                {
                    Item = BomSql.Text(r, 0, 40),
                    Desc = BomSql.Text(r, 1, 40),
                    StockUom = stockUom,
                    Uom = stockUom,
                    ItemClass = BomSql.Text(r, 3, 12),
                    MakeBuy = BomSql.Text(r, 4, 6),
                    Quantum = Pic.N(BomSql.Num(r, 5), 13, 6),
                    Indivisible = BomSql.Text(r, 6, 1),
                    Stocked = BomSql.Text(r, 7, 1),
                    PackQty = Pic.N(BomSql.Num(r, 8), 13, 6),
                    Rev = BomSql.Text(r, 9, 4),
                    Operation = BomSql.Text(r, 10, 10),
                    ItemFound = "Y",
                });
                return true;
            }, ct);
            if (!found)
            {
                read.Status = "NOTFOUND";
                read.Reason = "BOM0004";
                read.Message = Pic.Str(200, "Item ", Pic.T(itemCode), " is not held in the item master");
            }
        }
        catch (SqlioException ex)
        {
            SqlFailure(read, ex);
        }
        return read;
    }

    private static void SqlFailure(BillRead read, SqlioException ex)
    {
        read.Status = "SQLFAIL";
        read.Reason = "SQL0001";
        read.Message = BomSql.ErrMsg(ex, 200);
    }
}
